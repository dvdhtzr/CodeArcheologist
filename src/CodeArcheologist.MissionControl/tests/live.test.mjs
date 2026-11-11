import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join, dirname } from 'node:path';
import { ApiClient, ApiError, eligibleContinuation, normalizeBaseUrl } from '../src/live/api.ts';
import { displayEvent, hasGap, liveStages, mergeEvents, stageState } from '../src/live/ledger.ts';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const read = path => readFileSync(join(root, path), 'utf8');
const ws = '11111111-1111-4111-8111-111111111111';
const scope = { workspaceId: ws, repositoryId: ws.replace(/1/g, '2'), snapshotId: ws.replace(/1/g, '3') };
const run = ws.replace(/1/g, '4');

function fakeFetch(responses) {
  const calls = [];
  const impl = async (url, init) => {
    calls.push({ url, init });
    const next = responses.shift() ?? { status: 200, body: {} };
    return new Response(next.body === undefined ? '' : JSON.stringify(next.body), { status: next.status });
  };
  return { impl, calls };
}

test('only loopback API addresses are accepted', () => {
  assert.equal(normalizeBaseUrl('http://127.0.0.1:5127'), 'http://127.0.0.1:5127');
  assert.equal(normalizeBaseUrl(' http://localhost:5127/ '), 'http://localhost:5127');
  for (const bad of ['http://example.com:5127', 'http://192.168.1.5:5127', 'file:///x', 'http://127.0.0.1:5127/api', 'http://127.0.0.1:5127/?x=1'])
    assert.throws(() => normalizeBaseUrl(bad), bad);
});

test('pairing sends the custom header, keeps the token in memory and uses it as a bearer', async () => {
  const token = 'a'.repeat(64);
  const { impl, calls } = fakeFetch([
    { status: 200, body: { tokenType: 'Bearer', accessToken: token, expiresAtUtc: '2026-11-11T12:00:00Z' } },
    { status: 200, body: { items: [], totalCount: 0, hasMore: false } },
    { status: 201, body: { id: ws, name: 'W' } },
  ]);
  const api = new ApiClient('http://127.0.0.1:5127', impl);
  await api.pair(' 0123 ');
  assert.equal(calls[0].url, 'http://127.0.0.1:5127/api/v1/auth/token');
  assert.equal(calls[0].init.headers['X-CodeArcheologist-Request'], '1');
  assert.equal(JSON.parse(calls[0].init.body).code, '0123');
  assert.equal(api.connected, true);
  await api.listWorkspaces();
  assert.equal(calls[1].init.headers.Authorization, 'Bearer ' + token);
  assert.equal(calls[1].init.headers['Idempotency-Key'], undefined, 'GET carries no idempotency key');
  await api.createWorkspace('W');
  assert.match(calls[2].init.headers['Idempotency-Key'], /^[0-9a-f-]{36}$/);
  assert.equal(JSON.parse(calls[2].init.body).name, 'W');
});

test('a rejected pairing code and a 401 leave the client disconnected', async () => {
  const { impl } = fakeFetch([{ status: 401, body: null }]);
  const api = new ApiClient('http://127.0.0.1:5127', impl);
  await assert.rejects(api.pair('bad'), e => e instanceof ApiError && e.status === 401 && /pairing code/.test(e.message));
  assert.equal(api.connected, false);
  const second = fakeFetch([{ status: 200, body: { accessToken: 'b'.repeat(64) } }, { status: 401, body: null }]);
  const api2 = new ApiClient('http://127.0.0.1:5127', second.impl);
  await api2.pair('ok');
  await assert.rejects(api2.listWorkspaces(), e => e.status === 401);
  assert.equal(api2.connected, false, 'an expired token is forgotten');
});

test('a 413 explains the limit without claiming nothing was saved', async () => {
  const { impl } = fakeFetch([{ status: 200, body: { accessToken: 'd'.repeat(64) } }, { status: 413, body: { detail: 'Invocation syntax occurrences exceeded the 4500-site limit.' } }]);
  const api = new ApiClient('http://127.0.0.1:5127', impl);
  await api.pair('x');
  await assert.rejects(api.listEntryPoints(scope), e => e.status === 413 && /smaller subfolder/.test(e.message) && /4500-site/.test(e.message) && !/nothing was saved/.test(e.message));
});

test('non-GUID identifiers never reach a URL path', async () => {
  const { impl, calls } = fakeFetch([{ status: 200, body: { accessToken: 'c'.repeat(64) } }]);
  const api = new ApiClient('http://127.0.0.1:5127', impl);
  await api.pair('x');
  assert.throws(() => api.listRepositories('../../etc'), /Invalid identifier/);
  assert.throws(() => api.events({ ...scope, snapshotId: 'x/y' }, run), /Invalid identifier/);
  await assert.doesNotReject(api.cancel(scope, run, 1).catch(() => {}));
  assert.equal(JSON.parse(calls.at(-1).init.body).expectedAttemptNumber, 1);
});

test('the token is never persisted in browser storage or cookies', () => {
  for (const file of ['src/live/api.ts', 'src/live/realtime.ts', 'src/live/ledger.ts', 'src/live/LiveConsole.tsx', 'src/live/Findings.tsx', 'src/live/report.ts', 'src/App.tsx']) {
    const source = read(file).replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
    assert.ok(!/localStorage|sessionStorage|document\.cookie|indexedDB/.test(source), file);
  }
});

const event = (attempt, seq, type, extra = {}) => ({
  eventId: `${attempt}-${seq}`, attemptId: attempt, runId: run, sequenceNumber: seq, eventType: type,
  diagnostic: 'd', fromStatus: null, toStatus: null, occurredAtUtc: `2026-11-11T10:00:${String(seq).padStart(2, '0')}Z`,
  stage: null, ownershipEpoch: 1, checkpointId: null, ...extra,
});

test('events are de-duplicated by id, ordered per attempt, and gaps are detected', () => {
  const a1 = [event('a', 1, 'attempt-admitted'), event('a', 2, 'attempt-started'), event('a', 3, 'role-started', { stage: 'investigator' })];
  const merged = mergeEvents([a1[2], a1[0]], [a1[1], a1[2], a1[0]]);
  assert.deepEqual(merged.map(e => e.sequenceNumber), [1, 2, 3]);
  assert.equal(hasGap(merged), false);
  assert.equal(hasGap([a1[0], a1[2]]), true);
  const later = { ...event('b', 1, 'continuation-admitted'), occurredAtUtc: '2026-11-11T11:00:00Z' };
  assert.deepEqual(mergeEvents([later], a1).map(e => e.eventId), ['a-1', 'a-2', 'a-3', 'b-1']);
});

test('agent states come only from the execution projection and keep the host code', () => {
  assert.equal(stageState('running'), 'active');
  for (const s of ['accepted', 'accepted-earlier', 'reused-checkpoint']) assert.equal(stageState(s), 'accepted');
  for (const s of ['rejected', 'timed-out', 'cancelled', 'failed', 'abandoned']) assert.equal(stageState(s), 'halted');
  for (const s of ['not-started', 'legacy-unavailable', 'something-new']) assert.equal(stageState(s), 'queued');
  const stages = liveStages({ stages: [{ role: 'investigator', status: 'accepted', checkpoint: null },
    { role: 'skeptic', status: 'running', checkpoint: null }, { role: 'reviewer', status: 'not-started', checkpoint: null }] });
  assert.deepEqual(Object.fromEntries(Object.entries(stages).map(([k, v]) => [k, v.state])),
    { investigator: 'accepted', skeptic: 'active', reviewer: 'queued' });
  assert.equal(stages.skeptic.hostStatus, 'running');
  assert.equal(liveStages(null).reviewer.hostStatus, 'not-started');
});

test('timeline entries name the role only when the host recorded a stage', () => {
  const role = displayEvent(event('a', 3, 'role-assessment-accepted', { stage: 'skeptic' }));
  assert.equal(role.actor, 'skeptic');
  assert.equal(role.label, 'Skeptic assessment accepted by the host');
  const host = displayEvent(event('a', 2, 'attempt-started', { fromStatus: 'queued', toStatus: 'running' }));
  assert.equal(host.actor, 'host');
  assert.match(host.detail, /queued → running/);
  assert.equal(displayEvent(event('a', 9, 'role-started', { stage: 'not-a-role' })).actor, 'host');
  assert.equal(displayEvent(event('a', 4, 'evidence-verification-started')).label, 'Evidence verification started');
});

test('network code is confined to the live adapter; demo mode stays labelled', () => {
  const app = read('src/App.tsx');
  assert.ok(!/\bfetch\s*\(|HubConnectionBuilder|new\s+WebSocket/.test(app), 'App.tsx must use the adapter');
  for (const file of readdirSync(join(root, 'src')).filter(f => /\.(ts|tsx)$/.test(f)))
    assert.ok(!/\bfetch\s*\(|HubConnectionBuilder/.test(read('src/' + file)), file);
  assert.ok(app.includes('DEMO MODE') && app.includes('LIVE'));
  assert.ok(read('src/live/realtime.ts').includes("invoke('Subscribe'"));
  assert.ok(!/invoke\('(Start|Cancel|Continue)/.test(read('src/live/realtime.ts')), 'commands never go over SignalR');
});

test('host-owned continuation eligibility is not guessed from run status', () => {
  const eligible = { projectionVersion: 'gate-c6b-execution-projection-v1', legacy: false,
    recovery: { eligible: true, diagnostic: 'eligible-for-explicit-continuation', expectedAttemptNumber: 2 } };
  assert.equal(eligibleContinuation(eligible), 2);
  assert.equal(eligibleContinuation({ ...eligible, recovery: { ...eligible.recovery, eligible: false } }), null);
  assert.equal(eligibleContinuation({ ...eligible, recovery: { ...eligible.recovery, expectedAttemptNumber: null } }), null);
  assert.equal(eligibleContinuation({ ...eligible, legacy: true }), null);
  assert.equal(eligibleContinuation({ ...eligible, projectionVersion: 'future-incompatible' }), null);
  assert.equal(eligibleContinuation(null), null);
});

test('continuation sends explicit expected attempt and reuses caller-provided idempotency key', async () => {
  const key = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
  const { impl, calls } = fakeFetch([
    { status: 200, body: { accessToken: 'a'.repeat(64) } },
    { status: 202, body: { diagnostic: 'continuation-admitted', attempt: { number: 3 } } },
    { status: 200, body: { diagnostic: 'continuation-replayed', attempt: { number: 3 } } },
  ]);
  const api = new ApiClient('http://127.0.0.1:5127', impl);
  await api.pair('code');
  assert.equal(calls.length, 1, 'no host mutation before a deliberate user action');
  assert.throws(() => api.continueInvestigation(scope, run, 0, key), /positive/);
  assert.throws(() => api.continueInvestigation(scope, run, 2, 'not-a-uuid'), /UUID/);
  const admitted = await api.continueInvestigation(scope, run, 2, key);
  const replay = await api.continueInvestigation(scope, run, 2, key);
  assert.equal(admitted.diagnostic, 'continuation-admitted');
  assert.equal(replay.diagnostic, 'continuation-replayed');
  assert.equal(calls.length, 3);
  for (const call of calls.slice(1)) {
    assert.equal(call.init.headers['Idempotency-Key'], key);
    assert.equal(call.init.headers.Authorization, 'Bearer ' + 'a'.repeat(64));
    assert.equal(JSON.parse(call.init.body).expectedAttemptNumber, 2);
    assert.match(call.url, /\/investigations\/[^/]+\/continue$/);
  }
});

test('one real 401 fires session loss once across all consuming pages; explicit expiry also forgets token', async () => {
  const { impl } = fakeFetch([
    { status: 200, body: { accessToken: 'f'.repeat(64), expiresAtUtc: '2026-11-11T12:00:00Z' } },
    { status: 401, body: { error: 'revoked' } },
  ]);
  const api = new ApiClient('http://127.0.0.1:5127', impl);
  await api.pair('code');
  let calls = 0;
  api.onSessionLost(() => { calls++; });
  await assert.rejects(api.manifest(scope), e => e instanceof ApiError && e.status === 401);
  assert.equal(api.connected, false);
  assert.equal(calls, 1);
  api.expireSession();
  assert.equal(calls, 1, 'no duplicate notification after previous 401');

  const standalone = fakeFetch([{ status: 200, body: { accessToken: 'f'.repeat(64) } }]);
  const again = new ApiClient('http://127.0.0.1:5127', standalone.impl);
  await again.pair('code');
  let expired = 0;
  const unsubscribe = again.onSessionLost(() => { expired++; });
  again.expireSession();
  assert.equal(again.connected, false);
  assert.equal(expired, 1);
  unsubscribe();
});

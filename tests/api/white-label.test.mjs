import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { base, client, bootstrap, startApi, stopApi, bearer, post, get, connectHub } from './harness.mjs';

// W1 white-label boundary, end to end over real HTTP and WebSocket:
// pairing -> bearer token, origin allowlist + CORS, OpenAPI document, and the
// push-only SignalR hub (authorization, scope checks, ordered ledger events).
// Disposable data directory and fixture only. The investigation is cancelled
// immediately, so no long model run happens whether or not Ollama is running.

test('W1 white-label API: bearer auth, origin allowlist, OpenAPI and push-only SignalR', async t => {
  const directory = await mkdtemp(join(tmpdir(), 'code-archeologist-w1-'));
  const source = join(directory, 'source');
  await mkdir(source);
  await writeFile(join(source, 'Controller.cs'),
    'namespace W1;\n[Route("api/x")]\npublic class XController\n{\n    [HttpGet]\n    public int Get() => Help();\n    private int Help() => 1;\n}\n');
  let api;
  try {
    api = await startApi(join(directory, 'data'));
    let token;

    await t.test('token exchange requires the custom header and the pairing code', async () => {
      const noHeader = await fetch(base + '/api/v1/auth/token', { method: 'POST', headers: { 'Content-Type': 'application/json', Origin: client }, body: JSON.stringify({ code: bootstrap }) });
      assert.equal(noHeader.status, 403);
      const headers = { 'Content-Type': 'application/json', Origin: client, 'X-CodeArcheologist-Request': '1' };
      assert.equal((await fetch(base + '/api/v1/auth/token', { method: 'POST', headers, body: JSON.stringify({ code: '0'.repeat(32) }) })).status, 401);
      const ok = await fetch(base + '/api/v1/auth/token', { method: 'POST', headers, body: JSON.stringify({ code: bootstrap }) });
      assert.equal(ok.status, 200);
      const body = await ok.json();
      assert.equal(body.tokenType, 'Bearer');
      assert.match(body.accessToken, /^[0-9a-f]{64}$/);
      assert.equal(ok.headers.get('access-control-allow-origin'), client);
      token = body.accessToken;
    });

    await t.test('bearer tokens authenticate REST; invalid tokens do not', async () => {
      assert.equal((await get('/api/v1/workspaces', token)).status, 200);
      assert.equal((await get('/api/v1/workspaces', 'f'.repeat(64))).status, 401);
      assert.equal((await get('/api/v1/workspaces')).status, 401);
    });

    await t.test('bearer mutations need an allowed origin and an idempotency key, not CSRF', async () => {
      assert.equal((await post('/api/v1/workspaces', token, { name: 'Bad origin' }, { origin: 'http://localhost:4000' })).status, 403);
      assert.equal((await post('/api/v1/workspaces', token, { name: 'No key' }, { key: null })).status, 400);
      assert.equal((await post('/api/v1/workspaces', token, { name: 'W1' })).status, 201);
    });

    await t.test('CORS preflight is granted only to configured origins', async () => {
      const preflight = origin => fetch(base + '/api/v1/workspaces', { method: 'OPTIONS', headers: {
        Origin: origin, 'Access-Control-Request-Method': 'POST', 'Access-Control-Request-Headers': 'authorization,content-type,idempotency-key' } });
      assert.equal((await preflight(client)).headers.get('access-control-allow-origin'), client);
      assert.equal((await preflight('http://localhost:4000')).headers.get('access-control-allow-origin'), null);
    });

    await t.test('OpenAPI document describes the REST surface', async () => {
      const doc = await (await get('/api/v1/openapi.json')).json();
      assert.ok(doc.openapi);
      for (const path of ['/api/v1/auth/token', '/api/v1/workspaces',
        '/api/v1/workspaces/{workspaceId}/repositories/{repositoryId}/snapshots/{snapshotId}/investigations/{runId}/execution'])
        assert.ok(doc.paths[path], 'missing ' + path);
    });

    await t.test('the hub rejects anonymous and foreign-origin connections', async () => {
      const anonymous = await fetch(base + '/hubs/v1/investigations/negotiate?negotiateVersion=1', { method: 'POST', headers: { Origin: client } });
      assert.equal(anonymous.status, 401);
      assert.equal((await connectHub(token, 'http://localhost:4000')).status, 403);
    });

    await t.test('subscriptions are scope-checked and stream ordered ledger events', async () => {
      const workspaces = await (await get('/api/v1/workspaces', token)).json();
      const ws = workspaces.items[0].id;
      const repo = await (await post(`/api/v1/workspaces/${ws}/repositories`, token, { localPath: source })).json();
      const snap = await (await post(`/api/v1/workspaces/${ws}/repositories/${repo.id}/snapshots`, token, {})).json();
      const scope = `/api/v1/workspaces/${ws}/repositories/${repo.id}/snapshots/${snap.id}`;
      const graph = await (await get(scope + '/csharp-evidence-graph', token)).json();
      const entry = graph.nodes.find(n => n.kind === 'http-endpoint-attribute');
      assert.ok(entry, 'fixture produced no HTTP candidate');

      const hub = await connectHub(token);
      assert.equal(hub.status, 200);
      const foreign = await hub.invoke('Subscribe', { workspaceId: ws, repositoryId: repo.id, snapshotId: randomUUID(), runId: randomUUID() });
      assert.match(foreign.error ?? '', /run-not-found-in-scope/);

      const started = await post(scope + '/investigations', token, { entryPointId: entry.id });
      assert.equal(started.status, 202);
      const run = (await started.json()).runId;
      const accepted = await hub.invoke('Subscribe', { workspaceId: ws, repositoryId: repo.id, snapshotId: snap.id, runId: run });
      assert.equal(accepted.error, undefined);
      assert.equal(accepted.result.contractVersion, 'code-archeologist-realtime-v1');
      // End the run quickly and deterministically (no long model call).
      assert.ok([202, 409].includes((await post(scope + `/investigations/${run}/cancel`, token, { expectedAttemptNumber: 1 })).status));
      const terminal = await hub.until(m => m.type === 1 && m.target === 'eventAppended' &&
        /^attempt-(completed|incomplete|interrupted|cancelled|rejected|failed)$/.test(m.arguments[0].event.eventType), 60000);
      assert.equal(terminal.arguments[0].contractVersion, 'code-archeologist-realtime-v1');
      const events = [...new Map(hub.messages.filter(m => m.type === 1 && m.target === 'eventAppended')
        .map(m => [m.arguments[0].event.eventId, m.arguments[0]])).values()];
      assert.ok(events.every(e => e.workspaceId === ws && e.snapshotId === snap.id && e.event.runId === run));
      const sequences = events.map(e => e.event.sequenceNumber).sort((a, b) => a - b);
      assert.deepEqual(sequences, sequences.map((_, i) => i + 1), 'events must be gap-free after de-duplication');
      assert.equal(events.find(e => e.event.sequenceNumber === 1).event.eventType, 'attempt-admitted');
      const rest = await (await get(scope + `/investigations/${run}/events`, token)).json();
      assert.deepEqual(rest.map(e => e.eventId).sort(), events.map(e => e.event.eventId).sort(), 'push must equal the REST ledger');
      await hub.until(m => m.type === 1 && m.target === 'executionUpdated', 10000);
      hub.socket.close();
    });

    await t.test('revoked tokens stop working', async () => {
      assert.equal((await post('/api/v1/auth/token/revoke', token, {}, { key: null })).status, 204);
      assert.equal((await get('/api/v1/workspaces', token)).status, 401);
    });
  } finally {
    await stopApi(api);
    await rm(directory, { recursive: true, force: true });
  }
});

import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';

// W1 white-label boundary, end to end over real HTTP and WebSocket:
// pairing -> bearer token, origin allowlist + CORS, OpenAPI document, and the
// push-only SignalR hub (authorization, scope checks, ordered ledger events).
// Disposable data directory and fixture only. The investigation is cancelled
// immediately, so no long model run happens whether or not Ollama is running.

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const appDll = join(root, 'src', 'CodeArcheologist.Api', 'bin', 'Release', 'net10.0', 'CodeArcheologist.Api.dll');
const base = 'http://127.0.0.1:5127';
const client = 'http://localhost:3999';
const bootstrap = randomBytes(16).toString('hex');
const RS = '\x1e';

async function startApi(dataDirectory) {
  const variables = { ...process.env };
  delete variables.CODE_ARCHEOLOGIST_BOOTSTRAP_CODE;
  const child = spawn('dotnet', ['exec', appDll], {
    cwd: root, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true,
    env: { ...variables, CODE_ARCHEOLOGIST_BOOTSTRAP_CODE: bootstrap, CODE_ARCHEOLOGIST_DATA_DIR: dataDirectory,
      CODE_ARCHEOLOGIST_CLIENT_ORIGINS: client },
  });
  const output = [];
  child.stdout.on('data', c => output.push(c.toString()));
  child.stderr.on('data', c => output.push(c.toString()));
  for (let i = 0; i < 100; i++) {
    if (child.exitCode !== null) throw new Error('API exited: ' + output.join(''));
    try { if ((await fetch(base + '/api/v1/health')).ok) return child; } catch { }
    await delay(150);
  }
  child.kill();
  throw new Error('API did not start: ' + output.join(''));
}

async function stopApi(child) {
  if (!child || child.exitCode !== null) return;
  const exited = new Promise(r => child.once('exit', r));
  child.kill();
  await Promise.race([exited, delay(5000)]);
}

const bearer = token => ({ Authorization: 'Bearer ' + token });
function post(path, token, body, { origin = client, key = randomUUID() } = {}) {
  const headers = { 'Content-Type': 'application/json', Origin: origin, ...(token ? bearer(token) : {}) };
  if (key) headers['Idempotency-Key'] = key;
  return fetch(base + path, { method: 'POST', headers, body: JSON.stringify(body ?? {}) });
}
const get = (path, token) => fetch(base + path, { headers: token ? bearer(token) : {} });

/** Minimal SignalR JSON-protocol client (negotiate v1 + WebSocket). */
async function connectHub(token, origin = client) {
  const negotiate = await fetch(base + '/hubs/v1/investigations/negotiate?negotiateVersion=1',
    { method: 'POST', headers: { ...bearer(token), Origin: origin } });
  if (negotiate.status !== 200) return { status: negotiate.status };
  const { connectionToken } = await negotiate.json();
  const socket = new WebSocket(`ws://127.0.0.1:5127/hubs/v1/investigations?id=${connectionToken}&access_token=${token}`);
  const messages = [];
  const waiters = [];
  let buffer = '';
  socket.addEventListener('message', async event => {
    buffer += typeof event.data === 'string' ? event.data : await event.data.text();
    let index;
    while ((index = buffer.indexOf(RS)) >= 0) {
      const frame = buffer.slice(0, index); buffer = buffer.slice(index + 1);
      if (frame.length) { messages.push(JSON.parse(frame)); waiters.splice(0).forEach(w => w()); }
    }
  });
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS);
  const until = async (predicate, ms = 20000) => {
    const deadline = Date.now() + ms;
    while (!messages.some(predicate)) {
      if (Date.now() > deadline) throw new Error('timed out; got ' + JSON.stringify(messages).slice(0, 800));
      await new Promise(r => { waiters.push(r); setTimeout(r, 250); });
    }
    return messages.find(predicate);
  };
  let id = 0;
  const invoke = async (target, ...args) => {
    const invocationId = String(++id);
    socket.send(JSON.stringify({ type: 1, invocationId, target, arguments: args }) + RS);
    return until(m => m.type === 3 && m.invocationId === invocationId);
  };
  return { status: 200, socket, messages, invoke, until };
}

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

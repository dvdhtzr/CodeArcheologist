import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdtemp, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const appDll = join(root, 'src', 'CodeArcheologist.Api', 'bin', 'Release',
  'net10.0', 'CodeArcheologist.Api.dll');
const base = 'http://127.0.0.1:5127';
const origin = 'http://127.0.0.1:5174';
const bootstrap = randomBytes(16).toString('hex');

async function startApi(dataDirectory) {
  const output = [];
  const process = spawn('dotnet', ['exec', appDll], {
    cwd: root,
    env: {
      ...processEnvWithoutSecrets(),
      CODE_ARCHEOLOGIST_BOOTSTRAP_CODE: bootstrap,
      CODE_ARCHEOLOGIST_DEV_UI: '1',
      CODE_ARCHEOLOGIST_DATA_DIR: dataDirectory,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  });
  process.stdout.on('data', chunk => output.push(chunk.toString()));
  process.stderr.on('data', chunk => output.push(chunk.toString()));

  try {
    for (let attempt = 0; attempt < 100; attempt++) {
      if (process.exitCode !== null) {
        throw new Error('API exited during startup: ' + output.join(''));
      }
      try {
        const health = await fetch(base + '/api/v1/health');
        if (health.status === 200) return process;
      } catch { /* Not ready yet. */ }
      await delay(150);
    }
    throw new Error('API did not start. Stop the developer server first. ' + output.join(''));
  } catch (error) {
    await stopApi(process);
    throw error;
  }
}

// Test harness uses an isolated temporary directory; no user repository data.
function processEnvWithoutSecrets() {
  const variables = { ...process.env };
  delete variables.CODE_ARCHEOLOGIST_BOOTSTRAP_CODE;
  delete variables.CODE_ARCHEOLOGIST_DATA_DIR;
  return variables;
}

async function stopApi(process) {
  if (!process) return;
  if (process.exitCode !== null) return;
  const exited = new Promise(resolve => process.once('exit', resolve));
  process.kill();
  await Promise.race([exited, delay(3000)]);
}

async function pair() {
  const response = await fetch(base + '/api/v1/auth/login', {
    method: 'POST',
    headers: {
      Origin: origin,
      'Content-Type': 'application/json',
      'X-CodeArcheologist-Request': '1',
    },
    body: JSON.stringify({ code: bootstrap }),
  });
  assert.equal(response.status, 200, 'expected successful console pairing');
  const cookie = response.headers.get('set-cookie')?.split(';', 1)[0];
  assert.ok(cookie, 'expected session cookie');
  const body = await response.json();
  assert.match(body.csrfToken, /^[0-9A-F]{64}$/);
  return { cookie, csrf: body.csrfToken };
}

function authorizedPost(path, session, { name, key = randomUUID(), csrf = session?.csrf,
  originHeader = origin } = {}) {
  const headers = {
    Origin: originHeader,
    'Content-Type': 'application/json',
    'X-CodeArcheologist-Request': '1',
    'Idempotency-Key': key,
  };
  if (session?.cookie) headers.Cookie = session.cookie;
  if (csrf) headers['X-CodeArcheologist-Csrf'] = csrf;
  return fetch(base + path, {
    method: 'POST', headers, body: JSON.stringify({ name }),
  });
}

function authorizedGet(path, session) {
  return fetch(base + path, { headers: session ? { Cookie: session.cookie } : {} });
}

test('workspaces are durable, authenticated, validated and idempotent', async t => {
  const directory = await mkdtemp(join(tmpdir(), 'code-archeologist-workspaces-'));
  let api;
  try {
    api = await startApi(directory);

    await t.test('workspace reads require authentication', async () => {
      assert.equal((await authorizedGet('/api/v1/workspaces')).status, 401);
      assert.equal((await authorizedGet('/api/v1/workspaces/' + randomUUID())).status, 401);
    });

    await t.test('workspace writes require authentication', async () => {
      assert.equal((await authorizedPost('/api/v1/workspaces', null,
        { name: 'Not authorized' })).status, 401);
    });

    const session = await pair();

    await t.test('reject missing or invalid CSRF before any writes', async () => {
      assert.equal((await authorizedPost('/api/v1/workspaces', session,
        { name: 'CSRF missing', csrf: null })).status, 403);
      assert.equal((await authorizedPost('/api/v1/workspaces', session,
        { name: 'CSRF invalid', csrf: '0'.repeat(64) })).status, 403);
    });

    await t.test('reject forged origin', async () => {
      assert.equal((await authorizedPost('/api/v1/workspaces', session,
        { name: 'Bad origin', originHeader: 'https://attacker.example' })).status, 403);
    });

    await t.test('reject invalid names and idempotency headers', async () => {
      assert.equal((await authorizedPost('/api/v1/workspaces', session,
        { name: ' ' })).status, 400);
      assert.equal((await authorizedPost('/api/v1/workspaces', session,
        { name: 'x'.repeat(121) })).status, 400);
      assert.equal((await authorizedPost('/api/v1/workspaces', session,
        { name: 'Line\nBreak' })).status, 400);
      assert.equal((await authorizedPost('/api/v1/workspaces', session,
        { name: 'Valid', key: 'not-a-uuid' })).status, 400);
    });

    const key = randomUUID();
    let id;
    await t.test('creation returns 201 and an addressable stable ID', async () => {
      const result = await authorizedPost('/api/v1/workspaces', session,
        { name: '  Main codebase  ', key });
      assert.equal(result.status, 201);
      const created = await result.json();
      assert.match(created.id, /^[0-9a-f]{8}-[0-9a-f-]{27}$/i);
      assert.equal(created.name, 'Main codebase');
      assert.equal(created.version, 1);
      assert.match(result.headers.get('location'), /\/api\/v1\/workspaces\//);
      id = created.id;
    });

    await t.test('idempotent retries cannot duplicate a workspace', async () => {
      const replay = await authorizedPost('/api/v1/workspaces', session,
        { name: 'Main codebase', key });
      assert.equal(replay.status, 200);
      assert.equal((await replay.json()).id, id);

      const changed = await authorizedPost('/api/v1/workspaces', session,
        { name: 'Other name', key });
      assert.equal(changed.status, 409);

      const listed = await authorizedGet('/api/v1/workspaces', session);
      assert.equal(listed.status, 200);
      const page = await listed.json();
      assert.equal(page.totalCount, 1);
      assert.equal(page.items.length, 1);
      assert.equal(page.hasMore, false);
    });

    await t.test('open by ID and treat SQL-like names as ordinary data', async () => {
      const detail = await authorizedGet('/api/v1/workspaces/' + id, session);
      assert.equal(detail.status, 200);
      assert.equal((await detail.json()).name, 'Main codebase');

      const sqlLike = "A'); DROP TABLE workspaces; --";
      const result = await authorizedPost('/api/v1/workspaces', session,
        { name: sqlLike });
      assert.equal(result.status, 201);
      const listing = await (await authorizedGet('/api/v1/workspaces', session)).json();
      assert.equal(listing.totalCount, 2);
      assert.ok(listing.items.some(w => w.name === sqlLike));
      assert.ok(listing.items.some(w => w.id === id));

      assert.equal((await authorizedGet('/api/v1/workspaces/' + randomUUID(),
        session)).status, 404);
    });

    await t.test('SQLite data file was created in the isolated test directory', async () => {
      const info = await stat(join(directory, 'workspaces.sqlite3'));
      assert.ok(info.isFile());
      assert.ok(info.size > 0);
    });

    await stopApi(api);
    api = null;
    api = await startApi(directory);

    await t.test('session from previous process is rejected', async () => {
      assert.equal((await authorizedGet('/api/v1/workspaces', session)).status, 401);
    });

    const newSession = await pair();
    await t.test('workspaces can be listed and reopened after restart', async () => {
      const listed = await authorizedGet('/api/v1/workspaces', newSession);
      assert.equal(listed.status, 200);
      const page = await listed.json();
      assert.equal(page.totalCount, 2);
      const original = await authorizedGet('/api/v1/workspaces/' + id, newSession);
      assert.equal(original.status, 200);
      assert.equal((await original.json()).name, 'Main codebase');
    });
  } finally {
    await stopApi(api);
    await rm(directory, { recursive: true, force: true, maxRetries: 6, retryDelay: 250 });
  }
});

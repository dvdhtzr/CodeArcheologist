import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';

// Gate C6b HTTP boundary: execution projection, cancellation and continuation
// endpoints are authenticated, CSRF/Origin-guarded, idempotency-keyed and
// snapshot-scoped; a second process on the same data directory never gains
// execution ownership. Uses a disposable data directory only; no model runs.

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const appDll = join(root, 'src', 'CodeArcheologist.Api', 'bin', 'Release',
  'net10.0', 'CodeArcheologist.Api.dll');
const base = 'http://127.0.0.1:5127';
const origin = 'http://127.0.0.1:5174';
const bootstrap = randomBytes(16).toString('hex');

function env(dataDirectory) {
  const variables = { ...process.env };
  delete variables.CODE_ARCHEOLOGIST_BOOTSTRAP_CODE;
  delete variables.CODE_ARCHEOLOGIST_DATA_DIR;
  return { ...variables, CODE_ARCHEOLOGIST_BOOTSTRAP_CODE: bootstrap,
    CODE_ARCHEOLOGIST_DEV_UI: '1', CODE_ARCHEOLOGIST_DATA_DIR: dataDirectory };
}

function launch(dataDirectory) {
  const output = [];
  const child = spawn('dotnet', ['exec', appDll], {
    cwd: root, env: env(dataDirectory), stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true,
  });
  child.stdout.on('data', chunk => output.push(chunk.toString()));
  child.stderr.on('data', chunk => output.push(chunk.toString()));
  return { child, output };
}

async function startApi(dataDirectory) {
  const api = launch(dataDirectory);
  for (let attempt = 0; attempt < 100; attempt++) {
    if (api.child.exitCode !== null) throw new Error('API exited: ' + api.output.join(''));
    try {
      if ((await fetch(base + '/api/v1/health')).status === 200) return api;
    } catch { /* not ready */ }
    await delay(150);
  }
  await stopApi(api);
  throw new Error('API did not start. ' + api.output.join(''));
}

async function stopApi(api) {
  if (!api || api.child.exitCode !== null) return;
  const exited = new Promise(resolve => api.child.once('exit', resolve));
  api.child.kill();
  await Promise.race([exited, delay(5000)]);
}

async function pair() {
  const response = await fetch(base + '/api/v1/auth/login', {
    method: 'POST',
    headers: { Origin: origin, 'Content-Type': 'application/json', 'X-CodeArcheologist-Request': '1' },
    body: JSON.stringify({ code: bootstrap }),
  });
  assert.equal(response.status, 200);
  const cookie = response.headers.get('set-cookie')?.split(';', 1)[0];
  return { cookie, csrf: (await response.json()).csrfToken };
}

function post(path, session, { key = randomUUID(), csrf = session?.csrf, originHeader = origin,
  body = { expectedAttemptNumber: 1 } } = {}) {
  const headers = { Origin: originHeader, 'Content-Type': 'application/json',
    'X-CodeArcheologist-Request': '1' };
  if (key) headers['Idempotency-Key'] = key;
  if (session?.cookie) headers.Cookie = session.cookie;
  if (csrf) headers['X-CodeArcheologist-Csrf'] = csrf;
  return fetch(base + path, { method: 'POST', headers, body: JSON.stringify(body) });
}

const runPath = () => `/api/v1/workspaces/${randomUUID()}/repositories/${randomUUID()}` +
  `/snapshots/${randomUUID()}/investigations/${randomUUID()}`;

test('C6b execution control endpoints are protected and scoped', async t => {
  const directory = await mkdtemp(join(tmpdir(), 'code-archeologist-c6b-'));
  let api;
  try {
    api = await startApi(directory);
    const path = runPath();

    await t.test('unauthenticated reads and mutations are rejected', async () => {
      assert.equal((await fetch(base + path + '/execution')).status, 401);
      assert.equal((await post(path + '/cancel', null)).status, 401);
      assert.equal((await post(path + '/continue', null)).status, 401);
    });

    const session = await pair();

    await t.test('missing CSRF or forged Origin is rejected', async () => {
      for (const action of ['/cancel', '/continue']) {
        assert.equal((await post(path + action, session, { csrf: null })).status, 403);
        assert.equal((await post(path + action, session, { csrf: '0'.repeat(64) })).status, 403);
        assert.equal((await post(path + action, session,
          { originHeader: 'https://attacker.example' })).status, 403);
      }
    });

    await t.test('idempotency key and expected attempt are required', async () => {
      for (const action of ['/cancel', '/continue']) {
        assert.equal((await post(path + action, session, { key: null })).status, 400);
        assert.equal((await post(path + action, session, { body: {} })).status, 400);
        assert.equal((await post(path + action, session, { body: { expectedAttemptNumber: 0 } })).status, 400);
      }
    });

    await t.test('foreign or unknown runs are not found', async () => {
      const read = await fetch(base + path + '/execution', { headers: { Cookie: session.cookie } });
      assert.equal(read.status, 404);
      assert.equal((await post(path + '/cancel', session)).status, 404);
      assert.equal((await post(path + '/continue', session)).status, 404);
    });

    await t.test('a second process on the same data directory never gains execution', async () => {
      const second = launch(directory);
      const exited = new Promise(resolve => second.child.once('exit', resolve));
      await Promise.race([exited, delay(20000)]);
      await stopApi(second);
      assert.match(second.output.join(''), /execution disabled in this process: execution-lock-held-by-another-process/);
      assert.equal((await fetch(base + '/api/v1/health')).status, 200, 'first process must stay healthy');
    });
  } finally {
    await stopApi(api);
    await rm(directory, { recursive: true, force: true });
  }

  await t.test('restart on the same isolated directory reacquires ownership', async () => {
    const again = await mkdtemp(join(tmpdir(), 'code-archeologist-c6b-restart-'));
    let first, second;
    try {
      first = await startApi(again);
      await stopApi(first);
      second = await startApi(again);
      assert.doesNotMatch(second.output.join(''), /execution disabled/);
    } finally {
      await stopApi(second);
      await rm(again, { recursive: true, force: true });
    }
  });
});

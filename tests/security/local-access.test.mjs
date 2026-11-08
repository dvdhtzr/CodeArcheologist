import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import http from 'node:http';
import { randomBytes } from 'node:crypto';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const appDll = join(root, 'src', 'CodeArcheologist.Api', 'bin', 'Release', 'net10.0', 'CodeArcheologist.Api.dll');
const base = 'http://127.0.0.1:5127';
const expectedOrigin = 'http://127.0.0.1:5174';
const bootstrap = randomBytes(16).toString('hex');
const customHeader = { 'X-CodeArcheologist-Request': '1' };

async function waitForApi(process, output) {
  for (let i = 0; i < 100; i++) {
    if (process.exitCode !== null) {
      throw new Error('API exited before startup. Output: ' + output.join(''));
    }
    try {
      const response = await fetch(base + '/api/v1/health');
      if (response.ok) return;
    } catch {
      // Service has not started yet.
    }
    await delay(150);
  }
  throw new Error('Timed out waiting for the API. Stop scripts/start before running checks. Output: ' + output.join(''));
}

function requestWithHost(host) {
  return new Promise((resolve, reject) => {
    const request = http.request({
      hostname: '127.0.0.1',
      port: 5127,
      path: '/api/v1/health',
      method: 'GET',
      headers: { Host: host },
    }, (response) => {
      response.resume();
      response.on('end', () => resolve(response.statusCode));
    });
    request.on('error', reject);
    request.end();
  });
}

function authPost(path, { cookie, csrf, origin = expectedOrigin, code, addHeader = true } = {}) {
  const headers = { 'Content-Type': 'application/json', Origin: origin };
  if (addHeader) Object.assign(headers, customHeader);
  if (cookie) headers.Cookie = cookie;
  if (csrf) headers['X-CodeArcheologist-Csrf'] = csrf;
  return fetch(base + path, {
    method: 'POST',
    headers,
    body: code === undefined ? '{}' : JSON.stringify({ code }),
    redirect: 'manual',
  });
}

test('local browser access rejects forged and unauthorized requests', async (t) => {
  const output = [];
  const dataDirectory = await mkdtemp(join(tmpdir(), 'code-archeologist-security-'));
  const api = spawn('dotnet', ['exec', appDll], {
    cwd: root,
    env: {
      ...process.env,
      CODE_ARCHEOLOGIST_BOOTSTRAP_CODE: bootstrap,
      CODE_ARCHEOLOGIST_DEV_UI: '1',
      CODE_ARCHEOLOGIST_DATA_DIR: dataDirectory,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  });

  api.stdout.on('data', (chunk) => output.push(chunk.toString()));
  api.stderr.on('data', (chunk) => output.push(chunk.toString()));

  try {
    await waitForApi(api, output);

    await t.test('health is reachable only through permitted hosts', async () => {
      assert.equal((await fetch(base + '/api/v1/health')).status, 200);
      assert.equal(await requestWithHost('attacker.example:5127'), 400);
    });

    await t.test('session is unavailable before pairing', async () => {
      assert.equal((await fetch(base + '/api/v1/auth/session')).status, 401);
    });

    await t.test('unsafe login requires an explicit application header', async () => {
      assert.equal((await authPost('/api/v1/auth/login', {
        code: bootstrap,
        addHeader: false,
      })).status, 403);
    });

    await t.test('cross-origin pairing is rejected', async () => {
      assert.equal((await authPost('/api/v1/auth/login', {
        origin: 'https://attacker.example',
        code: bootstrap,
      })).status, 403);
    });

    await t.test('wrong pairing code is rejected', async () => {
      assert.equal((await authPost('/api/v1/auth/login', {
        code: '00000000000000000000000000000000',
      })).status, 401);
    });

    let cookie;
    let csrf;
    await t.test('correct code creates an HttpOnly Strict session', async () => {
      const response = await authPost('/api/v1/auth/login', { code: bootstrap });
      assert.equal(response.status, 200);
      const setCookie = response.headers.get('set-cookie');
      assert.ok(setCookie, 'expected Set-Cookie');
      assert.match(setCookie, /HttpOnly/i);
      assert.match(setCookie, /SameSite=Strict/i);
      cookie = setCookie.split(';', 1)[0];

      const session = await fetch(base + '/api/v1/auth/session', {
        headers: { Cookie: cookie },
      });
      assert.equal(session.status, 200);
      const data = await session.json();
      assert.equal(data.authenticated, true);
      assert.match(data.csrfToken, /^[0-9A-F]{64}$/);
      csrf = data.csrfToken;
    });

    await t.test('logout without CSRF token fails closed', async () => {
      assert.equal((await authPost('/api/v1/auth/logout', { cookie })).status, 403);
      assert.equal((await authPost('/api/v1/auth/logout', { cookie, csrf: '0'.repeat(64) })).status, 403);
    });

    await t.test('logout with forged Origin is rejected even with valid CSRF token', async () => {
      assert.equal((await authPost('/api/v1/auth/logout', {
        cookie, csrf, origin: 'https://attacker.example',
      })).status, 403);
    });

    await t.test('valid authenticated logout removes the session', async () => {
      assert.equal((await authPost('/api/v1/auth/logout', { cookie, csrf })).status, 204);
      // The server must reject even a replayed, previously authenticated cookie.
      const ended = await fetch(base + '/api/v1/auth/session', {
        headers: { Cookie: cookie },
      });
      assert.equal(ended.status, 401);
    });
  } finally {
    api.kill();
    await Promise.race([
      new Promise((resolve) => api.once('exit', resolve)),
      delay(3000),
    ]);
    await rm(dataDirectory, { recursive: true, force: true, maxRetries: 6, retryDelay: 250 });
  }
});

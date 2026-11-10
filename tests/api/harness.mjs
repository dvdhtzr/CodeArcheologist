import { spawn } from 'node:child_process';
import { randomBytes, randomUUID } from 'node:crypto';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

// Shared W1/W2 API integration harness: a disposable API process, bearer
// helpers and a minimal SignalR JSON-protocol client over WebSocket.
export const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const appDll = join(root, 'src', 'CodeArcheologist.Api', 'bin', 'Release', 'net10.0', 'CodeArcheologist.Api.dll');
export const base = 'http://127.0.0.1:5127';
export const client = 'http://localhost:3999';
export const bootstrap = randomBytes(16).toString('hex');
const RS = '\x1e';

export async function startApi(dataDirectory) {
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

export async function stopApi(child) {
  if (!child || child.exitCode !== null) return;
  const exited = new Promise(r => child.once('exit', r));
  child.kill();
  await Promise.race([exited, delay(5000)]);
}

export const bearer = token => ({ Authorization: 'Bearer ' + token });
export function post(path, token, body, { origin = client, key = randomUUID() } = {}) {
  const headers = { 'Content-Type': 'application/json', Origin: origin, ...(token ? bearer(token) : {}) };
  if (key) headers['Idempotency-Key'] = key;
  return fetch(base + path, { method: 'POST', headers, body: JSON.stringify(body ?? {}) });
}
export const get = (path, token) => fetch(base + path, { headers: token ? bearer(token) : {} });

/** Minimal SignalR JSON-protocol client (negotiate v1 + WebSocket). */
export async function connectHub(token, origin = client) {
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


export async function issueToken() {
  const response = await fetch(base + '/api/v1/auth/token', { method: 'POST',
    headers: { 'Content-Type': 'application/json', Origin: client, 'X-CodeArcheologist-Request': '1' },
    body: JSON.stringify({ code: bootstrap }) });
  if (response.status !== 200) throw new Error('token exchange failed: ' + response.status);
  return (await response.json()).accessToken;
}

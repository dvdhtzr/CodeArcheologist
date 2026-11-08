import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdtemp, mkdir, readFile, readdir, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, parse, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const appDll = join(root, 'src', 'CodeArcheologist.Api', 'bin', 'Release',
  'net10.0', 'CodeArcheologist.Api.dll');
const base = 'http://127.0.0.1:5127';
const origin = 'http://127.0.0.1:5174';
const bootstrap = randomBytes(16).toString('hex');

async function stopApi(process) {
  if (!process || process.exitCode !== null) return;
  const exited = new Promise(resolve => process.once('exit', resolve));
  process.kill();
  await Promise.race([exited, delay(3000)]);
}

async function startApi(dataDirectory) {
  const output = [];
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
  api.stdout.on('data', chunk => output.push(chunk.toString()));
  api.stderr.on('data', chunk => output.push(chunk.toString()));

  try {
    for (let attempt = 0; attempt < 100; attempt++) {
      if (api.exitCode !== null) {
        throw new Error('API exited before startup: ' + output.join(''));
      }
      try {
        if ((await fetch(base + '/api/v1/health')).status === 200) return api;
      } catch { /* not running yet */ }
      await delay(150);
    }
    throw new Error('API did not start. Stop the developer server. ' + output.join(''));
  } catch (error) {
    await stopApi(api);
    throw error;
  }
}

async function pair() {
  const res = await fetch(base + '/api/v1/auth/login', {
    method: 'POST',
    headers: {
      Origin: origin,
      'Content-Type': 'application/json',
      'X-CodeArcheologist-Request': '1',
    },
    body: JSON.stringify({ code: bootstrap }),
  });
  assert.equal(res.status, 200);
  const cookie = res.headers.get('set-cookie')?.split(';', 1)[0];
  const data = await res.json();
  assert.ok(cookie);
  assert.match(data.csrfToken, /^[0-9A-F]{64}$/);
  return { cookie, csrf: data.csrfToken };
}

function get(path, session) {
  return fetch(base + path, {
    headers: session ? { Cookie: session.cookie } : {},
  });
}

function post(path, session, body, options = {}) {
  const headers = {
    Origin: options.origin ?? origin,
    'Content-Type': 'application/json',
    'X-CodeArcheologist-Request': '1',
  };
  if (session?.cookie) headers.Cookie = session.cookie;
  if (options.csrf !== null && (options.csrf ?? session?.csrf)) {
    headers['X-CodeArcheologist-Csrf'] = options.csrf ?? session.csrf;
  }
  if (options.key !== null) headers['Idempotency-Key'] = options.key ?? randomUUID();
  return fetch(base + path, {
    method: 'POST',
    headers,
    body: JSON.stringify(body),
  });
}

test('local repository registration is scoped, safe and restart-durable', async t => {
  const testRoot = await mkdtemp(join(tmpdir(), 'code-archeologist-registration-'));
  const dataDirectory = join(testRoot, 'db');
  const sources = join(testRoot, 'source');
  const gitRepo = join(sources, 'git-repo');
  const folder = join(sources, 'plain-folder');
  const canary = join(gitRepo, 'do-not-touch.txt');
  const dangerous = join(gitRepo, 'do-not-execute.proj');
  let api;

  try {
    await mkdir(dataDirectory);
    await mkdir(join(gitRepo, '.git'), { recursive: true });
    await mkdir(folder, { recursive: true });
    await writeFile(canary, 'DO NOT MODIFY OR READ THIS SOURCE FILE');
    await writeFile(dangerous,
      '<Project><Target Name="Dangerous" BeforeTargets="Build">' +
      '<Exec Command="echo THIS_PROJECT_MUST_NOT_RUN"/></Target></Project>');
    const before = await readdir(gitRepo);

    api = await startApi(dataDirectory);
    assert.equal((await get('/api/v1/health')).status, 200);

    let workspaceId;
    let otherWorkspaceId;

    await t.test('all source endpoints require a paired session', async () => {
      assert.equal((await get('/api/v1/workspaces/' + randomUUID() +
        '/repositories')).status, 401);
      assert.equal((await post('/api/v1/workspaces/' + randomUUID() +
        '/repositories', null, { localPath: gitRepo })).status, 401);
    });

    let session = await pair();
    await t.test('create workspaces through the existing authenticated API', async () => {
      for (const [index, name] of ['Main', 'Other'].entries()) {
        const res = await post('/api/v1/workspaces', session, { name });
        assert.equal(res.status, 201);
        const created = await res.json();
        if (index === 0) workspaceId = created.id;
        else otherWorkspaceId = created.id;
      }
    });

    const endpoint = '/api/v1/workspaces/' + workspaceId + '/repositories';
    const otherEndpoint = '/api/v1/workspaces/' + otherWorkspaceId + '/repositories';

    await t.test('reject CSRF, forged Origins and unknown workspaces', async () => {
      assert.equal((await post(endpoint, session,
        { localPath: gitRepo }, { csrf: null })).status, 403);
      assert.equal((await post(endpoint, session,
        { localPath: gitRepo }, { csrf: '0'.repeat(64) })).status, 403);
      assert.equal((await post(endpoint, session,
        { localPath: gitRepo }, { origin: 'https://attacker.example' })).status, 403);
      assert.equal((await post('/api/v1/workspaces/' + randomUUID() +
        '/repositories', session, { localPath: gitRepo })).status, 404);
    });

    await t.test('reject invalid or unsafe paths before persistence', async () => {
      const bad = ['', ' ', 'relative/repo', join(sources, '..', 'not-real'),
        join(sources, 'missing'), parse(testRoot).root,
        gitRepo + sep + '..' + sep + 'plain-folder', 'a'.repeat(2049)];
      for (const localPath of bad) {
        const res = await post(endpoint, session, { localPath });
        assert.equal(res.status, 400, 'expected path rejection for ' + localPath);
      }
      assert.equal((await post(endpoint, session,
        { localPath: gitRepo }, { key: null })).status, 400);
      assert.equal((await post(endpoint, session,
        { localPath: gitRepo }, { key: 'bad-key' })).status, 400);
      assert.equal((await post(endpoint, session,
        { localPath: dataDirectory })).status, 400);

      if (process.platform === 'win32') {
        assert.equal((await post(endpoint, session,
          { localPath: '\\\\server\\share\\project' })).status, 400);
      }
    });

    await t.test('reject symlink aliases where the OS permits creating them', async t2 => {
      const alias = join(testRoot, 'link-to-repo');
      try {
        await symlink(gitRepo, alias, 'dir');
      } catch (error) {
        if (error?.code === 'EPERM' || error?.code === 'EACCES') {
          t2.diagnostic('Symlink creation requires OS privileges; path validation still exists.');
          return;
        }
        throw error;
      }
      assert.equal((await post(endpoint, session, { localPath: alias })).status, 400);
    });

    const key = randomUUID();
    let repositoryId;
    await t.test('register a Git working tree, retain identity and reject duplicates', async () => {
      const res = await post(endpoint, session, { localPath: gitRepo }, { key });
      assert.equal(res.status, 201);
      const record = await res.json();
      repositoryId = record.id;
      assert.equal(record.workspaceId, workspaceId);
      assert.equal(record.localPath, gitRepo);
      assert.equal(record.sourceKind, 'git');
      assert.equal(record.version, 1);

      const retry = await post(endpoint, session, { localPath: gitRepo }, { key });
      assert.equal(retry.status, 200);
      assert.equal((await retry.json()).id, repositoryId);

      const conflictingKey = await post(endpoint, session, { localPath: folder },
        { key });
      assert.equal(conflictingKey.status, 409);

      const duplicate = await post(endpoint, session, { localPath: gitRepo });
      assert.equal(duplicate.status, 409);

      const detail = await get(endpoint + '/' + repositoryId, session);
      assert.equal(detail.status, 200);
      assert.equal((await detail.json()).id, repositoryId);
      assert.equal((await get(otherEndpoint + '/' + repositoryId, session)).status, 404);
    });

    await t.test('multiple local sources per workspace, multiple workspaces per source', async () => {
      const another = await post(endpoint, session, { localPath: folder });
      assert.equal(another.status, 201);
      assert.equal((await another.json()).sourceKind, 'folder');
      const page = await (await get(endpoint, session)).json();
      assert.equal(page.totalCount, 2);
      assert.equal(page.items.length, 2);

      const crossWorkspace = await post(otherEndpoint, session, { localPath: gitRepo });
      assert.equal(crossWorkspace.status, 201);
      assert.equal((await (await get(otherEndpoint, session)).json()).totalCount, 1);
    });

    await t.test('source tree is unchanged by registration', async () => {
      assert.deepEqual(await readdir(gitRepo), before);
      assert.equal(await readFile(canary, 'utf8'),
        'DO NOT MODIFY OR READ THIS SOURCE FILE');
      assert.equal((await readdir(gitRepo)).includes('THIS_PROJECT_MUST_NOT_RUN'), false);
    });

    await stopApi(api);
    api = null;
    api = await startApi(dataDirectory);

    await t.test('previous process session cannot access persisted registration', async () => {
      assert.equal((await get(endpoint, session)).status, 401);
    });

    session = await pair();
    await t.test('repository identities and idempotency survive server restart', async () => {
      const response = await get(endpoint, session);
      assert.equal(response.status, 200);
      const page = await response.json();
      assert.equal(page.totalCount, 2);
      assert.ok(page.items.some(source => source.id === repositoryId));
      const original = await get(endpoint + '/' + repositoryId, session);
      assert.equal(original.status, 200);
      assert.equal((await original.json()).localPath, gitRepo);
      const replay = await post(endpoint, session, { localPath: gitRepo }, { key });
      assert.equal(replay.status, 200);
      assert.equal((await replay.json()).id, repositoryId);
    });
  } finally {
    await stopApi(api);
    await rm(testRoot, { recursive: true, force: true, maxRetries: 6, retryDelay: 250 });
  }
});

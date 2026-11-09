import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import {
  mkdtemp, mkdir, readFile, readdir, rm, symlink, writeFile
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const appDll = join(root, 'src', 'CodeArcheologist.Api', 'bin', 'Release',
  'net10.0', 'CodeArcheologist.Api.dll');
const base = 'http://127.0.0.1:5127';
const origin = 'http://127.0.0.1:5174';
const bootstrap = randomBytes(16).toString('hex');
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

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
      if (api.exitCode !== null)
        throw new Error('API failed to launch: ' + output.join(''));
      try {
        if ((await fetch(base + '/api/v1/health')).status === 200) return api;
      } catch { /* startup not complete */ }
      await delay(150);
    }
    throw new Error('Snapshot API launch timeout. ' + output.join(''));
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
      'X-CodeArcheologist-Request': '1',
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ code: bootstrap }),
  });
  assert.equal(res.status, 200, 'pairing must succeed');
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
    'X-CodeArcheologist-Request': '1',
  };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (session?.cookie) headers.Cookie = session.cookie;
  if (options.csrf !== null && (options.csrf ?? session?.csrf))
    headers['X-CodeArcheologist-Csrf'] = options.csrf ?? session.csrf;
  if (options.key !== null) headers['Idempotency-Key'] = options.key ?? randomUUID();
  return fetch(base + path, {
    method: 'POST',
    headers,
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
}

test('snapshots are bounded, immutable by app contract and evidence-anchored', async t => {
  const testRoot = await mkdtemp(join(tmpdir(), 'code-archeologist-capture-'));
  const data = join(testRoot, 'data');
  const source = join(testRoot, 'repository');
  const other = join(testRoot, 'other');
  const outside = join(testRoot, 'outside.txt');
  const originalCode = 'namespace Demo; public sealed class Example {}\n';
  const changedCode = 'namespace Demo; public sealed class Example { public int X => 42; }\n';
  let api;

  try {
    await mkdir(data);
    await mkdir(join(source, 'src'), { recursive: true });
    await mkdir(join(source, 'bin'), { recursive: true });
    await mkdir(join(source, '.git'), { recursive: true });
    await mkdir(other);
    await writeFile(join(source, 'src', 'Example.cs'), originalCode);
    await writeFile(join(source, 'README.md'), '# Demo\n');
    await writeFile(join(source, 'Demo.csproj'),
      '<Project><Target Name="Unsafe" BeforeTargets="Build">' +
      '<Exec Command="echo MUST_NOT_RUN"/></Target></Project>');
    await writeFile(join(source, '.env'), 'PRIVATE_TOKEN_MUST_NOT_BE_CAPTURED=secret');
    await writeFile(join(source, '.git', 'config'), 'PRIVATE_GIT_CONFIGURATION');
    await writeFile(join(source, 'bin', 'generated.dll'), 'DO NOT COPY');
    await writeFile(outside, 'OUTSIDE_ROOT_MUST_NOT_BE_CAPTURED');

    let canCreateSymlink = true;
    try {
      await symlink(outside, join(source, 'outside-link.txt'), 'file');
    } catch (error) {
      if (error?.code === 'EPERM' || error?.code === 'EACCES')
        canCreateSymlink = false;
      else
        throw error;
    }

    const originalDirectoryEntries = await readdir(source);
    api = await startApi(data);

    let wid;
    let rid;
    let session;
    await t.test('setup trusted workspace and source registration', async () => {
      session = await pair();
      const workspace = await post('/api/v1/workspaces', session,
        { name: 'Snapshot Lab' });
      assert.equal(workspace.status, 201);
      wid = (await workspace.json()).id;
      const registration = await post('/api/v1/workspaces/' + wid +
        '/repositories', session, { localPath: source });
      assert.equal(registration.status, 201);
      rid = (await registration.json()).id;
    });

    const endpoint = '/api/v1/workspaces/' + wid + '/repositories/' + rid + '/snapshots';

    await t.test('authenticated and CSRF-protected endpoints', async () => {
      assert.equal((await get(endpoint)).status, 401);
      assert.equal((await post(endpoint, null)).status, 401);
      assert.equal((await get(endpoint + '/' + randomUUID() + '/inventory')).status, 401);
      assert.equal((await post(endpoint, session, undefined,
        { csrf: null })).status, 403);
      assert.equal((await post(endpoint, session, undefined,
        { csrf: '0'.repeat(64) })).status, 403);
      assert.equal((await post(endpoint, session, undefined,
        { origin: 'https://attacker.example' })).status, 403);
      assert.equal((await post(endpoint, session, undefined,
        { key: null })).status, 400);
      assert.equal((await post(endpoint, session, undefined,
        { key: 'not-a-guid' })).status, 400);
      assert.equal((await get('/api/v1/workspaces/' + randomUUID() +
        '/repositories/' + rid + '/snapshots', session)).status, 404);
      assert.equal((await get('/api/v1/workspaces/' + wid +
        '/repositories/' + randomUUID() + '/snapshots', session)).status, 404);
    });

    const firstKey = randomUUID();
    let firstId;
    let firstFingerprint;
    let originalManifest;
    await t.test('explicit capture produces files, hashes and honest exclusions', async () => {
      const response = await post(endpoint, session, undefined, { key: firstKey });
      assert.equal(response.status, 201,
        'First snapshot capture must return 201. Response: ' +
        (response.status === 201 ? '<created>' : await response.clone().text()));
      const result = await response.json();
      firstId = result.id;
      firstFingerprint = result.fingerprintSha256;
      assert.match(firstFingerprint, /^[0-9a-f]{64}$/);
      assert.equal(result.policyVersion, 'local-tree-v1');
      assert.equal(result.fileCount, 3);
      assert.equal(result.totalBytes, Buffer.byteLength(originalCode) +
        Buffer.byteLength('# Demo\n') +
        Buffer.byteLength('<Project><Target Name="Unsafe" BeforeTargets="Build">' +
          '<Exec Command="echo MUST_NOT_RUN"/></Target></Project>'));

      const manifestResponse = await get(endpoint + '/' + firstId + '/manifest', session);
      assert.equal(manifestResponse.status, 200);
      originalManifest = await manifestResponse.json();
      assert.equal(originalManifest.fingerprintSha256, firstFingerprint);
      assert.equal(originalManifest.revisionKind,
        'filesystem-content-manifest-not-git-commit');
      assert.deepEqual(originalManifest.files.map(f => f.path).sort(),
        ['Demo.csproj', 'README.md', 'src/Example.cs']);
      assert.ok(originalManifest.exclusions.some(e => e.path === '.git'));
      assert.ok(originalManifest.exclusions.some(e => e.path === '.env'));
      assert.ok(originalManifest.exclusions.some(e => e.path === 'bin'));
      if (canCreateSymlink) {
        assert.ok(originalManifest.exclusions.some(
          e => e.path === 'outside-link.txt' &&
            e.reason === 'symbolic-link-or-junction'));
      }
      assert.ok(!originalManifest.files.some(f => f.path.includes('outside-link')));

      for (const item of originalManifest.files) {
        const captured = await readFile(join(data, 'snapshots', firstId,
          'files', ...item.path.split('/')));
        assert.equal(captured.length, item.sizeBytes);
        assert.equal(sha256(captured), item.sha256);
      }
      assert.equal((await get(endpoint + '/' + firstId, session)).status, 200);
    });

    if (!firstId) {
      t.diagnostic('Dependent snapshot tests skipped because the initial capture failed.');
      return;
    }

    await t.test('filename inventory is deterministic and evidence-scoped', async () => {
      const url = endpoint + '/' + firstId + '/inventory';
      const response = await get(url, session);
      assert.equal(response.status, 200,
        'inventory response: ' + (response.status === 200 ? 'OK' : await response.clone().text()));
      const inventory = await response.json();

      assert.equal(inventory.workspaceId, wid);
      assert.equal(inventory.repositoryId, rid);
      assert.equal(inventory.snapshotId, firstId);
      assert.equal(inventory.fingerprintSha256, firstFingerprint);
      assert.equal(inventory.analyzerVersion, 'manifest-inventory-v1');
      assert.equal(inventory.detectionMethod, 'path-and-extension-only');
      assert.equal(inventory.includedFileCount, 3);
      assert.equal(inventory.excludedEntryCount, originalManifest.exclusions.length);
      assert.deepEqual(inventory.files.map(file => file.path),
        ['Demo.csproj', 'README.md', 'src/Example.cs']);
      assert.deepEqual(inventory.files.map(file => file.role),
        ['project-manifest', 'documentation', 'source-code']);
      assert.deepEqual(inventory.files.map(file => file.language),
        ['XML', 'Markdown', 'C#']);
      assert.deepEqual(inventory.files.map(file => file.sha256),
        originalManifest.files.map(file => file.sha256));
      assert.ok(inventory.languages.some(group =>
        group.name === 'C#' && group.fileCount === 1));
      assert.ok(inventory.roles.some(group =>
        group.name === 'source-code' && group.fileCount === 1));
      assert.ok(inventory.exclusions.some(entry => entry.path === '.env'));
      assert.ok(inventory.limitations.some(text => text.includes('filenames only')));
      assert.equal((await get(url, session)).status, 200);

      const otherScope = '/api/v1/workspaces/' + randomUUID() +
        '/repositories/' + rid + '/snapshots/' + firstId + '/inventory';
      assert.equal((await get(otherScope, session)).status, 404);
      assert.equal((await get(endpoint + '/' + randomUUID() + '/inventory',
        session)).status, 404);
    });

    await t.test('identical request retry is idempotent, no duplicate snapshot', async () => {
      const retry = await post(endpoint, session, undefined, { key: firstKey });
      assert.equal(retry.status, 200);
      assert.equal((await retry.json()).id, firstId);
      const list = await (await get(endpoint, session)).json();
      assert.equal(list.totalCount, 1);
    });

    await t.test('secret and generated directory changes are outside fingerprint', async () => {
      await writeFile(join(source, '.env'), 'CHANGED_IGNORED_SECRET');
      await writeFile(join(source, 'bin', 'generated.dll'), 'CHANGED_IGNORED_BINARY');
      const reused = await post(endpoint, session);
      assert.equal(reused.status, 200);
      assert.equal((await reused.json()).id, firstId);
    });

    const secondKey = randomUUID();
    let secondId;
    await t.test('changing included source creates a new immutable snapshot', async () => {
      await writeFile(join(source, 'src', 'Example.cs'), changedCode);
      const newer = await post(endpoint, session, undefined, { key: secondKey });
      assert.equal(newer.status, 201);
      const record = await newer.json();
      secondId = record.id;
      assert.notEqual(secondId, firstId);
      assert.notEqual(record.fingerprintSha256, firstFingerprint);
      assert.equal((await readFile(join(data, 'snapshots', firstId,
        'files', 'src', 'Example.cs'), 'utf8')), originalCode);
      assert.equal((await readFile(join(data, 'snapshots', secondId,
        'files', 'src', 'Example.cs'), 'utf8')), changedCode);
      assert.equal((await readFile(join(source, 'src', 'Example.cs'), 'utf8')),
        changedCode);
    });

    await t.test('old idempotency key still resolves to old captured content', async () => {
      const replay = await post(endpoint, session, undefined, { key: firstKey });
      assert.equal(replay.status, 200);
      assert.equal((await replay.json()).id, firstId);
      const count = await (await get(endpoint, session)).json();
      assert.equal(count.totalCount, 2);
    });

    await t.test('oversize source fails closed without publishing a record', async () => {
      const oversized = join(source, 'src', 'too-large.cs');
      await writeFile(oversized, Buffer.alloc(8 * 1024 * 1024 + 1, 65));
      try {
        const rejected = await post(endpoint, session);
        assert.equal(rejected.status, 413);
        const list = await (await get(endpoint, session)).json();
        assert.equal(list.totalCount, 2);
      } finally {
        await rm(oversized, { force: true });
      }
    });

    await t.test('source containing application data is rejected', async () => {
      const registration = await post('/api/v1/workspaces/' + wid +
        '/repositories', session, { localPath: testRoot });
      assert.equal(registration.status, 201);
      const otherId = (await registration.json()).id;
      const rejected = await post('/api/v1/workspaces/' + wid +
        '/repositories/' + otherId + '/snapshots', session);
      assert.equal(rejected.status, 409);
    });

    await t.test('manifest corruption is detected, never treated as verified', async () => {
      const manifestFile = join(data, 'snapshots', firstId, 'manifest.json');
      const actual = await readFile(manifestFile);
      await writeFile(manifestFile, Buffer.from('{"tampered":true}'));
      try {
        const rejected = await get(endpoint + '/' + firstId + '/manifest', session);
        assert.equal(rejected.status, 409);
        assert.equal((await get(endpoint + '/' + firstId + '/inventory', session)).status, 409);
      } finally {
        await writeFile(manifestFile, actual);
      }
      assert.equal((await get(endpoint + '/' + firstId + '/manifest', session)).status, 200);
    });

    await t.test('captured content tampering invalidates the manifest view', async () => {
      const capturedFile = join(data, 'snapshots', firstId,
        'files', 'src', 'Example.cs');
      const bytes = await readFile(capturedFile);
      await writeFile(capturedFile, 'SAVED_CONTENT_HAS_BEEN_TAMPERED');
      try {
        const rejected = await get(endpoint + '/' + firstId + '/manifest', session);
        assert.equal(rejected.status, 409);
        assert.equal((await get(endpoint + '/' + firstId + '/inventory', session)).status, 409);
      } finally {
        await writeFile(capturedFile, bytes);
      }
      assert.equal((await get(endpoint + '/' + firstId + '/manifest', session)).status, 200);
    });

    await t.test('source registration did not mutate or execute the source tree', async () => {
      const after = await readdir(source);
      assert.deepEqual(after, originalDirectoryEntries);
      assert.equal((await readdir(source)).includes('MUST_NOT_RUN'), false);
      assert.equal((await readFile(outside, 'utf8')),
        'OUTSIDE_ROOT_MUST_NOT_BE_CAPTURED');
    });

    await stopApi(api);
    api = null;
    api = await startApi(data);
    await t.test('previous process cookie is invalidated after restart', async () => {
      assert.equal((await get(endpoint, session)).status, 401);
    });
    session = await pair();

    await t.test('captured bytes and evidence manifests persist after restart', async () => {
      const listing = await get(endpoint, session);
      assert.equal(listing.status, 200);
      const page = await listing.json();
      assert.equal(page.totalCount, 2);
      const manifest = await get(endpoint + '/' + firstId + '/manifest', session);
      assert.equal(manifest.status, 200);
      const first = await manifest.json();
      assert.deepEqual(first.files, originalManifest.files);
      const inventory = await get(endpoint + '/' + firstId + '/inventory', session);
      assert.equal(inventory.status, 200);
      assert.equal((await inventory.json()).fingerprintSha256, firstFingerprint);

      // Same key must resolve the same completed capture after restart.
      const retry = await post(endpoint, session, undefined, { key: secondKey });
      assert.equal(retry.status, 200);
      assert.equal((await retry.json()).id, secondId);
    });

    await t.test('mixed-language inventory labels remain explicitly heuristic', async () => {
      await mkdir(join(source, 'tests'));
      await mkdir(join(source, 'db'));
      await writeFile(join(source, 'tests', 'Widget.test.tsx'),
        'export function Widget() { return null; }\n');
      await writeFile(join(source, 'db', 'schema.sql'),
        'CREATE TABLE Demo (Id INT);\n');
      await writeFile(join(source, 'package.json'), '{"private":true}\n');

      const created = await post(endpoint, session);
      assert.equal(created.status, 201);
      const snapshot = await created.json();

      const response = await get(endpoint + '/' + snapshot.id + '/inventory', session);
      assert.equal(response.status, 200);
      const report = await response.json();
      const typeScriptTest = report.files.find(file =>
        file.path === 'tests/Widget.test.tsx');
      const sql = report.files.find(file => file.path === 'db/schema.sql');
      const dependencies = report.files.find(file =>
        file.path === 'package.json');

      assert.equal(typeScriptTest?.language, 'TypeScript');
      assert.equal(typeScriptTest?.role, 'test-source-candidate');
      assert.equal(sql?.language, 'SQL');
      assert.equal(sql?.role, 'database-script');
      assert.equal(dependencies?.role, 'dependency-manifest');
      assert.ok(report.limitations.some(text => text.includes('not parsed')));
      assert.notEqual(snapshot.id, firstId);
    });
  } finally {
    await stopApi(api);
    await rm(testRoot, { recursive: true, force: true, maxRetries: 6, retryDelay: 250 });
  }
});

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
      assert.equal((await get(endpoint + '/' + randomUUID() + '/csharp-syntax')).status, 401);
      assert.equal((await get(endpoint + '/' + randomUUID() + '/csharp-type-links')).status, 401);
      assert.equal((await get(endpoint + '/' + randomUUID() + '/csharp-projects')).status, 401);
      assert.equal((await get(endpoint + '/' + randomUUID() + '/csharp-link-context')).status, 401);
      assert.equal((await get(endpoint + '/' + randomUUID() + '/csharp-evidence-graph')).status, 401);
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

    await t.test('Roslyn syntax finds real C# declarations with snapshot anchors', async () => {
      const url = endpoint + '/' + firstId + '/csharp-syntax';
      const response = await get(url, session);
      assert.equal(response.status, 200,
        'syntax response: ' + (response.status === 200 ? 'OK' : await response.clone().text()));
      const report = await response.json();
      assert.equal(report.workspaceId, wid);
      assert.equal(report.repositoryId, rid);
      assert.equal(report.snapshotId, firstId);
      assert.equal(report.fingerprintSha256, firstFingerprint);
      assert.equal(report.analyzerVersion, 'roslyn-csharp-syntax-v1');
      assert.equal(report.cSharpFileCount, 1);
      assert.equal(report.hasSyntaxErrors, false);
      assert.equal(report.syntaxErrorCount, 0);
      assert.ok(report.declarations.some(d =>
        d.kind === 'namespace' && d.name === 'Demo' &&
        d.filePath === 'src/Example.cs' && d.startLine === 1));
      const example = report.declarations.find(d =>
        d.kind === 'class' && d.name === 'Example');
      assert.ok(example);
      assert.equal(example.containingNamespace, 'Demo');
      assert.equal(example.filePath, 'src/Example.cs');
      assert.equal(example.fileSha256,
        originalManifest.files.find(file => file.path === 'src/Example.cs').sha256);
      assert.ok(example.startColumn > 0);
      assert.ok(example.spanLength > 0);
      assert.ok(report.limitations.some(message => message.includes('not resolved symbols')));
      assert.deepEqual((await (await get(url, session)).json()).declarations,
        report.declarations);

      const foreignWorkspaceUrl = '/api/v1/workspaces/' + randomUUID() +
        '/repositories/' + rid + '/snapshots/' + firstId + '/csharp-syntax';
      assert.equal((await get(foreignWorkspaceUrl, session)).status, 404);
      assert.equal((await get(endpoint + '/' + randomUUID() + '/csharp-syntax',
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
      const newerSyntax = await get(endpoint + '/' + secondId + '/csharp-syntax', session);
      assert.equal(newerSyntax.status, 200);
      const newerReport = await newerSyntax.json();
      assert.ok(newerReport.declarations.some(d =>
        d.kind === 'property' && d.name === 'X' && d.containingType === 'Example'));
      const originalSyntax = await (await get(endpoint + '/' + firstId +
        '/csharp-syntax', session)).json();
      assert.equal(originalSyntax.declarations.some(d => d.name === 'X'), false);
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
        assert.equal((await get(endpoint + '/' + firstId + '/csharp-syntax', session)).status, 409);
        assert.equal((await get(endpoint + '/' + firstId + '/csharp-type-links', session)).status, 409);
        assert.equal((await get(endpoint + '/' + firstId + '/csharp-projects', session)).status, 409);
        assert.equal((await get(endpoint + '/' + firstId + '/csharp-link-context', session)).status, 409);
        assert.equal((await get(endpoint + '/' + firstId + '/csharp-evidence-graph', session)).status, 409);
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
        assert.equal((await get(endpoint + '/' + firstId + '/csharp-syntax', session)).status, 409);
        assert.equal((await get(endpoint + '/' + firstId + '/csharp-type-links', session)).status, 409);
        assert.equal((await get(endpoint + '/' + firstId + '/csharp-projects', session)).status, 409);
        assert.equal((await get(endpoint + '/' + firstId + '/csharp-link-context', session)).status, 409);
        assert.equal((await get(endpoint + '/' + firstId + '/csharp-evidence-graph', session)).status, 409);
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
      const syntax = await get(endpoint + '/' + firstId + '/csharp-syntax', session);
      assert.equal(syntax.status, 200);
      assert.equal((await syntax.json()).snapshotId, firstId);
      const localLinks = await get(endpoint + '/' + firstId + '/csharp-type-links', session);
      assert.equal(localLinks.status, 200);
      assert.equal((await localLinks.json()).snapshotId, firstId);

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
      await writeFile(join(source, 'src', 'Shapes.cs'),
        'namespace Demo.Shapes;\n' +
        'public interface IService { int Execute(string input); }\n' +
        'public sealed record Entry(int Id);\n' +
        'public sealed class Service { public int Size { get; init; } ' +
        'public Service() {} public int Run(int value) => value; }\n');
      await writeFile(join(source, 'src', 'Broken.cs'),
        'namespace Demo.Shapes; public class Broken { public void Bad( }\n');

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
      const syntaxResponse = await get(endpoint + '/' + snapshot.id + '/csharp-syntax', session);
      assert.equal(syntaxResponse.status, 200);
      const syntax = await syntaxResponse.json();
      assert.equal(syntax.hasSyntaxErrors, true);
      assert.ok(syntax.syntaxErrorCount > 0);
      assert.ok(syntax.diagnostics.some(d => d.filePath === 'src/Broken.cs'));
      assert.ok(syntax.declarations.some(d =>
        d.kind === 'interface' && d.name === 'IService'));
      assert.ok(syntax.declarations.some(d =>
        d.kind === 'record' && d.name === 'Entry'));
      assert.ok(syntax.declarations.some(d =>
        d.kind === 'method' && d.name === 'Run' && d.parameterCount === 1 &&
        d.containingNamespace === 'Demo.Shapes' && d.containingType === 'Service'));
      assert.ok(syntax.declarations.some(d =>
        d.kind === 'constructor' && d.name === 'Service'));
      assert.ok(syntax.declarations.some(d =>
        d.kind === 'property' && d.name === 'Size'));
    });

    await t.test('Roslyn source-local links preserve evidence and unresolved uses', async () => {
      // Previous malformed C# was intentionally tested above; this fixture
      // isolates type binding from unrelated parse recovery.
      await rm(join(source, 'src', 'Broken.cs'));
      await writeFile(join(source, 'src', 'TypesBase.cs'),
        'namespace Bridge;\npublic class BaseThing {}\n');
      await writeFile(join(source, 'src', 'TypesUse.cs'),
        'namespace Bridge;\n' +
        'public class Derived : BaseThing {\n' +
        '    public BaseThing Make() => new BaseThing();\n' +
        '}\n' +
        'public class UnknownChild : MissingExternal {}\n');

      const created = await post(endpoint, session);
      assert.equal(created.status, 201);
      const snapshot = await created.json();
      const url = endpoint + '/' + snapshot.id + '/csharp-type-links';
      const response = await get(url, session);
      assert.equal(response.status, 200,
        'type-link response: ' + (response.status === 200
          ? 'OK' : await response.clone().text()));
      const report = await response.json();

      assert.equal(report.workspaceId, wid);
      assert.equal(report.repositoryId, rid);
      assert.equal(report.snapshotId, snapshot.id);
      assert.equal(report.fingerprintSha256, snapshot.fingerprintSha256);
      assert.equal(report.analyzerVersion, 'roslyn-synthetic-type-links-v1');
      assert.equal(report.syntaxErrorCount, 0);
      assert.equal(report.provisionalLinkCount, 2);
      assert.equal(report.unresolvedCount, 1);
      const inherited = report.usages.find(usage =>
        usage.usageKind === 'base-type' && usage.writtenType === 'BaseThing');
      const createdType = report.usages.find(usage =>
        usage.usageKind === 'object-creation' && usage.writtenType === 'BaseThing');
      const missing = report.usages.find(usage =>
        usage.writtenType === 'MissingExternal');
      assert.ok(inherited);
      assert.equal(inherited.status, 'provisional-local');
      assert.equal(inherited.line, 2);
      assert.equal(inherited.target?.name, 'BaseThing');
      assert.equal(inherited.target?.namespace, 'Bridge');
      assert.equal(inherited.target?.filePath, 'src/TypesBase.cs');
      assert.equal(inherited.target?.line, 2);
      assert.equal(inherited.target?.fileSha256,
        sha256(await readFile(join(data, 'snapshots', snapshot.id,
          'files', 'src', 'TypesBase.cs'))));
      assert.equal(inherited.fileSha256,
        sha256(await readFile(join(data, 'snapshots', snapshot.id,
          'files', 'src', 'TypesUse.cs'))));
      assert.equal(createdType?.status, 'provisional-local');
      assert.equal(createdType?.target?.fileSha256, inherited.target.fileSha256);
      assert.equal(missing?.status, 'unresolved');
      assert.equal(missing?.target, null);
      assert.ok(report.limitations.some(text => text.includes('NOT a verified')));
      assert.deepEqual((await (await get(url, session)).json()).usages, report.usages);

      // An edit to the LIVE repository cannot rewrite captured relationships.
      await writeFile(join(source, 'src', 'TypesBase.cs'),
        'namespace Bridge; public class Unrelated {}\n');
      assert.deepEqual((await (await get(url, session)).json()).usages, report.usages);

      const original = await get(endpoint + '/' + firstId + '/csharp-type-links', session);
      assert.equal(original.status, 200);
      assert.equal((await original.json()).provisionalLinkCount, 0);

      const foreign = '/api/v1/workspaces/' + randomUUID() +
        '/repositories/' + rid + '/snapshots/' + snapshot.id + '/csharp-type-links';
      assert.equal((await get(foreign, session)).status, 404);
      assert.equal((await get(endpoint + '/' + randomUUID() + '/csharp-type-links',
        session)).status, 404);

      await stopApi(api);
      api = null;
      api = await startApi(data);
      assert.equal((await get(url, session)).status, 401,
        'previous browser session must not survive API restart');
      session = await pair();
      const reopened = await get(url, session);
      assert.equal(reopened.status, 200);
      const afterRestart = await reopened.json();
      assert.equal(afterRestart.snapshotId, snapshot.id);
      assert.deepEqual(afterRestart.usages, report.usages);
    });

    await t.test('C# type links enforce the explicit file limit, not partial reports', async () => {
      for (let index = 0; index < 251; index++) {
        await writeFile(join(source, 'src', 'Limit' + index + '.cs'),
          'namespace Limits; internal class Type' + index + ' {}\n');
      }

      const created = await post(endpoint, session);
      assert.equal(created.status, 201);
      const snapshot = await created.json();
      const bounded = await get(endpoint + '/' + snapshot.id + '/csharp-type-links', session);
      assert.equal(bounded.status, 413,
        'analyzer should fail closed on more than 250 C# files');
      const body = await bounded.json();
      assert.match(body.error, /limited to 250 files/);
      assert.equal((await get(endpoint + '/' + snapshot.id + '/csharp-link-context', session)).status, 413,
        'joined context must not publish partial type relationships after analyzer limit');
      assert.equal((await get(endpoint + '/' + snapshot.id + '/csharp-evidence-graph', session)).status, 413,
        'graph must not return partial output after upstream type-link limits');
      assert.equal((await get(endpoint + '/' + snapshot.id + '/manifest', session)).status, 200,
        'safe captured snapshot remains inspectable even when this analyzer declines it');
    });

    await t.test('static project candidates honor literal rules without MSBuild execution', async () => {
      // The earlier negative test deliberately exceeded the type-link cap.
      // Its saved snapshot is retained; remove only the disposable live files.
      for (let index = 0; index < 251; index++) {
        await rm(join(source, 'src', 'Limit' + index + '.cs'));
      }
      await mkdir(join(source, 'ProjectOne'));
      await mkdir(join(source, 'ProjectTwo'));
      await mkdir(join(source, 'Shared'));
      await writeFile(join(source, 'ProjectOne', 'One.csproj'),
        '<Project Sdk="Microsoft.NET.Sdk">' +
        '<ItemGroup><Compile Remove="Skip.cs"/>' +
        '<Compile Include="../Shared/Shared.cs"/></ItemGroup>' +
        '<Target Name="NeverExecute" BeforeTargets="Build">' +
        '<Exec Command="DO_NOT_RUN"/></Target></Project>');
      await writeFile(join(source, 'ProjectOne', 'Main.cs'),
        'namespace One; [Route("api/[controller]")] ' +
         'public class Main : global::Shared.Contract { ' +
         'public void Call() { global::Shared.Contract.Ping(); } ' +
         'public void Run() { Call(); } ' +
         '[HttpGet("items/{id}")] public int Details(int id) => id; ' +
         '[HttpPost] [Route("create")] public void Create() {} ' +
         '[HttpDelete(RouteToken)] public void Delete(int id) {} ' +
         '[CustomHttpGet("not-an-endpoint")] public void IgnoreThis() {} ' +
         '[global::HttpGet("qualified")] public void QualifiedNotSupported() {} ' +
         '}\n');
      await writeFile(join(source, 'ProjectOne', 'Skip.cs'),
        'namespace One; public class Skip {}\n');
      await writeFile(join(source, 'Shared', 'Shared.cs'),
        'namespace Shared; public class Contract { public static void Ping() {} }\n');
      await writeFile(join(source, 'ProjectTwo', 'Two.csproj'),
        '<Project Sdk="Microsoft.NET.Sdk">' +
        '<PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>' +
        '<ItemGroup><Compile Include="Direct.cs"/>' +
        '<Compile Include="Conditional.cs" Condition="\'$(Configuration)\' == \'Debug\'"/>' +
        '<Compile Include="**/Wildcard.cs"/></ItemGroup></Project>');
      await writeFile(join(source, 'ProjectTwo', 'Direct.cs'),
        'namespace Two; public class Direct : global::Shared.Contract { ' +
         'public void Call() { global::Shared.Contract.Ping(); } }\n');
      await writeFile(join(source, 'ProjectTwo', 'Conditional.cs'),
        'namespace Two; public class Conditional : MissingDependency {}\n');
      await writeFile(join(source, 'src', 'Orphan.cs'),
        'namespace Orphans; public class Orphan { ' +
        'public object Build() => new global::Shared.Contract(); }\n');
      // Minimal API-style call spellings are test data, not valid
      // ASP.NET route registrations. The symbols below need not compile:
      // the default analyzer parses only verified, copied source syntax.
      await writeFile(join(source, 'src', 'MinimalRoutes.cs'),
        'namespace Mini; public class Routes { ' +
        'public void Register(string routeName) { ' +
        'app.MapGet("/catalog", () => 42); ' +
        'app.MapPost(routeName, Handler); ' +
        'router.MapDelete("/catalog/{id}", (int id) => id); ' +
        'custom.MapGet("/maybe-not-route", Handler); ' +
        'app.CustomMapGet("/ignored", Handler); ' +
        'app.MapGroup("/group"); ' +
        '} private void Handler() {} }\n');
      // Written dependency-injection registration syntax includes generic
      // service/implementation pairs, a self-or-service form, a factory,
      // typeof arguments, an unrelated same-named method, and unresolved
      // dynamic arguments. No imported application code is executed.
      await writeFile(join(source, 'src', 'Registrations.cs'),
        'namespace Injection; public interface IDemo {} ' +
        'public class Demo : IDemo {} ' +
        'public class Configure { ' +
        'public void Setup(bool enabled) { ' +
        'services.AddScoped<IDemo, Demo>(); ' +
        'services.AddSingleton<Demo>(); ' +
        'services.AddTransient<IDemo>(_ => new Demo()); ' +
        'services.AddScoped(typeof(IDemo), typeof(Demo)); ' +
        'custom.AddSingleton<Demo>(); ' +
        'services.AddSingleton("dynamic", enabled); ' +
        'services.TryAddScoped<IDemo, Demo>(); ' +
        '} }\n');
      // This fixture has two source-level Act overloads with matching
      // written call names, plus nested lambda/local-function invocation
      // sites which must NOT be attributed to the outer method.
      await writeFile(join(source, 'src', 'ExtraMembers.cs'),
        'namespace Structure;\n' +
        'public class Parent {\n' +
        ' public sealed class Child {}\n' +
        ' public void Act() { Helper(5); }\n' +
        ' public void Act(int value) { Helper(value); }\n' +
        ' public int Count { get; set; }\n' +
        ' public int Compute() {\n' +
        '  System.Func<int> lambda = () => HelperAndReturn();\n' +
        '  int Local() { Helper(9); return 2; }\n' +
        '  return lambda() + Local();\n' +
        ' }\n' +
        ' private void Helper(int input) {}\n' +
        ' private int HelperAndReturn() { return 3; }\n' +
        ' private void Poke() {}\n' +
        ' public void Dispatch() { Poke(); ArcheologistNoSuchMethodForFixture(); }\n' +
        '}\n');

      const created = await post(endpoint, session);
      assert.equal(created.status, 201);
      const snapshot = await created.json();
      const url = endpoint + '/' + snapshot.id + '/csharp-projects';
      const response = await get(url, session);
      assert.equal(response.status, 200,
        'static projects: ' + (response.status === 200 ? 'OK' : await response.clone().text()));
      const report = await response.json();
      assert.equal(report.snapshotId, snapshot.id);
      assert.equal(report.workspaceId, wid);
      assert.equal(report.repositoryId, rid);
      assert.equal(report.fingerprintSha256, snapshot.fingerprintSha256);
      assert.equal(report.analyzerVersion, 'static-csproj-membership-v1');
      assert.equal(report.projectCount, 3);
      const one = report.projects.find(project => project.projectPath === 'ProjectOne/One.csproj');
      const two = report.projects.find(project => project.projectPath === 'ProjectTwo/Two.csproj');
      const legacy = report.projects.find(project => project.projectPath === 'Demo.csproj');
      assert.ok(one && two && legacy);
      assert.equal(one.defaultCompileItemsInferred, true);
      assert.deepEqual(one.candidateFiles.map(file => file.path),
        ['ProjectOne/Main.cs', 'Shared/Shared.cs']);
      assert.deepEqual(one.candidateFiles.map(file => file.inclusionBasis),
        ['sdk-default-pattern', 'literal-compile-include']);
      assert.equal(one.candidateFiles[0].sha256,
        sha256(await readFile(join(data, 'snapshots', snapshot.id,
          'files', 'ProjectOne', 'Main.cs'))));
      assert.equal(one.projectSha256,
        sha256(await readFile(join(data, 'snapshots', snapshot.id,
          'files', 'ProjectOne', 'One.csproj'))));
      assert.equal(two.defaultCompileItemsInferred, false);
      assert.deepEqual(two.candidateFiles.map(file => file.path), ['ProjectTwo/Direct.cs']);
      assert.ok(two.unresolvedRules.some(rule => rule.includes('Conditional Compile')));
      assert.ok(two.unresolvedRules.some(rule => rule.includes('pattern/property/path')));
      assert.equal(legacy.defaultCompileItemsInferred, false);
      assert.equal(legacy.candidateFiles.length, 0);
      assert.ok(report.unmappedFiles.some(file => file.path === 'ProjectOne/Skip.cs'));
      assert.ok(report.unmappedFiles.some(file => file.path === 'ProjectTwo/Conditional.cs'));
      assert.ok(report.limitations.some(rule => rule.includes('candidate-only')));
      const contextUrl = endpoint + '/' + snapshot.id + '/csharp-link-context';
      const contextResponse = await get(contextUrl, session);
      assert.equal(contextResponse.status, 200,
        'joined project/type context: ' +
          (contextResponse.status === 200 ? 'OK' : await contextResponse.clone().text()));
      const context = await contextResponse.json();
      assert.equal(context.snapshotId, snapshot.id);
      assert.equal(context.workspaceId, wid);
      assert.equal(context.repositoryId, rid);
      assert.equal(context.fingerprintSha256, snapshot.fingerprintSha256);
      assert.equal(context.analyzerVersion, 'project-type-link-context-v1');
      assert.equal(context.typeLinkAnalyzerVersion, 'roslyn-synthetic-type-links-v1');
      assert.equal(context.projectAnalyzerVersion, 'static-csproj-membership-v1');
      assert.ok(context.sharedCandidateCount >= 1);
      assert.ok(context.disjointCandidateCount >= 1);
      assert.ok(context.unknownProjectCount >= 1);
      assert.ok(context.unresolvedTargetCount >= 1);

      const shared = context.usages.find(item =>
        item.typeUsage.filePath === 'ProjectOne/Main.cs' &&
        item.typeUsage.usageKind === 'base-type');
      const disjoint = context.usages.find(item =>
        item.typeUsage.filePath === 'ProjectTwo/Direct.cs' &&
        item.typeUsage.usageKind === 'base-type');
      const unknown = context.usages.find(item =>
        item.typeUsage.filePath === 'src/Orphan.cs' &&
        item.typeUsage.usageKind === 'object-creation');
      const unresolved = context.usages.find(item =>
        item.typeUsage.filePath === 'ProjectTwo/Conditional.cs' &&
        item.typeUsage.usageKind === 'base-type');
      assert.ok(shared && disjoint && unknown && unresolved);
      assert.equal(shared.projectContextStatus, 'shared-project-candidate');
      assert.equal(shared.typeUsage.status, 'provisional-local');
      assert.deepEqual(shared.sharedCandidateProjects.map(project => project.projectPath),
        ['ProjectOne/One.csproj']);
      assert.equal(shared.typeUsage.target.filePath, 'Shared/Shared.cs');
      assert.equal(shared.typeUsage.target.fileSha256,
        sha256(await readFile(join(data, 'snapshots', snapshot.id,
          'files', 'Shared', 'Shared.cs'))));
      assert.equal(shared.sourceProjects[0].projectSha256, one.projectSha256);
      assert.equal(disjoint.projectContextStatus, 'disjoint-project-candidates');
      assert.deepEqual(disjoint.sourceProjects.map(project => project.projectPath),
        ['ProjectTwo/Two.csproj']);
      assert.deepEqual(disjoint.targetProjects.map(project => project.projectPath),
        ['ProjectOne/One.csproj']);
      assert.deepEqual(disjoint.sharedCandidateProjects, []);
      assert.equal(disjoint.typeUsage.status, 'provisional-local');
      assert.equal(unknown.projectContextStatus, 'unknown-project-candidate');
      assert.equal(unknown.sourceProjects.length, 0);
      assert.ok(unknown.typeUsage.target);
      assert.equal(unresolved.projectContextStatus, 'unresolved-target');
      assert.equal(unresolved.typeUsage.status, 'unresolved');
      assert.equal(unresolved.typeUsage.target, null);
      assert.ok(context.limitations.some(text => text.includes('not verified')));
      assert.deepEqual((await (await get(contextUrl, session)).json()).usages,
        context.usages);
      const graphUrl = endpoint + '/' + snapshot.id + '/csharp-evidence-graph';
      const graphResponse = await get(graphUrl, session);
      assert.equal(graphResponse.status, 200,
        'graph: ' + (graphResponse.status === 200 ? 'OK' : await graphResponse.clone().text()));
      const graph = await graphResponse.json();
      assert.equal(graph.workspaceId, wid);
      assert.equal(graph.repositoryId, rid);
      assert.equal(graph.snapshotId, snapshot.id);
      assert.equal(graph.fingerprintSha256, snapshot.fingerprintSha256);
      assert.equal(graph.analyzerVersion, 'csharp-snapshot-evidence-graph-v8');
      assert.equal(graph.diRegistrationAnalyzerVersion, 'roslyn-di-registration-candidates-v1');
      assert.equal(graph.diRegistrationCandidateCount, 6);
      assert.equal(graph.diUnresolvedShapeCount, 1);
      assert.equal(graph.minimalApiAnalyzerVersion, 'roslyn-minimal-api-candidates-v1');
      assert.equal(graph.minimalApiCandidateCount, 4);
      assert.equal(graph.nonliteralMinimalApiRouteCount, 1);
      assert.equal(graph.httpAttributeAnalyzerVersion, 'roslyn-http-attribute-candidates-v1');
      assert.equal(graph.httpAttributeCandidateCount, 3);
      assert.equal(graph.nonliteralHttpAttributeRouteCount, 1);
      assert.ok(graph.calleeSharedCandidateCount >= 1);
      assert.ok(graph.calleeDisjointCandidateCount >= 1);
      assert.ok(graph.calleeUnknownProjectCount >= 1);
      assert.equal(graph.calleeSharedCandidateCount + graph.calleeDisjointCandidateCount +
        graph.calleeUnknownProjectCount, graph.provisionalCalleeCount,
        'every candidate callee must have exactly one project context status');
      assert.equal(graph.calleeAnalyzerVersion, 'roslyn-synthetic-callees-v1');
      assert.ok(graph.provisionalCalleeCount >= 1);
      assert.ok(graph.unresolvedCalleeCount >= 1);
      assert.equal(graph.provisionalCalleeCount + graph.unresolvedCalleeCount,
        graph.invocationCount);
      assert.equal(graph.invocationAnalyzerVersion, 'roslyn-invocation-sites-v1');
      assert.ok(graph.invocationCount >= 6);
      assert.ok(graph.methodContainedInvocationCount >= 4);
      assert.equal(graph.syntaxAnalyzerVersion, 'roslyn-csharp-syntax-v1');
      assert.ok(graph.syntaxDeclarationCount >= 6);
      assert.equal(graph.linkContextAnalyzerVersion, 'project-type-link-context-v1');
      assert.equal(graph.projectAnalyzerVersion, 'static-csproj-membership-v1');
      assert.equal(graph.nodeCount, graph.nodes.length);
      assert.equal(graph.edgeCount, graph.edges.length);
      const httpNodes = graph.nodes.filter(node => node.kind === 'http-endpoint-attribute');
      const httpEdges = graph.edges.filter(edge => edge.kind === 'annotates-method');
      assert.equal(httpNodes.length, 3);
      assert.equal(httpEdges.length, 3);
      assert.deepEqual(httpNodes.map(node => node.label).sort(), [
        'DELETE [HttpDelete] Delete',
        'GET [HttpGet] Details',
        'POST [HttpPost] Create'
      ]);
      const nodeByLabel = label => httpNodes.find(node => node.label === label);
      const getCandidate = nodeByLabel('GET [HttpGet] Details');
      const postCandidate = nodeByLabel('POST [HttpPost] Create');
      const dynamicCandidate = nodeByLabel('DELETE [HttpDelete] Delete');
      assert.ok(getCandidate && postCandidate && dynamicCandidate);
      assert.ok(getCandidate.detail.includes('items/{id}'));
      assert.ok(getCandidate.detail.includes('api/[controller]'));
      assert.ok(getCandidate.detail.includes('NOT a composed effective URL'));
      assert.ok(postCandidate.detail.includes('create'));
      assert.ok(postCandidate.detail.includes('method [Route] argument'));
      assert.ok(dynamicCandidate.detail.includes('(nonliteral-or-unsupported)'));
      assert.ok(!dynamicCandidate.detail.includes('RouteToken'));
      assert.ok(!graph.nodes.some(node =>
        node.kind === 'http-endpoint-attribute' &&
        (node.label.includes('IgnoreThis') ||
         node.label.includes('QualifiedNotSupported'))));
      const mainHash = sha256(await readFile(join(data, 'snapshots',
        snapshot.id, 'files', 'ProjectOne', 'Main.cs')));
      for (const candidate of httpNodes) {
        assert.equal(candidate.filePath, 'ProjectOne/Main.cs');
        assert.equal(candidate.fileSha256, mainHash);
        assert.ok(candidate.line > 0 && candidate.column > 0);
        const related = httpEdges.filter(edge => edge.fromId === candidate.id);
        assert.equal(related.length, 1);
        assert.equal(related[0].status, 'candidate');
        assert.equal(related[0].evidencePath, candidate.filePath);
        assert.equal(related[0].evidenceSha256, candidate.fileSha256);
        assert.equal(related[0].line, candidate.line);
        assert.equal(related[0].column, candidate.column);
        assert.ok(related[0].explanation.includes('NOT verified'));
        const method = graph.nodes.find(node => node.id === related[0].toId);
        assert.ok(method && method.kind === 'syntax-declaration' &&
          method.label.startsWith('One.Main.'));
        assert.equal(method.filePath, candidate.filePath);
        assert.equal(method.fileSha256, candidate.fileSha256);
      }
      assert.ok(graph.limitations.some(text =>
        text.includes('not resolved ASP.NET symbols')));
      const minimalNodes = graph.nodes.filter(node =>
        node.kind === 'http-minimal-mapping');
      const minimalEdges = graph.edges.filter(edge =>
        edge.kind === 'candidate-http-mapping');
      assert.equal(minimalNodes.length, 4);
      assert.equal(minimalEdges.length, 4);
      assert.ok(minimalNodes.some(node =>
        node.label === 'GET MapGet /catalog'));
      assert.ok(minimalNodes.some(node =>
        node.label === 'POST MapPost (nonliteral-or-unsupported)'));
      assert.ok(minimalNodes.some(node =>
        node.label === 'DELETE MapDelete /catalog/{id}'));
      assert.ok(minimalNodes.some(node =>
        node.label === 'GET MapGet /maybe-not-route'),
        'same-named non-ASP.NET member calls may be candidates, never verified routes');
      assert.ok(!minimalNodes.some(node =>
        node.label.includes('CustomMapGet') || node.label.includes('MapGroup')));
      assert.ok(minimalNodes.some(node =>
        node.detail.includes('handler lambda-expression')));
      assert.ok(minimalNodes.some(node =>
        node.detail.includes('handler method-group-like-expression')));
      const minimalSha = sha256(await readFile(join(data, 'snapshots',
        snapshot.id, 'files', 'src', 'MinimalRoutes.cs')));
      for (const mapping of minimalNodes) {
        assert.equal(mapping.filePath, 'src/MinimalRoutes.cs');
        assert.equal(mapping.fileSha256, minimalSha);
        assert.ok(mapping.line > 0 && mapping.column > 0);
        assert.ok(mapping.detail.includes('not a verified ASP.NET method'));
        const edges = minimalEdges.filter(edge => edge.fromId === mapping.id);
        assert.equal(edges.length, 1);
        const edge = edges[0];
        assert.equal(edge.status, 'candidate');
        assert.equal(edge.evidencePath, mapping.filePath);
        assert.equal(edge.evidenceSha256, mapping.fileSha256);
        assert.equal(edge.line, mapping.line);
        assert.equal(edge.column, mapping.column);
        assert.ok(edge.explanation.includes('NOT a registered'));
        const callSite = graph.nodes.find(node =>
          node.id === edge.toId && node.kind === 'invocation-site');
        assert.ok(callSite, 'candidate must point to an observed invocation');
        assert.equal(callSite.fileSha256, minimalSha);
        assert.equal(callSite.line, mapping.line);
        assert.equal(callSite.column, mapping.column);
        const containment = graph.edges.find(connected =>
          connected.toId === callSite.id &&
          connected.kind === 'contains-invocation' &&
          connected.status === 'observed-syntax');
        assert.ok(containment);
        const owner = graph.nodes.find(node => node.id === containment.fromId);
        assert.equal(owner?.label, 'Mini.Routes.Register');
      }
      assert.ok(graph.limitations.some(text =>
        text.includes('MapGet/MapPost/MapPut/MapPatch/MapDelete')));

      const diNodes = graph.nodes.filter(node =>
        node.kind === 'di-registration-candidate');
      const diEdges = graph.edges.filter(edge =>
        edge.kind === 'candidate-di-registration');
      assert.equal(diNodes.length, 6);
      assert.equal(diEdges.length, 6);
      assert.ok(diNodes.some(node =>
        node.label === 'scoped AddScoped IDemo → Demo' &&
        node.detail.includes('generic-service-implementation')));
      assert.ok(diNodes.some(node =>
        node.label === 'scoped AddScoped IDemo → Demo' &&
        node.detail.includes('typeof-service-implementation')));
      assert.ok(diNodes.some(node =>
        node.label === 'singleton AddSingleton Demo → (unresolved)' &&
        node.detail.includes('generic-self-or-service')));
      assert.ok(diNodes.some(node =>
        node.label === 'transient AddTransient IDemo → (unresolved)' &&
        node.detail.includes('lambda-factory-syntax-unresolved')));
      assert.ok(diNodes.some(node =>
        node.detail.includes('Written receiver: custom') &&
        node.detail.includes('source-syntax candidate'),
        'unrelated same-name custom method remains a candidate, not a verified registration');
      assert.ok(diNodes.some(node =>
        node.detail.includes('registration shape: unresolved')));
      assert.ok(!diNodes.some(node => node.label.includes('TryAddScoped')));
      const registrationsSha = sha256(await readFile(join(data, 'snapshots',
        snapshot.id, 'files', 'src', 'Registrations.cs')));
      for (const candidate of diNodes) {
        assert.equal(candidate.filePath, 'src/Registrations.cs');
        assert.equal(candidate.fileSha256, registrationsSha);
        assert.ok(candidate.line > 0 && candidate.column > 0);
        assert.ok(candidate.detail.includes('NOT a compiler-bound'));
        const related = diEdges.filter(edge => edge.fromId === candidate.id);
        assert.equal(related.length, 1);
        const edge = related[0];
        assert.equal(edge.status, 'candidate');
        assert.equal(edge.evidencePath, candidate.filePath);
        assert.equal(edge.evidenceSha256, registrationsSha);
        assert.equal(edge.line, candidate.line);
        assert.equal(edge.column, candidate.column);
        assert.ok(edge.explanation.includes('NOT verified'));
        const invocation = graph.nodes.find(node =>
          node.id === edge.toId && node.kind === 'invocation-site');
        assert.ok(invocation);
        assert.equal(invocation.fileSha256, registrationsSha);
        assert.equal(invocation.line, candidate.line);
        assert.equal(invocation.column, candidate.column);
        const observed = graph.edges.find(other =>
          other.kind === 'contains-invocation' &&
          other.status === 'observed-syntax' &&
          other.toId === invocation.id);
        assert.ok(observed);
        const caller = graph.nodes.find(node => node.id === observed.fromId);
        assert.equal(caller?.label, 'Injection.Configure.Setup');
      }
      assert.ok(graph.limitations.some(message =>
        message.includes('AddScoped/AddSingleton/AddTransient')));



      assert.ok(graph.nodeCount > 0 && graph.edgeCount > 0);
      assert.deepEqual(graph.nodes.map(node => node.id),
        [...graph.nodes.map(node => node.id)].sort());
      assert.deepEqual(graph.edges.map(edge => edge.id),
        [...graph.edges.map(edge => edge.id)].sort());
      const graphNodeIds = new Set(graph.nodes.map(node => node.id));
      assert.equal(graphNodeIds.size, graph.nodes.length);
      assert.equal(new Set(graph.edges.map(edge => edge.id)).size, graph.edges.length);
      for (const edge of graph.edges) {
        assert.ok(graphNodeIds.has(edge.fromId) && graphNodeIds.has(edge.toId),
          'every graph edge connects actual snapshot nodes');
        assert.match(edge.evidenceSha256, /^[0-9a-f]{64}$/);
        assert.ok(!['verified', 'compiled', 'real-dependency'].includes(edge.status));
      }

      const sourceNode = graph.nodes.find(node =>
        node.kind === 'csharp-file' && node.filePath === 'ProjectOne/Main.cs');
      const targetNode = graph.nodes.find(node =>
        node.kind === 'target-declaration' &&
        node.filePath === 'Shared/Shared.cs' && node.label === 'Shared.Contract');
      const projectNode = graph.nodes.find(node =>
        node.kind === 'project-file' && node.filePath === 'ProjectOne/One.csproj');
      const unresolvedNode = graph.nodes.find(node =>
        node.kind === 'unresolved-use' &&
        node.filePath === 'ProjectTwo/Conditional.cs' &&
        node.label === 'MissingDependency');
      assert.ok(sourceNode && targetNode && projectNode && unresolvedNode);
      const parsedFilePath = 'src/ExtraMembers.cs';
      const parsedFileSha = sha256(await readFile(join(data, 'snapshots',
        snapshot.id, 'files', 'src', 'ExtraMembers.cs')));
      const parsedFile = graph.nodes.find(node =>
        node.kind === 'csharp-file' && node.filePath === parsedFilePath);
      assert.ok(parsedFile);
      assert.equal(parsedFile.fileSha256, parsedFileSha);
      const syntaxOnly = graph.nodes.filter(node =>
        node.kind === 'syntax-declaration' && node.filePath === parsedFilePath);
      assert.ok(syntaxOnly.length >= 5,
        'class, nested class, overloads and property require syntax declarations');
      assert.ok(syntaxOnly.some(node => node.label === 'Structure.Parent'));
      assert.ok(syntaxOnly.some(node => node.label === 'Structure.Parent.Child'));
      assert.ok(syntaxOnly.some(node => node.label === 'Structure.Parent.Count'));
      const overloads = syntaxOnly.filter(node =>
        node.label === 'Structure.Parent.Act');
      assert.equal(overloads.length, 2,
        'overloads have distinct source spans and cannot collapse by name');
      assert.notEqual(overloads[0].id, overloads[1].id);
      const invocationSites = graph.nodes.filter(node =>
        node.kind === 'invocation-site' && node.filePath === parsedFilePath);
      assert.equal(invocationSites.length, 8,
        'six original expressions plus resolved Poke() and unresolved missing method');
      assert.ok(invocationSites.every(node =>
        node.fileSha256 === parsedFileSha && node.line > 0 && node.column > 0 &&
        node.detail.includes('no project-verified callee')));
      const helperSites = invocationSites.filter(node => node.label === 'Helper(…)');
      assert.equal(helperSites.length, 3,
        'repeated written names are separate source spans, never a deduplicated callee');
      assert.equal(new Set(helperSites.map(node => node.id)).size, 3);
      const linkedToAct = helperSites.map(site => ({
        site,
        edge: graph.edges.find(edge => edge.toId === site.id &&
          edge.kind === 'contains-invocation')
      })).filter(item => overloads.some(method => method.id === item.edge?.fromId));
      assert.equal(linkedToAct.length, 2,
        'direct method calls are attributed to the correct observed method declarations');
      assert.equal(new Set(linkedToAct.map(item => item.edge.fromId)).size, 2,
        'identically named overloads must each own their own invocation observation');
      const localHelper = helperSites.find(site =>
        !linkedToAct.some(item => item.site.id === site.id));
      assert.ok(localHelper);
      const localEdge = graph.edges.find(edge => edge.toId === localHelper.id);
      assert.equal(localEdge.fromId, parsedFile.id,
        'invocation inside a local function must not be assigned to enclosing Compute');
      const lambdaCall = invocationSites.find(node =>
        node.label === 'HelperAndReturn(…)');
      assert.ok(lambdaCall);
      assert.equal(graph.edges.find(edge => edge.toId === lambdaCall.id)?.fromId,
        parsedFile.id,
        'lambda invocation must remain file-scoped instead of claiming a caller method');
      const compute = syntaxOnly.find(node => node.label === 'Structure.Parent.Compute');
      assert.ok(compute);
      for (const name of ['lambda(…)', 'Local(…)']) {
        const site = invocationSites.find(node => node.label === name);
        assert.ok(site);
        assert.equal(graph.edges.find(edge => edge.toId === site.id)?.fromId, compute.id,
          'direct method-body expression belongs to the observed Compute declaration');
      }
      for (const site of invocationSites) {
        const siteEdges = graph.edges.filter(edge => edge.toId === site.id);
        assert.equal(siteEdges.length, 1);
        assert.equal(siteEdges[0].status, 'observed-syntax');
        assert.equal(siteEdges[0].evidencePath, parsedFilePath);
        assert.equal(siteEdges[0].evidenceSha256, parsedFileSha);
        assert.ok(siteEdges[0].line > 0 && siteEdges[0].column > 0);
        assert.ok(graph.edges.filter(edge => edge.fromId === site.id)
          .every(edge => edge.kind === 'candidate-callee' &&
            edge.status === 'provisional-local'),
          'any outgoing invocation edges must be explicitly provisional');
      }
      const poke = syntaxOnly.find(node =>
        node.label === 'Structure.Parent.Poke');
      assert.ok(poke);
      const pokeSite = invocationSites.find(node => node.label === 'Poke(…)');
      assert.ok(pokeSite);
      const pokeCandidate = graph.edges.find(edge =>
        edge.fromId === pokeSite.id && edge.toId === poke.id &&
        edge.kind === 'candidate-callee');
      assert.ok(pokeCandidate,
        'unique source method selected by reference-free Roslyn should be a candidate');
      assert.equal(pokeCandidate.status, 'provisional-local');
      assert.equal(pokeCandidate.evidencePath, parsedFilePath);
      assert.equal(pokeCandidate.evidenceSha256, parsedFileSha);
      assert.ok(pokeCandidate.line > 0 && pokeCandidate.column > 0);
      assert.ok(pokeCandidate.explanation.includes('unverified'));
      const missingSite = invocationSites.find(node =>
        node.label === 'ArcheologistNoSuchMethodForFixture(…)');
      assert.ok(missingSite);
      assert.equal(graph.edges.filter(edge => edge.fromId === missingSite.id).length, 0,
        'missing method must not create a target or name-matching fallback');
      assert.ok(!graph.nodes.some(node =>
        node.kind === 'syntax-declaration' &&
        node.label.includes('ArcheologistNoSuchMethodForFixture')));

      const projectQualified = graph.edges.filter(edge =>
        edge.kind === 'candidate-callee' &&
        graph.nodes.some(node => node.id === edge.toId &&
          node.filePath === 'Shared/Shared.cs' &&
          node.label === 'Shared.Contract.Ping'));
      const sharedCall = projectQualified.find(edge =>
        edge.evidencePath === 'ProjectOne/Main.cs');
      const disjointCall = projectQualified.find(edge =>
        edge.evidencePath === 'ProjectTwo/Direct.cs');
      assert.ok(sharedCall && disjointCall,
        'both source projects have an observed synthetic candidate for Shared.Contract.Ping');
      // Direct-impact review reverses ONLY existing candidate-callee edges,
      // then follows each call site to its observed enclosing method/file.
      // Run() -> Call() -> Ping() must NOT imply Run() is a direct Ping caller.
      assert.equal(projectQualified.length, 2,
        'Ping has two observed direct candidate call sites, not transitive callers');
      const pingTargetId = sharedCall.toId;
      assert.equal(disjointCall.toId, pingTargetId);
      const directPingCandidates = graph.edges.filter(edge =>
        edge.kind === 'candidate-callee' && edge.toId === pingTargetId);
      assert.equal(directPingCandidates.length, 2);
      const pingCallerPaths = [];
      for (const edge of directPingCandidates) {
        const site = graph.nodes.find(node =>
          node.id === edge.fromId && node.kind === 'invocation-site');
        assert.ok(site, 'a direct impact candidate must originate at a saved invocation');
        const observedContainment = graph.edges.find(candidate =>
          candidate.kind === 'contains-invocation' &&
          candidate.toId === site.id && candidate.status === 'observed-syntax');
        assert.ok(observedContainment,
          'the saved callsite must have independently observed source containment');
        const owner = graph.nodes.find(node =>
          node.id === observedContainment.fromId);
        assert.ok(owner && owner.kind === 'syntax-declaration');
        assert.equal(owner.label.endsWith('.Call'), true);
        assert.equal(owner.filePath, edge.evidencePath);
        assert.equal(edge.status, 'provisional-local');
        assert.equal(edge.evidenceSha256, site.fileSha256);
        assert.ok(edge.line > 0 && edge.column > 0);
        assert.ok(owner.line > 0 && owner.fileSha256);
        pingCallerPaths.push(owner.filePath);
      }
      assert.deepEqual(pingCallerPaths.sort(), [
        'ProjectOne/Main.cs', 'ProjectTwo/Direct.cs']);
      const run = graph.nodes.find(node =>
        node.kind === 'syntax-declaration' &&
        node.filePath === 'ProjectOne/Main.cs' &&
        node.label === 'One.Main.Run');
      const oneCall = graph.nodes.find(node =>
        node.kind === 'syntax-declaration' &&
        node.filePath === 'ProjectOne/Main.cs' &&
        node.label === 'One.Main.Call');
      assert.ok(run && oneCall);
      const runToCall = graph.edges.find(edge =>
        edge.kind === 'candidate-callee' && edge.toId === oneCall.id &&
        edge.evidencePath === 'ProjectOne/Main.cs');
      assert.ok(runToCall, 'nested Run -> Call candidate exists separately');
      assert.ok(graph.edges.some(edge =>
        edge.kind === 'contains-invocation' &&
        edge.fromId === run.id && edge.toId === runToCall.fromId));
      assert.ok(!graph.edges.some(edge =>
        edge.kind === 'candidate-callee' && edge.fromId === runToCall.fromId &&
        edge.toId === pingTargetId),
        'reverse Ping impact must not traverse the Run -> Call link');

      assert.equal(sharedCall.status, 'provisional-local');
      assert.equal(sharedCall.projectContextStatus, 'shared-project-candidate');
      assert.equal(sharedCall.sourceProjectCandidates.length, 1);
      assert.equal(sharedCall.targetProjectCandidates.length, 1);
      assert.deepEqual(sharedCall.sharedProjectCandidates.map(item => item.projectPath),
        ['ProjectOne/One.csproj']);
      assert.equal(sharedCall.sourceProjectCandidates[0].projectSha256, one.projectSha256);
      assert.equal(sharedCall.sourceProjectCandidates[0].inclusionBasis, 'sdk-default-pattern');
      assert.equal(sharedCall.targetProjectCandidates[0].projectSha256, one.projectSha256);
      assert.equal(sharedCall.targetProjectCandidates[0].inclusionBasis, 'literal-compile-include');
      assert.ok(sharedCall.targetProjectCandidates[0].unresolvedRuleCount > 0);
      assert.equal(sharedCall.evidenceSha256,
        sha256(await readFile(join(data, 'snapshots', snapshot.id, 'files',
          'ProjectOne', 'Main.cs'))));
      assert.equal(disjointCall.status, 'provisional-local');
      assert.equal(disjointCall.projectContextStatus, 'disjoint-project-candidates');
      assert.deepEqual(disjointCall.sourceProjectCandidates.map(item => item.projectPath),
        ['ProjectTwo/Two.csproj']);
      assert.deepEqual(disjointCall.targetProjectCandidates.map(item => item.projectPath),
        ['ProjectOne/One.csproj']);
      assert.deepEqual(disjointCall.sharedProjectCandidates, []);
      assert.equal(disjointCall.sourceProjectCandidates[0].projectSha256,
        two.projectSha256);
      assert.ok(disjointCall.explanation.includes('not evaluated'));
      assert.ok(!disjointCall.explanation.includes('verified call'));
      assert.equal(pokeCandidate.projectContextStatus, 'unknown-project-candidate');
      assert.deepEqual(pokeCandidate.sourceProjectCandidates, []);
      assert.deepEqual(pokeCandidate.targetProjectCandidates, []);
      assert.deepEqual(pokeCandidate.sharedProjectCandidates, []);
      assert.ok(graph.limitations.some(text =>
        text.includes('shared, disjoint and unknown')));
      assert.ok(graph.limitations.some(text => text.includes('source-only')));


      assert.ok(overloads.every(node =>
        node.fileSha256 === parsedFileSha && node.line > 0 && node.column > 0 &&
        node.detail.includes('syntax declaration')));
      for (const declaration of syntaxOnly) {
        const containment = graph.edges.find(edge =>
          edge.kind === 'declared-in' && edge.fromId === declaration.id &&
          edge.toId === parsedFile.id);
        assert.ok(containment,
          'each parsed declaration must link to its exact captured source file');
        assert.equal(containment.status, 'observed-location');
        assert.equal(containment.evidenceSha256, parsedFileSha);
        assert.ok(containment.line > 0);
      }
      assert.equal(graph.nodes.filter(node => node.id === targetNode.id).length, 1);
      assert.equal(graph.edges.filter(edge =>
        edge.kind === 'declared-in' && edge.fromId === targetNode.id).length, 1,
        'linked target declaration must not duplicate the same syntax containment edge');

      assert.equal(sourceNode.fileSha256,
        sha256(await readFile(join(data, 'snapshots', snapshot.id,
          'files', 'ProjectOne', 'Main.cs'))));
      assert.equal(targetNode.fileSha256,
        sha256(await readFile(join(data, 'snapshots', snapshot.id,
          'files', 'Shared', 'Shared.cs'))));
      assert.equal(projectNode.fileSha256, one.projectSha256);
      assert.ok(targetNode.line > 0 && targetNode.column > 0);
      assert.ok(unresolvedNode.line > 0);
      assert.ok(!graph.nodes.some(node =>
        node.kind === 'target-declaration' && node.label === 'MissingDependency'));

      const member = graph.edges.find(edge =>
        edge.fromId === projectNode.id && edge.toId === sourceNode.id &&
        edge.kind === 'candidate-compile-item');
      assert.ok(member);
      assert.equal(member.status, 'candidate');
      assert.equal(member.projectContextStatus, 'sdk-default-pattern');
      assert.equal(member.evidencePath, 'ProjectOne/One.csproj');
      assert.equal(member.evidenceSha256, one.projectSha256);

      const linkEdge = graph.edges.find(edge =>
        edge.fromId === sourceNode.id && edge.toId === targetNode.id &&
        edge.kind === 'base-type');
      assert.ok(linkEdge);
      assert.equal(linkEdge.status, 'provisional-local');
      assert.equal(linkEdge.projectContextStatus, 'shared-project-candidate');
      assert.equal(linkEdge.evidenceSha256, sourceNode.fileSha256);
      assert.ok(linkEdge.line > 0 && linkEdge.column > 0);
      const declarationEdge = graph.edges.find(edge =>
        edge.fromId === targetNode.id && edge.toId === 'file:Shared/Shared.cs');
      assert.equal(declarationEdge?.status, 'observed-location');

      const unresolvedEdge = graph.edges.find(edge =>
        edge.toId === unresolvedNode.id && edge.kind === 'base-type');
      assert.ok(unresolvedEdge);
      assert.equal(unresolvedEdge.status, 'unresolved');
      assert.equal(unresolvedEdge.projectContextStatus, 'unresolved-target');
      assert.ok(graph.limitations.some(text => text.includes('not a verified')));
      assert.deepEqual((await (await get(graphUrl, session)).json()).edges, graph.edges);



      const foreign = '/api/v1/workspaces/' + randomUUID() +
        '/repositories/' + rid + '/snapshots/' + snapshot.id + '/csharp-projects';
      assert.equal((await get(foreign, session)).status, 404);
      assert.equal((await get(endpoint + '/' + randomUUID() + '/csharp-projects',
        session)).status, 404);
      const foreignContext = '/api/v1/workspaces/' + randomUUID() +
        '/repositories/' + rid + '/snapshots/' + snapshot.id + '/csharp-link-context';
      assert.equal((await get(foreignContext, session)).status, 404);
      assert.equal((await get(endpoint + '/' + randomUUID() + '/csharp-link-context',
        session)).status, 404);
      const foreignGraph = '/api/v1/workspaces/' + randomUUID() +
        '/repositories/' + rid + '/snapshots/' + snapshot.id + '/csharp-evidence-graph';
      assert.equal((await get(foreignGraph, session)).status, 404);
      assert.equal((await get(endpoint + '/' + randomUUID() + '/csharp-evidence-graph',
        session)).status, 404);
      assert.deepEqual((await (await get(url, session)).json()).projects, report.projects);

      // Working tree changes are not reflected in the saved project view.
      await writeFile(join(source, 'ProjectOne', 'Main.cs'),
        'namespace One; public class NewVersion {}\n');
      assert.deepEqual((await (await get(url, session)).json()).projects, report.projects);
      assert.deepEqual((await (await get(contextUrl, session)).json()).usages,
        context.usages);
      assert.deepEqual((await (await get(graphUrl, session)).json()).edges,
        graph.edges);

      const savedProject = join(data, 'snapshots', snapshot.id,
        'files', 'ProjectOne', 'One.csproj');
      const original = await readFile(savedProject);
      await writeFile(savedProject, '<Project Sdk="CORRUPTED"/>');
      try {
        assert.equal((await get(url, session)).status, 409);
        assert.equal((await get(contextUrl, session)).status, 409);
        assert.equal((await get(graphUrl, session)).status, 409);
      } finally {
        await writeFile(savedProject, original);
      }
      assert.equal((await get(url, session)).status, 200);

      await stopApi(api);
      api = null;
      api = await startApi(data);
      assert.equal((await get(url, session)).status, 401);
      session = await pair();
      assert.deepEqual((await (await get(url, session)).json()).projects, report.projects);
      assert.deepEqual((await (await get(contextUrl, session)).json()).usages,
        context.usages);
      assert.deepEqual((await (await get(graphUrl, session)).json()).edges,
        graph.edges);
    });

    await t.test('invocation inspection refuses excessive call sites without a partial graph', async () => {
      const flood = join(source, 'src', 'InvocationFlood.cs');
      await writeFile(flood,
        'namespace Limits; public class Flood { public void Run() {' +
        'Ping();'.repeat(4501) + '} }');
      try {
        const created = await post(endpoint, session);
        assert.equal(created.status, 201);
        const snapshot = await created.json();
        const response = await get(endpoint + '/' + snapshot.id + '/csharp-evidence-graph',
          session);
        assert.equal(response.status, 413);
        assert.match((await response.json()).error, /4500-site limit/);
        assert.equal((await get(endpoint + '/' + snapshot.id + '/manifest', session)).status,
          200, 'saved snapshot remains readable when graph inspection fails closed');
      } finally {
        await rm(flood);
      }
    });

    await t.test('project XML DTD is prohibited and never evaluated', async () => {
      await writeFile(join(source, 'ProjectTwo', 'Dtd.csproj'),
        '<!DOCTYPE Project [<!ENTITY leak SYSTEM "file:///C:/sensitive.txt">]>' +
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>' +
        '<AssemblyName>&leak;</AssemblyName></PropertyGroup></Project>');
      const created = await post(endpoint, session);
      assert.equal(created.status, 201);
      const snapshot = await created.json();
      const response = await get(endpoint + '/' + snapshot.id + '/csharp-projects', session);
      assert.equal(response.status, 422);
      assert.equal((await get(endpoint + '/' + snapshot.id + '/csharp-link-context',
        session)).status, 422, 'combined analysis must reject unsafe project XML');
      assert.equal((await get(endpoint + '/' + snapshot.id + '/csharp-evidence-graph',
        session)).status, 422, 'graph must reject unsafe project XML');
      const body = await response.json();
      assert.match(body.error, /cannot be safely parsed/);
      assert.doesNotMatch(JSON.stringify(body), /sensitive.txt/);
      assert.equal((await get(endpoint + '/' + snapshot.id + '/manifest', session)).status, 200,
        'snapshot remains intact although project analysis fails closed');
    });
  } finally {
    await stopApi(api);
    await rm(testRoot, { recursive: true, force: true, maxRetries: 6, retryDelay: 250 });
  }
});

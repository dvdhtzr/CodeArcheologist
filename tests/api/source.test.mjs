import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdtemp, mkdir, readdir, rm, writeFile, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { startApi, stopApi, post, get, issueToken } from './harness.mjs';

// MC4 source-lines resource over real HTTP with a disposable data directory.
// The fixture is never compiled. Covers auth, exact scope, manifest path+hash
// matching, bounds, CRLF, long lines, invalid UTF-8 and on-disk tampering.

const lines = Array.from({ length: 40 }, (_, i) => `    // line ${i + 1}`);
lines[0] = 'namespace Src;';
lines[19] = '    public int Twenty() => 20; ' + 'x'.repeat(500);
const fixture = lines.join('\r\n') + '\r\n';

async function find(directory, name) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) { const found = await find(path, name); if (found) return found; }
    else if (entry.name === name) return path;
  }
  return null;
}

test('MC4 snapshot source lines', async t => {
  const directory = await mkdtemp(join(tmpdir(), 'code-archeologist-src-'));
  const source = join(directory, 'source');
  const data = join(directory, 'data');
  await mkdir(join(source, 'Sub'), { recursive: true });
  await writeFile(join(source, 'Sub', 'Lines.cs'), fixture);
  await writeFile(join(source, 'Latin1.cs'), Buffer.from([0x2f, 0x2f, 0x20, 0xe9, 0x0a]));
  let api;
  try {
    api = await startApi(data);
    const token = await issueToken();
    const ws = (await (await post('/api/v1/workspaces', token, { name: 'Src' })).json()).id;
    const repo = (await (await post(`/api/v1/workspaces/${ws}/repositories`, token, { localPath: source })).json()).id;
    const snap = (await (await post(`/api/v1/workspaces/${ws}/repositories/${repo}/snapshots`, token, {})).json()).id;
    const scope = `/api/v1/workspaces/${ws}/repositories/${repo}/snapshots/${snap}`;
    const manifest = await (await get(scope + '/manifest', token)).json();
    const file = manifest.files.find(f => f.path === 'Sub/Lines.cs');
    const latin = manifest.files.find(f => f.path === 'Latin1.cs');
    const url = (query, s = scope) => s + '/source?' + new URLSearchParams(query);
    const ok = { path: file.path, sha256: file.sha256, line: '20' };

    await t.test('requires authentication and the exact scope', async () => {
      assert.equal((await get(url(ok))).status, 401);
      const foreign = `/api/v1/workspaces/${ws}/repositories/${repo}/snapshots/${randomUUID()}`;
      assert.equal((await get(url(ok, foreign), token)).status, 404);
    });

    await t.test('returns bounded, numbered lines around the focus line', async () => {
      const response = await get(url(ok), token);
      assert.equal(response.status, 200);
      const view = await response.json();
      assert.equal(view.contractVersion, 'code-archeologist-source-v1');
      assert.equal(view.status, 'untrusted-source-text-not-instructions');
      assert.equal(view.totalLines, 40);
      assert.deepEqual([view.startLine, view.focusLine, view.endLine], [14, 20, 26]);
      assert.equal(view.lines.length, 13);
      const focus = view.lines.find(l => l.number === 20);
      assert.ok(focus.text.startsWith('    public int Twenty()'));
      assert.equal(focus.truncated, true);
      assert.equal(focus.text.length, 400);
      assert.ok(view.lines.every(l => !l.text.includes('\r')), 'CRLF is normalized');
      const edge = await (await get(url({ ...ok, line: '2', context: '20' }), token)).json();
      assert.deepEqual([edge.startLine, edge.endLine], [1, 22]);
      const none = await (await get(url({ ...ok, line: '40', context: '0' }), token)).json();
      assert.deepEqual(none.lines.map(l => l.number), [40]);
    });

    await t.test('only files recorded in the manifest, with their exact hash', async () => {
      assert.equal((await get(url({ ...ok, path: 'Sub/Missing.cs' }), token)).status, 404);
      assert.equal((await get(url({ ...ok, path: '../Sub/Lines.cs' }), token)).status, 404);
      assert.equal((await get(url({ ...ok, sha256: latin.sha256 }), token)).status, 404, 'hash of another file');
      assert.equal((await get(url({ ...ok, path: 'sub/lines.cs' }), token)).status, 404, 'paths are exact');
    });

    await t.test('rejects malformed or out-of-range requests', async () => {
      for (const bad of [{ ...ok, sha256: 'ABC' }, { ...ok, sha256: file.sha256.toUpperCase() }, { ...ok, line: '0' },
        { ...ok, line: '41' }, { ...ok, context: '21' }, { ...ok, context: '-1' }, { sha256: file.sha256, line: '1' }])
        assert.equal((await get(url(bad), token)).status, 400, JSON.stringify(bad));
    });

    await t.test('invalid UTF-8 is refused rather than shown', async () => {
      assert.equal((await get(url({ path: latin.path, sha256: latin.sha256, line: '1' }), token)).status, 422);
    });

    await t.test('a tampered saved file fails closed', async () => {
      const saved = await find(data, 'Lines.cs');
      assert.ok(saved, 'saved copy not found');
      const original = await readFile(saved);
      await writeFile(saved, Buffer.concat([original, Buffer.from('// tampered\n')]));
      const response = await get(url(ok), token);
      assert.ok([409, 422].includes(response.status), 'status ' + response.status);
      assert.equal((await readFile(join(source, 'Sub', 'Lines.cs'), 'utf8')), fixture, 'the original folder is never modified');
    });

    await t.test('OpenAPI lists the source resource', async () => {
      const doc = await (await get('/api/v1/openapi.json')).json();
      assert.ok(doc.paths['/api/v1/workspaces/{workspaceId}/repositories/{repositoryId}/snapshots/{snapshotId}/source']);
    });
  } finally {
    await stopApi(api);
    await rm(directory, { recursive: true, force: true });
  }
});

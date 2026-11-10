import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { startApi, stopApi, post, get, connectHub, issueToken } from './harness.mjs';

// W2 capability resources over real HTTP/WebSocket with a disposable data
// directory and a never-compiled fixture: entry points, methods, the ADR 0015
// one-hop impact view, and snapshotCaptured push. No model is invoked.

const fixture = `namespace W2;
[Route("api/x")]
public class XController
{
    [HttpGet]
    public int Get() => Help();
    private int Help() => 1;
    private int Orphan() => 2;
}
`;

test('W2 capability resources and snapshot push', async t => {
  const directory = await mkdtemp(join(tmpdir(), 'code-archeologist-w2-'));
  const source = join(directory, 'source');
  await mkdir(source);
  await writeFile(join(source, 'Controller.cs'), fixture);
  let api;
  try {
    api = await startApi(join(directory, 'data'));
    const token = await issueToken();
    const ws = (await (await post('/api/v1/workspaces', token, { name: 'W2' })).json()).id;
    const repo = (await (await post(`/api/v1/workspaces/${ws}/repositories`, token, { localPath: source })).json()).id;

    const hub = await connectHub(token);
    await t.test('repository subscriptions are scope-checked', async () => {
      const foreign = await hub.invoke('SubscribeRepository', { workspaceId: ws, repositoryId: randomUUID() });
      assert.match(foreign.error ?? '', /repository-not-found-in-scope/);
      const ok = await hub.invoke('SubscribeRepository', { workspaceId: ws, repositoryId: repo });
      assert.equal(ok.error, undefined);
      assert.equal(ok.result.existingSnapshotCount, 0);
    });

    const snap = (await (await post(`/api/v1/workspaces/${ws}/repositories/${repo}/snapshots`, token, {})).json()).id;
    const scope = `/api/v1/workspaces/${ws}/repositories/${repo}/snapshots/${snap}`;

    await t.test('snapshotCaptured is pushed after commit', async () => {
      const message = await hub.until(m => m.type === 1 && m.target === 'snapshotCaptured', 10000);
      assert.equal(message.arguments[0].snapshot.id, snap);
      assert.equal(message.arguments[0].contractVersion, 'code-archeologist-realtime-v1');
    });

    await t.test('capability resources require authentication and exact scope', async () => {
      assert.equal((await get(scope + '/entry-points')).status, 401);
      assert.equal((await get(`/api/v1/workspaces/${ws}/repositories/${repo}/snapshots/${randomUUID()}/entry-points`, token)).status, 404);
    });

    await t.test('entry points list written HTTP candidates with anchors', async () => {
      const list = await (await get(scope + '/entry-points', token)).json();
      assert.equal(list.contractVersion, 'code-archeologist-capabilities-v1');
      assert.equal(list.count, 1);
      const [entry] = list.items;
      assert.equal(entry.kind, 'http-endpoint-attribute');
      assert.equal(entry.evidenceStatus, 'candidate');
      assert.equal(entry.anchor.filePath, 'Controller.cs');
      assert.equal(entry.anchor.line, 5);
      assert.match(entry.anchor.fileSha256, /^[0-9a-f]{64}$/);
      assert.match(list.caveat, /not registered endpoints/);
      const evidence = await get(scope + '/evidence-tools/entry-point?entryPointId=' + encodeURIComponent(entry.id), token);
      assert.equal(evidence.status, 200, 'the existing evidence resource must accept listed entry points');
    });

    let methods;
    await t.test('methods list declarations with inbound provisional counts', async () => {
      methods = await (await get(scope + '/methods', token)).json();
      const byName = Object.fromEntries(methods.items.map(m => [m.label.split('.').pop(), m]));
      assert.equal(byName.Help.inboundProvisionalCandidates, 1);
      assert.equal(byName.Orphan.inboundProvisionalCandidates, 0);
    });

    await t.test('impact returns one-hop candidates with explicit unknowns', async () => {
      const help = methods.items.find(m => m.label.endsWith('Help'));
      const impact = await (await get(scope + '/impact?methodId=' + encodeURIComponent(help.id), token)).json();
      assert.equal(impact.directCandidates.length, 1);
      const [candidate] = impact.directCandidates;
      assert.equal(candidate.evidenceStatus, 'provisional-local');
      assert.ok(candidate.callerLabel.endsWith('Get'));
      assert.equal(candidate.invocationAnchor.line, 6);
      assert.match(impact.caveat, /not a verified call graph/);

      const orphan = methods.items.find(m => m.label.endsWith('Orphan'));
      const none = await (await get(scope + '/impact?methodId=' + encodeURIComponent(orphan.id), token)).json();
      assert.equal(none.directCandidates.length, 0);
      assert.ok(none.unknowns.some(u => /unknown, not proof of no callers/.test(u)));
    });

    await t.test('impact rejects missing, non-method and foreign IDs', async () => {
      assert.equal((await get(scope + '/impact', token)).status, 400);
      const list = await (await get(scope + '/entry-points', token)).json();
      assert.equal((await get(scope + '/impact?methodId=' + encodeURIComponent(list.items[0].id), token)).status, 404);
      assert.equal((await get(scope + '/impact?methodId=not-a-node', token)).status, 404);
    });

    await t.test('OpenAPI lists the capability resources', async () => {
      const doc = await (await get('/api/v1/openapi.json')).json();
      for (const suffix of ['/entry-points', '/methods', '/impact'])
        assert.ok(doc.paths['/api/v1/workspaces/{workspaceId}/repositories/{repositoryId}/snapshots/{snapshotId}' + suffix], suffix);
    });
    hub.socket.close();
  } finally {
    await stopApi(api);
    await rm(directory, { recursive: true, force: true });
  }
});

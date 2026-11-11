import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import test from 'node:test';
import { startApi, stopApi, post, get, issueToken } from './harness.mjs';

// R3 PR1 HTTP boundary tests remain model-free: invalid requests are rejected
// BEFORE any MAF work is admitted. Positive fenced admission and persistence
// are exercised by the R3 C# model-free self-tests using a fixture owner.
const fixture = `namespace R3;
[Route("api/r3")]
public class QuestionController
{
    [HttpGet]
    public int Get() => Helper();
    private int Helper() => 1;
}
`;

test('R3 structured questions require auth, exact saved scope and approved kind', async t => {
  const directory = await mkdtemp(join(tmpdir(), 'code-archeologist-r3-question-'));
  const source = join(directory, 'source');
  await mkdir(source);
  await writeFile(join(source, 'QuestionController.cs'), fixture);
  let api;
  try {
    api = await startApi(join(directory, 'data'));
    const token = await issueToken();
    const ws = (await (await post('/api/v1/workspaces', token, { name: 'R3 questions' })).json()).id;
    const repo = (await (await post(`/api/v1/workspaces/${ws}/repositories`,
      token, { localPath: source })).json()).id;
    const snap = (await (await post(
      `/api/v1/workspaces/${ws}/repositories/${repo}/snapshots`, token, {})).json()).id;
    const prefix = `/api/v1/workspaces/${ws}/repositories/${repo}/snapshots/${snap}/investigations`;
    const items = (await (await get(
      `/api/v1/workspaces/${ws}/repositories/${repo}/snapshots/${snap}/entry-points`, token)).json()).items;
    assert.equal(items.length, 1);
    const valid = { subjectNodeId: items[0].id, questionKind: 'explain-http-entry-point' };

    await t.test('no unpaired account can read or admit questions', async () => {
      assert.equal((await get(prefix + '/' + randomUUID() + '/question-context')).status, 401);
      assert.equal((await post(prefix + '/questions', null, valid)).status, 401);
    });

    await t.test('request requires a strong idempotency key and trusted origin', async () => {
      assert.equal((await post(prefix + '/questions', token, valid, { key: null })).status, 400);
      assert.equal((await post(prefix + '/questions', token, valid, { key: 'invalid' })).status, 400);
      assert.equal((await post(prefix + '/questions', token, valid, { origin: 'https://evil.example' })).status, 403);
    });

    await t.test('unsupported questions and non-HTTP subjects cannot start MAF', async () => {
      for (const payload of [
        { ...valid, questionKind: 'explain-any-method' },
        { ...valid, questionKind: 'unrestricted-free-text' },
        { ...valid, subjectNodeId: '' },
        { ...valid, subjectNodeId: 'x'.repeat(1025) },
        { ...valid, subjectNodeId: 'line\nbreak' },
      ]) {
        const response = await post(prefix + '/questions', token, payload);
        assert.equal(response.status, 400, JSON.stringify(payload));
      }
      assert.equal((await post(prefix + '/questions', token,
        { ...valid, subjectNodeId: 'not-a-saved-node' })).status, 404);
      const foreign = prefix.replace(snap, randomUUID());
      assert.equal((await post(foreign + '/questions', token, valid)).status, 404);
    });

    await t.test('legacy or unknown context is never invented', async () => {
      assert.equal((await get(prefix + '/' + randomUUID() + '/question-context', token)).status, 404);
    });

    await t.test('OpenAPI lists structured question endpoints', async () => {
      const doc = await (await get('/api/v1/openapi.json')).json();
      const basePath = '/api/v1/workspaces/{workspaceId}/repositories/{repositoryId}/snapshots/{snapshotId}/investigations';
      assert.ok(doc.paths[basePath + '/questions']?.post);
      assert.ok(doc.paths[basePath + '/{runId}/question-context']?.get);
    });

    const history = await (await get(prefix, token)).json();
    assert.equal(history.totalCount, 0, 'no invalid request admitted a model run');
  } finally {
    await stopApi(api);
    await rm(directory, { recursive: true, force: true });
  }
});

import test from 'node:test';
import assert from 'node:assert/strict';
import { atlasFocus, atlasOverview, findAtlasNodes, indexAtlas } from '../src/live/atlasModel.ts';

// This validates worst-case-shaped data and presentation budgets, not wall-time.
// Timing baselines live in the opt-in npm run benchmark:atlas report instead.
test('10k nodes / 16k edges: bounded view, exact edges and complete search', () => {
  const sha = 'c'.repeat(64);
  const id = i => 'n:' + String(i).padStart(5, '0');
  const nodes = Array.from({ length: 10_000 }, (_, i) => ({
    id: id(i), kind: i < 60 ? 'project-file' : 'csharp-file',
    label: 'MeasuredNode' + i, filePath: 'File' + i + '.cs',
    fileSha256: sha, line: 1, column: 1, detail: 'Synthetic only',
  }));
  const edges = Array.from({ length: 16_000 }, (_, i) => ({
    id: 'e:' + i, fromId: id(i < 600 ? 100 : (i * 37) % 10_000),
    toId: id(i < 600 ? i + 101 : (i * 41 + 17) % 10_000),
    kind: 'candidate-compile-item', status: 'candidate', evidencePath: 'Synthetic.cs',
    evidenceSha256: sha, line: 1, column: 1, explanation: 'Synthetic display test',
    projectContextStatus: null, sourceProjectCandidates: null,
    targetProjectCandidates: null, sharedProjectCandidates: null,
  }));
  const graph = { workspaceId: 'w', repositoryId: 'r', snapshotId: 's',
    fingerprintSha256: sha, analyzerVersion: 'csharp-snapshot-evidence-graph-v8',
    nodes, nodeCount: nodes.length, edges, edgeCount: edges.length, limitations: ['Synthetic'] };
  const index = indexAtlas(graph);
  assert.equal(index.nodeById.size, 10_000);
  assert.equal(index.edgeById.size, 16_000);
  assert.equal(findAtlasNodes(index, '', 'all').total, 10_000);
  assert.equal(findAtlasNodes(index, '', 'all').matches.length, 80);
  assert.deepEqual(findAtlasNodes(index, 'MeasuredNode9999', 'all').matches.map(n => n.id),
    [id(9999)]);

  const overview = atlasOverview(index);
  assert.equal(overview.nodes.length, 80);
  assert.equal(overview.limited, true);
  const focused = atlasFocus(index, id(100), [], 150, 250);
  assert.ok(focused.eligibleEdgeCount >= 250);
  assert.ok(focused.nodes.length <= 150 && focused.edges.length <= 250);
  assert.equal(focused.limited, true);
  const visibleIds = new Set(focused.nodes.map(n => n.id));
  for (const edge of focused.edges) {
    assert.equal(index.edgeById.get(edge.id), edge, 'no synthetic additional relationship');
    assert.equal(visibleIds.has(edge.fromId) && visibleIds.has(edge.toId), true);
    assert.equal(edge.status, 'candidate');
  }
});

test('Atlas refuses duplicate edge IDs rather than rendering ambiguous evidence', () => {
  const node = id => ({ id, kind: 'csharp-file', label: id,
    filePath: 'A.cs', fileSha256: 'f'.repeat(64), line: null, column: null, detail: null });
  const edge = { id: 'duplicate', fromId: 'a', toId: 'b', kind: 'declared-in',
    status: 'observed-location', evidencePath: 'A.cs', evidenceSha256: 'f'.repeat(64),
    line: 1, column: 1, explanation: '', projectContextStatus: null,
    sourceProjectCandidates: null, targetProjectCandidates: null, sharedProjectCandidates: null };
  assert.throws(() => indexAtlas({
    nodes: [node('a'), node('b')], edges: [edge, { ...edge, status: 'candidate' }],
    nodeCount: 2, edgeCount: 2, workspaceId: 'w', repositoryId: 'r',
    snapshotId: 's', fingerprintSha256: 'f'.repeat(64),
    analyzerVersion: 'csharp-snapshot-evidence-graph-v8', limitations: [],
  }), /duplicate saved edge IDs/);
});

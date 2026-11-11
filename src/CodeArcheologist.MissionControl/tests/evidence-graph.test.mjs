import test from 'node:test';
import assert from 'node:assert/strict';
import { validateGraph, adjacent, graphOverview } from '../src/live/evidenceGraph.ts';

// R1: pure model-free projection contracts, no repo evaluation or Ollama.
const scope = {
  workspaceId: '00000000-0000-0000-0000-000000000001',
  repositoryId: '00000000-0000-0000-0000-000000000002',
  snapshotId: '00000000-0000-0000-0000-000000000003',
};
const fingerprint = 'ab'.repeat(32);
const a = { id: 'file:a', kind: 'csharp-file', label: 'A.cs', filePath: 'A.cs', fileSha256: 'cd'.repeat(32), line: null, column: null, detail: null };
const b = { id: 'method:b', kind: 'syntax-declaration', label: 'B', filePath: 'A.cs', fileSha256: a.fileSha256, line: 8, column: 1, detail: 'method syntax declaration' };
const c = { id: 'unresolved:c', kind: 'unresolved-use', label: 'Unknown', filePath: 'A.cs', fileSha256: a.fileSha256, line: 9, column: 1, detail: null };
const observed = { id: 'e1', fromId: a.id, toId: b.id, kind: 'contains-syntax', status: 'observed-syntax', projectContextStatus: null, evidencePath: 'A.cs', evidenceSha256: a.fileSha256, line: 8, column: 1, explanation: 'Observed saved syntax', sourceProjectCandidates: null, targetProjectCandidates: null, sharedProjectCandidates: null };
const provisional = { id: 'e2', fromId: b.id, toId: c.id, kind: 'candidate-type-use', status: 'provisional-local', projectContextStatus: 'unknown-project-candidate', evidencePath: 'A.cs', evidenceSha256: a.fileSha256, line: 9, column: 1, explanation: 'Provisional source hypothesis', sourceProjectCandidates: null, targetProjectCandidates: null, sharedProjectCandidates: null };
const graph = { ...scope, fingerprintSha256: fingerprint, analyzerVersion: 'csharp-snapshot-evidence-graph-v8', nodeCount: 3, edgeCount: 2, nodes: [a, b, c], edges: [observed, provisional], limitations: ['A candidate is not a verified compiler link.'] };

test('graph validation is exact-scope and exact-fingerprint, fail-closed', () => {
  assert.doesNotThrow(() => validateGraph(graph, scope, fingerprint));
  assert.throws(() => validateGraph(graph, { ...scope, snapshotId: 'other' }, fingerprint), /does not match/);
  assert.throws(() => validateGraph(graph, scope, 'ef'.repeat(32)), /does not match/);
  assert.throws(() => validateGraph({ ...graph, nodeCount: 4 }, scope, fingerprint), /unexpected contract/);
  assert.throws(() => validateGraph({ ...graph, edges: [{ ...observed, toId: 'fabricated' }, provisional] }, scope, fingerprint), /invalid node or edge/);
  assert.throws(() => validateGraph({ ...graph, nodes: [a, b, b] }, scope, fingerprint), /invalid node or edge/);
  assert.throws(() => validateGraph({ ...graph, edges: [observed, { ...provisional, id: 'e1' }] },
    scope, fingerprint), /duplicate edge IDs/);
});

test('adjacency preserves direction and original evidence labels, without inference', () => {
  const aroundB = adjacent(graph, b.id);
  assert.deepEqual(aroundB.map(item => [item.node.id, item.direction, item.edge.status, item.edge.projectContextStatus]),
    [[a.id, 'incoming', 'observed-syntax', null], [c.id, 'outgoing', 'provisional-local', 'unknown-project-candidate']]);
  assert.deepEqual(adjacent(graph, 'not-an-evidence-id'), []);
  assert.equal(aroundB[1].edge.explanation, provisional.explanation);
});

test('counts are computed solely from saved graph node kinds', () => {
  assert.deepEqual(graphOverview(graph), [
    { kind: 'csharp-file', count: 1 }, { kind: 'syntax-declaration', count: 1 }, { kind: 'unresolved-use', count: 1 },
  ]);
});

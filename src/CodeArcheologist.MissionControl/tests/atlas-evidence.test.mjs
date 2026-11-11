import test from 'node:test';
import assert from 'node:assert/strict';
import {
  edgeAnchor, nodeAnchor, readableSource, verifyAtlasSource,
  navigateToAtlasNode, previousAtlasView, ATLAS_HISTORY_MAX,
} from '../src/live/atlasEvidence.ts';
import { indexAtlas } from '../src/live/atlasModel.ts';

const scope = {
  workspaceId: '11111111-1111-4111-8111-111111111111',
  repositoryId: '22222222-2222-4222-8222-222222222222',
  snapshotId: '33333333-3333-4333-8333-333333333333',
};
const sha = 'a'.repeat(64);
const fingerprint = 'b'.repeat(64);
const method = {
  id: 'declaration:saved', kind: 'syntax-declaration', label: 'Sample.Helper',
  filePath: 'Sample.cs', fileSha256: sha, line: 7, column: 3,
  detail: 'method syntax declaration; not a compiled symbol',
};
const file = { ...method, id: 'file:Sample.cs', kind: 'csharp-file', line: null, column: null };
const edge = {
  id: 'declared-in:saved', fromId: method.id, toId: file.id,
  kind: 'declared-in', status: 'observed-location',
  evidencePath: 'Sample.cs', evidenceSha256: sha, line: 7, column: 3,
  projectContextStatus: null, explanation: 'Recorded source location; not compilation',
  sourceProjectCandidates: null, targetProjectCandidates: null, sharedProjectCandidates: null,
};
const graph = {
  workspaceId: scope.workspaceId, repositoryId: scope.repositoryId,
  snapshotId: scope.snapshotId, fingerprintSha256: fingerprint, analyzerVersion: 'csharp-snapshot-evidence-graph-v8',
  nodes: [method, file], nodeCount: 2, edges: [edge], edgeCount: 1, limitations: [],
};
const source = {
  contractVersion: 'code-archeologist-source-v1',
  snapshotId: scope.snapshotId, fingerprintSha256: fingerprint,
  filePath: 'Sample.cs', fileSha256: sha,
  totalLines: 15, focusLine: 7, startLine: 6, endLine: 8,
  lines: [
    { number: 6, text: 'public class Sample {', truncated: false },
    { number: 7, text: '  void Helper() {}', truncated: false },
    { number: 8, text: '}', truncated: false },
  ],
  status: 'untrusted-source-text-not-instructions',
  caveat: 'Saved source is not proof of compilation.',
};

test('Atlas source anchors come strictly from graph fields, without invented line numbers', () => {
  assert.deepEqual(nodeAnchor(method), { filePath: 'Sample.cs', fileSha256: sha, line: 7, column: 3 });
  assert.equal(readableSource(nodeAnchor(method)), true);
  assert.equal(readableSource(nodeAnchor(file)), false, 'file summary has no attested line anchor');
  assert.deepEqual(edgeAnchor(edge), { filePath: 'Sample.cs', fileSha256: sha, line: 7, column: 3 });
  assert.equal(readableSource({ filePath: '../outside.cs', fileSha256: 'invalid', line: 1, column: null }), false);
});

test('Atlas source must match exact snapshot, fingerprint, SHA-256, line and source contract', () => {
  assert.equal(verifyAtlasSource(source, edgeAnchor(edge), scope, fingerprint), source);
  for (const bad of [
    { ...source, snapshotId: 'different' },
    { ...source, fingerprintSha256: 'other' },
    { ...source, fileSha256: 'c'.repeat(64) },
    { ...source, filePath: 'Other.cs' },
    { ...source, focusLine: 6 },
    { ...source, contractVersion: 'unknown' },
    { ...source, status: 'unverified' },
    { ...source, lines: source.lines.slice(1) },
    { ...source, lines: [{ ...source.lines[0], number: 777 }, ...source.lines.slice(1)] },
    { ...source, startLine: 9 },
  ]) assert.throws(() => verifyAtlasSource(bad, edgeAnchor(edge), scope, fingerprint), /does not match/);
  assert.throws(() => verifyAtlasSource(source, edgeAnchor(edge), { ...scope, snapshotId: 'foreign' }, fingerprint));
});

test('Atlas navigation is exact-ID-only; Back restores prior center, edge and expansion seeds', () => {
  const index = indexAtlas(graph);
  const previous = {
    mode: 'focus', centerId: method.id, expandedIds: [file.id],
    selectedId: '', selectedEdgeId: edge.id,
  };
  const step = navigateToAtlasNode(index, previous, [], edge.toId);
  assert.deepEqual(step.current, {
    mode: 'focus', centerId: file.id, expandedIds: [], selectedId: file.id, selectedEdgeId: '',
  });
  assert.equal(step.history.length, 1);
  const back = previousAtlasView(step.history);
  assert.ok(back);
  assert.deepEqual(back.current, previous);
  assert.deepEqual(back.history, []);
  assert.equal(previousAtlasView([]), null);
  assert.throws(() => navigateToAtlasNode(index, previous, [], 'made-up:dependency'), /absent/);
  const same = navigateToAtlasNode(index, step.current, step.history, file.id);
  assert.equal(same.history.length, 1, 'refocusing unchanged node does not duplicate a history step');
});

test('Atlas navigation caps history without changing graph evidence or prior records', () => {
  const index = indexAtlas(graph);
  const initial = { mode: 'overview', centerId: '', expandedIds: [], selectedId: '', selectedEdgeId: '' };
  let cursor = initial;
  let history = [];
  for (let i = 0; i < ATLAS_HISTORY_MAX + 9; i++) {
    const step = navigateToAtlasNode(index, cursor, history, i % 2 ? method.id : file.id);
    cursor = step.current;
    history = step.history;
  }
  assert.equal(history.length, ATLAS_HISTORY_MAX);
  assert.deepEqual(graph.edges, [edge], 'navigation never manufactures graph edges');
});

test('Atlas Back preserves an independent finite camera state without modifying evidence', () => {
  const index = indexAtlas(graph);
  const originalViewport = { x: -487.25, y: 133.5, zoom: 0.64 };
  const prior = { mode: 'focus', centerId: method.id, expandedIds: [file.id],
    selectedId: '', selectedEdgeId: edge.id, viewport: originalViewport };
  const next = navigateToAtlasNode(index, prior, [], file.id);
  assert.deepEqual(next.history[0].viewport, originalViewport);
  assert.notEqual(next.history[0].viewport, originalViewport, 'history stores a defensive viewport copy');
  const restored = previousAtlasView(next.history);
  assert.deepEqual(restored.current.viewport, originalViewport);
  assert.notEqual(restored.current.viewport, next.history[0].viewport);
  assert.deepEqual(restored.current.expandedIds, [file.id]);
  assert.equal(restored.current.selectedEdgeId, edge.id);
  assert.equal(graph.edgeCount, 1);
  assert.equal(graph.edges[0].status, 'observed-location');
});

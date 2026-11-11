import test from 'node:test';
import assert from 'node:assert/strict';
import {
  ATLAS_EVIDENCE_STYLES, atlasFocus, atlasOverview, evidenceStyle,
  findAtlasNodes, indexAtlas,
} from '../src/live/atlasModel.ts';

const makeNode = (id, kind, filePath = 'Example.cs') =>
  ({ id, kind, label: id, filePath, fileSha256: 'a'.repeat(64), line: 3, column: 1,
    detail: kind + ' is saved source' });
const makeEdge = (id, fromId, toId, kind, status) =>
  ({ id, fromId, toId, kind, status, evidencePath: 'Example.cs',
    evidenceSha256: 'a'.repeat(64), line: 3, column: 1, explanation: 'Backend-written source evidence',
    projectContextStatus: null, sourceProjectCandidates: null,
    targetProjectCandidates: null, sharedProjectCandidates: null });
const nodes = [
  makeNode('project:A.csproj', 'project-file', 'A.csproj'),
  makeNode('file:Example.cs', 'csharp-file'),
  makeNode('file:Other.cs', 'csharp-file', 'Other.cs'),
  makeNode('method:M', 'syntax-declaration'),
  makeNode('method:Other', 'syntax-declaration'),
  makeNode('invocation:I', 'invocation-site'),
  makeNode('unresolved:U', 'unresolved-use'),
];
const edges = [
  makeEdge('e1', 'project:A.csproj', 'file:Example.cs', 'candidate-compile-item', 'candidate'),
  makeEdge('e2', 'method:M', 'file:Example.cs', 'declared-in', 'observed-location'),
  makeEdge('e3', 'method:M', 'invocation:I', 'contains-invocation', 'observed-syntax'),
  makeEdge('e4', 'invocation:I', 'method:Other', 'candidate-callee', 'provisional-local'),
  makeEdge('e5', 'file:Other.cs', 'unresolved:U', 'type-use', 'unresolved'),
  makeEdge('e6', 'method:Other', 'file:Other.cs', 'declared-in', 'observed-location'),
];
const graph = { analyzerVersion: 'csharp-snapshot-evidence-graph-v8',
  workspaceId: 'w', repositoryId: 'r', snapshotId: 's', fingerprintSha256: 'b'.repeat(64),
  nodes, nodeCount: nodes.length, edges, edgeCount: edges.length, limitations: ['Candidate-only'] };

test('Atlas index retains exact original directed evidence IDs and never invents edges', () => {
  const index = indexAtlas(graph);
  assert.deepEqual(index.outgoing.get('method:M').map(e => e.id), ['e2', 'e3']);
  assert.deepEqual(index.incoming.get('method:Other').map(e => e.id), ['e4']);
  assert.deepEqual(index.incoming.get('invocation:I').map(e => e.id), ['e3']);
  assert.equal(index.nodeById.get('method:M'), nodes[3]);
  assert.throws(() => indexAtlas({ ...graph, edges: [...edges, makeEdge('bad', 'not-present', 'method:M', 'x', 'candidate')] }),
    /without a saved node endpoint/);
});

test('Overview includes ONLY real saved project/file nodes and their candidate membership edges', () => {
  const projection = atlasOverview(indexAtlas(graph));
  assert.equal(projection.mode, 'overview');
  assert.deepEqual(projection.nodes.map(n => n.id), ['file:Example.cs','file:Other.cs','project:A.csproj'].sort());
  assert.deepEqual(projection.edges.map(e => e.id), ['e1']);
  assert.equal(projection.edges[0].status, 'candidate');
  assert.equal(projection.eligibleNodeCount, 3);
  assert.equal(projection.eligibleEdgeCount, 1);
  assert.equal(projection.totalNodeCount, 7);
  const narrow = atlasOverview(indexAtlas(graph), 1);
  assert.equal(narrow.nodes.length, 1);
  assert.equal(narrow.limited, true);
  assert.equal(narrow.edges.length, 0, 'no invented edge to an undisplayed file');
});

test('Focused view is strictly direct until explicitly expanded, no implicit method-to-method dependency', () => {
  const index = indexAtlas(graph);
  const focused = atlasFocus(index, 'method:M');
  assert.deepEqual(focused.edges.map(e => e.id), ['e2','e3']);
  assert.equal(focused.edges.some(e => e.id === 'e4'), false, 'a provisional call is not inferred across invocation');
  assert.equal(focused.totalEdgeCount, 6);
  const expanded = atlasFocus(index, 'method:M', ['invocation:I']);
  assert.deepEqual(expanded.edges.map(e => e.id), ['e2','e3','e4']);
  assert.equal(expanded.edges.find(e => e.id === 'e4').fromId, 'invocation:I');
  assert.equal(expanded.edges.find(e => e.id === 'e4').toId, 'method:Other');
  assert.equal(expanded.edges.find(e => e.id === 'e4').status, 'provisional-local');
  assert.equal(expanded.edges.some(e => e.fromId === 'method:M' && e.toId === 'method:Other'), false);
  assert.throws(() => atlasFocus(index, 'foreign'), /not in the verified graph/);
});

test('Budget truncation is explicit and stable; no orphan visible edge', () => {
  const focused = atlasFocus(indexAtlas(graph), 'method:M', ['invocation:I'], 2, 1);
  assert.equal(focused.nodes.length <= 2, true);
  assert.equal(focused.edges.length <= 1, true);
  assert.equal(focused.limited, true);
  const ids = new Set(focused.nodes.map(n => n.id));
  assert.ok(focused.edges.every(e => ids.has(e.fromId) && ids.has(e.toId)));
  const shuffled = indexAtlas({ ...graph, nodes: [...graph.nodes].reverse(), edges: [...graph.edges].reverse() });
  const standard = atlasFocus(indexAtlas(graph), 'method:M', ['invocation:I']);
  const alternate = atlasFocus(shuffled, 'method:M', ['invocation:I']);
  assert.deepEqual(alternate.nodes.map(n => n.id), standard.nodes.map(n => n.id));
  assert.deepEqual(alternate.edges.map(e => e.id), standard.edges.map(e => e.id));
});

test('Search covers full saved graph, even declarations never drawn in overview', () => {
  const index = indexAtlas(graph);
  const search = findAtlasNodes(index, 'method:M', 'syntax-declaration');
  assert.equal(search.total, 1);
  assert.equal(search.matches[0].id, 'method:M');
  const all = findAtlasNodes(index, '', 'all', 2);
  assert.equal(all.matches.length, 2);
  assert.equal(all.total, 7);
  assert.equal(findAtlasNodes(index, 'no such node', 'all').total, 0);
});

test('Every backend evidence status stays distinct; unknown status fails visually closed', () => {
  assert.deepEqual(Object.keys(ATLAS_EVIDENCE_STYLES),
    ['observed-syntax','observed-location','candidate','provisional-local','unresolved']);
  assert.equal(evidenceStyle('candidate').stroke !== evidenceStyle('observed-syntax').stroke, true);
  assert.equal(evidenceStyle('provisional-local').dash !== undefined, true);
  assert.match(evidenceStyle('future-unknown').label, /Unrecognized evidence status/);
  assert.ok(evidenceStyle('future-unknown').dash);
});

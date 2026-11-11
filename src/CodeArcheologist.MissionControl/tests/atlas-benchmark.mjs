import assert from 'node:assert/strict';
import { performance } from 'node:perf_hooks';
import { indexAtlas, atlasFocus, atlasOverview, findAtlasNodes } from '../src/live/atlasModel.ts';
import * as dagre from '@dagrejs/dagre';

const COUNT = 10_000, EDGE_COUNT = 16_000;
const SHA = 'a'.repeat(64), FINGERPRINT = 'b'.repeat(64);
const node = i => ({
  id: 'node:' + String(i).padStart(5, '0'),
  kind: i < 40 ? 'project-file' : i % 6 === 0 ? 'syntax-declaration' : 'csharp-file',
  label: 'Benchmark Node ' + i, filePath: 'Benchmark' + Math.floor(i / 40) + '.cs',
  fileSha256: SHA, line: 5, column: 1, detail: 'Synthetic benchmark node ' + i,
});
const edge = (i, fromId, toId) => ({
  id: 'edge:' + String(i).padStart(5, '0'), fromId, toId,
  kind: i % 3 === 0 ? 'candidate-compile-item' : 'observed-location',
  status: i % 3 === 0 ? 'candidate' : 'observed-location',
  evidencePath: 'Benchmark.cs', evidenceSha256: SHA, line: 5, column: 1,
  explanation: 'Synthetic benchmarking record, not observed repository evidence',
  projectContextStatus: null, sourceProjectCandidates: null,
  targetProjectCandidates: null, sharedProjectCandidates: null,
});

const nodes = Array.from({ length: COUNT }, (_, i) => node(i));
const edges = Array.from({ length: EDGE_COUNT }, (_, i) =>
  edge(i, 'node:' + String(i < 720 ? 70 : (i * 37) % COUNT).padStart(5, '0'),
    'node:' + String(i < 720 ? 100 + i : (i * 41 + 17) % COUNT).padStart(5, '0')));
const graph = { analyzerVersion: 'csharp-snapshot-evidence-graph-v8',
  workspaceId: 'synthetic-w', repositoryId: 'synthetic-r', snapshotId: 'synthetic-s',
  fingerprintSha256: FINGERPRINT, nodeCount: COUNT, edgeCount: EDGE_COUNT,
  nodes, edges, limitations: ['Synthetic performance input; no claims about real source'] };

function sample(action, repetitions = 5) {
  const values = [];
  for (let i = 0; i < repetitions; i++) {
    const start = performance.now();
    action();
    values.push(Number((performance.now() - start).toFixed(2)));
  }
  const sorted = [...values].sort((a, b) => a - b);
  return { samplesMs: values, medianMs: sorted[Math.floor(sorted.length / 2)],
    minMs: sorted[0], maxMs: sorted[sorted.length - 1] };
}

const indexing = sample(() => indexAtlas(graph), 3);
const index = indexAtlas(graph);
assert.equal(index.nodeById.size, COUNT);
assert.equal(index.edgeById.size, EDGE_COUNT);
assert.equal(index.searchRows.length, COUNT);

const fullSearch = sample(() => {
  const result = findAtlasNodes(index, 'Benchmark Node 9', 'all');
  assert.equal(result.total > 0, true);
  assert.equal(result.matches.length <= 80, true);
}, 20);

const overview = atlasOverview(index);
const focused = atlasFocus(index, nodes[70].id, [], 150, 250);
assert.ok(focused.eligibleEdgeCount >= 250);
assert.equal(focused.nodes.length <= 150, true);
assert.equal(focused.edges.length <= 250, true);
assert.equal(focused.limited, true);
assert.ok(focused.edges.every(e => index.edgeById.get(e.id) === e),
  'all projected edges must be exact original edges');

const focusTiming = sample(() => atlasFocus(index, nodes[70].id, [], 150, 250), 20);
const layoutTiming = sample(() => {
  const layout = new dagre.graphlib.Graph({ multigraph: true });
  layout.setGraph({ rankdir: 'LR', nodesep: 38, ranksep: 105 });
  layout.setDefaultEdgeLabel(() => ({}));
  for (const n of focused.nodes) layout.setNode(n.id, { width: 216, height: 74 });
  for (const e of focused.edges) layout.setEdge(e.fromId, e.toId, {}, e.id);
  dagre.layout(layout);
  assert.ok(focused.nodes.every(n => Number.isFinite(layout.node(n.id).x)));
}, 5);

const report = {
  schemaVersion: 'atlas-synthetic-benchmark-v1',
  evidence: 'SYNTHETIC ONLY — this is not a real-repository performance result',
  input: { nodes: COUNT, edges: EDGE_COUNT },
  visible: { overviewNodes: overview.nodes.length, overviewEdges: overview.edges.length,
    focusNodes: focused.nodes.length, focusEdges: focused.edges.length,
    eligibleFocusEdges: focused.eligibleEdgeCount, limited: focused.limited },
  timing: { index: indexing, search: fullSearch, focus: focusTiming, dagre: layoutTiming },
  memory: { heapUsedMiB: Number((process.memoryUsage().heapUsed / 1048576).toFixed(2)) },
  runtime: { node: process.version, platform: process.platform, arch: process.arch },
};
console.log(JSON.stringify(report, null, 2));

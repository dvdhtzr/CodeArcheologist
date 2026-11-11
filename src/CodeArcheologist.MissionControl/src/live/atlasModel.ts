import type { EvidenceGraph, GraphEdge, GraphNode } from './evidenceGraph.ts';

/** Presentation limits, NOT graph-analysis or source-completeness limits. */
export const ATLAS_OVERVIEW_NODES = 80;
export const ATLAS_FOCUS_NODES = 150;
export const ATLAS_FOCUS_EDGES = 250;
export const ATLAS_SEARCH_RESULTS = 80;

export type AtlasIndex = {
  graph: EvidenceGraph;
  nodeById: ReadonlyMap<string, GraphNode>;
  edgeById: ReadonlyMap<string, GraphEdge>;
  /** Sorted/indexed exactly once on load; subsequent searches never sort 10k matches. */
  searchRows: readonly { node: GraphNode; searchText: string }[];
  incoming: ReadonlyMap<string, readonly GraphEdge[]>;
  outgoing: ReadonlyMap<string, readonly GraphEdge[]>;
};
export type AtlasProjection = {
  nodes: GraphNode[];
  edges: GraphEdge[];
  mode: 'overview' | 'focus';
  /** Number of eligible nodes/edges in the selected view, before the display bounds. */
  eligibleNodeCount: number;
  eligibleEdgeCount: number;
  /** Number of nodes/edges of ALL kinds in the loaded saved graph. */
  totalNodeCount: number;
  totalEdgeCount: number;
  limited: boolean;
  explanation: string;
};
const byId = (a: { id: string }, b: { id: string }) => a.id.localeCompare(b.id);

/** Use backend IDs and edge direction verbatim. An edge is NEVER inferred. */
export function indexAtlas(graph: EvidenceGraph): AtlasIndex {
  const nodeById = new Map(graph.nodes.map(n => [n.id, n]));
  const edgeById = new Map(graph.edges.map(e => [e.id, e]));
  if (edgeById.size !== graph.edges.length)
    throw new Error('Atlas received duplicate saved edge IDs; graph identity is ambiguous.');
  const searchRows = [...graph.nodes].sort(byId).map(node => ({
    node,
    searchText: [node.id, node.label, node.kind, node.filePath ?? '', node.detail ?? '']
      .join(' ').toLowerCase(),
  }));
  const incoming = new Map<string, GraphEdge[]>();
  const outgoing = new Map<string, GraphEdge[]>();
  for (const edge of graph.edges) {
    if (!nodeById.has(edge.fromId) || !nodeById.has(edge.toId))
      throw new Error('Atlas received an edge without a saved node endpoint.');
    if (!incoming.has(edge.toId)) incoming.set(edge.toId, []);
    if (!outgoing.has(edge.fromId)) outgoing.set(edge.fromId, []);
    incoming.get(edge.toId)!.push(edge);
    outgoing.get(edge.fromId)!.push(edge);
  }
  for (const edges of incoming.values()) edges.sort(byId);
  for (const edges of outgoing.values()) edges.sort(byId);
  return { graph, nodeById, edgeById, searchRows, incoming, outgoing };
}

/**
 * A *subset* of actual saved projects, files, and candidate membership edges.
 * No folder/container node is synthesized, and no MSBuild evaluation implied.
 */
export function atlasOverview(index: AtlasIndex, maxNodes = ATLAS_OVERVIEW_NODES): AtlasProjection {
  const projects = index.graph.nodes.filter(n => n.kind === 'project-file').sort(byId);
  const files = index.graph.nodes.filter(n => n.kind === 'csharp-file').sort(byId);
  const eligible = [...projects, ...files];
  const budget = Math.max(1, Math.trunc(maxNodes));
  // Prioritize projects then files with observed candidate membership, so
  // the initial screen is useful even when a repository has many files.
  const memberFiles = new Set(index.graph.edges.filter(e => e.kind === 'candidate-compile-item').map(e => e.toId));
  files.sort((a, b) => Number(memberFiles.has(b.id)) - Number(memberFiles.has(a.id)) || byId(a, b));
  const nodes = [...projects, ...files].slice(0, budget);
  const included = new Set(nodes.map(n => n.id));
  const related = index.graph.edges.filter(e => e.kind === 'candidate-compile-item' &&
    included.has(e.fromId) && included.has(e.toId)).sort(byId);
  const edges = related.slice(0, ATLAS_FOCUS_EDGES);
  const limited = nodes.length < eligible.length || edges.length < related.length;
  return { nodes, edges, mode: 'overview', eligibleNodeCount: eligible.length,
    eligibleEdgeCount: index.graph.edges.filter(e => e.kind === 'candidate-compile-item').length,
    totalNodeCount: index.graph.nodeCount, totalEdgeCount: index.graph.edgeCount, limited,
    explanation: 'Overview shows saved project-file and C# file nodes with only recorded, candidate project-membership edges. Other declarations and relations remain searchable and can be focused. Project membership is not compiled input.' };
}

/**
 * Expands only the exact incoming/outgoing edges of selected seeds.
 * Edges and destinations that exceed the budgets are OMITTED, not summarized
 * into fictional relationships or implied paths.
 */
export function atlasFocus(index: AtlasIndex, centerId: string, expandedIds: readonly string[] = [],
  maxNodes = ATLAS_FOCUS_NODES, maxEdges = ATLAS_FOCUS_EDGES): AtlasProjection {
  const center = index.nodeById.get(centerId);
  if (!center) throw new Error('Requested Atlas center is not in the verified graph.');
  const seeds = [centerId, ...[...new Set(expandedIds)].filter(id => id !== centerId).sort()];
  const nodes = new Map<string, GraphNode>([[centerId, center]]);
  const edges = new Map<string, GraphEdge>();
  const possibleEdges = new Map<string, GraphEdge>();
  const possibleNodes = new Set<string>([centerId]);
  for (const seed of seeds) {
    if (!index.nodeById.has(seed)) continue;
    const related = [...(index.incoming.get(seed) ?? []), ...(index.outgoing.get(seed) ?? [])].sort(byId);
    for (const edge of related) {
      possibleEdges.set(edge.id, edge);
      possibleNodes.add(edge.fromId);
      possibleNodes.add(edge.toId);
      if (edges.has(edge.id) || edges.size >= maxEdges) continue;
      const from = index.nodeById.get(edge.fromId)!;
      const to = index.nodeById.get(edge.toId)!;
      const added = Number(!nodes.has(from.id)) + Number(!nodes.has(to.id));
      if (nodes.size + added > maxNodes) continue;
      nodes.set(from.id, from);
      nodes.set(to.id, to);
      edges.set(edge.id, edge);
    }
  }
  const visibleNodes = [...nodes.values()].sort(byId);
  const visibleEdges = [...edges.values()].sort(byId);
  return { nodes: visibleNodes, edges: visibleEdges, mode: 'focus',
    eligibleNodeCount: possibleNodes.size, eligibleEdgeCount: possibleEdges.size,
    totalNodeCount: index.graph.nodeCount, totalEdgeCount: index.graph.edgeCount,
    limited: visibleNodes.length < possibleNodes.size || visibleEdges.length < possibleEdges.size,
    explanation: 'Focused graph shows only recorded one-hop relationships of the center and deliberately expanded nodes. Candidate edges are not verified calls or dependencies; missing edges are unknown, not proof of absence.' };
}

export function findAtlasNodes(index: AtlasIndex, query: string, kind: string, limit = ATLAS_SEARCH_RESULTS): {
  matches: GraphNode[]; total: number;
} {
  const term = query.trim().toLowerCase();
  const matches: GraphNode[] = [];
  let total = 0;
  const cap = Math.max(0, Math.trunc(limit));
  for (const row of index.searchRows) {
    if ((kind !== 'all' && row.node.kind !== kind) ||
        (term && !row.searchText.includes(term))) continue;
    total++;
    if (matches.length < cap) matches.push(row.node);
  }
  return { matches, total };
}

/** Distinguish statuses rather than reducing uncertainty to a single stroke. */
export const ATLAS_EVIDENCE_STYLES: Record<string, { label: string; stroke: string; dash?: string }> = {
  'observed-syntax': { label: 'Observed syntax', stroke: '#71d7f0' },
  'observed-location': { label: 'Observed location', stroke: '#719bc5' },
  candidate: { label: 'Candidate only', stroke: '#e5b56d', dash: '7 5' },
  'provisional-local': { label: 'Provisional local', stroke: '#b2a0f0', dash: '5 5' },
  unresolved: { label: 'Unresolved', stroke: '#97a3b5', dash: '2 6' },
};
export function evidenceStyle(status: string) {
  return ATLAS_EVIDENCE_STYLES[status] ??
    { label: 'Unrecognized evidence status (' + status + ')', stroke: '#9da0a8', dash: '2 6' };
}

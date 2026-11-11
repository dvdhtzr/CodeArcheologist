import type { Scope } from './api.ts';

// Read-only projection of the existing csharp-evidence-graph v8 API.
// Every relationship retains the backend's source-defined evidence status.
export type GraphNode = {
  id: string; kind: string; label: string; filePath: string | null;
  fileSha256: string | null; line: number | null; column: number | null; detail: string | null;
};
export type ProjectCandidate = { projectPath: string; projectSha256: string; inclusionBasis: string; unresolvedRuleCount: number };
export type GraphEdge = {
  id: string; fromId: string; toId: string; kind: string; status: string;
  projectContextStatus: string | null; evidencePath: string; evidenceSha256: string;
  line: number | null; column: number | null; explanation: string;
  sourceProjectCandidates: ProjectCandidate[] | null; targetProjectCandidates: ProjectCandidate[] | null;
  sharedProjectCandidates: ProjectCandidate[] | null;
};
export type EvidenceGraph = {
  workspaceId: string; repositoryId: string; snapshotId: string; fingerprintSha256: string;
  analyzerVersion: string; nodeCount: number; edgeCount: number;
  nodes: GraphNode[]; edges: GraphEdge[]; limitations: string[];
};
export type Neighbor = { edge: GraphEdge; node: GraphNode; direction: 'outgoing' | 'incoming' };

export function validateGraph(report: EvidenceGraph, scope: Scope, fingerprint: string): void {
  if (report.workspaceId !== scope.workspaceId || report.repositoryId !== scope.repositoryId ||
      report.snapshotId !== scope.snapshotId || report.fingerprintSha256 !== fingerprint)
    throw new Error('Evidence graph does not match the selected verified snapshot.');
  if (!report.analyzerVersion.startsWith('csharp-snapshot-evidence-graph-') ||
      !Array.isArray(report.nodes) || !Array.isArray(report.edges) || !Array.isArray(report.limitations) ||
      report.nodeCount !== report.nodes.length || report.edgeCount !== report.edges.length)
    throw new Error('Evidence graph response is incomplete or has an unexpected contract.');
  const ids = new Set(report.nodes.map(node => node.id));
  const edgeIds = new Set(report.edges.map(edge => edge.id));
  if (ids.size !== report.nodes.length || edgeIds.size !== report.edges.length ||
      report.edges.some(edge => !ids.has(edge.fromId) || !ids.has(edge.toId)))
    throw new Error('Evidence graph contains invalid or duplicate node or edge identities.');
}

export function adjacent(graph: EvidenceGraph, nodeId: string): Neighbor[] {
  const byId = new Map(graph.nodes.map(node => [node.id, node]));
  return graph.edges.flatMap(edge => {
    if (edge.fromId !== nodeId && edge.toId !== nodeId) return [];
    const outgoing = edge.fromId === nodeId;
    const node = byId.get(outgoing ? edge.toId : edge.fromId);
    return node ? [{ edge, node, direction: outgoing ? 'outgoing' as const : 'incoming' as const }] : [];
  });
}

export function graphOverview(graph: EvidenceGraph): Array<{ kind: string; count: number }> {
  const counts = new Map<string, number>();
  for (const node of graph.nodes) counts.set(node.kind, (counts.get(node.kind) ?? 0) + 1);
  return [...counts.entries()].map(([kind, count]) => ({ kind, count }))
    .sort((a, b) => b.count - a.count || a.kind.localeCompare(b.kind));
}

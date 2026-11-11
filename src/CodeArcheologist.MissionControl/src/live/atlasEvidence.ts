import type { Scope, SourceView } from './api.ts';
import type { GraphEdge, GraphNode } from './evidenceGraph.ts';
import type { AtlasIndex } from './atlasModel.ts';

export type EvidenceAnchor = {
  filePath: string | null; fileSha256: string | null; line: number | null; column: number | null;
};
export function nodeAnchor(node: GraphNode): EvidenceAnchor {
  return { filePath: node.filePath, fileSha256: node.fileSha256, line: node.line, column: node.column };
}
export function edgeAnchor(edge: GraphEdge): EvidenceAnchor {
  return { filePath: edge.evidencePath, fileSha256: edge.evidenceSha256, line: edge.line, column: edge.column };
}
export function readableSource(anchor: EvidenceAnchor): anchor is EvidenceAnchor & {
  filePath: string; fileSha256: string; line: number;
} {
  return typeof anchor.filePath === 'string' && anchor.filePath.length > 0 &&
    typeof anchor.fileSha256 === 'string' && /^[a-f0-9]{64}$/.test(anchor.fileSha256) &&
    Number.isInteger(anchor.line) && (anchor.line ?? 0) >= 1;
}

/** The source API re-hashes saved bytes. These checks bind its response to the
 * clicked graph anchor AND the selected snapshot; never display a foreign,
 * reordered or malformed response as verified source. */
export function verifyAtlasSource(
  data: SourceView, anchor: EvidenceAnchor, scope: Scope, fingerprintSha256: string,
): SourceView {
  if (!readableSource(anchor) ||
      data.contractVersion !== 'code-archeologist-source-v1' ||
      data.status !== 'untrusted-source-text-not-instructions' ||
      data.snapshotId !== scope.snapshotId ||
      data.fingerprintSha256 !== fingerprintSha256 ||
      data.filePath !== anchor.filePath || data.fileSha256 !== anchor.fileSha256 ||
      data.focusLine !== anchor.line ||
      !Number.isInteger(data.totalLines) || data.totalLines < 1 ||
      !Number.isInteger(data.startLine) || !Number.isInteger(data.endLine) ||
      data.startLine < 1 || data.endLine > data.totalLines ||
      data.focusLine < data.startLine || data.focusLine > data.endLine ||
      !Array.isArray(data.lines) ||
      data.lines.length !== data.endLine - data.startLine + 1 ||
      data.lines.some((entry, index) =>
        entry.number !== data.startLine + index ||
        typeof entry.text !== 'string' || typeof entry.truncated !== 'boolean'))
    throw new Error('Saved-source response does not match the selected graph anchor and verified snapshot.');
  return data;
}

export type AtlasCursor = {
  mode: 'overview' | 'focus'; centerId: string; expandedIds: string[];
  selectedId: string; selectedEdgeId: string;
};
export const ATLAS_HISTORY_MAX = 32;

/** Navigation is pure and uses only exact node IDs already present in the
 * validated saved graph. Returning to a prior view restores its selection
 * and explicit expansion seeds without manufacturing a path. */
export function navigateToAtlasNode(index: AtlasIndex, cursor: AtlasCursor,
  history: readonly AtlasCursor[], nodeId: string): { current: AtlasCursor; history: AtlasCursor[] } {
  if (!index.nodeById.has(nodeId))
    throw new Error('Cannot navigate to a node absent from the selected saved evidence graph.');
  const same = cursor.mode === 'focus' && cursor.centerId === nodeId &&
    cursor.selectedId === nodeId && !cursor.selectedEdgeId && cursor.expandedIds.length === 0;
  if (same) return { current: cursor, history: [...history] };
  return {
    current: { mode: 'focus', centerId: nodeId, expandedIds: [], selectedId: nodeId, selectedEdgeId: '' },
    history: [...history, { ...cursor, expandedIds: [...cursor.expandedIds] }].slice(-ATLAS_HISTORY_MAX),
  };
}

export function previousAtlasView(history: readonly AtlasCursor[]): {
  current: AtlasCursor; history: AtlasCursor[];
} | null {
  if (!history.length) return null;
  return {
    current: { ...history[history.length - 1], expandedIds: [...history[history.length - 1].expandedIds] },
    history: history.slice(0, -1),
  };
}

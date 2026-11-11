import { useEffect, useState } from 'react';
import type { SourceView } from './api.ts';
import type { ExplorerSelection } from './DeterministicExplorer.tsx';
import type { GraphEdge, GraphNode, ProjectCandidate } from './evidenceGraph.ts';
import type { AtlasIndex } from './atlasModel.ts';
import { evidenceStyle } from './atlasModel.ts';
import { edgeAnchor, nodeAnchor, readableSource, verifyAtlasSource } from './atlasEvidence.ts';
import type { EvidenceAnchor } from './atlasEvidence.ts';

type Props = {
  selection: NonNullable<ExplorerSelection>;
  fingerprint: string;
  index: AtlasIndex;
  node: GraphNode | undefined;
  edge: GraphEdge | undefined;
  mode: 'overview' | 'focus';
  centerId: string;
  expandedIds: readonly string[];
  visibleIds: ReadonlySet<string>;
  onNavigate: (nodeId: string) => void;
  onExpand: () => void;
  onImpact: (nodeId: string) => void;
};

function CandidateGroup({ title, items }: { title: string; items: ProjectCandidate[] | null }) {
  return <section className="atlas-project-group">
    <h5>{title}</h5>
    {items === null ? <p>Project candidate details were not supplied for this relationship.</p>
      : items.length === 0 ? <p>No saved project candidates recorded.</p>
        : <ul>{items.map((item, i) => <li key={item.projectPath + ':' + item.projectSha256 + ':' + i}>
          <strong>{item.projectPath}</strong>
          <span>Inclusion basis: {item.inclusionBasis}</span>
          <span>Unresolved rules: {item.unresolvedRuleCount}</span>
          <code>Saved project SHA-256: {item.projectSha256}</code>
        </li>)}</ul>}
  </section>;
}

export function AtlasEvidenceInspector({
  selection, fingerprint, index, node, edge, mode, centerId, expandedIds, visibleIds,
  onNavigate, onExpand, onImpact,
}: Props) {
  const [sourceAnchor, setSourceAnchor] = useState<EvidenceAnchor | null>(null);
  const [source, setSource] = useState<SourceView | null>(null);
  const [loadingSource, setLoadingSource] = useState(false);
  const [sourceError, setSourceError] = useState('');

  useEffect(() => {
    let active = true;
    setSource(null);
    setSourceError('');
    if (!sourceAnchor || !readableSource(sourceAnchor)) {
      setLoadingSource(false);
      return () => { active = false; };
    }
    setLoadingSource(true);
    void selection.api.source(selection.scope, sourceAnchor.filePath,
      sourceAnchor.fileSha256, sourceAnchor.line, 8)
      .then(data => {
        if (!active) return;
        setSource(verifyAtlasSource(data, sourceAnchor, selection.scope, fingerprint));
      })
      .catch(error => {
        if (!active) return;
        setSourceError(error instanceof Error ? error.message : String(error));
      })
      .finally(() => { if (active) setLoadingSource(false); });
    return () => { active = false; };
  }, [sourceAnchor, selection.api, selection.scope.workspaceId,
      selection.scope.repositoryId, selection.scope.snapshotId, fingerprint]);

  const selectedAnchor = edge ? edgeAnchor(edge) : node ? nodeAnchor(node) : null;
  const status = edge ? evidenceStyle(edge.status) : null;
  const from = edge ? index.nodeById.get(edge.fromId) : null;
  const to = edge ? index.nodeById.get(edge.toId) : null;
  const canExpand = Boolean(node && mode === 'focus' &&
    node.id !== centerId && !expandedIds.includes(node.id) &&
    expandedIds.length < 12 && visibleIds.has(node.id));

  return <>
    <h3>{edge ? 'Recorded relationship' : node ? 'Saved graph node' : 'Explore a selection'}</h3>
    {node && <>
      <p className="atlas-selected-label">{node.label}</p>
      <p><strong>Kind:</strong> {node.kind}</p>
      <p><strong>Source:</strong> {node.filePath ? node.filePath + (node.line ? ':' + node.line : '') : 'No saved line anchor'}</p>
      <p><strong>Node ID:</strong> <code>{node.id}</code></p>
      <p><strong>File SHA-256:</strong> <code>{node.fileSha256 ?? 'Not supplied'}</code></p>
      <p className="live-help">{node.detail ?? 'No further details recorded.'}</p>
      {node.kind === 'syntax-declaration' && node.detail?.startsWith('method syntax declaration') &&
        <button type="button" className="secondary-action"
          onClick={() => onImpact(node.id)}>Inspect direct impact</button>}
      {mode === 'focus' ? <>
        <button type="button" className="secondary-action" disabled={!canExpand}
          onClick={onExpand}>Expand this node's recorded neighbors</button>
        <button type="button" className="secondary-action"
          onClick={() => onNavigate(node.id)}>Refocus here</button>
      </> : <button type="button" className="secondary-action"
        onClick={() => onNavigate(node.id)}>Inspect direct relationships</button>}
    </>}
    {edge && from && to && <>
      <p className="atlas-selected-label">{edge.kind}</p>
      <p><strong>Evidence status:</strong> <span style={{ color: status?.stroke }}>
        {edge.status} — {status?.label}</span></p>
      <p><strong>Direction:</strong> Recorded from → to. Not necessarily a runtime call or dependency.</p>
      <p><strong>Edge ID:</strong> <code>{edge.id}</code></p>
      <p><strong>From ID:</strong> <code>{edge.fromId}</code></p>
      <p><strong>To ID:</strong> <code>{edge.toId}</code></p>
      <div className="atlas-endpoints" aria-label="Exact recorded relationship endpoints">
        <button type="button" onClick={() => onNavigate(edge.fromId)}>
          <span>Go to source node</span>
          <strong>{from.label}</strong>
          <small>{from.kind}</small>
        </button>
        <button type="button" onClick={() => onNavigate(edge.toId)}>
          <span>Go to target node</span>
          <strong>{to.label}</strong>
          <small>{to.kind}</small>
        </button>
      </div>
      <p><strong>Evidence path:</strong> {edge.evidencePath || 'Not supplied'}
        {edge.line ? ':' + edge.line + (edge.column ? ':' + edge.column : '') : ''}</p>
      <p><strong>Evidence file SHA-256:</strong> <code>{edge.evidenceSha256 || 'Not supplied'}</code></p>
      <p><strong>Project-context status:</strong> {edge.projectContextStatus ?? 'No project-context status supplied'}</p>
      <p className="live-help">{edge.explanation}</p>
      <details className="atlas-project-evidence">
        <summary>Static project membership candidates (not verified compiler inputs)</summary>
        <CandidateGroup title="Source project candidates" items={edge.sourceProjectCandidates}/>
        <CandidateGroup title="Target project candidates" items={edge.targetProjectCandidates}/>
        <CandidateGroup title="Shared project candidates" items={edge.sharedProjectCandidates}/>
      </details>
    </>}
    {!node && !edge && <p className="live-help">Select a saved node or an exact recorded edge. Arrows express the backend edge direction, not necessarily execution flow.</p>}

    {selectedAnchor && <button type="button" className="secondary-action"
      disabled={!readableSource(selectedAnchor)}
      onClick={() => setSourceAnchor({ ...selectedAnchor })}>
      {edge ? 'Inspect edge source' : 'Inspect saved source'}
    </button>}
    {loadingSource && <p role="status" className="atlas-source-status">Verifying copied source bytes…</p>}
    {sourceError && <p role="alert" className="live-error">{sourceError}</p>}
    {source && <section className="atlas-inline-source" aria-label="Atlas verified saved source">
      <div className="atlas-source-heading">
        <h4>Verified saved source</h4>
        <button type="button" onClick={() => { setSourceAnchor(null); setSource(null); }}>Close source</button>
      </div>
      <p>{source.filePath} · focus line {source.focusLine} · lines {source.startLine}–{source.endLine} of {source.totalLines}</p>
      <p>Saved file SHA-256: <code>{source.fileSha256}</code></p>
      <div className="atlas-source-lines" role="group" aria-label="Line-numbered saved source context">
        {source.lines.map(line => <div key={line.number}
          className={line.number === source.focusLine ? 'atlas-source-line is-focus' : 'atlas-source-line'}>
          <span aria-hidden="true">{line.number}</span>
          <code>{line.text}{line.truncated ? ' …[line truncated]' : ''}</code>
        </div>)}
      </div>
      <p className="live-help">{source.caveat}</p>
      <p className="live-help">This is copied source text rechecked by SHA-256, not proof of compilation, runtime behavior, or a verified dependency.</p>
    </section>}
  </>;
}

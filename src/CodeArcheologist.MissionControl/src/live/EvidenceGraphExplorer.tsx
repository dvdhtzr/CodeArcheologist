import { useEffect, useMemo, useRef, useState } from 'react';
import type { ExplorerSelection } from './DeterministicExplorer.tsx';
import type { GraphNode, GraphEdge, ProjectCandidate, EvidenceGraph } from './evidenceGraph.ts';
import { adjacent, graphOverview, validateGraph } from './evidenceGraph.ts';

type EvidenceLocation = { filePath: string | null; fileSha256: string | null; line: number | null; column: number | null };
type Props = {
  selection: ExplorerSelection;
  fingerprint: string | null;
  onInspect: (anchor: EvidenceLocation) => void;
  onMethod: (methodId: string) => void;
};

const clip = (s: string, max: number) => s.length > max ? s.slice(0, max - 1) + '…' : s;
const pathLine = (path: string | null, line: number | null) => path ? path + (line ? ':' + line : '') : 'No source location';
const isMethod = (n: GraphNode) => n.kind === 'syntax-declaration' && n.detail?.startsWith('method syntax declaration');
const canInspect = (a: EvidenceLocation) => Boolean(a.filePath && a.fileSha256 && a.line);
const MAX_VISIBLE_NODES = 100;
const MAX_VISIBLE_RELATIONSHIPS = 100;
const MAX_DIAGRAM_NEIGHBORS = 8;

function ProjectEvidence({ title, items }: { title: string; items: ProjectCandidate[] | null }) {
  return <section className="graph-project-evidence">
    <strong>{title}</strong>
    {!items?.length ? <p>No candidate project membership recorded.</p>
      : <ul>{items.map(c => <li key={c.projectPath + c.projectSha256}>
          <code>{c.projectPath}</code> · {c.inclusionBasis} · {c.unresolvedRuleCount} unresolved rules · project SHA-256 {c.projectSha256}
        </li>)}</ul>}
  </section>;
}

export function EvidenceGraphExplorer({ selection, fingerprint, onInspect, onMethod }: Props) {
  const [loaded, setLoaded] = useState<{ key: string; api: ExplorerSelection; report: EvidenceGraph } | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [selectedId, setSelectedId] = useState('');
  const [search, setSearch] = useState('');
  const [kind, setKind] = useState('all');
  const requestEpoch = useRef(0);
  const scope = selection?.scope;
  const key = scope ? [scope.workspaceId, scope.repositoryId, scope.snapshotId, fingerprint ?? ''].join('/') : '';

  useEffect(() => {
    requestEpoch.current++;
    setLoaded(null); setSelectedId(''); setError(''); setLoading(false); setSearch(''); setKind('all');
  }, [selection?.api, key]);

  const report = loaded?.key === key && loaded.api?.api === selection?.api ? loaded.report : null;
  const kinds = useMemo(() => report ? graphOverview(report) : [], [report]);
  const nodes = useMemo(() => report?.nodes.filter(n =>
    (kind === 'all' || kind === n.kind) &&
    (n.label + ' ' + n.kind + ' ' + (n.filePath ?? '')).toLowerCase().includes(search.toLowerCase())) ?? [],
  [report, kind, search]);
  const selected = useMemo(() => report?.nodes.find(n => n.id === selectedId), [report, selectedId]);
  const neighbors = useMemo(() => report && selected ? adjacent(report, selected.id) : [], [report, selected]);
  const diagram = useMemo(() => Array.from(new Map(neighbors.map(n => [n.node.id, n])).values())
    .slice(0, MAX_DIAGRAM_NEIGHBORS), [neighbors]);

  async function loadGraph() {
    if (!selection || !fingerprint || loading) return;
    const epoch = ++requestEpoch.current;
    setError(''); setLoading(true);
    try {
      const result = await selection.api.evidenceGraph(selection.scope);
      validateGraph(result, selection.scope, fingerprint);
      if (epoch !== requestEpoch.current) return;
      setLoaded({ key, api: selection, report: result });
      setSelectedId(result.nodes[0]?.id ?? '');
    } catch (e) {
      if (epoch === requestEpoch.current) setError(e instanceof Error ? e.message : String(e));
    } finally {
      if (epoch === requestEpoch.current) setLoading(false);
    }
  }

  function inspectEdge(edge: GraphEdge) {
    onInspect({ filePath: edge.evidencePath, fileSha256: edge.evidenceSha256, line: edge.line, column: edge.column });
  }

  return (
    <section className="graph-explorer secondary-panel" aria-label="Saved C# evidence graph">
      <span className="eyebrow">SOURCE-DERIVED, NOT A COMPILED DEPENDENCY GRAPH</span>
      <h2>C# evidence graph</h2>
      <p className="live-help">Explore observed syntax and explicitly provisional/candidate relationships. The diagram shows a bounded neighborhood; it does not imply verified linking, execution, reachability, or completeness.</p>
      {!fingerprint && <p>Select a verified snapshot in Live API to explore its evidence graph.</p>}
      {fingerprint && !report && <button type="button" className="primary-action" disabled={loading} onClick={() => void loadGraph()}>
        {loading ? 'Reading snapshot evidence…' : 'Load saved evidence graph'}
      </button>}
      {error && <p className="live-error" role="alert">{error}</p>}
      {report && <>
        <div className="deterministic-summary">
          <div><strong>{report.nodeCount}</strong><span>Evidence nodes</span></div>
          <div><strong>{report.edgeCount}</strong><span>Source-derived relations</span></div>
          <div><strong>{kinds.length}</strong><span>Observed graph node kinds</span></div>
        </div>
        <p className="live-help">Analyzer: {report.analyzerVersion} · Saved snapshot fingerprint: <code>{report.fingerprintSha256}</code></p>
        <div className="graph-kind-overview" aria-label="Evidence inventory by kind">
          {kinds.map(item => <button type="button" key={item.kind} aria-pressed={kind === item.kind}
            onClick={() => { setKind(kind === item.kind ? 'all' : item.kind); setSearch(''); }}>
            <strong>{item.count}</strong><span>{item.kind}</span>
          </button>)}
        </div>
        <div className="graph-picker">
          <label>Find saved node
            <input type="search" placeholder="Label, kind, or file path" value={search} onChange={e => setSearch(e.target.value)} />
          </label>
          <label>Node kind
            <select value={kind} onChange={e => setKind(e.target.value)}>
              <option value="all">All kinds</option>
              {kinds.map(k => <option key={k.kind} value={k.kind}>{k.kind} ({k.count})</option>)}
            </select>
          </label>
        </div>
        <p className="live-help">{nodes.length} matching node(s); showing at most {MAX_VISIBLE_NODES}. Select a node to inspect only its directly recorded edges.</p>
        <div className="graph-node-results">
          {nodes.slice(0, MAX_VISIBLE_NODES).map(node => <button key={node.id} type="button"
            className={selectedId === node.id ? 'graph-node-choice is-selected' : 'graph-node-choice'}
            aria-pressed={selectedId === node.id} onClick={() => setSelectedId(node.id)}>
            <strong>{node.label}</strong><span>{node.kind} · {pathLine(node.filePath, node.line)}</span>
          </button>)}
        </div>
        {nodes.length === 0 && <p>No matching nodes. An empty result is not evidence that a declaration or dependency is absent.</p>}
        {selected && <div className="graph-focus">
          <h3>Selected: {selected.label}</h3>
          <p className="live-help">{selected.kind} · {selected.detail ?? 'No additional detail provided'} · {pathLine(selected.filePath, selected.line)}</p>
          <div className="graph-actions">
            <button type="button" className="secondary-action" disabled={!canInspect(selected)} onClick={() => onInspect(selected)}>Inspect verified source</button>
            {isMethod(selected) && <button type="button" className="secondary-action" onClick={() => onMethod(selected.id)}>Inspect direct impact</button>}
          </div>
          <p className="live-help">{neighbors.length} direct edge(s). Diagram shows {diagram.length} distinct neighboring node(s), capped at {MAX_DIAGRAM_NEIGHBORS}.</p>
          {diagram.length > 0 && <svg className="graph-orbit" viewBox="0 0 900 450" role="img" aria-label="Selected source node in the center with up to eight directly related nodes. Use the relationship list below to navigate.">
            <rect x="326" y="186" width="248" height="78" rx="14" className="graph-center"/>
            <text x="450" y="218" textAnchor="middle" className="graph-svg-title">{clip(selected.label, 33)}</text>
            <text x="450" y="242" textAnchor="middle" className="graph-svg-caption">{clip(selected.kind, 33)}</text>
            {diagram.map(({ edge, node, direction }, index) => {
              const a = 2 * Math.PI * index / diagram.length - Math.PI / 2;
              const x = 450 + Math.cos(a) * 300, y = 225 + Math.sin(a) * 152;
              return <g key={node.id}>
                <line x1="450" y1="225" x2={x} y2={y} className={edge.status === 'observed-syntax' ? 'graph-observed-edge' : 'graph-provisional-edge'}/>
                <rect x={x - 88} y={y - 29} width="176" height="58" rx="10" className="graph-neighbor"/>
                <text x={x} y={y - 4} textAnchor="middle" className="graph-svg-title">{clip(node.label, 22)}</text>
                <text x={x} y={y + 13} textAnchor="middle" className="graph-svg-caption">{direction === 'outgoing' ? 'out' : 'in'} · {clip(edge.status, 17)}</text>
              </g>;
            })}
          </svg>}
          <h3>Direct evidence relationships</h3>
          <p className="live-help">Both the edge direction and the backend evidence status matter. Click the neighboring node to recenter; inspect the source location used as evidence separately.</p>
          {neighbors.length === 0 && <p>No recorded adjacent relationships; this does not imply no dependencies.</p>}
          <ul className="graph-relation-list">
            {neighbors.slice(0, MAX_VISIBLE_RELATIONSHIPS).map(({ edge, node, direction }) =>
              <li key={edge.id}>
                <div className="graph-relation-heading">
                  <span className="graph-status">{edge.kind} · {edge.status}{edge.projectContextStatus ? ' · ' + edge.projectContextStatus : ''}</span>
                  <span className="graph-direction">{direction === 'outgoing' ? 'Selected → neighbor' : 'Neighbor → selected'}</span>
                </div>
                <button type="button" onClick={() => { setSelectedId(node.id); setKind('all'); setSearch(''); }}>{node.label} <span>({node.kind})</span></button>
                <p>{edge.explanation}</p>
                <div className="graph-actions">
                  <code>{pathLine(edge.evidencePath, edge.line)} · SHA-256 {clip(edge.evidenceSha256, 18)}</code>
                  <button type="button" className="secondary-action" disabled={!edge.evidencePath || !edge.evidenceSha256 || !edge.line} onClick={() => inspectEdge(edge)}>Inspect edge source</button>
                </div>
                {(edge.sourceProjectCandidates?.length || edge.targetProjectCandidates?.length || edge.sharedProjectCandidates?.length) ? <details>
                  <summary>Static project candidates (not compiled project references)</summary>
                  <ProjectEvidence title="Source project candidates" items={edge.sourceProjectCandidates}/>
                  <ProjectEvidence title="Target project candidates" items={edge.targetProjectCandidates}/>
                  <ProjectEvidence title="Shared candidates" items={edge.sharedProjectCandidates}/>
                </details> : null}
              </li>)}
          </ul>
          {neighbors.length > MAX_VISIBLE_RELATIONSHIPS && <p className="live-help">First {MAX_VISIBLE_RELATIONSHIPS} of {neighbors.length} relationships displayed; refine by selecting a neighbor.</p>}
        </div>}
        <details className="graph-limitations"><summary>What this graph cannot establish ({report.limitations.length} limitations)</summary>
          <ul>{report.limitations.map((l, i) => <li key={i}>{l}</li>)}</ul>
        </details>
      </>}
    </section>
  );
}

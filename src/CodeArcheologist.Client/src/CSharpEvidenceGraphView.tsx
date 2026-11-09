import { useEffect, useMemo, useState } from 'react';

type GraphNode = {
  id: string;
  kind: string;
  label: string;
  filePath: string | null;
  fileSha256: string | null;
  line: number | null;
  column: number | null;
  detail: string | null;
};

type GraphEdge = {
  id: string;
  fromId: string;
  toId: string;
  kind: string;
  status: string;
  projectContextStatus: string | null;
  evidencePath: string;
  evidenceSha256: string;
  line: number | null;
  column: number | null;
  explanation: string;
};

type EvidenceGraphReport = {
  snapshotId: string;
  fingerprintSha256: string;
  analyzerVersion: string;
  linkContextAnalyzerVersion: string;
  projectAnalyzerVersion: string;
  nodeCount: number;
  edgeCount: number;
  nodes: GraphNode[];
  edges: GraphEdge[];
  limitations: string[];
};

type AdjacentEvidence = { edge: GraphEdge; other: GraphNode; outgoing: boolean };

function Short({ value, length = 24 }: { value: string; length?: number }) {
  return <>{value.length > length ? value.slice(0, length - 1) + '…' : value}</>;
}

function EvidenceAddress({ path, hash, line, column }: {
  path: string; hash: string; line: number | null; column: number | null;
}) {
  return (
    <code>
      {path}{line === null ? '' : ':' + line}{column === null ? '' : ':' + column}
      {' '}· SHA-256 {hash.slice(0, 16)}…
    </code>
  );
}

export function CSharpEvidenceGraphView({ endpoint }: { endpoint: string }) {
  const [report, setReport] = useState<EvidenceGraphReport | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [selectedId, setSelectedId] = useState('');
  const [search, setSearch] = useState('');
  const [kind, setKind] = useState('all');

  useEffect(() => {
    const controller = new AbortController();
    async function load() {
      setLoading(true);
      setError('');
      setReport(null);
      setSelectedId('');
      try {
        const response = await fetch(endpoint, {
          credentials: 'same-origin', cache: 'no-store', signal: controller.signal,
        });
        if (!response.ok) {
          const body: { error?: string } = await response.json().catch(() => ({}));
          throw new Error(body.error ?? 'Evidence graph failed (HTTP ' + response.status + ')');
        }
        const data: EvidenceGraphReport = await response.json();
        if (!controller.signal.aborted) {
          setReport(data);
          setSelectedId(data.nodes.find(node => node.kind === 'project-file')?.id ??
            data.nodes.find(node => node.kind === 'csharp-file')?.id ??
            data.nodes[0]?.id ?? '');
        }
      } catch (cause) {
        if (!controller.signal.aborted)
          setError(cause instanceof Error ? cause.message : 'Evidence graph unavailable.');
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    }
    void load();
    return () => controller.abort();
  }, [endpoint]);

  const nodesById = useMemo(() => new Map(
    (report?.nodes ?? []).map(node => [node.id, node])), [report]);

  const selectedNode = nodesById.get(selectedId);
  const allNeighbors = useMemo((): AdjacentEvidence[] => {
    if (!report || !selectedId) return [];
    return report.edges.flatMap(edge => {
      if (edge.fromId !== selectedId && edge.toId !== selectedId) return [];
      const outgoing = edge.fromId === selectedId;
      const other = nodesById.get(outgoing ? edge.toId : edge.fromId);
      return other ? [{ edge, other, outgoing }] : [];
    });
  }, [report, selectedId, nodesById]);

  const visibleNodes = useMemo(() => (report?.nodes ?? []).filter(node =>
    (kind === 'all' || node.kind === kind) &&
    (search.trim() === '' ||
      (node.label + ' ' + (node.filePath ?? '') + ' ' + node.kind)
        .toLocaleLowerCase().includes(search.trim().toLocaleLowerCase()))),
  [report, kind, search]);

  const diagramNeighbors = Array.from(
    new Map(allNeighbors.map(item => [item.other.id, item])).values()
  ).slice(0, 8);
  const center = { x: 300, y: 170 };
  const positions = diagramNeighbors.map((item, index) => {
    const angle = (index / Math.max(1, diagramNeighbors.length)) * Math.PI * 2 - Math.PI / 2;
    return {
      ...item,
      x: center.x + Math.cos(angle) * 195,
      y: center.y + Math.sin(angle) * 108,
    };
  });

  return (
    <section className="snapshot-manifest syntax-view evidence-graph" aria-label="C# evidence graph explorer">
      <h4>C# evidence graph explorer</h4>
      {loading && <p role="status">Constructing the saved-snapshot evidence graph…</p>}
      {error && <p role="alert" className="error">{error}</p>}
      {report && (
        <>
          <p>{report.nodeCount} evidence nodes · {report.edgeCount} typed edges</p>
          <p className="workspace-note">
            {report.analyzerVersion}. Every project membership is a candidate;
            every type reference is provisional or unresolved. This is not a verified dependency graph.
          </p>
          <div className="graph-controls">
            <label>
              Find graph nodes
              <input
                value={search}
                onChange={event => setSearch(event.target.value)}
                placeholder="Search project, file, or declaration"
              />
            </label>
            <label>
              Node kind
              <select value={kind} onChange={event => setKind(event.target.value)}>
                <option value="all">All node kinds</option>
                <option value="project-file">Project files</option>
                <option value="csharp-file">C# source files</option>
                <option value="target-declaration">Referenced declarations</option>
                <option value="unresolved-use">Unresolved occurrences</option>
              </select>
            </label>
          </div>
          <div className="graph-explorer-layout">
            <div className="graph-node-picker" aria-label="Select a graph node">
              <strong>Nodes ({visibleNodes.length})</strong>
              {visibleNodes.length === 0 && <p>No matching nodes.</p>}
              {visibleNodes.slice(0, 150).map(node => (
                <button
                  type="button"
                  key={node.id}
                  className={node.id === selectedId ? 'graph-selected' : ''}
                  aria-pressed={node.id === selectedId}
                  onClick={() => setSelectedId(node.id)}
                >
                  <span>{node.label}</span>
                  <small>{node.kind}</small>
                </button>
              ))}
              {visibleNodes.length > 150 && (
                <p className="workspace-note">
                  Showing 150 matching nodes; narrow your search to find others.
                </p>
              )}
            </div>
            <div className="graph-detail">
              {!selectedNode && <p>Select a graph node to inspect its relationships.</p>}
              {selectedNode && (
                <>
                  <h4>{selectedNode.label}</h4>
                  <p className="workspace-note">
                    {selectedNode.kind} · {selectedNode.detail ?? 'Captured evidence'}
                  </p>
                  {selectedNode.filePath && selectedNode.fileSha256 && (
                    <EvidenceAddress
                      path={selectedNode.filePath}
                      hash={selectedNode.fileSha256}
                      line={selectedNode.line}
                      column={selectedNode.column}
                    />
                  )}
                  {allNeighbors.length > 0 && (
                    <svg
                      className="graph-neighborhood"
                      viewBox="0 0 600 340"
                      role="img"
                      aria-label="Diagram of selected evidence node and up to eight connected neighbors"
                    >
                      {positions.map(item => (
                        <line
                          key={item.edge.id}
                          x1={item.outgoing ? center.x : item.x}
                          y1={item.outgoing ? center.y : item.y}
                          x2={item.outgoing ? item.x : center.x}
                          y2={item.outgoing ? item.y : center.y}
                          stroke={item.edge.status === 'observed-location' ? '#7aa29c' : '#c5a36c'}
                          strokeWidth="2"
                          strokeDasharray={item.edge.status === 'observed-location' ? undefined : '5 5'}
                        />
                      ))}
                      {positions.map(item => (
                        <g key={item.other.id}>
                          <circle cx={item.x} cy={item.y} r="23" fill="#324656" stroke="#9fb3c1" strokeWidth="1.5" />
                          <text x={item.x} y={item.y + 5} textAnchor="middle" fontSize="12" fill="#fff">
                            {item.other.kind === 'project-file' ? 'P' :
                              item.other.kind === 'csharp-file' ? 'F' :
                              item.other.kind === 'target-declaration' ? 'T' : '?'}
                          </text>
                          <text x={item.x} y={item.y + (item.y < center.y ? -32 : 39)}
                            textAnchor="middle" fontSize="10" fill="currentColor">
                            {item.other.label.length > 19
                              ? item.other.label.slice(0, 18) + '…'
                              : item.other.label}
                          </text>
                        </g>
                      ))}
                      <circle cx={center.x} cy={center.y} r="32" fill="#2e6575" stroke="#a0c3c9" strokeWidth="2" />
                      <text x={center.x} y={center.y + 5} textAnchor="middle" fontSize="15" fill="#fff">
                        {selectedNode.kind === 'project-file' ? 'P' :
                          selectedNode.kind === 'csharp-file' ? 'F' :
                          selectedNode.kind === 'target-declaration' ? 'T' : '?'}
                      </text>
                    </svg>
                  )}
                  {allNeighbors.length === 0 && (
                    <p>No observed relationships connect to this node in the current snapshot.</p>
                  )}
                  <h4>Connected evidence ({allNeighbors.length})</h4>
                  <ul className="syntax-declaration-list">
                    {allNeighbors.slice(0, 100).map(item => (
                      <li key={item.edge.id}>
                        <button type="button" onClick={() => setSelectedId(item.other.id)}>
                          {item.outgoing ? '→ ' : '← '}
                          <Short value={item.other.label} length={55} />
                        </button>
                        <span>
                          {item.edge.kind} · <strong>{item.edge.status}</strong>
                          {item.edge.projectContextStatus
                            ? ' · ' + item.edge.projectContextStatus : ''}
                        </span>
                        <EvidenceAddress
                          path={item.edge.evidencePath}
                          hash={item.edge.evidenceSha256}
                          line={item.edge.line}
                          column={item.edge.column}
                        />
                        <span>{item.edge.explanation}</span>
                      </li>
                    ))}
                  </ul>
                  {allNeighbors.length > 100 && (
                    <p className="workspace-note">
                      Showing the first 100 direct edges; full graph is available from the local API.
                    </p>
                  )}
                </>
              )}
            </div>
          </div>
          <details>
            <summary>Graph scope and limitations</summary>
            <ul>{report.limitations.map(item => <li key={item}>{item}</li>)}</ul>
          </details>
        </>
      )}
    </section>
  );
}

import { useEffect, useMemo, useRef, useState } from 'react';
import type { MouseEvent } from 'react';
import {
  Background, BackgroundVariant, Controls, MarkerType, MiniMap, Position, ReactFlow,
  ReactFlowProvider, useReactFlow,
} from '@xyflow/react';
import type { Edge as FlowEdge, Node as FlowNode } from '@xyflow/react';
import * as dagre from '@dagrejs/dagre';
import '@xyflow/react/dist/style.css';
import type { ExplorerSelection } from './DeterministicExplorer.tsx';
import type { EvidenceGraph, GraphNode } from './evidenceGraph.ts';
import { validateGraph } from './evidenceGraph.ts';
import {
  atlasFocus, atlasOverview, evidenceStyle, findAtlasNodes, indexAtlas,
  ATLAS_EVIDENCE_STYLES, ATLAS_SEARCH_RESULTS,
} from './atlasModel.ts';
import type { AtlasProjection } from './atlasModel.ts';

type EvidenceLocation = { filePath: string | null; fileSha256: string | null; line: number | null; column: number | null };
type Props = { selection: ExplorerSelection;
  onInspect: (anchor: EvidenceLocation) => void; onMethod: (methodId: string) => void };
type AtlasRecord = { key: string; api: NonNullable<ExplorerSelection>['api']; graph: EvidenceGraph };
const NODE_W = 216;
const NODE_H = 74;
const MAX_EXPANSIONS = 12;
const isMethod = (n: GraphNode) =>
  n.kind === 'syntax-declaration' && n.detail?.startsWith('method syntax declaration');
const canInspect = (item: EvidenceLocation) => Boolean(item.filePath && item.fileSha256 && item.line);
const clip = (value: string, max = 68) => value.length > max ? value.slice(0, max - 1) + '…' : value;

function nodeStyle(kind: string) {
  if (kind === 'project-file') return 'project';
  if (kind === 'csharp-file') return 'file';
  if (kind === 'syntax-declaration' || kind === 'target-declaration') return 'declaration';
  if (kind === 'unresolved-use') return 'unresolved';
  if (kind.startsWith('http-')) return 'http';
  if (kind === 'di-registration-candidate') return 'di';
  return 'other';
}

/** Dagre controls only placement. It never creates an evidence relationship. */
export function layoutAtlas(view: AtlasProjection): { nodes: FlowNode[]; edges: FlowEdge[] } {
  const layout = new dagre.graphlib.Graph({ multigraph: true });
  layout.setGraph({ rankdir: 'LR', ranksep: 105, nodesep: 38, marginx: 32, marginy: 28 });
  layout.setDefaultEdgeLabel(() => ({}));
  for (const node of view.nodes) layout.setNode(node.id, { width: NODE_W, height: NODE_H });
  for (const edge of view.edges) layout.setEdge(edge.fromId, edge.toId, {}, edge.id);
  dagre.layout(layout);
  return {
    nodes: view.nodes.map(node => {
      const pos = layout.node(node.id);
      return {
        id: node.id,
        type: 'default',
        position: { x: pos.x - NODE_W / 2, y: pos.y - NODE_H / 2 },
        sourcePosition: Position.Right,
        targetPosition: Position.Left,
        data: { label: clip(node.label, 65) },
        className: 'atlas-node atlas-node--' + nodeStyle(node.kind),
        ariaLabel: node.kind + ': ' + node.label,
        draggable: false,
        selectable: true,
      };
    }),
    edges: view.edges.map(edge => {
      const style = evidenceStyle(edge.status);
      return {
        id: edge.id, source: edge.fromId, target: edge.toId,
        type: 'smoothstep',
        label: undefined,
        markerEnd: { type: MarkerType.ArrowClosed, color: style.stroke, width: 17, height: 17 },
        style: { stroke: style.stroke, strokeWidth: 1.8,
          ...(style.dash ? { strokeDasharray: style.dash } : {}) },
        className: 'atlas-edge',
        data: { evidenceStatus: edge.status, kind: edge.kind },
      };
    }),
  };
}

/** Split out so useReactFlow stays under the provider. */
function AtlasCanvas({ projected, selectedId, selectedEdgeId, onNode, onEdge }: {
  projected: AtlasProjection;
  selectedId: string;
  selectedEdgeId: string;
  onNode: (id: string) => void;
  onEdge: (id: string) => void;
}) {
  const { fitView } = useReactFlow();
  const layout = useMemo(() => layoutAtlas(projected), [projected]);
  const visibleNodes = useMemo(() => layout.nodes.map(node => ({
    ...node, selected: node.id === selectedId,
  })), [layout, selectedId]);
  const visibleEdges = useMemo(() => layout.edges.map(edge => ({
    ...edge, selected: edge.id === selectedEdgeId,
  })), [layout, selectedEdgeId]);

  useEffect(() => {
    const frame = requestAnimationFrame(() => {
      void fitView({ padding: 0.2, duration: 260, maxZoom: 1.1 });
    });
    return () => cancelAnimationFrame(frame);
  }, [fitView, layout]);

  return <div className="atlas-viewport" aria-label="Interactive saved-source architecture canvas">
    <div className="atlas-canvas-toolbar">
      <span>{projected.mode === 'overview' ? 'Project / file overview' : 'Focused evidence neighborhood'}</span>
      <button type="button" onClick={() => void fitView({ padding: 0.2, duration: 250, maxZoom: 1.1 })}>Fit visible graph</button>
    </div>
    <ReactFlow
      nodes={visibleNodes} edges={visibleEdges}
      onNodeClick={(_event: MouseEvent, node: FlowNode) => onNode(node.id)}
      onEdgeClick={(_event: MouseEvent, edge: FlowEdge) => onEdge(edge.id)}
      nodesDraggable={false} nodesConnectable={false} edgesReconnectable={false}
      panOnDrag zoomOnScroll zoomOnPinch zoomOnDoubleClick={false}
      elementsSelectable fitView minZoom={0.03} maxZoom={2}
      proOptions={{ hideAttribution: false }}
    >
      <Background variant={BackgroundVariant.Dots} gap={22} size={1} color="#2c4561"/>
      <Controls showInteractive={false} position="bottom-left"/>
      <MiniMap nodeStrokeWidth={2} pannable zoomable maskColor="rgba(8,15,27,.64)"/>
    </ReactFlow>
  </div>;
}

export function ArchitectureAtlas({ selection, onInspect, onMethod }: Props) {
  const [loaded, setLoaded] = useState<AtlasRecord | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [mode, setMode] = useState<'overview' | 'focus'>('overview');
  const [centerId, setCenterId] = useState('');
  const [expandedIds, setExpandedIds] = useState<string[]>([]);
  const [selectedId, setSelectedId] = useState('');
  const [selectedEdgeId, setSelectedEdgeId] = useState('');
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState('all');
  const epoch = useRef(0);
  const scope = selection?.scope;
  const key = scope ? [scope.workspaceId, scope.repositoryId, scope.snapshotId].join('/') : '';

  useEffect(() => {
    epoch.current++;
    setLoaded(null); setLoading(false); setError('');
    setMode('overview'); setCenterId(''); setExpandedIds([]);
    setSelectedId(''); setSelectedEdgeId(''); setQuery(''); setKind('all');
  }, [selection?.api, key]);

  const graph = loaded?.api === selection?.api && loaded.key === key ? loaded.graph : null;
  const index = useMemo(() => graph ? indexAtlas(graph) : null, [graph]);
  const projection = useMemo(() => !index ? null : mode === 'overview' || !centerId
    ? atlasOverview(index) : atlasFocus(index, centerId, expandedIds),
  [index, mode, centerId, expandedIds]);
  const kindOptions = useMemo(() => graph
    ? [...new Set(graph.nodes.map(node => node.kind))].sort() : [], [graph]);
  const found = useMemo(() => index ? findAtlasNodes(index, query, kind) :
    { matches: [] as GraphNode[], total: 0 }, [index, query, kind]);
  const shownResults = useMemo(() => query.trim() || kind !== 'all'
    ? found.matches : found.matches.slice(0, 30), [found, query, kind]);
  const selectedNode = index?.nodeById.get(selectedId);
  const selectedEdge = graph?.edges.find(e => e.id === selectedEdgeId);
  const visibleIds = useMemo(() => new Set(projection?.nodes.map(n => n.id) ?? []), [projection]);

  async function load() {
    if (!selection || loading) return;
    const request = ++epoch.current;
    setLoading(true); setError('');
    try {
      // The independent snapshot record supplies the expected fingerprint,
      // even if C# entry-point or method enumeration is unavailable.
      const snapshot = await selection.api.getSnapshot(selection.scope);
      if (snapshot.id !== selection.scope.snapshotId ||
          snapshot.workspaceId !== selection.scope.workspaceId ||
          snapshot.repositoryId !== selection.scope.repositoryId)
        throw new Error('Atlas snapshot identity does not match the current selection.');
      const report = await selection.api.evidenceGraph(selection.scope);
      validateGraph(report, selection.scope, snapshot.fingerprintSha256);
      if (epoch.current === request) setLoaded({ key, api: selection.api, graph: report });
    } catch (cause) {
      if (epoch.current === request) setError(cause instanceof Error ? cause.message : String(cause));
    } finally {
      if (epoch.current === request) setLoading(false);
    }
  }

  function focusNode(id: string) {
    if (!index?.nodeById.has(id)) return;
    setMode('focus'); setCenterId(id); setExpandedIds([]);
    setSelectedId(id); setSelectedEdgeId('');
  }

  function resetOverview() {
    setMode('overview'); setCenterId(''); setExpandedIds([]);
    setSelectedId(''); setSelectedEdgeId('');
  }

  function expandSelected() {
    if (!selectedId || !visibleIds.has(selectedId) || mode !== 'focus' ||
        selectedId === centerId || expandedIds.includes(selectedId) ||
        expandedIds.length >= MAX_EXPANSIONS) return;
    setExpandedIds(values => [...values, selectedId]);
  }

  function chooseNode(id: string) { setSelectedId(id); setSelectedEdgeId(''); }
  function chooseEdge(id: string) { setSelectedEdgeId(id); setSelectedId(''); }

  return <section className="atlas-root" aria-label="Architecture Atlas">
    <header className="atlas-introduction">
      <div>
        <span className="eyebrow">READ-ONLY · C# SAVED SOURCE EVIDENCE</span>
        <h2>Architecture Atlas</h2>
        <p className="live-help">Navigate source-backed nodes and their recorded relationships. Positions are a visualization, not proof of architecture, effective routes, compilation, or execution.</p>
      </div>
      {selection && !graph && <button type="button" className="primary-action" disabled={loading}
        onClick={() => void load()}>{loading ? 'Verifying graph evidence…' : 'Load Architecture Atlas'}</button>}
    </header>
    {!selection && <p className="atlas-message">Select a workspace, repository, and saved snapshot in Agent Observatory → Live API first.</p>}
    {error && <div className="atlas-message" role="alert">
      <strong>Atlas unavailable for this snapshot.</strong>
      <p>{error}</p>
      <p>Snapshot Coverage remains independent of C# graph analysis. An unavailable graph is not an empty architecture.</p>
      {selection && <button type="button" className="secondary-action" onClick={() => void load()}>Retry verified graph</button>}
    </div>}
    {graph && index && projection && <>
      <div className="atlas-metrics" aria-label="Atlas scope and visible limits">
        <span><strong>{graph.nodeCount}</strong> saved nodes</span>
        <span><strong>{graph.edgeCount}</strong> saved edges</span>
        <span><strong>{projection.nodes.length} / {projection.eligibleNodeCount}</strong> visible / eligible nodes</span>
        <span><strong>{projection.edges.length} / {projection.eligibleEdgeCount}</strong> visible / eligible edges</span>
        {projection.limited && <strong className="atlas-limited">Display bounds active — some eligible evidence is not drawn</strong>}
      </div>
      <p className="atlas-provenance">Analyzer: {graph.analyzerVersion} · Fingerprint: <code>{graph.fingerprintSha256}</code></p>
      <div className="atlas-workspace">
        <aside className="atlas-navigation" aria-label="Atlas search and navigation">
          <h3>Explore saved evidence</h3>
          <div className="atlas-mode-controls">
            <button type="button" aria-pressed={mode === 'overview'} onClick={resetOverview}>Project / file overview</button>
            {mode === 'focus' && <button type="button" aria-pressed={true} disabled>Focused view</button>}
          </div>
          <label>Find node across entire loaded graph
            <input type="search" placeholder="Name, file, or declaration" value={query}
              onChange={event => setQuery(event.target.value)} />
          </label>
          <label>Node kind
            <select aria-label="Atlas node kind" value={kind} onChange={event => setKind(event.target.value)}>
              <option value="all">All kinds</option>
              {kindOptions.map(k => <option key={k} value={k}>{k}</option>)}
            </select>
          </label>
          <p className="atlas-search-count">{found.total} matching nodes; {shownResults.length} listed (maximum {ATLAS_SEARCH_RESULTS})</p>
          <div className="atlas-search-results" aria-label="Atlas node search results">
            {shownResults.map(node =>
              <button type="button" key={node.id} onClick={() => focusNode(node.id)}
                className={centerId === node.id ? 'is-focused' : ''}>
                <strong title={node.label}>{node.label}</strong>
                <small>{node.kind} · {node.filePath ?? 'No file anchor'}</small>
              </button>)}
          </div>
          <p className="live-help">Search includes declarations not visible on the overview. Select a result to center its exact direct relationships.</p>
        </aside>
        {projection.nodes.length === 0
          ? <div className="atlas-message atlas-empty-canvas" role="status">
              No project/file evidence nodes were recorded for this view. This is not proof that the repository has no architecture, APIs, or dependencies. Try another snapshot or inspect Snapshot Coverage.
            </div>
          : <ReactFlowProvider>
              <AtlasCanvas projected={projection} selectedId={selectedId} selectedEdgeId={selectedEdgeId}
                onNode={chooseNode} onEdge={chooseEdge}/>
            </ReactFlowProvider>}
        <aside className="atlas-details" aria-label="Atlas selection and evidence summary">
          <h3>{selectedEdge ? 'Recorded relationship' : selectedNode ? 'Saved graph node' : 'Explore a selection'}</h3>
          {selectedNode && <>
            <p className="atlas-selected-label">{selectedNode.label}</p>
            <p><strong>Kind:</strong> {selectedNode.kind}</p>
            <p><strong>Source:</strong> {selectedNode.filePath ? selectedNode.filePath + (selectedNode.line ? ':' + selectedNode.line : '') : 'No saved line anchor'}</p>
            <p><strong>Node ID:</strong> <code>{selectedNode.id}</code></p>
            <p className="live-help">{selectedNode.detail}</p>
            <button type="button" className="secondary-action" disabled={!canInspect(selectedNode)}
              onClick={() => onInspect(selectedNode)}>Inspect saved source</button>
            {isMethod(selectedNode) && <button type="button" className="secondary-action"
              onClick={() => onMethod(selectedNode.id)}>Inspect direct impact</button>}
            {mode === 'focus' && <>
              <button type="button" className="secondary-action" disabled={selectedId === centerId ||
                expandedIds.includes(selectedId) || expandedIds.length >= MAX_EXPANSIONS ||
                !visibleIds.has(selectedId)}
                onClick={expandSelected}>Expand this node's recorded neighbors</button>
              <button type="button" className="secondary-action" onClick={() => focusNode(selectedNode.id)}>Refocus here</button>
            </>}
            {mode === 'overview' && <button type="button" className="secondary-action"
              onClick={() => focusNode(selectedNode.id)}>Inspect direct relationships</button>}
          </>}
          {selectedEdge && <>
            <p className="atlas-selected-label">{selectedEdge.kind}</p>
            <p><strong>Status:</strong> {selectedEdge.status}</p>
            <p><strong>Direction:</strong> {selectedEdge.fromId} → {selectedEdge.toId}</p>
            <p><strong>Evidence:</strong> {selectedEdge.evidencePath}{selectedEdge.line ? ':' + selectedEdge.line : ''}</p>
            <p><strong>Edge ID:</strong> <code>{selectedEdge.id}</code></p>
            <p className="live-help">{selectedEdge.explanation}</p>
            <button type="button" className="secondary-action"
              disabled={!canInspect({ filePath: selectedEdge.evidencePath, fileSha256: selectedEdge.evidenceSha256, line: selectedEdge.line, column: selectedEdge.column })}
              onClick={() => onInspect({ filePath: selectedEdge.evidencePath, fileSha256: selectedEdge.evidenceSha256,
                line: selectedEdge.line, column: selectedEdge.column })}>Inspect edge source</button>
          </>}
          {!selectedNode && !selectedEdge && <p className="live-help">Click a visible node or edge, or search the full loaded evidence graph. A line shows the direction of a saved relationship, not necessarily runtime flow.</p>}
          <div className="atlas-legend">
            <h4>Evidence status legend</h4>
            {Object.entries(ATLAS_EVIDENCE_STYLES).map(([status, style]) =>
              <div key={status}><span style={{ borderTop: '2px ' + (style.dash ? 'dashed' : 'solid') + ' ' + style.stroke }} />
                <span>{style.label}</span></div>)}
            <small>Unknown status is shown gray/dotted; never upgraded to observed.</small>
          </div>
        </aside>
      </div>
      <p className="atlas-scope-note">{projection.explanation}</p>
      {mode === 'focus' && <p className="atlas-scope-note">Expansion seeds: {1 + expandedIds.length} of {1 + MAX_EXPANSIONS}. Only directly recorded edges are drawn. Select a neighbor before expanding it.</p>}
      <details className="graph-limitations"><summary>Graph limitations ({graph.limitations.length})</summary>
        <ul>{graph.limitations.map((statement, i) => <li key={i}>{statement}</li>)}</ul>
      </details>
    </>}
  </section>;
}

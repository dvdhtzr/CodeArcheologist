import { useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties, MouseEvent } from 'react';
import {
  Background, BackgroundVariant, Controls, MarkerType, MiniMap, Position, ReactFlow,
  ReactFlowProvider, useReactFlow,
} from '@xyflow/react';
import type { Edge as FlowEdge, Node as FlowNode, Viewport } from '@xyflow/react';
import * as dagre from '@dagrejs/dagre';
import '@xyflow/react/dist/style.css';
import type { ExplorerSelection } from './DeterministicExplorer.tsx';
import type { EvidenceGraph, GraphNode } from './evidenceGraph.ts';
import { AtlasEvidenceInspector } from './AtlasEvidenceInspector.tsx';
import { isAtlasViewport, navigateToAtlasNode, previousAtlasView } from './atlasEvidence.ts';
import { anchorAtlasLayout } from './atlasLayout.ts';
import type { AtlasCursor, AtlasViewport } from './atlasEvidence.ts';
import { validateGraph } from './evidenceGraph.ts';
import {
  atlasFocus, atlasOverview, evidenceStyle, findAtlasNodes, indexAtlas,
  ATLAS_EVIDENCE_STYLES, ATLAS_SEARCH_RESULTS,
} from './atlasModel.ts';
import type { AtlasProjection } from './atlasModel.ts';

type Props = { selection: ExplorerSelection; onMethod: (methodId: string) => void };
type AtlasRecord = { key: string; api: NonNullable<ExplorerSelection>['api']; graph: EvidenceGraph };
const NODE_W = 216;
const NODE_H = 74;
const MAX_EXPANSIONS = 12;
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
        // Explicit geometry allows the saved relationship path to render
        // before ResizeObserver measurement; these handles are display-only.
        width: NODE_W,
        height: NODE_H,
        handles: [
          { type: 'source' as const, position: Position.Right, x: NODE_W - 1, y: NODE_H / 2,
            width: 1, height: 1 },
          { type: 'target' as const, position: Position.Left, x: 0, y: NODE_H / 2,
            width: 1, height: 1 },
        ],
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

type AtlasViewportCommand = {
  serial: number; action: 'focus' | 'restore' | 'fit';
  nodeId?: string; viewport?: AtlasViewport | null;
};
const motionDuration = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 0 : 160;

/** ReactFlow owns the viewport. The parent records exact pre-navigation
 * viewport snapshots; only explicit navigation/fit actions move the camera.
 * Graph expansion and evidence selection NEVER automatically fit/recenter. */
function AtlasCanvas({ projected, centerId, selectedId, selectedEdgeId, command, onViewport, onNode, onEdge }: {
  projected: AtlasProjection;
  centerId: string;
  selectedId: string;
  selectedEdgeId: string;
  command: AtlasViewportCommand | null;
  onViewport: (viewport: AtlasViewport) => void;
  onNode: (id: string) => void;
  onEdge: (id: string) => void;
}) {
  const { fitView, setViewport, setCenter, getViewport } = useReactFlow();
  const previousPositions = useRef<Map<string, { x: number; y: number }>>(new Map());
  const appliedCommand = useRef(0);
  const layout = useMemo(() => {
    const computed = layoutAtlas(projected);
    const nodes = anchorAtlasLayout(computed.nodes, previousPositions.current, centerId);
    previousPositions.current = new Map(nodes.map(node => [node.id, node.position]));
    return { nodes, edges: computed.edges };
  }, [projected, centerId]);
  const visibleNodes = useMemo(() => layout.nodes.map(node => ({
    ...node, selected: node.id === selectedId,
  })), [layout, selectedId]);
  const visibleEdges = useMemo(() => layout.edges.map(edge => ({
    ...edge, selected: edge.id === selectedEdgeId,
  })), [layout, selectedEdgeId]);

  useEffect(() => {
    if (!command || command.serial <= appliedCommand.current) return;
    const frame = requestAnimationFrame(() => {
      appliedCommand.current = command.serial;
      if (command.action === 'restore' && isAtlasViewport(command.viewport)) {
        void setViewport(command.viewport, { duration: 0 });
      } else if (command.action === 'fit') {
        void fitView({ padding: 0.2, duration: motionDuration(), maxZoom: 1.1 });
      } else if (command.action === 'focus' && command.nodeId) {
        const node = layout.nodes.find(n => n.id === command.nodeId);
        if (node) {
          const zoom = Math.max(0.4, Math.min(1.1, getViewport().zoom));
          void setCenter(node.position.x + NODE_W / 2, node.position.y + NODE_H / 2,
            { zoom, duration: motionDuration() });
        }
      }
    });
    return () => cancelAnimationFrame(frame);
  }, [command, layout, fitView, getViewport, setViewport, setCenter]);

  return <div className="atlas-viewport" aria-label="Interactive saved-source architecture canvas">
    <div className="atlas-canvas-toolbar">
      <span>{projected.mode === 'overview' ? 'Project / file overview' : 'Focused evidence neighborhood'}</span>
      <button type="button" onClick={() => void fitView({
        padding: 0.2, duration: motionDuration(), maxZoom: 1.1,
      })}>Fit visible graph</button>
    </div>
    <ReactFlow
      nodes={visibleNodes} edges={visibleEdges}
      onNodeClick={(_event: MouseEvent, node: FlowNode) => onNode(node.id)}
      onEdgeClick={(_event: MouseEvent, edge: FlowEdge) => onEdge(edge.id)}
      onMoveEnd={(_event, viewport: Viewport) => onViewport(viewport)}
      nodesDraggable={false} nodesConnectable={false} edgesReconnectable={false}
      panOnDrag zoomOnScroll zoomOnPinch zoomOnDoubleClick={false}
      elementsSelectable fitView fitViewOptions={{ padding: 0.2, maxZoom: 1.1 }}
      minZoom={0.03} maxZoom={2}
      proOptions={{ hideAttribution: false }}
    >
      <Background variant={BackgroundVariant.Dots} gap={22} size={1} color="#2c4561"/>
      <Controls showInteractive={false} position="bottom-left"/>
      <MiniMap nodeStrokeWidth={2} pannable zoomable maskColor="rgba(8,15,27,.64)"/>
    </ReactFlow>
  </div>;
}

export function ArchitectureAtlas({ selection, onMethod }: Props) {
  const [loaded, setLoaded] = useState<AtlasRecord | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [mode, setMode] = useState<'overview' | 'focus'>('overview');
  const [centerId, setCenterId] = useState('');
  const [expandedIds, setExpandedIds] = useState<string[]>([]);
  const [selectedId, setSelectedId] = useState('');
  const [selectedEdgeId, setSelectedEdgeId] = useState('');
  const [history, setHistory] = useState<AtlasCursor[]>([]);
  const viewportRef = useRef<AtlasViewport | null>(null);
  const commandSerialRef = useRef(0);
  const [viewportCommand, setViewportCommand] = useState<AtlasViewportCommand | null>(null);
  const [navigationCollapsed, setNavigationCollapsed] = useState(false);
  const [inspectorWidth, setInspectorWidth] = useState(360);
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState('all');
  const epoch = useRef(0);
  const scope = selection?.scope;
  const key = scope ? [scope.workspaceId, scope.repositoryId, scope.snapshotId].join('/') : '';

  useEffect(() => {
    epoch.current++;
    setLoaded(null); setLoading(false); setError('');
    setMode('overview'); setCenterId(''); setExpandedIds([]);
    setSelectedId(''); setSelectedEdgeId(''); setHistory([]); setQuery(''); setKind('all');
    viewportRef.current = null; setViewportCommand(null); commandSerialRef.current = 0;
    setNavigationCollapsed(false); setInspectorWidth(360);
  }, [selection?.api, key]);

  const graph = loaded && loaded.api === selection?.api && loaded.key === key ? loaded.graph : null;
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
  const selectedEdge = index?.edgeById.get(selectedEdgeId);
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

  function requestViewport(command: Omit<AtlasViewportCommand, 'serial'>) {
    setViewportCommand({ ...command, serial: ++commandSerialRef.current });
  }

  function currentCursor(): AtlasCursor {
    return { mode, centerId, expandedIds, selectedId, selectedEdgeId,
      ...(isAtlasViewport(viewportRef.current) ? { viewport: { ...viewportRef.current } } : {}) };
  }

  function restore(cursor: AtlasCursor) {
    setMode(cursor.mode);
    setCenterId(cursor.centerId);
    setExpandedIds([...cursor.expandedIds]);
    setSelectedId(cursor.selectedId);
    setSelectedEdgeId(cursor.selectedEdgeId);
  }

  function focusNode(id: string) {
    if (!index) return;
    const step = navigateToAtlasNode(index, currentCursor(), history, id);
    setHistory(step.history);
    restore(step.current);
    requestViewport({ action: 'focus', nodeId: id });
  }

  function goBack() {
    const step = previousAtlasView(history);
    if (!step) return;
    setHistory(step.history);
    restore(step.current);
    if (isAtlasViewport(step.current.viewport))
      requestViewport({ action: 'restore', viewport: step.current.viewport });
  }

  function resetOverview() {
    setHistory([]);
    restore({ mode: 'overview', centerId: '', expandedIds: [], selectedId: '', selectedEdgeId: '' });
    requestViewport({ action: 'fit' });
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
      <div className="atlas-display-controls" aria-label="Atlas display controls">
        <button type="button" aria-expanded={!navigationCollapsed} aria-controls="atlas-search-panel"
          onClick={() => setNavigationCollapsed(value => !value)}>
          {navigationCollapsed ? 'Show search panel' : 'Hide search panel'}
        </button>
        <label htmlFor="atlas-inspector-width">Evidence panel width: <output>{inspectorWidth}px</output></label>
        <input id="atlas-inspector-width" aria-label="Evidence panel width" type="range"
          min={280} max={540} step={20} value={inspectorWidth}
          onChange={event => setInspectorWidth(Number(event.target.value))}/>
      </div>
      <div className={navigationCollapsed ? 'atlas-workspace atlas-workspace--search-hidden' : 'atlas-workspace'}
        style={{ '--atlas-inspector-width': inspectorWidth + 'px' } as CSSProperties}>
        <aside id="atlas-search-panel" className="atlas-navigation"
          aria-label="Atlas search and navigation" hidden={navigationCollapsed}>
          <h3>Explore saved evidence</h3>
          <div className="atlas-mode-controls">
            <button type="button" disabled={history.length === 0} onClick={goBack}
              aria-label="Back to previous Atlas view">Back</button>
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
              <AtlasCanvas projected={projection} centerId={centerId}
                selectedId={selectedId} selectedEdgeId={selectedEdgeId}
                command={viewportCommand}
                onViewport={viewport => { if (isAtlasViewport(viewport)) viewportRef.current = { ...viewport }; }}
                onNode={chooseNode} onEdge={chooseEdge}/>
            </ReactFlowProvider>}
        <aside className="atlas-details" aria-label="Atlas selection and evidence summary">
          {selection && <AtlasEvidenceInspector
            key={selectedEdge ? 'edge:' + selectedEdge.id : selectedNode ? 'node:' + selectedNode.id : 'unselected'}
            selection={selection} fingerprint={graph.fingerprintSha256}
            index={index} node={selectedNode} edge={selectedEdge}
            mode={mode} centerId={centerId} expandedIds={expandedIds} visibleIds={visibleIds}
            onNavigate={focusNode} onExpand={expandSelected} onImpact={onMethod}/>}
          {projection.edges.length > 0 && <div className="atlas-relationships" aria-label="Drawn evidence relationships">
            <h4>Drawn relationships ({projection.edges.length})</h4>
            <p className="live-help">These are exact recorded edges, not derived paths. Select one to inspect its evidence even if the diagram is zoomed out.</p>
            {projection.edges.slice(0, 24).map(edge =>
              <button type="button" key={edge.id} aria-pressed={selectedEdgeId === edge.id}
                onClick={() => chooseEdge(edge.id)}>
                <strong>{edge.kind} · {edge.status}</strong>
                <small>{index.nodeById.get(edge.fromId)?.label ?? edge.fromId} → {index.nodeById.get(edge.toId)?.label ?? edge.toId}</small>
              </button>)}
            {projection.edges.length > 24 && <p className="live-help">Showing the first 24 of {projection.edges.length} drawn relationships. No edges have been synthesized.</p>}
          </div>}
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

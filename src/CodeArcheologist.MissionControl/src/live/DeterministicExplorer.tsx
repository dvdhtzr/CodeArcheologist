import { useEffect, useMemo, useRef, useState } from 'react';
import type { ApiClient, Scope, SourceView } from './api.ts';
import { EvidenceGraphExplorer } from './EvidenceGraphExplorer.tsx';
import { ArchitectureAtlas } from './ArchitectureAtlas.tsx';
import { SnapshotCoverage } from './SnapshotCoverage.tsx';

export type ExplorerSelection = { api: ApiClient; scope: Scope } | null;
type Anchor = { filePath: string | null; fileSha256: string | null; line: number | null; column: number | null };
type Entry = { id: string; kind: string; evidenceStatus: string; label: string; detail: string | null; anchor: Anchor };
type Method = { id: string; label: string; detail: string | null; anchor: Anchor; inboundProvisionalCandidates: number };
type List<T> = { contractVersion: string; snapshotId: string; fingerprintSha256: string; count: number; truncated: boolean; items: T[]; caveat: string };
type Impact = {
  contractVersion: string; snapshotId: string; fingerprintSha256: string;
  method: Method; directCandidates: Array<{
    calleeEdgeId: string; evidenceStatus: string; invocationAnchor: Anchor;
    callerLabel: string; callerKind: string; callerAnchor: Anchor;
  }>;
  candidateEdgesWithoutObservedContainment: number;
  snapshotUnresolvedCalleeCount: number; unknowns: string[]; caveat: string;
};
const VERSION = 'code-archeologist-capabilities-v1';

export function DeterministicExplorer({ selection }: { selection: ExplorerSelection }) {
  const [entries, setEntries] = useState<List<Entry> | null>(null);
  const [methods, setMethods] = useState<List<Method> | null>(null);
  const [selectedMethod, setSelectedMethod] = useState('');
  const [view, setView] = useState<'atlas' | 'inventory' | 'graph' | 'coverage'>('atlas');
  const impactFocus = useRef<HTMLDivElement>(null);
  const [search, setSearch] = useState('');
  const [impact, setImpact] = useState<Impact | null>(null);
  const [source, setSource] = useState<SourceView | null>(null);
  const [error, setError] = useState('');
  const [sourceError, setSourceError] = useState('');
  const [loading, setLoading] = useState(false);
  const [impactLoading, setImpactLoading] = useState(false);
  const sourceEpoch = useRef(0);
  const scope = selection?.scope;
  const key = scope ? [scope.workspaceId, scope.repositoryId, scope.snapshotId].join('/') : '';

  useEffect(() => {
    let active = true;
    setEntries(null); setMethods(null); setSelectedMethod('');
    sourceEpoch.current++;
    setImpact(null); setSource(null); setError(''); setSourceError(''); setSearch('');
    if (!selection) return;
    setLoading(true);
    Promise.all([selection.api.listEntryPoints(selection.scope), selection.api.listMethods(selection.scope)])
      .then(([entryResult, methodResult]) => {
        if (!active) return;
        if (entryResult.contractVersion !== VERSION || methodResult.contractVersion !== VERSION ||
          entryResult.snapshotId !== selection.scope.snapshotId || methodResult.snapshotId !== selection.scope.snapshotId ||
          entryResult.fingerprintSha256 !== methodResult.fingerprintSha256) {
          throw new Error('Capability response does not match the selected verified snapshot.');
        }
        setEntries(entryResult); setMethods(methodResult);
      })
      .catch(e => { if (active) setError(e instanceof Error ? e.message : String(e)); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [selection?.api, key]);

  useEffect(() => {
    let active = true;
    setImpact(null);
    if (!selection || !selectedMethod) return;
    setImpactLoading(true);
    selection.api.impact(selection.scope, selectedMethod)
      .then(result => {
        if (!active) return;
        if (result.contractVersion !== VERSION || result.snapshotId !== selection.scope.snapshotId ||
          result.method.id !== selectedMethod || result.fingerprintSha256 !== methods?.fingerprintSha256)
          throw new Error('Impact response does not match the selected method or snapshot.');
        setImpact(result);
      })
      .catch(e => { if (active) setError(e instanceof Error ? e.message : String(e)); })
      .finally(() => { if (active) setImpactLoading(false); });
    return () => { active = false; };
  }, [selection?.api, key, selectedMethod, methods?.fingerprintSha256]);

  useEffect(() => {
    if (view === 'inventory' && selectedMethod && impact) {
      impactFocus.current?.scrollIntoView({ block: 'start', behavior: 'smooth' });
    }
  }, [view, selectedMethod, impact]);

  const filtered = useMemo(() => (methods?.items ?? [])
    .filter(m => (m.label + ' ' + (m.anchor.filePath ?? '')).toLowerCase().includes(search.toLowerCase()))
    .slice(0, 100), [methods, search]);

  async function inspect(anchor: Anchor) {
    if (!selection || !anchor.filePath || !anchor.fileSha256 || !anchor.line) return;
    const epoch = ++sourceEpoch.current;
    setSource(null); setSourceError('');
    try {
      const result = await selection.api.source(selection.scope, anchor.filePath, anchor.fileSha256, anchor.line, 8);
      if (epoch !== sourceEpoch.current) return;
      if (result.filePath !== anchor.filePath || result.fileSha256 !== anchor.fileSha256)
        throw new Error('Source response does not match the saved file.');
      setSource(result);
    } catch (e) { if (epoch === sourceEpoch.current) setSourceError(e instanceof Error ? e.message : String(e)); }
  }

  function location(anchor: Anchor) {
    return anchor.filePath ? anchor.filePath + (anchor.line ? ':' + anchor.line : '') : 'No source location';
  }

  return (
    <section className="deterministic-explorer" aria-label="Deterministic architecture explorer">
      <span className="eyebrow eyebrow--accent">SAVED SOURCE · NOT AI</span>
      <h1>Explore the code you captured.</h1>
      <p className="secondary-intro">Source-written observations and one-hop provisional relationships. This is not a verified runtime architecture or complete call graph.</p>
      <div className="graph-tabs" role="group" aria-label="Architecture explorer views">
        <button type="button" aria-pressed={view === 'atlas'} onClick={() => setView('atlas')}>Architecture Atlas</button>
        <button type="button" aria-pressed={view === 'inventory'} onClick={() => setView('inventory')}>Candidates &amp; direct impact</button>
        <button type="button" aria-pressed={view === 'graph'} onClick={() => setView('graph')}>Evidence graph</button>
        <button type="button" aria-pressed={view === 'coverage'} onClick={() => setView('coverage')}>Snapshot coverage</button>
      </div>
      {!selection && <div className="secondary-panel">
        <h2>Choose a snapshot</h2>
        <p>Open Agent Observatory → Live API, connect, and select a workspace, repository and saved snapshot. The same selection powers this explorer and your investigations.</p>
      </div>}
      {view !== 'coverage' && view !== 'atlas' && loading && <p role="status">Loading verified snapshot capabilities…</p>}
      {view !== 'coverage' && view !== 'atlas' && error && <p className="live-error" role="alert">{error}</p>}
      {view === 'atlas' && <ArchitectureAtlas selection={selection}
        onInspect={anchor => void inspect(anchor)}
        onMethod={id => { setSelectedMethod(id); setView('inventory'); }} />}
      {view === 'coverage' && <SnapshotCoverage selection={selection} />}
      {view === 'graph' && <EvidenceGraphExplorer selection={selection}
        fingerprint={entries?.fingerprintSha256 ?? null}
        onInspect={anchor => void inspect(anchor)}
        onMethod={id => { setSelectedMethod(id); setView('inventory'); }} />}
      {view === 'inventory' && entries && methods && <div className="secondary-panel">
        <div className="deterministic-summary">
          <div><strong>{entries.count}</strong><span>Written HTTP candidates</span></div>
          <div><strong>{methods.count}</strong><span>Parsed C# methods</span></div>
          <div><strong>{methods.items.filter(m => m.inboundProvisionalCandidates > 0).length}</strong><span>Listed methods with incoming candidates</span></div>
        </div>
        {(entries.truncated || methods.truncated) && <p className="live-help">Results are capped by the API. Counts reflect the reported total; displayed methods and entry points may be incomplete.</p>}
        <h2>HTTP entry-point candidates</h2>
        <p className="live-help">{entries.caveat}</p>
        {entries.items.length === 0 && <p>No supported written candidates found; this does not establish the absence of endpoints.</p>}
        <ul className="deterministic-list">
          {entries.items.slice(0, 100).map(e => <li key={e.id}>
            <div><strong>{e.label}</strong><small>{e.kind} · {e.evidenceStatus}</small></div>
            <span>{location(e.anchor)}</span>
            {e.anchor.filePath && e.anchor.fileSha256 && e.anchor.line && <button type="button" onClick={() => void inspect(e.anchor)}>Inspect saved source</button>}
          </li>)}
        </ul>
        {entries.items.length > 100 && <p className="live-help">Showing the first 100 entry-point candidates.</p>}
        <h2>Method declarations and direct impact</h2>
        <p className="live-help">{methods.caveat} Select a method to inspect potential direct callers; missing candidates do not imply safety.</p>
        <label className="deterministic-search">Find method or source path
          <input type="search" value={search} onChange={e => setSearch(e.target.value)} placeholder="Method name or captured path" />
        </label>
        <div className="deterministic-methods">
          <ul className="deterministic-list">
            {filtered.map(m => <li key={m.id}>
              <button type="button" aria-pressed={selectedMethod === m.id} onClick={() => { setError(''); setSelectedMethod(m.id); }}>{m.label}</button>
              <small>{m.inboundProvisionalCandidates} inbound candidate(s) · {location(m.anchor)}</small>
            </li>)}
          </ul>
        </div>
        {filtered.length === 0 && <p>No matching listed methods.</p>}
        {methods.items.length > 100 && <p className="live-help">Showing at most 100 matching methods; narrow the search to find others.</p>}
        {impactLoading && <p role="status">Inspecting direct impact candidates…</p>}
        {impact && <div className="deterministic-impact" ref={impactFocus} tabIndex={-1}>
          <h2>Direct impact candidates: {impact.method.label}</h2>
          <p className="live-help">{impact.caveat}</p>
          <button type="button" disabled={!impact.method.anchor.filePath || !impact.method.anchor.fileSha256 || !impact.method.anchor.line} onClick={() => void inspect(impact.method.anchor)}>Inspect selected method source</button>
          {impact.directCandidates.length === 0 && <p>No attributable provisional callers were found. This does not establish safety.</p>}
          <ul className="deterministic-list">{impact.directCandidates.slice(0, 100).map(c => <li key={c.calleeEdgeId}>
            <strong>{c.callerLabel}</strong><small>{c.callerKind} · {c.evidenceStatus} · {location(c.invocationAnchor)}</small>
            <button type="button" disabled={!c.invocationAnchor.filePath || !c.invocationAnchor.fileSha256 || !c.invocationAnchor.line} onClick={() => void inspect(c.invocationAnchor)}>Inspect invocation source</button>
          </li>)}</ul>
          {impact.directCandidates.length > 100 && <p className="live-help">Showing the first 100 candidates.</p>}
          <p>{impact.candidateEdgesWithoutObservedContainment} candidate edges without observed caller containment · {impact.snapshotUnresolvedCalleeCount} unresolved invocation targets across this snapshot.</p>
          <ul>{impact.unknowns.map(u => <li key={u}>{u}</li>)}</ul>
        </div>}
      </div>}
      {sourceError && <p role="alert" className="live-error">{sourceError}</p>}
      {source && <div className="secondary-panel deterministic-source">
        <div className="panel-heading"><h2>Verified saved source</h2><button type="button" className="secondary-action" onClick={() => setSource(null)}>Close source</button></div>
        <p>{source.filePath} · lines {source.startLine}–{source.endLine} · SHA-256 {source.fileSha256}</p>
        <pre>{source.lines.map(l => String(l.number).padStart(5, ' ') + '  ' + l.text + (l.truncated ? ' …[truncated]' : '')).join('\n')}</pre>
        <p className="live-help">{source.caveat}</p>
      </div>}
    </section>
  );
}

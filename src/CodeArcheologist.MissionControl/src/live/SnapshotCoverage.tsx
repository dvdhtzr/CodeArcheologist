import { useEffect, useRef, useState } from 'react';
import type { ExplorerSelection } from './DeterministicExplorer.tsx';
import type { SnapshotCoverage as Coverage } from './snapshotCoverage.ts';
import { verifySnapshotCoverage } from './snapshotCoverage.ts';

const PREVIEW_LIMIT = 100;

/** Deterministic scope coverage works even when graph analysis fails or exceeds limits. */
export function SnapshotCoverage({ selection }: { selection: ExplorerSelection }) {
  const [result, setResult] = useState<{ key: string; api: ExplorerSelection; data: Coverage } | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [filter, setFilter] = useState('');
  const epoch = useRef(0);
  const key = selection?.scope
    ? [selection.scope.workspaceId, selection.scope.repositoryId, selection.scope.snapshotId].join('/')
    : '';

  useEffect(() => {
    epoch.current++;
    setResult(null);
    setLoading(false);
    setError('');
    setFilter('');
  }, [key, selection?.api]);

  const coverage = result?.key === key && result.api?.api === selection?.api ? result.data : null;

  async function load() {
    if (!selection || loading) return;
    const request = ++epoch.current;
    const client = selection.api;
    const scope = selection.scope;
    setLoading(true);
    setError('');
    try {
      const [snapshot, manifest, inventory] = await Promise.all([
        client.getSnapshot(scope), client.manifest(scope), client.inventory(scope),
      ]);
      const data = verifySnapshotCoverage(scope, snapshot, manifest, inventory);
      if (epoch.current === request) setResult({ key, api: selection, data });
    } catch (e) {
      if (epoch.current === request) setError(e instanceof Error ? e.message : String(e));
    } finally {
      if (epoch.current === request) setLoading(false);
    }
  }

  const files = coverage?.inventory.files.filter(f =>
    (f.path + ' ' + f.language + ' ' + f.role).toLowerCase().includes(filter.toLowerCase())) ?? [];
  const exclusions = coverage?.inventory.exclusions ?? [];

  return <section className="snapshot-coverage secondary-panel" aria-label="Saved snapshot coverage">
    <span className="eyebrow">VERIFIED SNAPSHOT · FILENAME-ONLY INVENTORY</span>
    <h2>What was actually captured?</h2>
    <p className="live-help">See included files, exclusions and source classifications before interpreting any graph or agent report. A filename classification is a heuristic, not a parser result or proof of architecture. Excluded directories were not traversed.</p>
    {!selection && <p>Select a saved snapshot under Agent Observatory → Live API.</p>}
    {selection && !coverage && <button type="button" className="primary-action" disabled={loading} onClick={() => void load()}>
      {loading ? 'Verifying saved capture…' : 'Load snapshot coverage'}
    </button>}
    {error && <p className="live-error" role="alert">{error}</p>}
    {coverage && <>
      <div className="deterministic-summary">
        <div><strong>{coverage.snapshot.fileCount}</strong><span>Captured files</span></div>
        <div><strong>{coverage.snapshot.exclusionCount}</strong><span>Excluded entries</span></div>
        <div><strong>{coverage.snapshot.totalBytes.toLocaleString()}</strong><span>Captured bytes</span></div>
      </div>
      <p className="live-help">Saved fingerprint: <code>{coverage.snapshot.fingerprintSha256}</code></p>
      <p className="live-help">Manifest digest: <code>{coverage.snapshot.manifestSha256}</code> · capture policy: {coverage.manifest.policyVersion} · source kind: {coverage.manifest.sourceKind} · revision label: {coverage.manifest.revisionKind}</p>
      <p className="live-help">Classification method: <strong>{coverage.inventory.detectionMethod}</strong> · {coverage.inventory.analyzerVersion}. The API verified saved manifest and file bytes; this does not authenticate an upstream Git commit or prove complete source coverage.</p>
      <div className="coverage-groups">
        <section aria-label="Probable languages"><h3>Probable languages</h3>
          {coverage.inventory.languages.map(x => <div className="coverage-count" key={x.name}><span>{x.name}</span><strong>{x.fileCount}</strong></div>)}
        </section>
        <section aria-label="Likely file roles"><h3>Likely file roles</h3>
          {coverage.inventory.roles.map(x => <div className="coverage-count" key={x.name}><span>{x.name}</span><strong>{x.fileCount}</strong></div>)}
        </section>
      </div>
      <details open className="coverage-detail">
        <summary>Included files ({coverage.inventory.files.length})</summary>
        <label className="deterministic-search">Filter captured files
          <input type="search" placeholder="File path, language, or role" value={filter} onChange={event => setFilter(event.target.value)}/>
        </label>
        <p className="live-help">Showing {Math.min(PREVIEW_LIMIT, files.length)} of {files.length} matching saved files. This display is capped; counts are from the verified manifest.</p>
        <ul className="coverage-files">{files.slice(0, PREVIEW_LIMIT).map(file => <li key={file.path}>
          <strong>{file.path}</strong>
          <span>{file.language} · {file.role} · {file.sizeBytes.toLocaleString()} bytes</span>
          <code>SHA-256 {file.sha256}</code>
        </li>)}</ul>
        {files.length === 0 && <p>No matching captured files.</p>}
      </details>
      <details className="coverage-detail">
        <summary>Excluded entries ({exclusions.length})</summary>
        <p className="live-help">Exclusions are paths and reasons recorded during capture. Ignored subtrees were not scanned; these records do not identify every file beneath them.</p>
        <ul className="coverage-files">{exclusions.slice(0, PREVIEW_LIMIT).map(x =>
          <li key={x.path + ':' + x.reason}><strong>{x.path}</strong><span>{x.reason}</span></li>)}</ul>
        {exclusions.length > PREVIEW_LIMIT && <p>Showing the first {PREVIEW_LIMIT} exclusions. The capture recorded {exclusions.length}.</p>}
        {exclusions.length === 0 && <p>No exclusions recorded by this snapshot policy; this is not a secret-scanning guarantee.</p>}
      </details>
      <details className="coverage-detail">
        <summary>Limitations and unexamined areas ({coverage.inventory.limitations.length})</summary>
        <ul>{coverage.inventory.limitations.map((x,i)=><li key={i}>{x}</li>)}</ul>
      </details>
    </>}
  </section>;
}

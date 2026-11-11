import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { ApiClient, ApiError, eligibleContinuation } from './api.ts';
import type { EntryPoint, Repository, RunSummary, Scope, Snapshot, Workspace } from './api.ts';
import { LiveRun } from './realtime.ts';
import type { ExplorerSelection } from './DeterministicExplorer.tsx';
import { launchMatchesSelection } from './questionJourney.ts';
import type { AtlasQuestionLaunch } from './questionJourney.ts';
import type { LiveSnapshot } from './realtime.ts';

export type LiveView = { api: ApiClient; scope: Scope; runId: string; entryPoint: string; snapshot: LiveSnapshot };

/**
 * MC3 connection and command panel. Every command is an explicit button
 * press sent over REST; the live view is fed by LiveRun (SignalR + REST).
 */
export function LiveConsole({ onView, onConnected, onFindings, onSelection, externalRun }: {
  onView: (view: LiveView | null) => void;
  onConnected: (connected: boolean) => void;
  onFindings: () => void;
  onSelection: (selection: ExplorerSelection) => void;
  externalRun: AtlasQuestionLaunch | null;
}) {
  const [address, setAddress] = useState('http://127.0.0.1:5127');
  const [code, setCode] = useState('');
  const [api, setApi] = useState<ApiClient | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [workspaces, setWorkspaces] = useState<Workspace[]>([]);
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [snapshots, setSnapshots] = useState<Snapshot[]>([]);
  const [entryPoints, setEntryPoints] = useState<EntryPoint[]>([]);
  const [entryCaveat, setEntryCaveat] = useState('');
  const [runs, setRuns] = useState<RunSummary[]>([]);
  const [workspaceId, setWorkspaceId] = useState('');
  const [repositoryId, setRepositoryId] = useState('');
  const [snapshotId, setSnapshotId] = useState('');
  const [entryPointId, setEntryPointId] = useState('');
  const [newWorkspace, setNewWorkspace] = useState('');
  const [newFolder, setNewFolder] = useState('');
  const [runId, setRunId] = useState('');
  const [live, setLive] = useState<LiveSnapshot | null>(null);
  const [editing, setEditing] = useState(false);
  const liveRun = useRef<LiveRun | null>(null);
  const sessionSubscription = useRef<(() => void) | null>(null);
  const continuationKey = useRef<{ runId: string; expected: number; key: string } | null>(null);

  const scope: Scope | null = workspaceId && repositoryId && snapshotId ? { workspaceId, repositoryId, snapshotId } : null;
  // One shared explicit scope for investigations and deterministic exploration.
  useEffect(() => {
    onSelection(api && scope ? { api, scope } : null);
  }, [api, workspaceId, repositoryId, snapshotId, onSelection]);

  const entryLabel = entryPoints.find(e => e.id === entryPointId);

  async function attempt<T>(work: () => Promise<T>): Promise<T | undefined> {
    setBusy(true);
    setError(null);
    try { return await work(); } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      if (e instanceof ApiError && e.status === 401) disconnectLocally();
      return undefined;
    } finally { setBusy(false); }
  }

  async function connect(event: FormEvent) {
    event.preventDefault();
    await attempt(async () => {
      const client = new ApiClient(address);
      await client.pair(code);
      sessionSubscription.current?.();
      sessionSubscription.current = client.onSessionLost(() => {
        disconnectLocally();
        setError('Session expired or revoked. Pair again with the current API code.');
      });
      setCode('');
      setApi(client);
      onConnected(true);
      setWorkspaces((await client.listWorkspaces()).items);
    });
  }

  function clearRunSelection() {
    continuationKey.current = null;
    const current = liveRun.current;
    liveRun.current = null;
    void current?.stop();
    setRunId('');
    setLive(null);
    onView(null);
  }

  function disconnectLocally() {
    sessionSubscription.current?.();
    sessionSubscription.current = null;
    continuationKey.current = null;
    void liveRun.current?.stop();
    liveRun.current = null;
    setApi(null); setRunId(''); setLive(null); onView(null); onConnected(false);
    setWorkspaces([]); setRepositories([]); setSnapshots([]); setEntryPoints([]); setRuns([]);
    setWorkspaceId(''); setRepositoryId(''); setSnapshotId(''); setEntryPointId('');
  }

  async function disconnect() {
    const client = api;
    disconnectLocally();
    await client?.revoke().catch(() => {});
  }

  // The 30-minute server expiry should clear the UI even if no request is made.
  // Unauthorized responses independently invalidate the session from any tab.
  useEffect(() => {
    if (!api || !api.expiresAtUtc) return;
    const millis = Date.parse(api.expiresAtUtc) - Date.now();
    if (!Number.isFinite(millis)) return;
    const timer = window.setTimeout(() => api.expireSession(), Math.max(0, Math.min(millis + 250, 2_147_483_647)));
    return () => window.clearTimeout(timer);
  }, [api]);

  // Cascading selections, each re-read from the API.
  useEffect(() => {
    setRepositories([]); setRepositoryId('');
    if (api && workspaceId) void attempt(async () => setRepositories((await api.listRepositories(workspaceId)).items));
  }, [api, workspaceId]);
  useEffect(() => {
    setSnapshots([]); setSnapshotId('');
    if (api && workspaceId && repositoryId) void attempt(async () => setSnapshots((await api.listSnapshots(workspaceId, repositoryId)).items));
  }, [api, workspaceId, repositoryId]);
  useEffect(() => {
    let active = true;
    setEntryPoints([]); setEntryPointId(''); setRuns([]); setRunId('');
    // Existing runs remain accessible even when graph/candidate analysis hits
    // a 413 limit. Recovery eligibility comes from the host, not the graph.
    if (api && scope) void attempt(async () => {
      const [entryResult, runResult] = await Promise.allSettled([
        api.listEntryPoints(scope), api.listRuns(scope),
      ]);
      if (!active) return;
      if (runResult.status === 'fulfilled') setRuns(runResult.value.items);
      if (entryResult.status === 'fulfilled') {
        setEntryPoints(entryResult.value.items);
        setEntryCaveat(entryResult.value.caveat);
      } else if (api.connected) {
        setError('Entry-point candidates unavailable: ' +
          (entryResult.reason instanceof Error ? entryResult.reason.message : String(entryResult.reason)) +
          ' Saved run history and snapshot coverage remain available.');
      }
      if (runResult.status === 'rejected') throw runResult.reason;
    });
    return () => { active = false; };
  }, [api, workspaceId, repositoryId, snapshotId]);

  // The Atlas may start a question using this exact paired ApiClient and
  // scope. Observe the newly admitted, persisted run through the existing
  // hosted worker feed; never run a second investigation on navigation.
  useEffect(() => {
    if (!launchMatchesSelection(externalRun, api, scope)) return;
    setEntryPointId(externalRun.subjectNodeId);
    setRuns(list => list.some(run => run.runId === externalRun.run.runId)
      ? list : [externalRun.run, ...list]);
    setRunId(externalRun.run.runId);
    setEditing(false);
  }, [externalRun, api, workspaceId, repositoryId, snapshotId]);

  // The live feed for the selected run.
  useEffect(() => {
    void liveRun.current?.stop();
    liveRun.current = null;
    setLive(null);
    setEditing(false);
    if (!api || !scope || !runId) { onView(null); return; }
    const feed = new LiveRun(api, scope, runId, state => {
      if (liveRun.current === feed) setLive(state);
    });
    liveRun.current = feed;
    void feed.start();
    return () => { void feed.stop(); };
  }, [api, runId, workspaceId, repositoryId, snapshotId]);

  useEffect(() => {
    // Keep the run list's status in step with the authoritative projection.
    const status = live?.execution?.runStatus;
    if (status) setRuns(list => list.map(r => r.runId === runId && r.status !== status ? { ...r, status } : r));
    if (scope && runId && live) {
      const run = runs.find(r => r.runId === runId);
      const entry = entryPoints.find(e => e.id === (run?.scope.entryPointId ?? entryPointId));
      onView({ api: api!, scope, runId, entryPoint: entry ? `${entry.label} (${entry.anchor.filePath}:${entry.anchor.line})` : 'entry point', snapshot: live });
    }
  }, [live]);

  async function createWorkspace() {
    if (!api || !newWorkspace.trim()) return;
    await attempt(async () => {
      const created = await api.createWorkspace(newWorkspace.trim());
      setNewWorkspace('');
      setWorkspaces((await api.listWorkspaces()).items);
      clearRunSelection();
      setWorkspaceId(created.id);
    });
  }

  async function registerFolder() {
    if (!api || !workspaceId || !newFolder.trim()) return;
    await attempt(async () => {
      const created = await api.registerRepository(workspaceId, newFolder.trim());
      setNewFolder('');
      setRepositories((await api.listRepositories(workspaceId)).items);
      clearRunSelection();
      setRepositoryId(created.id);
    });
  }

  async function capture() {
    if (!api || !workspaceId || !repositoryId) return;
    await attempt(async () => {
      const created = await api.captureSnapshot(workspaceId, repositoryId);
      setSnapshots((await api.listSnapshots(workspaceId, repositoryId)).items);
      clearRunSelection();
      setSnapshotId(created.id);
    });
  }

  async function start() {
    if (!api || !scope || !entryPointId) return;
    await attempt(async () => {
      const started = await api.startInvestigation(scope, entryPointId);
      setRuns((await api.listRuns(scope)).items);
      setRunId(started.runId);
    });
  }

  async function continueRun() {
    const expected = eligibleContinuation(live?.execution ?? null);
    if (!api || !scope || !runId || expected === null || busy) return;
    const retained = continuationKey.current;
    if (!retained || retained.runId !== runId || retained.expected !== expected)
      continuationKey.current = { runId, expected, key: crypto.randomUUID() };
    const key = continuationKey.current!.key;
    await attempt(async () => {
      const response = await api.continueInvestigation(scope, runId, expected, key);
      // The host is authoritative; an accepted command does not itself mean execution completed.
      if (!['continuation-admitted', 'continuation-replayed'].includes(response.diagnostic))
        throw new Error('Unexpected continuation response: ' + response.diagnostic);
      continuationKey.current = null;
      await liveRun.current?.resync();
      setRuns((await api.listRuns(scope)).items);
    });
  }

  async function cancel() {
    const number = live?.execution?.currentAttempt?.number;
    if (!api || !scope || !runId || number === undefined) return;
    await attempt(async () => { await api.cancel(scope, runId, number); await liveRun.current?.resync(); });
  }

  if (!api) {
    return (
      <form className="live-form" onSubmit={connect}>
        <label>Pairing code (shown in the API window)<input type="password" autoComplete="off" value={code} onChange={e => setCode(e.target.value)} spellCheck={false} /></label>
        <details className="live-advanced"><summary>API address</summary>
          <input aria-label="API address" value={address} onChange={e => setAddress(e.target.value)} spellCheck={false} />
        </details>
        <p className="live-help">The session lives in memory only and ends when you close this tab.</p>
        <button type="submit" className="primary-action" disabled={busy || !code.trim()}>Connect</button>
        {error && <p className="live-error" role="alert">{error}</p>}
      </form>
    );
  }

  const execution = live?.execution;
  const workspaceName = workspaces.find(w => w.id === workspaceId)?.name ?? '';
  const folderName = repositories.find(r => r.id === repositoryId)?.localPath.split(/[\\/]/).filter(Boolean).pop() ?? '';
  return (
    <div className="live-form">
      {runId && <div className="live-run" aria-live="polite">
        <span className="detail-label">RUN</span>
        <p>Feed: <strong>{live?.connection ?? 'connecting'}</strong>{execution && <> · run <strong>{execution.runStatus}</strong> ({execution.runDiagnostic})</>}</p>
        {execution?.currentAttempt && <p>Attempt {execution.currentAttempt.number}: {execution.currentAttempt.status} · {execution.currentAttempt.diagnostic}</p>}
        {live?.error && <p className="live-error">{live.error}</p>}
        {execution && <div className="recovery-status" aria-label="Host recovery eligibility">
          <strong>Recovery: {eligibleContinuation(execution) !== null ? 'Eligible for explicit continuation' : 'Not currently eligible'}</strong>
          <p>{execution.recovery?.diagnostic ?? 'No host recovery eligibility has been recorded.'}</p>
          <small>Only the host verifies saved checkpoints and admits a new attempt. Nothing resumes automatically.</small>
        </div>}
        <div className="control-actions">
          <button type="button" className="primary-action" onClick={onFindings}>View findings</button>
          <button type="button" className="secondary-action" disabled={busy || !execution?.cancellable} onClick={cancel}>Cancel</button>
          {eligibleContinuation(execution ?? null) !== null &&
            <button type="button" className="primary-action" disabled={busy}
              onClick={() => void continueRun()}>Continue interrupted run</button>}
          <button type="button" className="secondary-action" onClick={() => setRunId('')}>Close</button>
        </div>
      </div>}
      {runId && !editing
        ? <div className="setup-summary">
            <span>{workspaceName} › {folderName} › {entryLabel ? `${entryLabel.label} (${entryLabel.anchor.filePath}:${entryLabel.anchor.line})` : 'earlier run'}</span>
            <button type="button" className="text-button" onClick={() => setEditing(true)}>Change</button>
          </div>
        : <>
      <label><span className="step-title"><span className="step-no">1</span>Workspace</span>
        <select value={workspaceId} onChange={e => { clearRunSelection(); setWorkspaceId(e.target.value); }}>
          <option value="">Choose…</option>
          {workspaces.map(w => <option key={w.id} value={w.id}>{w.name}</option>)}
        </select>
      </label>
      <div className="live-inline">
        <input placeholder="New workspace name" value={newWorkspace} onChange={e => setNewWorkspace(e.target.value)} />
        <button type="button" className="secondary-action" disabled={busy || !newWorkspace.trim()} onClick={createWorkspace}>Create</button>
      </div>

      {workspaceId && <>
        <label><span className="step-title"><span className="step-no">2</span>Repository (local folder)</span>
          <select value={repositoryId} onChange={e => { clearRunSelection(); setRepositoryId(e.target.value); }}>
            <option value="">Choose…</option>
            {repositories.map(r => <option key={r.id} value={r.id}>{r.localPath}</option>)}
          </select>
        </label>
        <div className="live-inline">
          <input placeholder="Local folder path to register" value={newFolder} onChange={e => setNewFolder(e.target.value)} spellCheck={false} />
          <button type="button" className="secondary-action" disabled={busy || !newFolder.trim()} onClick={registerFolder}>Register</button>
        </div>
      </>}

      {repositoryId && <>
        <label><span className="step-title"><span className="step-no">3</span>Snapshot (saved read-only copy)</span>
          <select value={snapshotId} onChange={e => { clearRunSelection(); setSnapshotId(e.target.value); }}>
            <option value="">Choose…</option>
            {snapshots.map(s => <option key={s.id} value={s.id}>{new Date(s.capturedAtUtc).toLocaleString()} · {s.fileCount} files</option>)}
          </select>
        </label>
        <button type="button" className="secondary-action" disabled={busy} onClick={capture}>Capture new snapshot</button>
      </>}

      {scope && <>
        <label><span className="step-title"><span className="step-no">4</span>Entry point</span>
          <select value={entryPointId} onChange={e => setEntryPointId(e.target.value)}>
            <option value="">{entryPoints.length ? 'Choose…' : 'No supported candidates (unknown, not proof of none)'}</option>
            {entryPoints.map(e => <option key={e.id} value={e.id}>{e.label} · {e.anchor.filePath}:{e.anchor.line}</option>)}
          </select>
        </label>
        {(entryLabel || entryCaveat) && <details className="live-advanced"><summary>About this entry point</summary>
          {entryLabel && <p className="live-help">{entryLabel.detail}</p>}
          {entryCaveat && <p className="live-help">{entryCaveat}</p>}
        </details>}
        <button type="button" className="primary-action" disabled={busy || !entryPointId} onClick={start}>Start investigation</button>
        {runs.length > 0 && <label>Or watch an earlier run
          <select value={runId} onChange={e => setRunId(e.target.value)}>
            <option value="">Choose…</option>
            {runs.map(r => <option key={r.runId} value={r.runId}>{new Date(r.startedAtUtc).toLocaleString()} · {r.status}</option>)}
          </select>
        </label>}
      </>}
        </>}

      {error && <p className="live-error" role="alert">{error}</p>}
      <button type="button" className="text-button" onClick={disconnect}>Disconnect</button>
    </div>
  );
}

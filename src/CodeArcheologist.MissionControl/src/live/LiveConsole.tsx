import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { ApiClient, ApiError } from './api.ts';
import type { EntryPoint, Repository, RunSummary, Scope, Snapshot, Workspace } from './api.ts';
import { LiveRun } from './realtime.ts';
import type { LiveSnapshot } from './realtime.ts';

export type LiveView = { scope: Scope; runId: string; entryPoint: string; snapshot: LiveSnapshot };

/**
 * MC3 connection and command panel. Every command is an explicit button
 * press sent over REST; the live view is fed by LiveRun (SignalR + REST).
 */
export function LiveConsole({ onView, onConnected }: {
  onView: (view: LiveView | null) => void;
  onConnected: (connected: boolean) => void;
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
  const liveRun = useRef<LiveRun | null>(null);

  const scope: Scope | null = workspaceId && repositoryId && snapshotId ? { workspaceId, repositoryId, snapshotId } : null;
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
      setCode('');
      setApi(client);
      onConnected(true);
      setWorkspaces((await client.listWorkspaces()).items);
    });
  }

  function disconnectLocally() {
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
    setEntryPoints([]); setEntryPointId(''); setRuns([]); setRunId('');
    if (api && scope) void attempt(async () => {
      const list = await api.listEntryPoints(scope);
      setEntryPoints(list.items);
      setEntryCaveat(list.caveat);
      setRuns((await api.listRuns(scope)).items);
    });
  }, [api, snapshotId]);

  // The live feed for the selected run.
  useEffect(() => {
    void liveRun.current?.stop();
    liveRun.current = null;
    setLive(null);
    if (!api || !scope || !runId) { onView(null); return; }
    const feed = new LiveRun(api, scope, runId, state => setLive(state));
    liveRun.current = feed;
    void feed.start();
    return () => { void feed.stop(); };
  }, [api, runId]);

  useEffect(() => {
    // Keep the run list's status in step with the authoritative projection.
    const status = live?.execution?.runStatus;
    if (status) setRuns(list => list.map(r => r.runId === runId && r.status !== status ? { ...r, status } : r));
    if (scope && runId && live) {
      const run = runs.find(r => r.runId === runId);
      const entry = entryPoints.find(e => e.id === (run?.scope.entryPointId ?? entryPointId));
      onView({ scope, runId, entryPoint: entry ? `${entry.label} (${entry.anchor.filePath}:${entry.anchor.line})` : 'entry point', snapshot: live });
    }
  }, [live]);

  async function createWorkspace() {
    if (!api || !newWorkspace.trim()) return;
    await attempt(async () => {
      const created = await api.createWorkspace(newWorkspace.trim());
      setNewWorkspace('');
      setWorkspaces((await api.listWorkspaces()).items);
      setWorkspaceId(created.id);
    });
  }

  async function registerFolder() {
    if (!api || !workspaceId || !newFolder.trim()) return;
    await attempt(async () => {
      const created = await api.registerRepository(workspaceId, newFolder.trim());
      setNewFolder('');
      setRepositories((await api.listRepositories(workspaceId)).items);
      setRepositoryId(created.id);
    });
  }

  async function capture() {
    if (!api || !workspaceId || !repositoryId) return;
    await attempt(async () => {
      const created = await api.captureSnapshot(workspaceId, repositoryId);
      setSnapshots((await api.listSnapshots(workspaceId, repositoryId)).items);
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

  async function cancel() {
    const number = live?.execution?.currentAttempt?.number;
    if (!api || !scope || !runId || number === undefined) return;
    await attempt(async () => { await api.cancel(scope, runId, number); await liveRun.current?.resync(); });
  }

  if (!api) {
    return (
      <form className="live-form" onSubmit={connect}>
        <p className="live-help">Start the Code Archeologist API. Its window shows a pairing code. The session token is kept in memory only and ends when you close this tab.</p>
        <label>API address<input value={address} onChange={e => setAddress(e.target.value)} spellCheck={false} /></label>
        <label>Pairing code<input type="password" autoComplete="off" value={code} onChange={e => setCode(e.target.value)} spellCheck={false} /></label>
        <button type="submit" className="primary-action" disabled={busy || !code.trim()}>Connect</button>
        {error && <p className="live-error" role="alert">{error}</p>}
      </form>
    );
  }

  const execution = live?.execution;
  return (
    <div className="live-form">
      <label>Workspace
        <select value={workspaceId} onChange={e => setWorkspaceId(e.target.value)}>
          <option value="">Choose…</option>
          {workspaces.map(w => <option key={w.id} value={w.id}>{w.name}</option>)}
        </select>
      </label>
      <div className="live-inline">
        <input placeholder="New workspace name" value={newWorkspace} onChange={e => setNewWorkspace(e.target.value)} />
        <button type="button" className="secondary-action" disabled={busy || !newWorkspace.trim()} onClick={createWorkspace}>Create</button>
      </div>

      {workspaceId && <>
        <label>Repository (registered local folder)
          <select value={repositoryId} onChange={e => setRepositoryId(e.target.value)}>
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
        <label>Snapshot (read-only saved copy)
          <select value={snapshotId} onChange={e => setSnapshotId(e.target.value)}>
            <option value="">Choose…</option>
            {snapshots.map(s => <option key={s.id} value={s.id}>{new Date(s.capturedAtUtc).toLocaleString()} · {s.fileCount} files</option>)}
          </select>
        </label>
        <button type="button" className="secondary-action" disabled={busy} onClick={capture}>Capture new snapshot</button>
      </>}

      {scope && <>
        <label>Entry point (written HTTP candidate)
          <select value={entryPointId} onChange={e => setEntryPointId(e.target.value)}>
            <option value="">{entryPoints.length ? 'Choose…' : 'No supported candidates (unknown, not proof of none)'}</option>
            {entryPoints.map(e => <option key={e.id} value={e.id}>{e.label} · {e.anchor.filePath}:{e.anchor.line}</option>)}
          </select>
        </label>
        {entryLabel && <p className="live-help">{entryLabel.detail}</p>}
        {entryCaveat && <p className="live-help">{entryCaveat}</p>}
        <button type="button" className="primary-action" disabled={busy || !entryPointId} onClick={start}>Start investigation</button>
        {runs.length > 0 && <label>Or watch an earlier run
          <select value={runId} onChange={e => setRunId(e.target.value)}>
            <option value="">Choose…</option>
            {runs.map(r => <option key={r.runId} value={r.runId}>{new Date(r.startedAtUtc).toLocaleString()} · {r.status}</option>)}
          </select>
        </label>}
      </>}

      {runId && <div className="live-run" aria-live="polite">
        <span className="detail-label">RUN</span>
        <p>Feed: <strong>{live?.connection ?? 'connecting'}</strong>{execution && <> · run <strong>{execution.runStatus}</strong> ({execution.runDiagnostic})</>}</p>
        {execution?.currentAttempt && <p>Attempt {execution.currentAttempt.number}: {execution.currentAttempt.status} · {execution.currentAttempt.diagnostic}</p>}
        {live?.error && <p className="live-error">{live.error}</p>}
        <div className="control-actions">
          <button type="button" className="secondary-action" disabled={busy || !execution?.cancellable} onClick={cancel}>Cancel investigation</button>
          <button type="button" className="secondary-action" onClick={() => setRunId('')}>Close run view</button>
        </div>
      </div>}

      {error && <p className="live-error" role="alert">{error}</p>}
      <button type="button" className="text-button" onClick={disconnect}>Disconnect</button>
    </div>
  );
}

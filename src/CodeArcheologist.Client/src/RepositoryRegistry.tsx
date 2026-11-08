import { useEffect, useRef, useState } from 'react';
import { SnapshotExplorer } from './SnapshotExplorer';

type RepositorySource = {
  id: string;
  workspaceId: string;
  localPath: string;
  sourceKind: 'git' | 'folder';
  addedAtUtc: string;
  version: number;
};

type RepositoryPage = {
  items: RepositorySource[];
  totalCount: number;
  hasMore: boolean;
};

type Props = {
  workspaceId: string;
  csrfToken: string;
};

export function RepositoryRegistry({ workspaceId, csrfToken }: Props) {
  const [path, setPath] = useState('');
  const [sources, setSources] = useState<RepositoryPage | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [expandedSourceId, setExpandedSourceId] = useState<string | null>(null);
  const retryKey = useRef<string | null>(null);
  const endpoint = '/api/v1/workspaces/' + encodeURIComponent(workspaceId) + '/repositories';

  useEffect(() => {
    const controller = new AbortController();
    async function read() {
      setError('');
      setSources(null);
      try {
        const response = await fetch(endpoint, {
          credentials: 'same-origin',
          cache: 'no-store',
          signal: controller.signal,
        });
        if (!response.ok) throw new Error('Unable to load repository registrations');
        const data: RepositoryPage = await response.json();
        if (!controller.signal.aborted) setSources(data);
      } catch {
        if (!controller.signal.aborted) setError('Could not load local repositories.');
      }
    }

    void read();
    return () => controller.abort();
  }, [endpoint]);

  async function refresh() {
    const response = await fetch(endpoint, {
      credentials: 'same-origin',
      cache: 'no-store',
    });
    if (!response.ok) throw new Error('Could not refresh the local repository list');
    const data: RepositoryPage = await response.json();
    setSources(data);
  }

  async function register() {
    if (!path.trim()) return;
    setBusy(true);
    setError('');
    setNotice('');
    retryKey.current ??= crypto.randomUUID();

    try {
      const response = await fetch(endpoint, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'X-CodeArcheologist-Request': '1',
          'X-CodeArcheologist-Csrf': csrfToken,
          'Idempotency-Key': retryKey.current,
        },
        body: JSON.stringify({ localPath: path.trim() }),
      });

      if (!response.ok) {
        const body: { error?: string } = await response.json().catch(() => ({}));
        throw new Error(body.error ?? 'Registration failed (HTTP ' + response.status + ')');
      }

      setPath('');
      retryKey.current = null;
      setNotice('Repository location registered. No files were read or executed.');
      await refresh();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Unable to register repository');
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="repository-panel" aria-label="Local repository sources">
      <h3>Local repository sources</h3>
      <p>
        Register one or more existing folders from this computer. This records only
        their locations. Nothing is scanned, built, executed, or copied at this stage.
      </p>
      <form onSubmit={(event) => { event.preventDefault(); void register(); }}>
        <label htmlFor="repository-path">Absolute local folder path</label>
        <div className="repository-controls">
          <input
            id="repository-path"
            value={path}
            placeholder="e.g. C:\\Users\\you\\source\\repos\\Project"
            disabled={busy}
            maxLength={2048}
            spellCheck={false}
            onChange={(event) => {
              setPath(event.target.value);
              retryKey.current = null;
              setNotice('');
            }}
          />
          <button type="submit" disabled={busy || !path.trim()}>
            {busy ? 'Registering…' : 'Register folder'}
          </button>
        </div>
      </form>
      {error && <p role="alert" className="error">{error}</p>}
      {notice && <p role="status" className="registration-notice">{notice}</p>}
      {sources === null && !error && <p>Loading registered locations…</p>}
      {sources?.items.length === 0 && <p>No local directories registered yet.</p>}
      <ul className="repository-list">
        {sources?.items.map((source) => (
          <li key={source.id}>
            <span className="repository-kind">{source.sourceKind === 'git' ? 'Git working tree' : 'Local folder'}</span>
            <code>{source.localPath}</code>
            <button
              type="button"
              className="repository-expand"
              aria-expanded={expandedSourceId === source.id}
              onClick={() => setExpandedSourceId(
                expandedSourceId === source.id ? null : source.id
              )}
            >
              {expandedSourceId === source.id ? 'Hide snapshots' : 'View snapshots'}
            </button>
            {expandedSourceId === source.id && (
              <SnapshotExplorer
                key={source.id}
                workspaceId={workspaceId}
                repositoryId={source.id}
                csrfToken={csrfToken}
              />
            )}
          </li>
        ))}
      </ul>
      {sources?.hasMore && (
        <p className="workspace-note">
          Showing the newest {sources.items.length} of {sources.totalCount} registrations.
        </p>
      )}
    </section>
  );
}

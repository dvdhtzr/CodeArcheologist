import { useEffect, useRef, useState } from 'react';
import { RepositoryRegistry } from './RepositoryRegistry';

type Workspace = {
  id: string;
  name: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  version: number;
};

type WorkspacePage = {
  items: Workspace[];
  totalCount: number;
  hasMore: boolean;
};

type Props = {
  csrfToken: string;
};

export function WorkspaceExplorer({ csrfToken }: Props) {
  const [list, setList] = useState<WorkspacePage | null>(null);
  const [selected, setSelected] = useState<Workspace | null>(null);
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const retryKey = useRef<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    async function refresh() {
      try {
        const response = await fetch('/api/v1/workspaces', {
          credentials: 'same-origin',
          cache: 'no-store',
          signal: controller.signal,
        });
        if (!response.ok) throw new Error('Unable to load local workspaces');
        const data: WorkspacePage = await response.json();
        if (!controller.signal.aborted) setList(data);
      } catch {
        if (!controller.signal.aborted) {
          setError('Workspace list unavailable. Check the local API.');
        }
      }
    }
    void refresh();
    return () => controller.abort();
  }, []);

  async function reload() {
    const response = await fetch('/api/v1/workspaces', {
      credentials: 'same-origin',
      cache: 'no-store',
    });
    if (!response.ok) throw new Error('Workspace list could not be refreshed');
    const data: WorkspacePage = await response.json();
    setList(data);
  }

  async function openWorkspace(id: string) {
    setBusy(true);
    setError('');
    try {
      const response = await fetch('/api/v1/workspaces/' + encodeURIComponent(id), {
        credentials: 'same-origin',
        cache: 'no-store',
      });
      if (!response.ok) throw new Error('Workspace could not be reopened');
      const data: Workspace = await response.json();
      setSelected(data);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Failed to open workspace');
    } finally {
      setBusy(false);
    }
  }

  async function createWorkspace() {
    const trimmed = name.trim();
    if (!trimmed) return;
    setBusy(true);
    setError('');
    retryKey.current ??= crypto.randomUUID();

    try {
      const response = await fetch('/api/v1/workspaces', {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'X-CodeArcheologist-Request': '1',
          'X-CodeArcheologist-Csrf': csrfToken,
          'Idempotency-Key': retryKey.current,
        },
        body: JSON.stringify({ name: trimmed }),
      });

      if (!response.ok) {
        if (response.status === 409) {
          throw new Error('Creation key already used. Change the name and try again.');
        }
        throw new Error('Workspace creation failed (HTTP ' + response.status + ')');
      }

      const newWorkspace: Workspace = await response.json();
      setSelected(newWorkspace);
      setName('');
      retryKey.current = null;
      await reload();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not create workspace');
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="workspace-panel" aria-labelledby="workspace-heading">
      <div className="eyebrow">PERSISTENT WORKSPACES / SQLITE</div>
      <h2 id="workspace-heading">Your workspaces</h2>
      <p>
        Create a named workspace, or reopen one from a previous session.
        Workspace metadata is stored on this computer, not in the cloud.
      </p>
      <form onSubmit={(event) => { event.preventDefault(); void createWorkspace(); }}>
        <label htmlFor="workspace-name">Workspace name</label>
        <div className="pairing-controls">
          <input
            id="workspace-name"
            value={name}
            maxLength={120}
            placeholder="e.g. Engineering Platform"
            disabled={busy}
            onChange={event => {
              setName(event.target.value);
              retryKey.current = null;
            }}
          />
          <button type="submit" disabled={busy || !name.trim()}>
            {busy ? 'Working…' : 'Create workspace'}
          </button>
        </div>
      </form>

      {error && <p className="error" role="alert">{error}</p>}

      <div className="workspace-columns">
        <section aria-label="Saved workspaces">
          <h3>Saved locally</h3>
          {list === null && <p>Loading saved workspaces…</p>}
          {list?.items.length === 0 && <p>No workspaces yet.</p>}
          <ul className="workspace-list">
            {list?.items.map(workspace => (
              <li key={workspace.id}>
                <button
                  type="button"
                  disabled={busy}
                  aria-current={selected?.id === workspace.id ? 'true' : undefined}
                  onClick={() => void openWorkspace(workspace.id)}
                >
                  <strong>{workspace.name}</strong>
                  <span>{new Date(workspace.createdAtUtc).toLocaleString()}</span>
                </button>
              </li>
            ))}
          </ul>
          {list?.hasMore && (
            <p className="workspace-note">
              Showing the newest {list.items.length} of {list.totalCount} workspaces.
              Pagination will be added in a subsequent update.
            </p>
          )}
        </section>
        <section className="workspace-detail" aria-label="Current workspace">
          <h3>Open workspace</h3>
          {selected ? (
            <>
              <h4>{selected.name}</h4>
              <p>Created {new Date(selected.createdAtUtc).toLocaleString()}</p>
              <small>Workspace ID: {selected.id}</small>
              <p className="workspace-note">
                Workspace metadata, registered directories and captured snapshots persist
                locally. Open a registered repository to inspect its saved snapshots.
                Vault notes and semantic analysis are not implemented yet.
              </p>
              <RepositoryRegistry
                key={selected.id}
                workspaceId={selected.id}
                csrfToken={csrfToken}
              />
            </>
          ) : (
            <p>Select a saved workspace or create a new one to open its details.</p>
          )}
        </section>
      </div>
    </section>
  );
}

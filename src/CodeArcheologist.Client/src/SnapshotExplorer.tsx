import { useEffect, useRef, useState } from 'react';

type Snapshot = {
  id: string;
  workspaceId: string;
  repositoryId: string;
  fingerprintSha256: string;
  manifestSha256: string;
  capturedAtUtc: string;
  fileCount: number;
  totalBytes: number;
  exclusionCount: number;
  policyVersion: string;
};

type SnapshotPage = {
  items: Snapshot[];
  totalCount: number;
  hasMore: boolean;
};

type Manifest = {
  formatVersion: number;
  revisionKind: string;
  fingerprintSha256: string;
  files: { path: string; sizeBytes: number; sha256: string }[];
  exclusions: { path: string; reason: string }[];
};

type Props = {
  workspaceId: string;
  repositoryId: string;
  csrfToken: string;
};

export function SnapshotExplorer({ workspaceId, repositoryId, csrfToken }: Props) {
  const [list, setList] = useState<SnapshotPage | null>(null);
  const [manifest, setManifest] = useState<Manifest | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const retryKey = useRef<string | null>(null);

  const endpoint = '/api/v1/workspaces/' + encodeURIComponent(workspaceId) +
    '/repositories/' + encodeURIComponent(repositoryId) + '/snapshots';

  useEffect(() => {
    const controller = new AbortController();
    async function read() {
      setList(null);
      setError('');
      setManifest(null);
      try {
        const response = await fetch(endpoint, {
          credentials: 'same-origin',
          cache: 'no-store',
          signal: controller.signal,
        });
        if (!response.ok) throw new Error('Snapshot list request failed.');
        const page: SnapshotPage = await response.json();
        if (!controller.signal.aborted) setList(page);
      } catch {
        if (!controller.signal.aborted) setError('Unable to load saved snapshot metadata.');
      }
    }
    void read();
    return () => controller.abort();
  }, [endpoint]);

  async function reload() {
    const response = await fetch(endpoint, {
      credentials: 'same-origin',
      cache: 'no-store',
    });
    if (!response.ok) throw new Error('Could not refresh snapshots.');
    setList(await response.json() as SnapshotPage);
  }

  async function capture() {
    setBusy(true);
    setError('');
    setNotice('');
    retryKey.current ??= crypto.randomUUID();
    try {
      const response = await fetch(endpoint, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'X-CodeArcheologist-Request': '1',
          'X-CodeArcheologist-Csrf': csrfToken,
          'Idempotency-Key': retryKey.current,
        },
      });
      if (!response.ok) {
        const result: { error?: string } = await response.json().catch(() => ({}));
        throw new Error(result.error ?? 'Snapshot capture failed (HTTP ' + response.status + ')');
      }

      const snapshot: Snapshot = await response.json();
      retryKey.current = null;
      setNotice(
        (response.status === 201 ? 'New snapshot captured.' : 'Existing snapshot reused.') +
        ' ' + snapshot.fileCount + ' files / ' + snapshot.exclusionCount + ' exclusions.'
      );
      await reload();
      await inspect(snapshot.id);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Snapshot capture failed.');
    } finally {
      setBusy(false);
    }
  }

  async function inspect(id: string) {
    setBusy(true);
    setError('');
    try {
      const response = await fetch(endpoint + '/' + encodeURIComponent(id) + '/manifest', {
        credentials: 'same-origin',
        cache: 'no-store',
      });
      if (!response.ok) throw new Error('Unable to verify and read snapshot manifest.');
      const data: Manifest = await response.json();
      setManifest(data);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Manifest could not be opened.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="snapshot-panel" aria-label="Read-only repository snapshots">
      <h4>Read-only snapshots</h4>
      <p>
        Capture an application-managed copy of eligible local source files and
        a SHA-256 manifest. This reads files, but does not execute builds, Git,
        project scripts or analysis agents.
      </p>
      <p className="workspace-note">
        Known secret filenames, vendor directories and binaries are excluded.
        Secret values inside ordinary source files are NOT automatically detected
        or redacted. Captures remain on this computer.
      </p>
      <button type="button" disabled={busy} onClick={() => void capture()}>
        {busy ? 'Working…' : 'Capture read-only snapshot'}
      </button>
      {error && <p role="alert" className="error">{error}</p>}
      {notice && <p role="status" className="registration-notice">{notice}</p>}
      {list === null && !error && <p>Loading snapshot history…</p>}
      {list?.items.length === 0 && <p>No snapshots have been captured.</p>}
      <ul className="snapshot-list">
        {list?.items.map((snapshot) => (
          <li key={snapshot.id}>
            <button type="button" disabled={busy} onClick={() => void inspect(snapshot.id)}>
              Inspect manifest
            </button>
            <span>
              {new Date(snapshot.capturedAtUtc).toLocaleString()} —
              {' '}{snapshot.fileCount} files, {snapshot.exclusionCount} exclusions
            </span>
            <code title={snapshot.fingerprintSha256}>
              SHA-256: {snapshot.fingerprintSha256.slice(0, 20)}…
            </code>
          </li>
        ))}
      </ul>
      {list?.hasMore && <p className="workspace-note">
        Showing the newest {list.items.length} of {list.totalCount} snapshots.
      </p>}
      {manifest && (
        <section className="snapshot-manifest" aria-label="Captured manifest">
          <h4>Manifest (verified against saved digest)</h4>
          <p>Revision identity: {manifest.revisionKind}</p>
          <code>{manifest.fingerprintSha256}</code>
          <p>
            {manifest.files.length} included files, {manifest.exclusions.length} excluded entries
          </p>
          <ul>
            {manifest.files.slice(0, 100).map((file) => (
              <li key={file.path}>
                <code>{file.path}</code>
                <span>{file.sizeBytes} bytes — {file.sha256.slice(0, 16)}…</span>
              </li>
            ))}
          </ul>
          {manifest.files.length > 100 && <p className="workspace-note">
            Showing the first 100 included paths. The full manifest is retained locally.
          </p>}
          {manifest.exclusions.length > 0 && (
            <details>
              <summary>Review exclusions ({manifest.exclusions.length})</summary>
              <ul>
                {manifest.exclusions.slice(0, 100).map((entry) => (
                  <li key={entry.path}>
                    <code>{entry.path}</code>
                    <span>{entry.reason}</span>
                  </li>
                ))}
              </ul>
              {manifest.exclusions.length > 100 && (
                <p>Showing the first 100 exclusions.</p>
              )}
            </details>
          )}
        </section>
      )}
    </section>
  );
}

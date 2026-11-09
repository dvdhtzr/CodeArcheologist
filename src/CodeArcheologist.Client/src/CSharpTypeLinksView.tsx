import { useEffect, useState } from 'react';

type LinkTarget = {
  name: string;
  namespace: string;
  filePath: string;
  fileSha256: string;
  line: number;
  column: number;
  spanStart: number;
  spanLength: number;
};

type TypeUsage = {
  usageKind: string;
  writtenType: string;
  status: 'provisional-local' | 'unresolved';
  reason: string;
  filePath: string;
  fileSha256: string;
  line: number;
  column: number;
  spanStart: number;
  spanLength: number;
  target: LinkTarget | null;
};

type TypeRelationshipReport = {
  workspaceId: string;
  repositoryId: string;
  snapshotId: string;
  fingerprintSha256: string;
  analyzerVersion: string;
  bindingContext: string;
  cSharpFileCount: number;
  syntaxErrorCount: number;
  provisionalLinkCount: number;
  unresolvedCount: number;
  usages: TypeUsage[];
  limitations: string[];
};

export function CSharpTypeLinksView({ endpoint }: { endpoint: string }) {
  const [report, setReport] = useState<TypeRelationshipReport | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    const controller = new AbortController();
    async function load() {
      try {
        const response = await fetch(endpoint, {
          credentials: 'same-origin', cache: 'no-store', signal: controller.signal,
        });
        if (!response.ok) {
          const message: { error?: string } = await response.json().catch(() => ({}));
          throw new Error(message.error ?? 'Unable to inspect type links (HTTP ' + response.status + ')');
        }
        const result: TypeRelationshipReport = await response.json();
        if (!controller.signal.aborted) setReport(result);
      } catch (cause) {
        if (!controller.signal.aborted) {
          setError(cause instanceof Error ? cause.message : 'Type links unavailable.');
        }
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    }
    void load();
    return () => controller.abort();
  }, [endpoint]);

  return (
    <section className="snapshot-manifest syntax-view" aria-label="Provisional C# type relationships">
      <h4>Provisional C# type relationships</h4>
      {loading && <p role="status">Binding type names in verified snapshot files…</p>}
      {error && <p className="error" role="alert">{error}</p>}
      {report && (
        <>
          <p>
            {report.cSharpFileCount} C# files · {report.provisionalLinkCount} provisional local links ·
            {' '}{report.unresolvedCount} unresolved uses
          </p>
          <p className="workspace-note">
            {report.analyzerVersion} · {report.bindingContext}.
            These are candidate relationships, not verified project dependencies.
          </p>
          {report.syntaxErrorCount > 0 && (
            <p className="error" role="alert">
              {report.syntaxErrorCount} syntax error(s) were found in the captured C# source.
              Bindings in this provisional compilation may be incomplete.
            </p>
          )}
          {report.usages.length === 0 && (
            <p>No base-type or explicit object-creation references were found in this snapshot.</p>
          )}
          <ul className="syntax-declaration-list">
            {report.usages.slice(0, 100).map((usage) => (
              <li key={usage.filePath + ':' + usage.spanStart + ':' + usage.usageKind}>
                <strong>
                  {usage.usageKind}: {usage.writtenType}
                  {' · '}{usage.status === 'provisional-local'
                    ? 'Provisional snapshot-local match' : 'Unresolved'}
                </strong>
                <code>
                  From {usage.filePath}:{usage.line}:{usage.column} · SHA-256
                  {' '}{usage.fileSha256.slice(0, 16)}…
                </code>
                {usage.target && (
                  <code>
                    Candidate target: {usage.target.namespace
                      ? usage.target.namespace + '.' : ''}{usage.target.name}
                    {' · '}{usage.target.filePath}:{usage.target.line}:{usage.target.column}
                    {' · SHA-256 '}{usage.target.fileSha256.slice(0, 16)}…
                  </code>
                )}
                <span>{usage.reason}</span>
              </li>
            ))}
          </ul>
          {report.usages.length > 100 && (
            <p className="workspace-note">
              Showing the first 100 uses. The complete report is available from the local API.
            </p>
          )}
          <details>
            <summary>Coverage and trust limitations</summary>
            <ul>{report.limitations.map(limit => <li key={limit}>{limit}</li>)}</ul>
          </details>
        </>
      )}
    </section>
  );
}

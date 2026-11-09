import { useEffect, useState } from 'react';

type Declaration = {
  kind: string;
  name: string;
  containingNamespace: string;
  containingType: string | null;
  filePath: string;
  fileSha256: string;
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
  spanStart: number;
  spanLength: number;
  parameterCount: number | null;
};

type ParseIssue = {
  filePath: string;
  fileSha256: string;
  code: string;
  message: string;
  line: number;
  column: number;
  severity: string;
};

type SyntaxReport = {
  workspaceId: string;
  repositoryId: string;
  snapshotId: string;
  fingerprintSha256: string;
  analyzerVersion: string;
  extractionMethod: string;
  parseConfiguration: string;
  cSharpFileCount: number;
  syntaxErrorCount: number;
  hasSyntaxErrors: boolean;
  declarations: Declaration[];
  diagnostics: ParseIssue[];
  limitations: string[];
};

export function CSharpSyntaxView({ endpoint }: { endpoint: string }) {
  const [report, setReport] = useState<SyntaxReport | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const controller = new AbortController();

    async function load() {
      try {
        const response = await fetch(endpoint, {
          credentials: 'same-origin',
          cache: 'no-store',
          signal: controller.signal,
        });
        if (!response.ok) {
          const body: { error?: string } = await response.json().catch(() => ({}));
          throw new Error(body.error ?? 'C# inspection failed (HTTP ' + response.status + ')');
        }
        const payload: SyntaxReport = await response.json();
        if (!controller.signal.aborted) setReport(payload);
      } catch (cause) {
        if (!controller.signal.aborted) {
          setError(cause instanceof Error ? cause.message : 'C# syntax unavailable.');
        }
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    }

    void load();
    return () => controller.abort();
  }, [endpoint]);

  const kinds = report
    ? Array.from(new Set(report.declarations.map(item => item.kind)))
        .map(kind => ({ kind, count: report.declarations.filter(item => item.kind === kind).length }))
        .sort((a, b) => b.count - a.count || a.kind.localeCompare(b.kind))
    : [];

  return (
    <section className="snapshot-manifest syntax-view" aria-label="C# syntax inspection">
      <h4>C# syntax inspection</h4>
      {loading && <p role="status">Parsing verified C# snapshot files…</p>}
      {error && <p className="error" role="alert">{error}</p>}
      {report && (
        <>
          <p>
            {report.cSharpFileCount} C# files · {report.declarations.length} syntax declarations ·
            {' '}{report.syntaxErrorCount} syntax errors
          </p>
          {report.hasSyntaxErrors && (
            <p className="error" role="alert">
              Some files have syntax errors. Declarations may be incomplete.
              Inspect the diagnostics below before relying on these observations.
            </p>
          )}
          <p className="workspace-note">
            {report.analyzerVersion} · {report.extractionMethod}.
            This is a parser, not a semantic compilation or dependency resolver.
          </p>
          {kinds.length > 0 && (
            <div className="syntax-kind-summary" aria-label="Declaration categories">
              {kinds.map(item => (
                <span key={item.kind}>{item.kind}: {item.count}</span>
              ))}
            </div>
          )}
          {report.cSharpFileCount === 0 && (
            <p>No C# files are present in this snapshot.</p>
          )}
          <details open>
            <summary>Observed declarations ({report.declarations.length})</summary>
            <ul className="syntax-declaration-list">
              {report.declarations.slice(0, 100).map(item => (
                <li key={item.filePath + ':' + item.spanStart + ':' + item.kind}>
                  <strong>{item.kind}: {item.name}</strong>
                  <span>
                    {item.containingNamespace || '(global namespace)'}
                    {item.containingType ? ' · ' + item.containingType : ''}
                    {item.parameterCount === null ? '' : ' · ' + item.parameterCount + ' parameters'}
                  </span>
                  <code>
                    {item.filePath}:{item.startLine}:{item.startColumn} · SHA-256
                    {' '}{item.fileSha256.slice(0, 16)}…
                  </code>
                </li>
              ))}
            </ul>
            {report.declarations.length > 100 && (
              <p className="workspace-note">
                Displaying the first 100 declarations; the complete report is available from the local API.
              </p>
            )}
          </details>
          {report.diagnostics.length > 0 && (
            <details>
              <summary>Syntax diagnostics ({report.diagnostics.length})</summary>
              <ul className="syntax-declaration-list">
                {report.diagnostics.slice(0, 100).map((issue, index) => (
                  <li key={issue.filePath + ':' + issue.line + ':' + issue.code + ':' + index}>
                    <strong>{issue.severity} {issue.code}</strong>
                    <span>{issue.message}</span>
                    <code>{issue.filePath}:{issue.line}:{issue.column}</code>
                  </li>
                ))}
              </ul>
              {report.diagnostics.length > 100 && (
                <p className="workspace-note">Showing the first 100 diagnostics.</p>
              )}
            </details>
          )}
          <details>
            <summary>Analysis limitations</summary>
            <ul>{report.limitations.map(limit => <li key={limit}>{limit}</li>)}</ul>
          </details>
        </>
      )}
    </section>
  );
}

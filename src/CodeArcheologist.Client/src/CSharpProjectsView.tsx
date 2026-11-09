import { useEffect, useState } from 'react';

type CandidateFile = {
  path: string;
  sha256: string;
  inclusionBasis: string;
};

type ProjectCandidate = {
  projectPath: string;
  projectSha256: string;
  declaredSdk: string | null;
  defaultCompileItemsInferred: boolean;
  candidateFiles: CandidateFile[];
  unresolvedRules: string[];
};

type ProjectReport = {
  workspaceId: string;
  repositoryId: string;
  snapshotId: string;
  fingerprintSha256: string;
  analyzerVersion: string;
  method: string;
  projectCount: number;
  cSharpFileCount: number;
  projects: ProjectCandidate[];
  unmappedFiles: CandidateFile[];
  limitations: string[];
};

export function CSharpProjectsView({ endpoint }: { endpoint: string }) {
  const [report, setReport] = useState<ProjectReport | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const controller = new AbortController();
    async function load() {
      try {
        const response = await fetch(endpoint, {
          credentials: 'same-origin', cache: 'no-store', signal: controller.signal,
        });
        if (!response.ok) {
          const result: { error?: string } = await response.json().catch(() => ({}));
          throw new Error(result.error ?? 'Static project inspection failed (HTTP ' + response.status + ')');
        }
        const data: ProjectReport = await response.json();
        if (!controller.signal.aborted) setReport(data);
      } catch (cause) {
        if (!controller.signal.aborted) {
          setError(cause instanceof Error ? cause.message : 'Project inspection unavailable.');
        }
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    }
    void load();
    return () => controller.abort();
  }, [endpoint]);

  return (
    <section className="snapshot-manifest syntax-view" aria-label="Static C# project membership">
      <h4>Static C# project candidates</h4>
      {loading && <p role="status">Inspecting saved project files without running MSBuild…</p>}
      {error && <p className="error" role="alert">{error}</p>}
      {report && (
        <>
          <p>{report.projectCount} project files · {report.cSharpFileCount} captured C# files ·
            {' '}{report.unmappedFiles.length} files without a static project candidate</p>
          <p className="workspace-note">
            {report.analyzerVersion} · {report.method}.
            These are candidate file memberships, not compiler-verified project inputs.
          </p>
          {report.projects.length === 0 && (
            <p>No .csproj files were captured in this snapshot. No project membership is inferred.</p>
          )}
          {report.projects.map(project => (
            <section className="snapshot-manifest" key={project.projectPath}>
              <h4>{project.projectPath}</h4>
              <code>Project SHA-256: {project.projectSha256.slice(0, 20)}…</code>
              <p>
                SDK: {project.declaredSdk ?? 'not declared'} ·
                {' '}{project.candidateFiles.length} candidate C# files
              </p>
              <p className="workspace-note">
                Implicit SDK Compile pattern inferred:
                {' '}{project.defaultCompileItemsInferred ? 'yes, provisionally' : 'no'}
              </p>
              <details open>
                <summary>Candidate source files ({project.candidateFiles.length})</summary>
                <ul className="syntax-declaration-list">
                  {project.candidateFiles.slice(0, 100).map(file => (
                    <li key={file.path}>
                      <code>{file.path} · SHA-256 {file.sha256.slice(0, 16)}…</code>
                      <span>{file.inclusionBasis}</span>
                    </li>
                  ))}
                </ul>
                {project.candidateFiles.length > 100 && (
                  <p className="workspace-note">
                    Showing the first 100 candidates; the local API retains the complete result.
                  </p>
                )}
              </details>
              <details>
                <summary>Unresolved project rules ({project.unresolvedRules.length})</summary>
                <ul>{project.unresolvedRules.map(rule => <li key={rule}>{rule}</li>)}</ul>
              </details>
            </section>
          ))}
          {report.unmappedFiles.length > 0 && (
            <details>
              <summary>Files without candidate project membership ({report.unmappedFiles.length})</summary>
              <ul className="syntax-declaration-list">
                {report.unmappedFiles.slice(0, 100).map(file => (
                  <li key={file.path}>
                    <code>{file.path} · SHA-256 {file.sha256.slice(0, 16)}…</code>
                  </li>
                ))}
              </ul>
            </details>
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

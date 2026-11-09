import { useEffect, useState } from 'react';

type ProjectEvidence = {
  projectPath: string;
  projectSha256: string;
  inclusionBasis: string;
  unresolvedRuleCount: number;
};

type TypeUsage = {
  usageKind: string;
  writtenType: string;
  status: string;
  filePath: string;
  fileSha256: string;
  line: number;
  column: number;
  target: {
    name: string;
    filePath: string;
    fileSha256: string;
    line: number;
    column: number;
  } | null;
};

type ContextualUsage = {
  typeUsage: TypeUsage;
  projectContextStatus: string;
  explanation: string;
  sourceProjects: ProjectEvidence[];
  targetProjects: ProjectEvidence[];
  sharedCandidateProjects: ProjectEvidence[];
};

type ContextReport = {
  workspaceId: string;
  repositoryId: string;
  snapshotId: string;
  fingerprintSha256: string;
  analyzerVersion: string;
  typeLinkAnalyzerVersion: string;
  projectAnalyzerVersion: string;
  typeUsageCount: number;
  sharedCandidateCount: number;
  disjointCandidateCount: number;
  unknownProjectCount: number;
  unresolvedTargetCount: number;
  usages: ContextualUsage[];
  limitations: string[];
};

function ProjectList({ projects, label }: { projects: ProjectEvidence[]; label: string }) {
  return (
    <div className="workspace-note">
      <strong>{label}:</strong>{' '}
      {projects.length === 0
        ? 'None identified by static inspection'
        : projects.map(project => (
            <span key={project.projectPath}>
              <code>{project.projectPath}</code>
              {' '}({project.inclusionBasis}; project SHA-256
              {' '}{project.projectSha256.slice(0, 12)}…;
              {' '}{project.unresolvedRuleCount} unresolved rules){' '}
            </span>
          ))}
    </div>
  );
}

export function CSharpLinkContextView({ endpoint }: { endpoint: string }) {
  const [report, setReport] = useState<ContextReport | null>(null);
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
          throw new Error(result.error ?? 'Project/link context failed (HTTP ' + response.status + ')');
        }
        const result: ContextReport = await response.json();
        if (!controller.signal.aborted) setReport(result);
      } catch (cause) {
        if (!controller.signal.aborted)
          setError(cause instanceof Error ? cause.message : 'Project/link context unavailable.');
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    }
    void load();
    return () => controller.abort();
  }, [endpoint]);

  return (
    <section className="snapshot-manifest syntax-view" aria-label="C# project and type-link evidence">
      <h4>C# project and type-link context</h4>
      {loading && <p role="status">Joining verified snapshot evidence…</p>}
      {error && <p role="alert" className="error">{error}</p>}
      {report && (
        <>
          <p>
            {report.typeUsageCount} type uses ·
            {' '}{report.sharedCandidateCount} shared project candidates ·
            {' '}{report.disjointCandidateCount} disjoint project candidates ·
            {' '}{report.unknownProjectCount} unknown memberships ·
            {' '}{report.unresolvedTargetCount} unresolved targets
          </p>
          <p className="workspace-note">
            This reconciles {report.typeLinkAnalyzerVersion} with {report.projectAnalyzerVersion}.
            All relationships remain provisional. No project has been built or evaluated.
          </p>
          {report.usages.length === 0 && (
            <p>No inspected C# type uses were found in this saved snapshot.</p>
          )}
          <ul className="syntax-declaration-list">
            {report.usages.slice(0, 100).map((item, index) => (
              <li key={item.typeUsage.filePath + ':' + item.typeUsage.line + ':' +
                item.typeUsage.column + ':' + index}>
                <strong>{item.typeUsage.usageKind}: {item.typeUsage.writtenType} ·
                  {' '}{item.projectContextStatus}</strong>
                <code>
                  Usage: {item.typeUsage.filePath}:{item.typeUsage.line}:{item.typeUsage.column}
                  {' '}· SHA-256 {item.typeUsage.fileSha256.slice(0, 16)}…
                  {' '}· original link: {item.typeUsage.status}
                </code>
                {item.typeUsage.target && (
                  <code>
                    Candidate target: {item.typeUsage.target.name}
                    {' '}at {item.typeUsage.target.filePath}:{item.typeUsage.target.line}:
                    {item.typeUsage.target.column}
                    {' '}· SHA-256 {item.typeUsage.target.fileSha256.slice(0, 16)}…
                  </code>
                )}
                <span>{item.explanation}</span>
                <ProjectList label="Source candidates" projects={item.sourceProjects} />
                <ProjectList label="Target candidates" projects={item.targetProjects} />
                <ProjectList label="Shared candidates" projects={item.sharedCandidateProjects} />
              </li>
            ))}
          </ul>
          {report.usages.length > 100 && (
            <p className="workspace-note">
              Showing the first 100 uses; the full report remains available from the local API.
            </p>
          )}
          <details>
            <summary>Evidence and confidence limitations</summary>
            <ul>{report.limitations.map(limit => <li key={limit}>{limit}</li>)}</ul>
          </details>
        </>
      )}
    </section>
  );
}

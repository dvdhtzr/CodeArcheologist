namespace CodeArcheologist.Api.Workspaces;

public sealed record ProjectMembershipEvidence(
    string ProjectPath,
    string ProjectSha256,
    string InclusionBasis,
    int UnresolvedRuleCount);

public sealed record ContextualCSharpTypeUsage(
    CSharpTypeUsage TypeUsage,
    string ProjectContextStatus,
    string Explanation,
    IReadOnlyList<ProjectMembershipEvidence> SourceProjects,
    IReadOnlyList<ProjectMembershipEvidence> TargetProjects,
    IReadOnlyList<ProjectMembershipEvidence> SharedCandidateProjects);

public sealed record CSharpProjectLinkContextReport(
    Guid WorkspaceId,
    Guid RepositoryId,
    Guid SnapshotId,
    string FingerprintSha256,
    string AnalyzerVersion,
    string TypeLinkAnalyzerVersion,
    string ProjectAnalyzerVersion,
    int TypeUsageCount,
    int SharedCandidateCount,
    int DisjointCandidateCount,
    int UnknownProjectCount,
    int UnresolvedTargetCount,
    IReadOnlyList<ContextualCSharpTypeUsage> Usages,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Cross-checks two existing, independent snapshot analyzers. No project
/// evaluation or new Roslyn compilation is performed in this layer.
/// This reconciliation cannot turn provisional facts into verified edges.
/// </summary>
public sealed class CSharpProjectLinkContextInspector(
    CSharpTypeRelationshipInspector linkInspector,
    StaticCSharpProjectInspector projectInspector)
{
    public const string Version = "project-type-link-context-v1";

    public CSharpProjectLinkContextReport Inspect(
        SnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        // Both analyzers use only the captured file copies and each validates
        // the entire manifest and the individual files it consumes.
        var linkReport = linkInspector.Inspect(snapshot, cancellationToken);
        var projectReport = projectInspector.Inspect(snapshot, cancellationToken);
        if (linkReport.WorkspaceId != projectReport.WorkspaceId ||
            linkReport.RepositoryId != projectReport.RepositoryId ||
            linkReport.SnapshotId != projectReport.SnapshotId ||
            !string.Equals(linkReport.FingerprintSha256,
                projectReport.FingerprintSha256, StringComparison.Ordinal) ||
            linkReport.SnapshotId != snapshot.Id)
        {
            throw new SnapshotCaptureException(
                "Analyzers returned inconsistent saved snapshot provenance.", 409);
        }

        var projectsByFile = new Dictionary<string, List<ProjectMembershipEvidence>>(
            StringComparer.Ordinal);

        foreach (var project in projectReport.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in project.CandidateFiles)
            {
                if (!projectsByFile.TryGetValue(file.Path, out var candidates))
                {
                    candidates = new List<ProjectMembershipEvidence>();
                    projectsByFile.Add(file.Path, candidates);
                }
                candidates.Add(new ProjectMembershipEvidence(
                    project.ProjectPath,
                    project.ProjectSha256,
                    file.InclusionBasis,
                    project.UnresolvedRules.Count));
            }
        }

        IReadOnlyList<ProjectMembershipEvidence> ForFile(string path) =>
            projectsByFile.TryGetValue(path, out var projects)
                ? projects.OrderBy(item => item.ProjectPath, StringComparer.Ordinal).ToArray()
                : Array.Empty<ProjectMembershipEvidence>();

        var usages = new List<ContextualCSharpTypeUsage>(linkReport.Usages.Count);
        foreach (var usage in linkReport.Usages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ForFile(usage.FilePath);
            var target = usage.Target is null
                ? Array.Empty<ProjectMembershipEvidence>()
                : ForFile(usage.Target.FilePath).ToArray();

            var shared = source
                .Where(item => target.Any(other =>
                    string.Equals(item.ProjectPath, other.ProjectPath,
                        StringComparison.Ordinal) &&
                    string.Equals(item.ProjectSha256, other.ProjectSha256,
                        StringComparison.Ordinal)))
                .ToArray();

            string status;
            string explanation;
            if (usage.Target is null)
            {
                status = "unresolved-target";
                explanation = "Roslyn did not identify a unique captured declaration; no project relationship is inferred.";
            }
            else if (source.Count == 0 || target.Length == 0)
            {
                status = "unknown-project-candidate";
                explanation = "One or both source files have no static project candidate; missing candidates do not prove exclusion from a real project.";
            }
            else if (shared.Length > 0)
            {
                status = "shared-project-candidate";
                explanation = "The usage and candidate target appear together under a static project membership hypothesis; this is not verified compilation or a dependency.";
            }
            else
            {
                status = "disjoint-project-candidates";
                explanation = "The current static project candidates do not overlap; real imports, conditions and project references were not evaluated.";
            }

            usages.Add(new ContextualCSharpTypeUsage(
                usage, status, explanation, source, target, shared));
        }

        return new CSharpProjectLinkContextReport(
            snapshot.WorkspaceId,
            snapshot.RepositoryId,
            snapshot.Id,
            snapshot.FingerprintSha256,
            Version,
            linkReport.AnalyzerVersion,
            projectReport.AnalyzerVersion,
            usages.Count,
            usages.Count(item => item.ProjectContextStatus == "shared-project-candidate"),
            usages.Count(item => item.ProjectContextStatus == "disjoint-project-candidates"),
            usages.Count(item => item.ProjectContextStatus == "unknown-project-candidate"),
            usages.Count(item => item.ProjectContextStatus == "unresolved-target"),
            usages,
            new[]
            {
                "Project context is a cross-check of candidate-only .csproj membership and reference-free synthetic Roslyn binding.",
                "Shared project candidates are not verified compiler inputs, real inheritance or confirmed dependencies.",
                "Disjoint or missing candidates do not prove a source file cannot belong to or be referenced by another project.",
                "Imports, conditional Compile items, SDK default excludes, generated sources and referenced assemblies were not evaluated.",
                "No MSBuild, restore, source generators, imported analyzers, build targets or source code were executed.",
                "Every location and SHA-256 is from the saved snapshot; the registered source tree is not reopened."
            });
    }
}

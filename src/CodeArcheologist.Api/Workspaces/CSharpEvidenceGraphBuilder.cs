namespace CodeArcheologist.Api.Workspaces;

public sealed record EvidenceGraphNode(
    string Id,
    string Kind,
    string Label,
    string? FilePath,
    string? FileSha256,
    int? Line,
    int? Column,
    string? Detail);

public sealed record EvidenceGraphEdge(
    string Id,
    string FromId,
    string ToId,
    string Kind,
    string Status,
    string? ProjectContextStatus,
    string EvidencePath,
    string EvidenceSha256,
    int? Line,
    int? Column,
    string Explanation);

public sealed record CSharpEvidenceGraphReport(
    Guid WorkspaceId,
    Guid RepositoryId,
    Guid SnapshotId,
    string FingerprintSha256,
    string AnalyzerVersion,
    string LinkContextAnalyzerVersion,
    string ProjectAnalyzerVersion,
    int NodeCount,
    int EdgeCount,
    IReadOnlyList<EvidenceGraphNode> Nodes,
    IReadOnlyList<EvidenceGraphEdge> Edges,
    IReadOnlyList<string> Limitations);

/// <summary>
/// A deterministic, read-only graph projection of verified snapshot bytes
/// and two existing, explicitly provisional analyzers. No imported binaries,
/// source code, MSBuild or project targets are executed. This is neither a
/// compiled dependency graph nor an authenticated Git revision.
/// </summary>
public sealed class CSharpEvidenceGraphBuilder(
    SnapshotCaptureService capture,
    StaticCSharpProjectInspector projectInspector,
    CSharpProjectLinkContextInspector linkContextInspector)
{
    public const string Version = "csharp-snapshot-evidence-graph-v1";
    private const int MaxNodes = 10000;
    private const int MaxEdges = 16000;

    public CSharpEvidenceGraphReport Build(
        SnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        var context = linkContextInspector.Inspect(snapshot, cancellationToken);
        var projects = projectInspector.Inspect(snapshot, cancellationToken);
        var manifest = capture.ReadManifest(snapshot);
        if (context.WorkspaceId != snapshot.WorkspaceId ||
            context.RepositoryId != snapshot.RepositoryId ||
            context.SnapshotId != snapshot.Id ||
            projects.WorkspaceId != snapshot.WorkspaceId ||
            projects.RepositoryId != snapshot.RepositoryId ||
            projects.SnapshotId != snapshot.Id ||
            !string.Equals(context.FingerprintSha256,
                snapshot.FingerprintSha256, StringComparison.Ordinal) ||
            !string.Equals(projects.FingerprintSha256,
                snapshot.FingerprintSha256, StringComparison.Ordinal) ||
            !string.Equals(manifest.FingerprintSha256,
                snapshot.FingerprintSha256, StringComparison.Ordinal))
        {
            throw new SnapshotCaptureException(
                "Evidence analyzers returned mismatched snapshot provenance.", 409);
        }

        var nodes = new Dictionary<string, EvidenceGraphNode>(StringComparer.Ordinal);
        var edges = new Dictionary<string, EvidenceGraphEdge>(StringComparer.Ordinal);

        void AddNode(EvidenceGraphNode node)
        {
            if (nodes.TryGetValue(node.Id, out var existing))
            {
                if (existing != node)
                    throw new SnapshotCaptureException(
                        "Conflicting graph node evidence for the same saved identity.", 409);
                return;
            }
            if (nodes.Count >= MaxNodes)
                throw new SnapshotCaptureException(
                    "C# evidence graph exceeds the 10000-node limit.", 413);
            nodes.Add(node.Id, node);
        }

        void AddEdge(EvidenceGraphEdge edge)
        {
            if (!nodes.ContainsKey(edge.FromId) || !nodes.ContainsKey(edge.ToId))
                throw new SnapshotCaptureException(
                    "Evidence edge endpoints were not found in this snapshot.", 409);
            if (edges.TryGetValue(edge.Id, out var existing))
            {
                if (existing != edge)
                    throw new SnapshotCaptureException(
                        "Conflicting graph edge evidence for the same saved identity.", 409);
                return;
            }
            if (edges.Count >= MaxEdges)
                throw new SnapshotCaptureException(
                    "C# evidence graph exceeds the 16000-edge limit.", 413);
            edges.Add(edge.Id, edge);
        }

        // File nodes are copied artifacts, not inferred dependencies.
        foreach (var file in manifest.Files
            .Where(file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddNode(new EvidenceGraphNode(
                "file:" + file.Path, "csharp-file", file.Path,
                file.Path, file.Sha256, null, null,
                "SHA-256 verified copied source file"));
        }

        // Static .csproj membership is only a hypothesis, even for a literal
        // unconditional Compile Include: imported rules were not evaluated.
        foreach (var project in projects.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectId = "project:" + project.ProjectPath;
            AddNode(new EvidenceGraphNode(
                projectId, "project-file", project.ProjectPath,
                project.ProjectPath, project.ProjectSha256, null, null,
                "Saved project XML, not evaluated by MSBuild"));
            foreach (var member in project.CandidateFiles)
            {
                var fileId = "file:" + member.Path;
                AddEdge(new EvidenceGraphEdge(
                    "membership:" + project.ProjectPath + "->" + member.Path,
                    projectId, fileId, "candidate-compile-item", "candidate",
                    member.InclusionBasis, project.ProjectPath, project.ProjectSha256,
                    null, null,
                    "Static project-file inclusion hypothesis (" +
                    member.InclusionBasis + "); " +
                    project.UnresolvedRules.Count +
                    " unresolved project rules. Not a verified compiler input."));
            }
        }

        // Each candidate target is a real observed saved source location,
        // but its binding to the usage is only provisional-local.
        foreach (var contextual in context.Usages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var usage = contextual.TypeUsage;
            var fileId = "file:" + usage.FilePath;

            if (usage.Target is { } target)
            {
                var declarationId = "declaration:" + target.FilePath + ":" +
                    target.FileSha256 + ":" + target.SpanStart + ":" + target.SpanLength;
                AddNode(new EvidenceGraphNode(
                    declarationId, "target-declaration",
                    (target.Namespace.Length == 0 ? "" : target.Namespace + ".") +
                    target.Name,
                    target.FilePath, target.FileSha256,
                    target.Line, target.Column,
                    "Unique candidate source declaration from synthetic Roslyn binding"));
                AddEdge(new EvidenceGraphEdge(
                    "declared-in:" + declarationId,
                    declarationId, "file:" + target.FilePath,
                    "declared-in", "observed-location", null,
                    target.FilePath, target.FileSha256,
                    target.Line, target.Column,
                    "Candidate declaration location observed in saved source text; not proof of project compilation."));
                AddEdge(new EvidenceGraphEdge(
                    "type-use:" + usage.FilePath + ":" + usage.SpanStart +
                    ":" + usage.SpanLength + ":" + usage.UsageKind,
                    fileId, declarationId,
                    usage.UsageKind, "provisional-local",
                    contextual.ProjectContextStatus,
                    usage.FilePath, usage.FileSha256,
                    usage.Line, usage.Column,
                    contextual.Explanation));
            }
            else
            {
                // An unresolved occurrence has no fabricated declaration target.
                var occurrenceId = "unresolved:" + usage.FilePath + ":" +
                    usage.FileSha256 + ":" + usage.SpanStart + ":" +
                    usage.SpanLength + ":" + usage.UsageKind;
                AddNode(new EvidenceGraphNode(
                    occurrenceId, "unresolved-use",
                    usage.WrittenType, usage.FilePath, usage.FileSha256,
                    usage.Line, usage.Column,
                    "Unresolved type-use occurrence, not a declaration"));
                AddEdge(new EvidenceGraphEdge(
                    "unresolved-use:" + occurrenceId,
                    fileId, occurrenceId,
                    usage.UsageKind, "unresolved",
                    contextual.ProjectContextStatus,
                    usage.FilePath, usage.FileSha256,
                    usage.Line, usage.Column,
                    contextual.Explanation));
            }
        }

        return new CSharpEvidenceGraphReport(
            snapshot.WorkspaceId,
            snapshot.RepositoryId,
            snapshot.Id,
            snapshot.FingerprintSha256,
            Version,
            context.AnalyzerVersion,
            projects.AnalyzerVersion,
            nodes.Count,
            edges.Count,
            nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray(),
            edges.Values.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray(),
            new[]
            {
                "This is an on-demand evidence graph projection, not a verified compiler dependency graph or persistent graph database.",
                "Project-to-file edges are candidate-only; imports, conditions, SDK excludes and real compilation inputs were not evaluated.",
                "C# type-use edges are provisional-local even when both files share a project candidate.",
                "Target-declaration nodes are limited to declarations encountered by the prior type-use inspector; this is not a complete symbol graph.",
                "An unresolved-use node records the written source occurrence and never fabricates a declaration or external dependency.",
                "Only base-type clauses and explicit object creations are covered, not method calls, DI, data flow or runtime behavior.",
                "The nodes and links are derived entirely from verified saved snapshot file paths, hashes and source spans."
            });
    }
}

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
    string SyntaxAnalyzerVersion,
    int SyntaxDeclarationCount,
    string InvocationAnalyzerVersion,
    int InvocationCount,
    int MethodContainedInvocationCount,
    string CalleeAnalyzerVersion,
    int ProvisionalCalleeCount,
    int UnresolvedCalleeCount,
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
    CSharpProjectLinkContextInspector linkContextInspector,
    CSharpSyntaxInspector syntaxInspector,
    CSharpInvocationSyntaxInspector invocationInspector,
    CSharpProvisionalInvocationBinder calleeBinder)
{
    public const string Version = "csharp-snapshot-evidence-graph-v4";
    private const int MaxNodes = 10000;
    private const int MaxEdges = 16000;

    public CSharpEvidenceGraphReport Build(
        SnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        var context = linkContextInspector.Inspect(snapshot, cancellationToken);
        var projects = projectInspector.Inspect(snapshot, cancellationToken);
        var syntax = syntaxInspector.Inspect(snapshot, cancellationToken);
        var invocations = invocationInspector.Inspect(snapshot, cancellationToken);
        var callees = calleeBinder.Inspect(snapshot, cancellationToken);
        var manifest = capture.ReadManifest(snapshot);
        if (context.WorkspaceId != snapshot.WorkspaceId ||
            context.RepositoryId != snapshot.RepositoryId ||
            context.SnapshotId != snapshot.Id ||
            projects.WorkspaceId != snapshot.WorkspaceId ||
            projects.RepositoryId != snapshot.RepositoryId ||
            projects.SnapshotId != snapshot.Id ||
            syntax.WorkspaceId != snapshot.WorkspaceId ||
            syntax.RepositoryId != snapshot.RepositoryId ||
            syntax.SnapshotId != snapshot.Id ||
            invocations.WorkspaceId != snapshot.WorkspaceId ||
            invocations.RepositoryId != snapshot.RepositoryId ||
            invocations.SnapshotId != snapshot.Id ||
            callees.WorkspaceId != snapshot.WorkspaceId ||
            callees.RepositoryId != snapshot.RepositoryId ||
            callees.SnapshotId != snapshot.Id ||
            callees.InvocationCount != invocations.InvocationCount ||
            !string.Equals(callees.FingerprintSha256,
                snapshot.FingerprintSha256, StringComparison.Ordinal) ||
            !string.Equals(invocations.FingerprintSha256,
                snapshot.FingerprintSha256, StringComparison.Ordinal) ||
            !string.Equals(syntax.FingerprintSha256,
                snapshot.FingerprintSha256, StringComparison.Ordinal) ||
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

        // The syntax-only Roslyn inspector supplies ALL supported declaration
        // locations, not only those named by provisional type usages.
        // Target declarations already indexed by type links share the same
        // exact saved file hash and source span; reuse their graph nodes.
        // This avoids representing one physical declaration twice.
        foreach (var declaration in syntax.Declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var declarationId = "declaration:" + declaration.FilePath + ":" +
                declaration.FileSha256 + ":" + declaration.SpanStart +
                ":" + declaration.SpanLength;

            if (!nodes.ContainsKey(declarationId))
            {
                var qualifiedName = string.Join(".", new[]
                {
                    declaration.ContainingNamespace,
                    declaration.ContainingType,
                    declaration.Name
                }.Where(part => !string.IsNullOrEmpty(part)));

                AddNode(new EvidenceGraphNode(
                    declarationId, "syntax-declaration",
                    qualifiedName, declaration.FilePath, declaration.FileSha256,
                    declaration.StartLine, declaration.StartColumn,
                    declaration.Kind + " syntax declaration" +
                    (declaration.ParameterCount is int count
                        ? " (" + count + " parameters)" : "") +
                    "; not a resolved compiler symbol"));
            }

            var containmentId = "declared-in:" + declarationId;
            if (!edges.ContainsKey(containmentId))
            {
                AddEdge(new EvidenceGraphEdge(
                    containmentId,
                    declarationId, "file:" + declaration.FilePath,
                    "declared-in", "observed-location", null,
                    declaration.FilePath, declaration.FileSha256,
                    declaration.StartLine, declaration.StartColumn,
                    "Roslyn parsed a " + declaration.Kind +
                    " declaration from the saved source; this is not evidence that it compiles."));
            }
        }

        // These are call-expression occurrences, NOT links to the called
        // method. The nearest directly enclosing method/constructor is
        // observable syntax. All unsupported execution scopes (lambdas,
        // accessors, local functions, initializers) remain file-scoped rather
        // than assigning a potentially false caller.
        foreach (var invocation in invocations.Invocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var invocationId = "invocation:" + invocation.FilePath + ":" +
                invocation.FileSha256 + ":" + invocation.SpanStart +
                ":" + invocation.SpanLength;
            AddNode(new EvidenceGraphNode(
                invocationId,
                "invocation-site",
                invocation.WrittenExpression + "(…)",
                invocation.FilePath,
                invocation.FileSha256,
                invocation.Line,
                invocation.Column,
                invocation.ArgumentCount +
                " written argument(s); no project-verified callee"));

            var ownerId = invocation.ContainingMethod is { } caller
                ? "declaration:" + invocation.FilePath + ":" +
                    invocation.FileSha256 + ":" + caller.SpanStart +
                    ":" + caller.SpanLength
                : "file:" + invocation.FilePath;

            AddEdge(new EvidenceGraphEdge(
                "contains-invocation:" + invocationId,
                ownerId,
                invocationId,
                "contains-invocation",
                "observed-syntax",
                null,
                invocation.FilePath,
                invocation.FileSha256,
                invocation.Line,
                invocation.Column,
                "Observed invocation expression with " + invocation.ArgumentCount +
                " argument(s), contained in " +
                (invocation.ContainingMethod is null
                    ? "this source file (caller not assigned)"
                    : "the enclosing " + invocation.ContainingMethod.Kind +
                      " declaration") +
                ". No callee binding, runtime execution or project compilation verified."));
        }

        // Join the two independent reports by exact saved file/hash/span,
        // never by a method spelling. Missing or inconsistent occurrence
        // evidence invalidates the whole derived graph, not a subset.
        var observedSites = invocations.Invocations.ToDictionary(
            item => "invocation:" + item.FilePath + ":" +
                item.FileSha256 + ":" + item.SpanStart + ":" + item.SpanLength,
            item => item,
            StringComparer.Ordinal);
        var matchedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var binding in callees.Bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var siteId = "invocation:" + binding.FilePath + ":" +
                binding.FileSha256 + ":" + binding.SpanStart +
                ":" + binding.SpanLength;
            if (!observedSites.TryGetValue(siteId, out var observed) ||
                !matchedIds.Add(siteId) ||
                !nodes.TryGetValue(siteId, out var siteNode) ||
                siteNode.Kind != "invocation-site" ||
                observed.FileSha256 != binding.FileSha256)
            {
                throw new SnapshotCaptureException(
                    "Synthetic call binding did not match an observed saved invocation site.", 409);
            }

            if (binding.Target is not { } target)
            {
                if (binding.Status != "unresolved")
                    throw new SnapshotCaptureException(
                        "Unresolved callee evidence has an invalid status.", 409);
                continue; // No invented callee node or target edge.
            }

            if (binding.Status != "provisional-local")
                throw new SnapshotCaptureException(
                    "Candidate callee evidence has an invalid status.", 409);

            var targetId = "declaration:" + target.FilePath + ":" +
                target.FileSha256 + ":" + target.SpanStart +
                ":" + target.SpanLength;
            if (!nodes.TryGetValue(targetId, out var targetNode) ||
                (targetNode.Kind != "syntax-declaration" &&
                    targetNode.Kind != "target-declaration") ||
                targetNode.FileSha256 != target.FileSha256 ||
                targetNode.FilePath != target.FilePath)
            {
                throw new SnapshotCaptureException(
                    "Synthetic callee target was not present in saved syntax declarations.", 409);
            }

            AddEdge(new EvidenceGraphEdge(
                "provisional-callee:" + siteId,
                siteId, targetId,
                "candidate-callee", "provisional-local",
                null,
                binding.FilePath,
                binding.FileSha256,
                observed.Line,
                observed.Column,
                binding.Reason));
        }

        if (matchedIds.Count != observedSites.Count)
        {
            throw new SnapshotCaptureException(
                "Synthetic callee report omitted an observed invocation site.", 409);
        }

        return new CSharpEvidenceGraphReport(
            snapshot.WorkspaceId,
            snapshot.RepositoryId,
            snapshot.Id,
            snapshot.FingerprintSha256,
            Version,
            context.AnalyzerVersion,
            projects.AnalyzerVersion,
            syntax.AnalyzerVersion,
            syntax.Declarations.Count,
            invocations.AnalyzerVersion,
            invocations.InvocationCount,
            invocations.MethodContainedCount,
            callees.AnalyzerVersion,
            callees.ProvisionalCount,
            callees.UnresolvedCount,
            nodes.Count,
            edges.Count,
            nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray(),
            edges.Values.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray(),
            new[]
            {
                "This is an on-demand evidence graph projection, not a verified compiler dependency graph or persistent graph database.",
                "Project-to-file edges are candidate-only; imports, conditions, SDK excludes and real compilation inputs were not evaluated.",
                "C# type-use edges are provisional-local even when both files share a project candidate.",
                "Syntax declaration nodes cover the supported C# parser kinds; these are parsed source locations, not resolved compiler symbols.",
                "Existing target-declaration nodes are reused when their saved file hash and source span match a syntax declaration.",
                "Preprocessor conditions, partial types, generated code and overloads may prevent a complete semantic symbol inventory.",
                "An unresolved-use node records the written source occurrence and never fabricates a declaration or external dependency.",
                "Invocation syntax sites are observed and attributed to direct method/constructor syntax where possible; only provisional source-local callee bindings are proposed.",
                "A candidate-callee edge is a source-only reference-free synthetic Roslyn hypothesis, not a project-verified or runtime method-to-method call.",
                "Missing, ambiguous and unsupported callee targets have no outgoing candidate-callee edge; no name-based fallback is permitted.",
                "The earlier provisional type links cover only base-type clauses and explicit object creations; DI, data flow and runtime behavior remain unexamined.",
                "Lambda, local function, accessor and initializer invocations are file-scoped to avoid claiming an incorrect containing method.",
                "The nodes and links are derived entirely from verified saved snapshot file paths, hashes and source spans."
            });
    }
}

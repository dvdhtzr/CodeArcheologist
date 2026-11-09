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
    string Explanation,
    IReadOnlyList<ProjectMembershipEvidence>? SourceProjectCandidates = null,
    IReadOnlyList<ProjectMembershipEvidence>? TargetProjectCandidates = null,
    IReadOnlyList<ProjectMembershipEvidence>? SharedProjectCandidates = null);

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
    int CalleeSharedCandidateCount,
    int CalleeDisjointCandidateCount,
    int CalleeUnknownProjectCount,
    string HttpAttributeAnalyzerVersion,
    int HttpAttributeCandidateCount,
    int NonliteralHttpAttributeRouteCount,
    string MinimalApiAnalyzerVersion,
    int MinimalApiCandidateCount,
    int NonliteralMinimalApiRouteCount,
    string DiRegistrationAnalyzerVersion,
    int DiRegistrationCandidateCount,
    int DiUnresolvedShapeCount,
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
    CSharpProvisionalInvocationBinder calleeBinder,
    CSharpHttpAttributeInspector httpInspector,
    CSharpMinimalApiInspector minimalApiInspector,
    CSharpDiRegistrationInspector diInspector)
{
    public const string Version = "csharp-snapshot-evidence-graph-v8";
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
        var http = httpInspector.Inspect(snapshot, cancellationToken);
        var minimal = minimalApiInspector.Inspect(snapshot, cancellationToken);
        var di = diInspector.Inspect(snapshot, cancellationToken);
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
            http.WorkspaceId != snapshot.WorkspaceId ||
            http.RepositoryId != snapshot.RepositoryId ||
            http.SnapshotId != snapshot.Id ||
            minimal.WorkspaceId != snapshot.WorkspaceId ||
            minimal.RepositoryId != snapshot.RepositoryId ||
            minimal.SnapshotId != snapshot.Id ||
            di.WorkspaceId != snapshot.WorkspaceId ||
            di.RepositoryId != snapshot.RepositoryId ||
            di.SnapshotId != snapshot.Id ||
            !string.Equals(di.FingerprintSha256,
                snapshot.FingerprintSha256, StringComparison.Ordinal) ||
            !string.Equals(minimal.FingerprintSha256,
                snapshot.FingerprintSha256, StringComparison.Ordinal) ||
            !string.Equals(http.FingerprintSha256,
                snapshot.FingerprintSha256, StringComparison.Ordinal) ||
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

        // This is the exact same static, unevaluated candidate membership
        // report used to render project/file hypotheses. Do not call MSBuild,
        // derive project dependencies or promote any synthetic callee edge.
        var projectsByFile = new Dictionary<string, List<ProjectMembershipEvidence>>(
            StringComparer.Ordinal);
        foreach (var project in projects.Projects)
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

        IReadOnlyList<ProjectMembershipEvidence> Candidates(string path) =>
            projectsByFile.TryGetValue(path, out var candidates)
                ? candidates.OrderBy(item => item.ProjectPath, StringComparer.Ordinal).ToArray()
                : Array.Empty<ProjectMembershipEvidence>();

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

        // HTTP verb annotations are source-observed *spellings*. Their
        // presence is merely a candidate ASP.NET surface, NOT a verified
        // effective HTTP endpoint. The source method itself must have
        // already been indexed as a saved syntax declaration at this exact
        // path/hash/span. Never synthesize or guess a target method node.
        var syntaxMethods = syntax.Declarations
            .Where(item => item.Kind == "method")
            .ToDictionary(item =>
                "declaration:" + item.FilePath + ":" + item.FileSha256 +
                ":" + item.SpanStart + ":" + item.SpanLength,
                item => item,
                StringComparer.Ordinal);
        foreach (var candidate in http.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var methodId = "declaration:" + candidate.FilePath + ":" +
                candidate.FileSha256 + ":" + candidate.MethodSpanStart +
                ":" + candidate.MethodSpanLength;
            if (!syntaxMethods.TryGetValue(methodId, out var method) ||
                method.Name != candidate.MethodName ||
                !nodes.TryGetValue(methodId, out var methodNode) ||
                methodNode.Kind != "syntax-declaration" ||
                methodNode.FilePath != candidate.FilePath ||
                methodNode.FileSha256 != candidate.FileSha256)
            {
                throw new SnapshotCaptureException(
                    "HTTP annotation does not match a captured C# method declaration.", 409);
            }

            var endpointId = "http-attribute:" + candidate.FilePath + ":" +
                candidate.FileSha256 + ":" + candidate.AttributeSpanStart +
                ":" + candidate.AttributeSpanLength;
            var label = candidate.HttpVerb + " [" +
                candidate.WrittenAttribute + "] " + candidate.MethodName;
            var description =
                "Convention-name HTTP attribute candidate (not an ASP.NET-bound or registered endpoint). " +
                "Attribute argument route: " +
                (candidate.AttributeRouteLiteral is null
                    ? "(" + candidate.AttributeRouteStatus + ")"
                    : candidate.AttributeRouteLiteral) + "; " +
                "method [Route] argument: " +
                (candidate.MethodRouteLiteral is null
                    ? "(" + candidate.MethodRouteStatus + ")"
                    : candidate.MethodRouteLiteral) + "; " +
                "containing type [Route] argument: " +
                (candidate.TypeRouteLiteral is null
                    ? "(" + candidate.TypeRouteStatus + ")"
                    : candidate.TypeRouteLiteral) +
                ". These are separately observed source strings, NOT a composed effective URL.";
            AddNode(new EvidenceGraphNode(
                endpointId,
                "http-endpoint-attribute",
                label,
                candidate.FilePath, candidate.FileSha256,
                candidate.Line, candidate.Column,
                description));
            AddEdge(new EvidenceGraphEdge(
                "http-attribute-method:" + endpointId,
                endpointId,
                methodId,
                "annotates-method",
                "candidate",
                null,
                candidate.FilePath, candidate.FileSha256,
                candidate.Line, candidate.Column,
                "Observed a conventional written HTTP verb attribute on this method. " +
                "The actual attribute symbol, route mapping, controller activation, " +
                "project compilation and runtime reachability are NOT verified."));
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

        // Minimal API mapping spellings refer to EXISTING invocation syntax
        // nodes by exact snapshot file/hash/span. A written MapGet can be a
        // completely unrelated extension method: the mapping is candidate
        // evidence only, never a registered route or bound handler.
        var invocationNodes = invocations.Invocations.ToDictionary(
            site => "invocation:" + site.FilePath + ":" +
                site.FileSha256 + ":" + site.SpanStart + ":" + site.SpanLength,
            site => site,
            StringComparer.Ordinal);
        foreach (var mapping in minimal.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var invocationId = "invocation:" + mapping.FilePath + ":" +
                mapping.FileSha256 + ":" + mapping.SpanStart + ":" +
                mapping.SpanLength;
            if (!invocationNodes.TryGetValue(invocationId, out var observed) ||
                !nodes.TryGetValue(invocationId, out var invocationNode) ||
                invocationNode.Kind != "invocation-site" ||
                observed.Line != mapping.Line ||
                observed.Column != mapping.Column ||
                observed.FileSha256 != mapping.FileSha256)
            {
                throw new SnapshotCaptureException(
                    "Minimal API mapping did not match an observed saved invocation site.", 409);
            }

            var mappingId = "minimal-map:" + mapping.FilePath + ":" +
                mapping.FileSha256 + ":" + mapping.SpanStart + ":" +
                mapping.SpanLength;
            var routeText = mapping.WrittenRouteLiteral is null
                ? "(" + mapping.RouteStatus + ")"
                : mapping.WrittenRouteLiteral;
            AddNode(new EvidenceGraphNode(
                mappingId,
                "http-minimal-mapping",
                mapping.HttpVerb + " " + mapping.WrittenMemberName + " " + routeText,
                mapping.FilePath,
                mapping.FileSha256,
                mapping.Line,
                mapping.Column,
                "Conventional written " + mapping.WrittenMemberName +
                " member invocation on receiver " + mapping.WrittenReceiver +
                "; first route argument " + mapping.RouteStatus +
                ": " + routeText + "; handler " + mapping.HandlerSyntax +
                ". Candidate source spelling only: not a verified ASP.NET method, " +
                "registered runtime endpoint, effective route or bound handler."));
            AddEdge(new EvidenceGraphEdge(
                "minimal-mapping-invocation:" + mappingId,
                mappingId,
                invocationId,
                "candidate-http-mapping",
                "candidate",
                null,
                mapping.FilePath,
                mapping.FileSha256,
                mapping.Line,
                mapping.Column,
                "Observed a MapGet/MapPost/MapPut/MapPatch/MapDelete spelling " +
                "on this saved invocation. It may be an unrelated same-named " +
                "method and is NOT a registered or reachable HTTP endpoint."));
        }

        // DI candidates are conventional *written member names*. A source
        // AddScoped<TService,TImplementation>() does NOT prove the real DI
        // extension method, service identity, lifetime or runtime instance.
        // Preserve the independently observed invocation syntax and refuse
        // to invent a resolved registration edge between type declarations.
        foreach (var registration in di.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var invocationId = "invocation:" + registration.FilePath + ":" +
                registration.FileSha256 + ":" + registration.SpanStart + ":" +
                registration.SpanLength;
            if (!invocationNodes.TryGetValue(invocationId, out var observed) ||
                !nodes.TryGetValue(invocationId, out var callNode) ||
                callNode.Kind != "invocation-site" ||
                observed.FileSha256 != registration.FileSha256 ||
                observed.Line != registration.Line ||
                observed.Column != registration.Column)
            {
                throw new SnapshotCaptureException(
                    "DI registration did not match an observed saved invocation site.", 409);
            }

            var registrationId = "di-registration:" + registration.FilePath +
                ":" + registration.FileSha256 + ":" + registration.SpanStart +
                ":" + registration.SpanLength;
            var service = registration.ServiceTypeSyntax ?? "(unresolved)";
            var implementation = registration.ImplementationTypeSyntax ??
                "(unresolved)";
            AddNode(new EvidenceGraphNode(
                registrationId,
                "di-registration-candidate",
                registration.Lifetime + " " + registration.WrittenMemberName +
                    " " + service + " → " + implementation,
                registration.FilePath,
                registration.FileSha256,
                registration.Line,
                registration.Column,
                "Written receiver: " + registration.WrittenReceiver +
                "; lifetime by member spelling: " + registration.Lifetime +
                "; registration shape: " + registration.RegistrationShape +
                "; factory/instance status: " + registration.FactoryStatus +
                "; service type syntax: " + service +
                "; implementation type syntax: " + implementation +
                ". This is a source-syntax candidate, NOT a compiler-bound " +
                "Microsoft DI registration or runtime-resolved service."));
            AddEdge(new EvidenceGraphEdge(
                "di-registration-invocation:" + registrationId,
                registrationId,
                invocationId,
                "candidate-di-registration",
                "candidate",
                null,
                registration.FilePath,
                registration.FileSha256,
                registration.Line,
                registration.Column,
                "Observed conventional DI member name " +
                registration.WrittenMemberName + " at this exact invocation. " +
                "Service/implementation text and lifetime are source-only " +
                "hypotheses; runtime registration and resolution were NOT verified."));
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

            var sourceProjects = Candidates(binding.FilePath);
            var targetProjects = Candidates(target.FilePath);
            var sharedProjects = sourceProjects
                .Where(source => targetProjects.Any(other =>
                    string.Equals(source.ProjectPath, other.ProjectPath,
                        StringComparison.Ordinal) &&
                    string.Equals(source.ProjectSha256, other.ProjectSha256,
                        StringComparison.Ordinal)))
                .ToArray();

            // A shared candidate project may support the plausibility of a
            // synthetic match; missing/disjoint candidates never disprove it.
            string projectContext;
            string explanation;
            if (sourceProjects.Count == 0 || targetProjects.Count == 0)
            {
                projectContext = "unknown-project-candidate";
                explanation = "One or both captured files lack a static .csproj membership candidate; no real project exclusion can be inferred.";
            }
            else if (sharedProjects.Length > 0)
            {
                projectContext = "shared-project-candidate";
                explanation = "The source and target files share an unevaluated .csproj candidate; this does not prove the call compiles or is a runtime dependency.";
            }
            else
            {
                projectContext = "disjoint-project-candidates";
                explanation = "The current static .csproj candidates do not overlap. Real project references, imports and conditions were not evaluated.";
            }

            AddEdge(new EvidenceGraphEdge(
                "provisional-callee:" + siteId,
                siteId, targetId,
                "candidate-callee", "provisional-local",
                projectContext,
                binding.FilePath,
                binding.FileSha256,
                observed.Line,
                observed.Column,
                binding.Reason + " " + explanation,
                sourceProjects,
                targetProjects,
                sharedProjects));
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
            edges.Values.Count(edge => edge.Kind == "candidate-callee" &&
                edge.ProjectContextStatus == "shared-project-candidate"),
            edges.Values.Count(edge => edge.Kind == "candidate-callee" &&
                edge.ProjectContextStatus == "disjoint-project-candidates"),
            edges.Values.Count(edge => edge.Kind == "candidate-callee" &&
                edge.ProjectContextStatus == "unknown-project-candidate"),
            http.AnalyzerVersion,
            http.CandidateCount,
            http.NonliteralAttributeRouteCount,
            minimal.AnalyzerVersion,
            minimal.CandidateCount,
            minimal.NonliteralRouteCount,
            di.AnalyzerVersion,
            di.CandidateCount,
            di.UnresolvedShapeCount,
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
                "Callee project context is an independent static .csproj membership cross-check: shared, disjoint and unknown statuses never establish real compilation, project references, or execution.",
                "Candidate source/target/shared project evidence retains the saved .csproj SHA-256, inclusion basis and unresolved-rule count.",
                "Missing, ambiguous and unsupported callee targets have no outgoing candidate-callee edge; no name-based fallback is permitted.",
                "The earlier provisional type links cover only base-type clauses and explicit object creations; data flow and runtime behavior remain unexamined.",
                "HTTP endpoint attribute candidates recognize only simple conventional HTTP verb spellings on parsed methods; these are not resolved ASP.NET symbols, verified routes or runtime endpoints.",
                "HTTP attribute, method-level and type-level route strings are reported separately, never composed, substituted or interpreted as effective URLs.",
                "Controller attributes and Minimal API method spellings are distinct candidate inventories; neither provides verified ASP.NET symbols, registration or effective URL routes.",
                "Minimal API MapGet/MapPost/MapPut/MapPatch/MapDelete invocations are mapped only to exact saved invocation syntax nodes, without claiming handler binding.",
                "Same-named unrelated extension methods and conditional mappings may produce false-positive or incomplete Minimal API candidate sets.",
                "MapGroup/MapMethods/MapFallback, route composition, middleware, authorization and runtime configuration are not analyzed.",
                "DI candidate registrations recognize conventional AddScoped/AddSingleton/AddTransient member spellings with source-only service/implementation syntax; they do not prove Microsoft DI binding or runtime lifetime.",
                "DI factory/instance overloads, unresolved types, conditional overrides, registration order and real service resolution remain explicitly unknown.",
                "Lambda, local function, accessor and initializer invocations are file-scoped to avoid claiming an incorrect containing method.",
                "The nodes and links are derived entirely from verified saved snapshot file paths, hashes and source spans."
            });
    }
}

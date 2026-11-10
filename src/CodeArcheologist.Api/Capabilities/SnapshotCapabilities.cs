using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Capabilities;

/// <summary>
/// W2: first-class, read-only capability resources over the EXISTING
/// SHA-256-verified evidence graph. No new analyzer, edge kind, status or
/// inference: these are server-side projections of what the graph already
/// says, with candidate/provisional statuses and unknowns kept explicit.
/// </summary>
public static class SnapshotCapabilities
{
    public const string ContractVersion = "code-archeologist-capabilities-v1";
    private const int MaxListed = 2000;

    public sealed record SourceAnchor(string? FilePath, string? FileSha256, int? Line, int? Column);

    /// <summary>A written HTTP candidate. Label/detail are reported verbatim; routes are never composed.</summary>
    public sealed record EntryPointItem(string Id, string Kind, string EvidenceStatus, string Label,
        string? Detail, SourceAnchor Anchor);

    public sealed record EntryPointList(string ContractVersion, Guid SnapshotId, string FingerprintSha256,
        string AnalyzerVersion, int Count, bool Truncated, IReadOnlyList<EntryPointItem> Items, string Caveat);

    public sealed record MethodItem(string Id, string Label, string? Detail, SourceAnchor Anchor,
        int InboundProvisionalCandidates);

    public sealed record MethodList(string ContractVersion, Guid SnapshotId, string FingerprintSha256,
        string AnalyzerVersion, int Count, bool Truncated, IReadOnlyList<MethodItem> Items, string Caveat);

    public sealed record ImpactCandidate(string CalleeEdgeId, string EvidenceStatus, string InvocationId,
        SourceAnchor InvocationAnchor, string CallerId, string CallerKind, string CallerLabel, SourceAnchor CallerAnchor);

    public sealed record ImpactView(string ContractVersion, Guid SnapshotId, string FingerprintSha256,
        string AnalyzerVersion, MethodItem Method, IReadOnlyList<ImpactCandidate> DirectCandidates,
        int CandidateEdgesWithoutObservedContainment, int SnapshotUnresolvedCalleeCount,
        IReadOnlyList<string> Unknowns, string Caveat);

    private static SourceAnchor Anchor(EvidenceGraphNode n) => new(n.FilePath, n.FileSha256, n.Line, n.Column);

    private static bool IsMethod(EvidenceGraphNode n) =>
        n.Kind == "syntax-declaration" && n.Detail?.StartsWith("method syntax declaration", StringComparison.Ordinal) == true;

    public static void MapSnapshotCapabilities(this WebApplication app)
    {
        const string scope = "/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}";

        app.MapGet(scope + "/entry-points", (Guid workspaceId, Guid repositoryId, Guid snapshotId,
            WorkspaceStore store, CSharpEvidenceGraphBuilder builder, CancellationToken ct) =>
            WithGraph(store, builder, workspaceId, repositoryId, snapshotId, ct, (snapshot, graph) =>
            {
                var all = graph.Nodes.Where(n => n.Kind is "http-endpoint-attribute" or "http-minimal-mapping")
                    .OrderBy(n => n.FilePath, StringComparer.Ordinal).ThenBy(n => n.Line).ThenBy(n => n.Id, StringComparer.Ordinal)
                    .ToArray();
                return Results.Ok(new EntryPointList(ContractVersion, snapshot.Id, snapshot.FingerprintSha256,
                    graph.AnalyzerVersion, all.Length, all.Length > MaxListed,
                    all.Take(MaxListed).Select(n => new EntryPointItem(n.Id, n.Kind, "candidate", n.Label, n.Detail, Anchor(n))).ToArray(),
                    "Written HTTP syntax candidates only: not registered endpoints, effective URLs or reachable behavior. " +
                    "Unsupported routing conventions produce no candidates; absence of candidates is not absence of endpoints."));
            })).RequireAuthorization();

        app.MapGet(scope + "/methods", (Guid workspaceId, Guid repositoryId, Guid snapshotId,
            WorkspaceStore store, CSharpEvidenceGraphBuilder builder, CancellationToken ct) =>
            WithGraph(store, builder, workspaceId, repositoryId, snapshotId, ct, (snapshot, graph) =>
            {
                var inbound = graph.Edges.Where(e => e.Kind == "candidate-callee" && e.Status == "provisional-local")
                    .GroupBy(e => e.ToId).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                var all = graph.Nodes.Where(IsMethod)
                    .OrderBy(n => n.Label, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal).ToArray();
                return Results.Ok(new MethodList(ContractVersion, snapshot.Id, snapshot.FingerprintSha256,
                    graph.AnalyzerVersion, all.Length, all.Length > MaxListed,
                    all.Take(MaxListed).Select(n => new MethodItem(n.Id, n.Label, n.Detail, Anchor(n),
                        inbound.GetValueOrDefault(n.Id))).ToArray(),
                    "Method syntax declarations (parse locations), not compiler-verified symbols or a complete inventory."));
            })).RequireAuthorization();

        // ADR 0015 one-hop rule, server-side: reverse provisional candidate-callee
        // edges by exact node ID; attribute each invocation to its observed
        // containing scope. Never names, similarity or transitive propagation.
        app.MapGet(scope + "/impact", (Guid workspaceId, Guid repositoryId, Guid snapshotId, string? methodId,
            WorkspaceStore store, CSharpEvidenceGraphBuilder builder, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(methodId) || methodId.Length > 1024 || methodId.Any(char.IsControl))
                return Results.BadRequest(new { error = "A bounded methodId (graph node ID) is required." });
            return WithGraph(store, builder, workspaceId, repositoryId, snapshotId, ct, (snapshot, graph) =>
            {
                var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
                if (!nodes.TryGetValue(methodId, out var method) || !IsMethod(method))
                    return Results.NotFound(new { error = "methodId is not a method declaration in this snapshot." });
                var containment = graph.Edges.Where(e => e.Kind == "contains-invocation" && e.Status == "observed-syntax")
                    .GroupBy(e => e.ToId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                var callees = graph.Edges.Where(e => e.Kind == "candidate-callee" && e.Status == "provisional-local" &&
                    e.ToId == method.Id).OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
                var candidates = new List<ImpactCandidate>();
                var withoutContainment = 0;
                foreach (var edge in callees)
                {
                    if (!nodes.TryGetValue(edge.FromId, out var invocation) ||
                        !containment.TryGetValue(edge.FromId, out var contains) ||
                        !nodes.TryGetValue(contains.FromId, out var caller))
                    {
                        withoutContainment++; // kept visible as unknown, never guessed
                        continue;
                    }
                    candidates.Add(new(edge.Id, edge.Status, invocation.Id, Anchor(invocation),
                        caller.Id, caller.Kind, caller.Label, Anchor(caller)));
                }
                var inboundCount = callees.Length;
                return Results.Ok(new ImpactView(ContractVersion, snapshot.Id, snapshot.FingerprintSha256,
                    graph.AnalyzerVersion,
                    new MethodItem(method.Id, method.Label, method.Detail, Anchor(method), inboundCount),
                    candidates, withoutContainment, graph.UnresolvedCalleeCount,
                    new[]
                    {
                        "Invocations whose target could not be uniquely bound are unresolved and cannot be attributed to this method.",
                        "Dynamic, reflection-based, generated, external and cross-repository callers are unexamined.",
                        candidates.Count == 0 ? "No inbound candidates were found; this is unknown, not proof of no callers." : ""
                    }.Where(x => x.Length > 0).ToArray(),
                    "One-hop provisional-local candidates for review: not a verified call graph, breaking-change risk " +
                    "or transitive impact."));
            });
        }).RequireAuthorization();
    }

    private static IResult WithGraph(WorkspaceStore store, CSharpEvidenceGraphBuilder builder, Guid workspaceId,
        Guid repositoryId, Guid snapshotId, CancellationToken ct,
        Func<SnapshotRecord, CSharpEvidenceGraphReport, IResult> body)
    {
        var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
        if (snapshot is null) return Results.NotFound();
        try { return body(snapshot, builder.Build(snapshot, ct)); }
        catch (SnapshotCaptureException error) { return Results.Json(new { error = error.Message }, statusCode: error.StatusCode); }
        catch (IOException) { return Results.Conflict(new { error = "Saved graph evidence could not be verified." }); }
    }
}

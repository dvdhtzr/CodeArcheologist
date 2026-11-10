using System.Diagnostics;
using CodeArcheologist.Api.Execution;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeArcheologist.EvaluationLab;

public sealed record LabCitation(string CanonicalId, string? FilePath, string? FileSha256, int? Line,
    string EvidenceStatus);

/// <summary>One comparable statement. Kind: observation | hypothesis | concern | decision | uncertainty | summary.</summary>
public sealed record LabClaim(string Kind, string Role, string Text, IReadOnlyList<LabCitation> Citations);

/// <summary>Host facts needed for integrity scoring of a Gate C report.</summary>
public sealed record LabGateCFacts(IReadOnlyList<string> RoleStatuses, IReadOnlyList<string> ConcernIds,
    IReadOnlyList<string> AddressedConcernIds, bool AnyVerifiedFact, bool AnyStructurallySupported,
    bool ReconciliationAccepted, int CheckpointCount);

public sealed record LabReport(string SchemaVersion, string CaseId, string Comparator, int Trial,
    string Outcome, string Diagnostic, Guid SnapshotId, string FingerprintSha256, string EntryPointId,
    IReadOnlyList<LabClaim> Claims, LabGateCFacts? GateC, long DurationMs);

/// <summary>A case captured in an isolated data directory, ready for comparators.</summary>
public sealed class CapturedCase(LoadedCase loaded, ServiceProvider services, WorkspaceStore store,
    SnapshotRecord snapshot, GateCScope scope, AgentEvidencePackage package, string root) : IAsyncDisposable
{
    public LoadedCase Loaded { get; } = loaded;
    public ServiceProvider Services { get; } = services;
    public WorkspaceStore Store { get; } = store;
    public SnapshotRecord Snapshot { get; } = snapshot;
    public GateCScope Scope { get; } = scope;
    public AgentEvidencePackage Package { get; } = package;

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { Console.Error.WriteLine("WARNING: lab temp cleanup failed: " + root); }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine("WARNING: lab temp cleanup denied: " + root); }
    }
}

/// <summary>
/// Isolated execution of one case: copies the pinned fixture to a temporary
/// source directory, captures it through the normal read-only snapshot path
/// into a disposable data directory, resolves the entry point by exact
/// written-syntax location, and runs comparators through production code.
/// Never compiles or executes fixture code; never touches the user's data.
/// </summary>
public static class LabHarness
{
    public static async Task<CapturedCase> CaptureAsync(LoadedCase loaded, CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "code-archeologist-lab-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(data);
        foreach (var (file, hash) in loaded.Case.Source.FileSha256)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(loaded.Directory, "source", file), ct);
            if (LabSchema.Sha256(bytes) != hash)
                throw new InvalidOperationException("Fixture bytes changed since validation: " + file);
            var target = Path.Combine(source, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes, ct);
        }
        var (services, store, workspace, repository, snapshot, graph) =
            await CaptureSourceAsync(source, data, "Lab " + loaded.Case.Id, ct);
        var selector = loaded.Case.EntryPoint;
        var matches = graph.Nodes.Where(n => n.Kind == selector.Kind && n.FilePath == selector.FilePath &&
            n.Line == selector.Line).ToArray();
        if (matches.Length != 1)
            throw new LabCaseException(loaded.Case.Id, $"entry-point selector matched {matches.Length} candidates; " +
                "available: " + string.Join(", ", graph.Nodes.Where(n => n.Kind.StartsWith("http-", StringComparison.Ordinal))
                    .Select(n => $"{n.Kind}@{n.FilePath}:{n.Line}")));
        var package = services.GetRequiredService<SnapshotEvidenceTools>().ReadEntryPoint(snapshot, matches[0].Id, ct);
        var scope = new GateCScope(workspace.Id, repository.Id, snapshot.Id, snapshot.FingerprintSha256,
            matches[0].Id, SnapshotEvidenceTools.Version);
        return new(loaded, services, store, snapshot, scope, package, root);
    }

    /// <summary>
    /// Read-only capture of a local source directory through the production
    /// services into the given disposable data directory. The source is only
    /// read; .git and build outputs are excluded by the snapshot policy.
    /// </summary>
    internal static async Task<(ServiceProvider, WorkspaceStore, WorkspaceRecord, RepositorySourceRecord,
        SnapshotRecord, CSharpEvidenceGraphReport)> CaptureSourceAsync(string source, string data, string name,
        CancellationToken ct)
    {
        // Process-local override for this disposable lab run only.
        Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", data);
        var services = new ServiceCollection()
            .AddSingleton<WorkspaceStore>().AddSingleton<SnapshotCaptureService>()
            .AddSingleton<CSharpSyntaxInspector>().AddSingleton<CSharpInvocationSyntaxInspector>()
            .AddSingleton<CSharpProvisionalInvocationBinder>().AddSingleton<CSharpHttpAttributeInspector>()
            .AddSingleton<CSharpMinimalApiInspector>().AddSingleton<CSharpDiRegistrationInspector>()
            .AddSingleton<CSharpTypeRelationshipInspector>().AddSingleton<StaticCSharpProjectInspector>()
            .AddSingleton<CSharpProjectLinkContextInspector>().AddSingleton<CSharpEvidenceGraphBuilder>()
            .AddSingleton<SnapshotEvidenceTools>().AddSingleton<GateCEvidenceDiscovery>()
            .AddSingleton<GateBInvestigator>()
            .BuildServiceProvider();
        try
        {
            var store = services.GetRequiredService<WorkspaceStore>();
            store.Initialize();
            var workspace = store.Create(name, Guid.NewGuid()).Workspace;
            var location = RepositoryPathPolicy.Validate(source, store.DatabasePath).Location
                ?? throw new InvalidOperationException("Lab source registration rejected.");
            var repository = store.RegisterRepository(workspace.Id, location, Guid.NewGuid()).Repository
                ?? throw new InvalidOperationException("Lab source registration failed.");
            var snapshot = (await services.GetRequiredService<SnapshotCaptureService>()
                .CaptureAsync(workspace.Id, repository.Id, Guid.NewGuid(), ct)).Snapshot;
            var graph = services.GetRequiredService<CSharpEvidenceGraphBuilder>().Build(snapshot, ct);
            return (services, store, workspace, repository, snapshot, graph);
        }
        catch
        {
            await services.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Every required-observation anchor must be citable: some reference or
    /// relation in the evidence package sits at that file and line.
    /// </summary>
    public static IReadOnlyList<string> UnsatisfiableAnchors(CapturedCase captured) =>
        captured.Loaded.Labels.RequiredObservations
            .Where(o => !Citable(captured.Package).Any(c => c.FilePath == o.Anchor.FilePath && c.Line == o.Anchor.Line))
            .Select(o => o.Id).ToArray();

    public static IEnumerable<LabCitation> Citable(AgentEvidencePackage package) =>
        package.References.Select(r => new LabCitation(r.Id, r.FilePath, r.FileSha256, r.Line, r.EvidenceStatus))
            .Concat(package.Relations.Select(r =>
                new LabCitation(r.Id, r.EvidencePath, r.EvidenceSha256, r.Line, r.EvidenceStatus)));

    private static LabCitation? Cite(AgentEvidencePackage package, string canonicalId) =>
        Citable(package).FirstOrDefault(c => c.CanonicalId == canonicalId);

    // ------------------------------------------------------------- baseline

    /// <summary>
    /// Deterministic comparator: the evidence package restated without any
    /// model. It states only host-observed relations and declared unknowns.
    /// </summary>
    public static LabReport Baseline(CapturedCase c, int trial)
    {
        var watch = Stopwatch.StartNew();
        var claims = new List<LabClaim>();
        foreach (var relation in c.Package.Relations.OrderBy(r => r.Id, StringComparer.Ordinal))
            claims.Add(new("observation", "baseline",
                $"{relation.Kind} ({relation.EvidenceStatus}): {relation.Explanation}",
                [new(relation.Id, relation.EvidencePath, relation.EvidenceSha256, relation.Line, relation.EvidenceStatus)]));
        foreach (var unknown in c.Package.Unknowns.Concat(c.Package.Limitations))
            claims.Add(new("uncertainty", "baseline", unknown, []));
        return Report(c, Comparator.Baseline, trial, "completed", "deterministic-baseline", claims, null, watch);
    }

    // --------------------------------------------------------------- Gate B

    public static async Task<LabReport> GateBAsync(CapturedCase c, int trial, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var result = await c.Services.GetRequiredService<GateBInvestigator>()
            .InvestigateAsync(c.Snapshot, c.Scope.EntryPointId, ct);
        var claims = new List<LabClaim>();
        var accepted = result.Result is not null &&
            result.Outcome == "accepted-reference-valid-not-semantic-proof";
        if (result.Result is { } r)
        {
            claims.Add(new("summary", "gate-b", r.Summary, []));
            foreach (var finding in r.Findings)
                claims.Add(new("observation", "gate-b", finding.ClaimKind + ": " + finding.Statement,
                    Cite(c.Package, finding.RelationId) is { } cite ? [cite]
                        : [new(finding.RelationId, null, null, null, "unknown")]));
            foreach (var unknown in r.Unknowns) claims.Add(new("uncertainty", "gate-b", unknown, []));
        }
        return Report(c, Comparator.GateB, trial, accepted ? "completed" : "incomplete", result.Diagnostic,
            claims, null, watch);
    }

    // --------------------------------------------------------------- Gate C

    /// <summary>
    /// Production Gate C path: fenced admission, the managed worker and the
    /// checkpoint-aware coordinator with the supplied role runner (genuine
    /// MAF in real runs; a model-free fixture only in self-tests).
    /// </summary>
    public static async Task<LabReport> GateCAsync(CapturedCase c, int trial, IGateCRoleStageRunner roles,
        TimeSpan timeout, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var discovery = c.Services.GetRequiredService<GateCEvidenceDiscovery>();
        var revalidator = new GateC6CheckpointRevalidator(discovery);
        using var ownership = new ExecutionOwnershipState();
        if (!ownership.TryAcquire(c.Store))
            throw new InvalidOperationException("Lab could not acquire execution ownership: " + ownership.Diagnostic);
        var signal = new InvestigationWorkSignal();
        var worker = new InvestigationWorker(c.Store, ownership, signal,
            new GateC6StageCoordinator(c.Store, discovery, roles, revalidator),
            InvestigationWorkerOptions.Default, NullLogger<InvestigationWorker>.Instance);
        await worker.StartAsync(ct);
        Guid run;
        try
        {
            var admitted = new InvestigationAdmissionService(c.Store, ownership, signal).Admit(c.Scope, Guid.NewGuid());
            if (admitted.Outcome != InvestigationAdmissionOutcome.Admitted)
                throw new InvalidOperationException("Lab admission refused: " + admitted.Outcome);
            run = admitted.Record!.RunId;
            while (!GateC6AttemptTransitions.IsTerminal(c.Store.GetAttempt(run)!.Status))
            {
                if (watch.Elapsed > timeout) break;
                await Task.Delay(200, ct);
            }
        }
        finally
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await worker.StopAsync(grace.Token);
        }
        var attempt = c.Store.GetAttempt(run)!;
        var detail = c.Store.GetInvestigation(c.Scope.WorkspaceId, c.Scope.RepositoryId, c.Scope.SnapshotId, run)!;
        var claims = new List<LabClaim>();
        LabGateCFacts? facts = null;
        if (detail.Report is { } report)
        {
            LabCitation[] Cites(GateCRole role, IEnumerable<string> handles) => handles
                .Select(h => report.EvidenceLinks.FirstOrDefault(l => l.Role == role && l.Handle == h))
                .Select(l => l is null ? new LabCitation("unresolved-handle", null, null, null, "unknown")
                    : new LabCitation(l.CanonicalId, l.FilePath, l.FileSha256, l.Line, l.EvidenceStatus))
                .ToArray();
            foreach (var h in report.Investigator.Assessment?.Hypotheses ?? [])
                claims.Add(new("hypothesis", "investigator", h.Text + " | Uncertainty: " + h.Uncertainty,
                    Cites(GateCRole.Investigator, h.EvidenceHandles)));
            foreach (var s in report.Skeptic.Assessment?.Concerns ?? [])
                claims.Add(new("concern", "skeptic", s.Kind + ": " + s.Explanation,
                    Cites(GateCRole.Skeptic, s.EvidenceHandles)));
            foreach (var d in report.Reviewer.Assessment?.Decisions ?? [])
                claims.Add(new("decision", "reviewer", d.HypothesisId + " " + d.Recommendation + ": " + d.Explanation, []));
            foreach (var u in (report.Investigator.Assessment?.Unknowns ?? []).Concat(report.Skeptic.Assessment?.Unknowns ?? []))
                claims.Add(new("uncertainty", "gate-c", u, []));
            facts = new(
                [report.Investigator.Status.ToString(), report.Skeptic.Status.ToString(), report.Reviewer.Status.ToString()],
                report.Skeptic.Assessment?.Concerns.Select(x => x.Id).ToArray() ?? [],
                report.Reviewer.Assessment is { } ra
                    ? ra.Decisions.SelectMany(d => d.AddressedConcernIds).Concat(ra.UnresolvedGlobalConcernIds).ToArray()
                    : [],
                report.Reviewer.Reconciliation?.Dispositions.Any(d => d.IsVerifiedFact) == true,
                report.Reviewer.Assessment?.Decisions.Any(d =>
                    d.Recommendation == GateCRecommendation.StructurallySupported) == true,
                report.Reviewer.Reconciliation?.Check.Accepted == true,
                c.Store.ListCheckpoints(run).Count);
        }
        var outcome = detail.Summary.Status switch
        {
            "completed" => "completed",
            "running" => "timed-out-in-lab",
            var s => s
        };
        return Report(c, Comparator.GateC, trial, outcome, attempt.TerminalCause ?? detail.Summary.Diagnostic,
            claims, facts, watch);
    }

    private static LabReport Report(CapturedCase c, string comparator, int trial, string outcome, string diagnostic,
        IReadOnlyList<LabClaim> claims, LabGateCFacts? facts, Stopwatch watch) =>
        new(LabSchema.Report, c.Loaded.Case.Id, comparator, trial, outcome, diagnostic, c.Snapshot.Id,
            c.Snapshot.FingerprintSha256, c.Scope.EntryPointId, claims, facts, watch.ElapsedMilliseconds);
}

public sealed class LabCaseException(string caseId, string reason) : Exception(caseId + ": " + reason);

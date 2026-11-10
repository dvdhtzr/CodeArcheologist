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
    public static async Task<CapturedCase> CaptureAsync(LoadedCase loaded, CancellationToken ct,
        string? curatedRoot = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "code-archeologist-lab-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        string source;
        if (loaded.Curated is { } repo)
        {
            // Curated checkouts are captured in place, read-only; never copied, built or executed.
            source = CuratedSource(loaded, repo, curatedRoot);
        }
        else
        {
            source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            foreach (var (file, hash) in loaded.Case.Source.FileSha256)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(loaded.Directory, loaded.Case.Source.Directory, file), ct);
                if (LabSchema.Sha256(bytes) != hash)
                    throw new InvalidOperationException("Fixture bytes changed since validation: " + file);
                var target = Path.Combine(source, file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, bytes, ct);
            }
        }
        ServiceProvider services; WorkspaceStore store; WorkspaceRecord workspace;
        RepositorySourceRecord repository; SnapshotRecord snapshot; CSharpEvidenceGraphReport graph;
        try
        {
            (services, store, workspace, repository, snapshot, graph) =
                await CaptureSourceAsync(source, data, "Lab " + loaded.Case.Id, ct);
        }
        catch
        {
            TryDelete(root);
            throw;
        }
        if (loaded.Curated is { } pinned && snapshot.FingerprintSha256 != pinned.SnapshotFingerprint)
        {
            var detail = DescribeDifference(loaded, services, snapshot);
            await services.DisposeAsync();
            TryDelete(root);
            throw new LabCaseException(loaded.Case.Id, "snapshot fingerprint differs from the pinned manifest: " + detail);
        }
        var selector = loaded.Case.EntryPoint
            ?? throw new LabCaseException(loaded.Case.Id, "case has no entry point (non-investigate expectation)");
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
    /// Resolves and verifies an owner-acquired checkout: it must exist under
    /// the curated root, and its licence file must match the pinned SHA-256.
    /// The lab never invokes Git; the acquisition script handles fetching.
    /// </summary>
    public static string CuratedSource(LoadedCase loaded, CuratedRepository repo, string? curatedRoot)
    {
        if (curatedRoot is null)
            throw new LabCaseException(loaded.Case.Id, "curated root not supplied (--curated-root); case not acquired");
        var checkout = Path.GetFullPath(Path.Combine(curatedRoot, repo.Id));
        var licence = Path.Combine(checkout, repo.LicensePath);
        if (!File.Exists(licence))
            throw new LabCaseException(loaded.Case.Id, "checkout missing; run the curated acquisition script");
        if (LabSchema.Sha256(File.ReadAllBytes(licence)) != repo.LicenseSha256)
            throw new LabCaseException(loaded.Case.Id, "licence file differs from the pinned SHA-256");
        var source = Path.GetFullPath(Path.Combine(checkout, repo.Subtree));
        if (!source.StartsWith(checkout, StringComparison.Ordinal) || !Directory.Exists(source))
            throw new LabCaseException(loaded.Case.Id, "pinned subtree missing from checkout");
        return source;
    }

    public sealed record ExpectationResult(string CaseId, string Expectation, bool Confirmed, string Detail);

    /// <summary>
    /// Non-investigate curated cases. no-supported-candidate: capture must match
    /// the pinned fingerprint and report zero HTTP candidates (nothing invented).
    /// capture-limit-fail-closed: capture must fail with a snapshot limit and
    /// leave no snapshot record behind.
    /// </summary>
    public static async Task<ExpectationResult> CheckExpectationAsync(LoadedCase loaded, string? curatedRoot,
        CancellationToken ct)
    {
        var repo = loaded.Curated ?? throw new LabCaseException(loaded.Case.Id, "not a curated case");
        var source = CuratedSource(loaded, repo, curatedRoot);
        var root = Path.Combine(Path.GetTempPath(), "code-archeologist-lab-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        try
        {
            if (loaded.Case.Expectation == CaseExpectation.CaptureLimitFailClosed)
            {
                try
                {
                    var (s, _, _, _, _, _) = await CaptureSourceAsync(source, data, "Lab " + loaded.Case.Id, ct);
                    await s.DisposeAsync();
                    return new(loaded.Case.Id, loaded.Case.Expectation, false, "capture unexpectedly succeeded");
                }
                catch (SnapshotCaptureException error) when (error.StatusCode == 413)
                {
                    Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", data);
                    var check = new WorkspaceStore();
                    var snapshots = check.List(100).Items.Sum(w => check.ListRepositories(w.Id).Items
                        .Sum(r => check.ListSnapshots(w.Id, r.Id).Items.Count));
                    return new(loaded.Case.Id, loaded.Case.Expectation, snapshots == 0,
                        snapshots == 0 ? "failed closed (413) with no snapshot record" : "a partial snapshot record remained");
                }
            }
            var (services, _, _, _, snapshot, graph) = await CaptureSourceAsync(source, data, "Lab " + loaded.Case.Id, ct);
            await using var _s = services;
            if (snapshot.FingerprintSha256 != repo.SnapshotFingerprint)
                return new(loaded.Case.Id, loaded.Case.Expectation, false,
                    "fingerprint differs from the pinned manifest: " + DescribeDifference(loaded, services, snapshot));
            var candidates = graph.Nodes.Count(n => n.Kind is "http-endpoint-attribute" or "http-minimal-mapping");
            return new(loaded.Case.Id, loaded.Case.Expectation, candidates == 0,
                candidates == 0 ? "zero supported HTTP candidates; nothing invented" : $"{candidates} unexpected candidates");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDelete(root);
        }
    }

    /// <summary>
    /// Normalized entry evidence for change experiments: entry kind plus every
    /// reference and relation by kind, status, label, detail and file. Hashes,
    /// lines and IDs are excluded because they shift for unrelated reasons.
    /// </summary>
    public static string EvidenceSignature(AgentEvidencePackage package)
    {
        var refs = package.References.ToDictionary(r => r.Id, StringComparer.Ordinal);
        string Ref(string id) => refs.TryGetValue(id, out var r) ? $"{r.Kind}|{r.EvidenceStatus}|{r.Label}|{r.Detail}|{r.FilePath}" : "absent";
        return string.Join("\n", new[] { "entry|" + package.EntryPointKind }
            .Concat(package.References.Select(r => "ref|" + Ref(r.Id)))
            .Concat(package.Relations.Select(r => $"rel|{r.Kind}|{r.EvidenceStatus}|{Ref(r.FromId)}|{Ref(r.ToId)}"))
            .OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// C6c-2b deterministic checks (no model): (1) the entry's normalized
    /// evidence differs between before and after exactly when the edit is
    /// declared affected; (2) if an impact target is named, the ADR 0015
    /// one-hop rule (reverse provisional candidate-callee edges, attribute
    /// each invocation to its containing method) lists the entry method as a
    /// caller of that method in the BEFORE version exactly when affected.
    /// </summary>
    public static async Task<ExpectationResult> CheckChangeAsync(CapturedCase after, CancellationToken ct)
    {
        var loaded = after.Loaded;
        var spec = loaded.Case.Change ?? throw new LabCaseException(loaded.Case.Id, "not a change experiment");
        var root = Path.Combine(Path.GetTempPath(), "code-archeologist-lab-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        try
        {
            foreach (var (file, hash) in loaded.Case.Source.BeforeFileSha256!)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(loaded.Directory, "before", file), ct);
                if (LabSchema.Sha256(bytes) != hash) throw new LabCaseException(loaded.Case.Id, "before bytes changed: " + file);
                var target = Path.Combine(source, file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, bytes, ct);
            }
            var (services, _, _, _, snapshot, graph) =
                await CaptureSourceAsync(source, Path.Combine(root, "data"), "Lab before " + loaded.Case.Id, ct);
            await using var _s = services;
            var sel = spec.BeforeEntryPoint;
            var entry = graph.Nodes.Where(n => n.Kind == sel.Kind && n.FilePath == sel.FilePath && n.Line == sel.Line).ToArray();
            if (entry.Length != 1) throw new LabCaseException(loaded.Case.Id, "before entry selector matched " + entry.Length);
            var before = services.GetRequiredService<SnapshotEvidenceTools>().ReadEntryPoint(snapshot, entry[0].Id, ct);
            var affected = EvidenceSignature(before) != EvidenceSignature(after.Package);
            var expected = spec.Impact == "affected";
            var details = new List<string> { $"evidence {(affected ? "changed" : "unchanged")}" };
            var ok = affected == expected;
            if (spec.ImpactTarget is { } t)
            {
                var targets = graph.Nodes.Where(n => n.Kind == "syntax-declaration" && n.FilePath == t.FilePath && n.Line == t.Line).ToArray();
                var entryMethod = graph.Edges.SingleOrDefault(e => e.FromId == entry[0].Id && e.Kind == "annotates-method")?.ToId;
                if (targets.Length != 1 || entryMethod is null)
                    throw new LabCaseException(loaded.Case.Id, "impact target or entry method not uniquely resolvable");
                var callers = graph.Edges
                    .Where(e => e.Kind == "candidate-callee" && e.Status == "provisional-local" && e.ToId == targets[0].Id)
                    .SelectMany(callee => graph.Edges.Where(c => c.Kind == "contains-invocation" && c.ToId == callee.FromId))
                    .Select(c => c.FromId).ToHashSet(StringComparer.Ordinal);
                var isCaller = callers.Contains(entryMethod);
                details.Add($"one-hop: entry method {(isCaller ? "is" : "is not")} a candidate caller of the edited method");
                ok &= isCaller == expected;
            }
            return new(loaded.Case.Id, "change-" + spec.Impact, ok, string.Join("; ", details));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDelete(root);
        }
    }

    /// <summary>Pinned per-file manifest lines: file\tpath\tsize\tsha256 / excluded\tpath\treason.</summary>
    public static IReadOnlyList<string> ManifestLines(SnapshotManifest manifest) =>
        manifest.Files.Select(f => $"file\t{f.Path}\t{f.SizeBytes}\t{f.Sha256}")
            .Concat(manifest.Exclusions.Select(e => $"excluded\t{e.Path}\t{e.Reason}")).ToArray();

    /// <summary>
    /// Names exactly which captured entries differ from the pinned manifest so a
    /// mismatch is diagnosable (never accepted): missing, extra or changed files.
    /// </summary>
    private static string DescribeDifference(LoadedCase loaded, ServiceProvider services, SnapshotRecord snapshot)
    {
        var pinnedPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(loaded.Directory)!)!,
            "curated", "manifests", loaded.Curated!.Id + ".tsv");
        if (!File.Exists(pinnedPath)) return "no pinned per-file manifest to compare";
        var expected = File.ReadAllLines(pinnedPath).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
        var actual = ManifestLines(services.GetRequiredService<SnapshotCaptureService>().ReadManifest(snapshot))
            .ToHashSet(StringComparer.Ordinal);
        string Key(string line) => string.Join('\t', line.Split('\t').Take(2));
        var missing = expected.Except(actual).ToArray();
        var extra = actual.Except(expected).ToArray();
        var changed = missing.Select(Key).Intersect(extra.Select(Key)).ToHashSet(StringComparer.Ordinal);
        var parts = changed.Select(k => "changed " + k)
            .Concat(missing.Where(m => !changed.Contains(Key(m))).Select(m => "missing " + Key(m)))
            .Concat(extra.Where(x => !changed.Contains(Key(x))).Select(x => "extra " + Key(x)))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return parts.Length == 0 ? "per-file manifest identical (policy version differs?)"
            : $"{parts.Length} difference(s): " + string.Join("; ", parts.Take(12)).Replace('\t', ' ') +
              (parts.Length > 12 ? "; ..." : "");
    }

    private static void TryDelete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { Console.Error.WriteLine("WARNING: lab temp cleanup failed: " + root); }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine("WARNING: lab temp cleanup denied: " + root); }
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

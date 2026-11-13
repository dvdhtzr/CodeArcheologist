using System.Diagnostics;
using CodeArcheologist.Api.Execution;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Explicit GENUINE MAF/Ollama R3 adaptive workflow over a disposable saved
/// repository. Not part of check.ps1. Every requested trial must complete
/// and pass full saved-evidence receipt/checkpoint revalidation.
/// </summary>
internal static class R3AdaptiveLocalRun
{
    public static async Task<int> RunAsync(string[] args, bool observed = false)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out var trials) ||
            trials is < 1 or > 3)
        {
            Console.Error.WriteLine("Usage: " + (observed
                ? "--v2-handoff-run" : "--r3-adaptive-run") + " <trials 1..3>");
            return 2;
        }

        var original = Environment.GetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR");
        var root = Path.Combine(Path.GetTempPath(),
            "code-archeologist-r3-adaptive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pass = 0;
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "Controller.cs"), """
                namespace R3AdaptiveFixture;
                [Route("api/orders")]
                public class OrdersController
                {
                    [HttpGet("lookup/{id}")]
                    public int Lookup(int id) => ReadOrder(id);
                    private int ReadOrder(int id) => id;
                }
                """);
            for (var i = 1; i <= trials; i++)
            {
                Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR",
                    Path.Combine(root, "data-" + i));
                var result = await TrialAsync(source, observed);
                Console.WriteLine($"adaptive trial {i}/{trials}: {result}");
                if (result == "GREEN") pass++;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", original);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { Console.Error.WriteLine("WARNING: temporary cleanup failed."); }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine("WARNING: temporary cleanup denied.");
            }
        }
        Console.WriteLine(pass == trials
            ? $"GREEN: {pass}/{trials} real adaptive Ollama trials; all receipts and v3 checkpoints replayed." +
                (observed ? " V2 fenced model-handoff events also verified; NOT READY FOR SCORING." : "")
            : $"RED: only {pass}/{trials} real adaptive Ollama trials completed and revalidated.");
        return pass == trials ? 0 : 1;
    }

    /// <summary>The exact production-dependency subset needed by the
    /// genuine adaptive probe. Also instantiated by the model-free contract
    /// test so missing DI registrations cannot hide until an Ollama run.</summary>
    internal static ServiceProvider BuildEvidenceServices() =>
        new ServiceCollection()
            .AddSingleton<WorkspaceStore>().AddSingleton<SnapshotCaptureService>()
            .AddSingleton<CSharpSyntaxInspector>().AddSingleton<CSharpInvocationSyntaxInspector>()
            .AddSingleton<CSharpProvisionalInvocationBinder>()
            .AddSingleton<CSharpHttpAttributeInspector>()
            .AddSingleton<CSharpMinimalApiInspector>()
            .AddSingleton<CSharpHostCautionAnalyzer>()
            .AddSingleton<CSharpDiRegistrationInspector>()
            .AddSingleton<CSharpTypeRelationshipInspector>()
            .AddSingleton<StaticCSharpProjectInspector>()
            .AddSingleton<CSharpProjectLinkContextInspector>()
            .AddSingleton<CSharpEvidenceGraphBuilder>()
            .AddSingleton<SnapshotEvidenceTools>()
            .AddSingleton<GateCEvidenceDiscovery>()
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

    private static async Task<string> TrialAsync(string source, bool observed)
    {
        await using var services = BuildEvidenceServices();
        var store = services.GetRequiredService<WorkspaceStore>();
        store.Initialize();
        var workspace = store.Create("R3 adaptive probe", Guid.NewGuid()).Workspace;
        var location = RepositoryPathPolicy.Validate(source, store.DatabasePath).Location
            ?? throw new InvalidOperationException("Probe source was not eligible.");
        var repository = store.RegisterRepository(workspace.Id, location,
            Guid.NewGuid()).Repository!;
        var snapshot = (await services.GetRequiredService<SnapshotCaptureService>()
            .CaptureAsync(workspace.Id, repository.Id, Guid.NewGuid(),
                CancellationToken.None)).Snapshot;
        var graph = services.GetRequiredService<CSharpEvidenceGraphBuilder>()
            .Build(snapshot, CancellationToken.None);
        var candidate = graph.Nodes
            .Where(n => n.Kind == "http-endpoint-attribute")
            .OrderBy(n => n.Id, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new InvalidOperationException("Fixture has no saved HTTP candidate.");
        var scope = new GateCScope(workspace.Id, repository.Id, snapshot.Id,
            snapshot.FingerprintSha256, candidate.Id, SnapshotEvidenceTools.Version);
        var discovery = services.GetRequiredService<GateCEvidenceDiscovery>();
        var revalidator = new GateC6CheckpointRevalidator(discovery, store);
        var coordinator = new GateC6StageCoordinator(store, discovery,
            new MafRoleStageRunner(), revalidator,
            new AdaptiveMafRoleStageRunner(store));

        var ownership = new ExecutionOwnershipState();
        if (!ownership.TryAcquire(store))
            return "RED: model worker could not obtain execution ownership";
        var signal = new InvestigationWorkSignal();
        var worker = new InvestigationWorker(store, ownership, signal,
            coordinator, InvestigationWorkerOptions.Default,
            NullLogger<InvestigationWorker>.Instance);
        try
        {
            // For V2, admit and explicitly opt in BEFORE the managed worker
            // can claim the queued attempt. Legacy adaptive smoke is unchanged.
            if (!observed) await worker.StartAsync(CancellationToken.None);
            var admitted = new InvestigationAdmissionService(store, ownership, signal)
                .AdmitAdaptive(scope, Guid.NewGuid(), candidate.Kind);
            if (admitted.Outcome != InvestigationAdmissionOutcome.Admitted ||
                admitted.Record is null)
                return "RED: adaptive question admission rejected";
            var runId = admitted.Record.RunId;
            if (observed)
            {
                store.EnableV2Handoff(runId, ownership.Fence!);
                await worker.StartAsync(CancellationToken.None);
            }
            if (store.GetAdaptiveContext(workspace.Id, repository.Id, snapshot.Id,
                    runId) is not { } question ||
                !AdaptiveQuestionContract.Verify(question))
                return "RED: admitted question context could not be verified";

            var watch = Stopwatch.StartNew();
            while (!GateC6AttemptTransitions.IsTerminal(store.GetAttempt(runId)!.Status) &&
                   watch.Elapsed < TimeSpan.FromMinutes(7))
                await Task.Delay(500);
            var attempt = store.GetAttempt(runId)!;
            var detail = store.GetInvestigation(workspace.Id, repository.Id,
                snapshot.Id, runId)!;
            var checkpoints = store.ListCheckpoints(runId);
            var receiptCounts = new[]
            {
                store.ListAdaptiveEvidenceReceipts(runId, attempt.AttemptId,
                    GateCRole.Investigator).Count,
                store.ListAdaptiveEvidenceReceipts(runId, attempt.AttemptId,
                    GateCRole.Skeptic).Count
            };
            Console.WriteLine("  host: attempt=" + attempt.Status +
                "/" + attempt.TerminalCause + "; run=" + detail.Summary.Status +
                "; checkpoints=" + checkpoints.Count +
                "; verified read-only calls=" + string.Join(",", receiptCounts));
            if (attempt.Status != "completed" ||
                detail.Summary.Status != "completed" || detail.Report is null ||
                checkpoints.Count != 3 ||
                checkpoints.Any(cp =>
                    cp.ContractVersion != R3AdaptiveCheckpointContract.Version))
                return "RED-model: run did not complete with three v3 checkpoints";
            if (receiptCounts.Any(n => n is < 1 or > 4) ||
                store.ListAdaptiveGapEvents(runId, attempt.AttemptId,
                    GateCRole.Investigator).Count > 3 ||
                store.ListAdaptiveGapEvents(runId, attempt.AttemptId,
                    GateCRole.Skeptic).Count > 3)
                return "RED: role receipt or gap budget exceeded";
            var restored = revalidator.Revalidate(snapshot, admitted.Record,
                checkpoints, store.ListAcceptedCheckpointEventIds(runId),
                CancellationToken.None);
            if (!restored.Accepted || restored.Investigator is null ||
                restored.Skeptic is null || restored.Reviewer is null)
                return "RED: v3 checkpoint/receipt replay " + restored.Diagnostic;

            if (observed)
            {
                var events = store.ListV2HandoffEvents(runId, attempt.AttemptId);
                var perRole = events.GroupBy(e => e.Role, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
                foreach (var role in new[] { "investigator", "skeptic", "reviewer" })
                    if (!perRole.TryGetValue(role, out var roleEvents) ||
                        roleEvents.Length < 3 ||
                        roleEvents.Length % 3 != 0 ||
                        roleEvents.Any(e => e.EventKind == V2HandoffContract.Faulted))
                        return "RED: V2 missing or unsuccessful live model handoff for " + role;
                var modelCalls = events.Count(e => e.EventKind == V2HandoffContract.Entered);
                var citedReceipts = events.Count(e =>
                    e.EventKind == V2HandoffContract.Entered &&
                    e.ReceiptIdsJson != "[]");
                if (modelCalls < 5 || citedReceipts < 2 ||
                    perRole["reviewer"].Any(e =>
                        e.ReceiptIdsJson != "[]" || e.EvidenceLinksJson != "[]"))
                    return "RED: V2 live observed handoff count or source-role isolation invalid";
                Console.WriteLine($"  V2 observed: adapter calls={modelCalls}; " +
                    $"receipt-bound calls={citedReceipts}; " +
                    "prompt bytes=SHA-256 only; NOT READY FOR SCORING.");
            }
            return "GREEN";
        }
        finally
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await worker.StopAsync(grace.Token);
            ownership.Dispose();
        }
    }
}

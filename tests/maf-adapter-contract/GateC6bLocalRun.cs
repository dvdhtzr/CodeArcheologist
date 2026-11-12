using System.Diagnostics;
using CodeArcheologist.Api.Execution;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Explicit GENUINE MAF/Ollama C6b walkthrough (not part of check.ps1):
/// production worker + checkpoint coordinator + MafRoleStageRunner on a
/// disposable, synthetic, never-compiled fixture in an isolated data dir.
/// Each trial: start → Investigator checkpoint → application shutdown
/// mid-Skeptic → restart (must invoke no model) → explicit continuation
/// (must reuse the Investigator checkpoint, run a fresh Skeptic + Reviewer)
/// → completed report. A trial whose model output is host-rejected is
/// reported as such; it never counts as GREEN.
/// </summary>
internal static class GateC6bLocalRun
{
    private sealed class CountingRoles(IGateCRoleStageRunner inner) : IGateCRoleStageRunner
    {
        public int Investigator, Skeptic, Reviewer;
        public Task<GateC3RoleOutcome> RunIndependentAsync(GateCEvidenceSession session, Guid runId,
            GateCScope scope, CancellationToken ct)
        {
            if (session.Role == GateCRole.Investigator) Interlocked.Increment(ref Investigator);
            else Interlocked.Increment(ref Skeptic);
            return inner.RunIndependentAsync(session, runId, scope, ct);
        }
        public Task<GateC4ReviewerOutcome> ReviewAsync(GateCIndependentAssessment investigator,
            GateCIndependentAssessment skeptic, Guid runId, GateCScope scope, CancellationToken ct)
        {
            Interlocked.Increment(ref Reviewer);
            return inner.ReviewAsync(investigator, skeptic, runId, scope, ct);
        }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out var trials) || trials is < 1 or > 3)
        {
            Console.Error.WriteLine("Usage: --gate-c6b-run <trials 1..3>");
            return 2;
        }
        var prior = Environment.GetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR");
        var temp = Path.Combine(Path.GetTempPath(), "code-archeologist-c6b-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var green = 0;
        try
        {
            var source = Path.Combine(temp, "source");
            Directory.CreateDirectory(source);
            // Never compiled or executed; recognized only as written syntax.
            await File.WriteAllTextAsync(Path.Combine(source, "Controller.cs"), """
                namespace GateC6bFixture;
                [Route("api/items")]
                public class ItemsController
                {
                    private int Lookup(int id) => id;
                    [HttpGet("details/{id}")]
                    public int Details(int id)
                    {
                        return Lookup(id);
                    }
                }
                """);
            for (var trial = 1; trial <= trials; trial++)
            {
                var data = Path.Combine(temp, "data-" + trial);
                Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", data);
                var verdict = await TrialAsync(source);
                Console.WriteLine($"trial {trial}: {verdict}");
                if (verdict == "GREEN") green++;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", prior);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { Console.Error.WriteLine("WARNING: temporary cleanup failed."); }
            catch (UnauthorizedAccessException) { Console.Error.WriteLine("WARNING: temporary cleanup denied."); }
        }
        Console.WriteLine(green > 0
            ? $"GREEN: {green}/{trials} genuine MAF shutdown/restart/continuation walkthrough(s) completed with checkpoint reuse."
            : $"RED: 0/{trials} walkthroughs completed.");
        return green > 0 ? 0 : 1;
    }

    private static async Task<string> TrialAsync(string source)
    {
        await using var services = new ServiceCollection()
            .AddSingleton<WorkspaceStore>().AddSingleton<SnapshotCaptureService>()
            .AddSingleton<CSharpSyntaxInspector>().AddSingleton<CSharpInvocationSyntaxInspector>()
            .AddSingleton<CSharpProvisionalInvocationBinder>().AddSingleton<CSharpHttpAttributeInspector>()
            .AddSingleton<CSharpMinimalApiInspector>()
            .AddSingleton<CSharpHostCautionAnalyzer>().AddSingleton<CSharpDiRegistrationInspector>()
            .AddSingleton<CSharpTypeRelationshipInspector>().AddSingleton<StaticCSharpProjectInspector>()
            .AddSingleton<CSharpProjectLinkContextInspector>().AddSingleton<CSharpEvidenceGraphBuilder>()
            .AddSingleton<SnapshotEvidenceTools>().AddSingleton<GateCEvidenceDiscovery>()
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
        var store = services.GetRequiredService<WorkspaceStore>();
        store.Initialize();
        var workspace = store.Create("C6b walkthrough", Guid.NewGuid()).Workspace;
        var location = RepositoryPathPolicy.Validate(source, store.DatabasePath).Location
            ?? throw new InvalidOperationException("Fixture registration rejected.");
        var repository = store.RegisterRepository(workspace.Id, location, Guid.NewGuid()).Repository!;
        var snapshot = (await services.GetRequiredService<SnapshotCaptureService>().CaptureAsync(
            workspace.Id, repository.Id, Guid.NewGuid(), CancellationToken.None)).Snapshot;
        var graph = services.GetRequiredService<CSharpEvidenceGraphBuilder>().Build(snapshot, CancellationToken.None);
        var entry = graph.Nodes.Where(n => n.Kind == "http-endpoint-attribute")
            .OrderBy(n => n.Id, StringComparer.Ordinal).Select(n => n.Id).FirstOrDefault()
            ?? throw new InvalidOperationException("Fixture produced no HTTP candidate.");
        var scope = new GateCScope(workspace.Id, repository.Id, snapshot.Id,
            snapshot.FingerprintSha256, entry, SnapshotEvidenceTools.Version);

        var discovery = services.GetRequiredService<GateCEvidenceDiscovery>();
        var roles = new CountingRoles(new MafRoleStageRunner());
        var revalidator = new GateC6CheckpointRevalidator(discovery);

        // ---- first process: run until the Investigator checkpoint, then shut down.
        var (ownership, worker, admission, _) = StartHost(store, discovery, roles, revalidator);
        await worker.StartAsync(CancellationToken.None);
        var run = admission.Admit(scope, Guid.NewGuid()).Record!.RunId;
        var watch = Stopwatch.StartNew();
        while (store.ListCheckpoints(run).Count == 0 &&
               !GateC6AttemptTransitions.IsTerminal(store.GetAttempt(run)!.Status) &&
               watch.Elapsed < TimeSpan.FromMinutes(4))
            await Task.Delay(200);
        if (store.ListCheckpoints(run).Count == 0)
        {
            await StopAsync(worker, ownership);
            return "RED-model: no Investigator checkpoint (" + store.GetAttempt(run)!.TerminalCause + ")";
        }
        await StopAsync(worker, ownership); // application shutdown during the Skeptic stage
        var first = store.GetAttempt(run)!;
        if (first.Status == "completed")
            return "INCONCLUSIVE: run completed before shutdown could interrupt it";
        if (first.TerminalCause != "application-shutdown")
            return "RED: first attempt ended as " + first.Status + "/" + first.TerminalCause;

        // ---- restart: reconciliation only; must not call a model.
        var before = (roles.Investigator, roles.Skeptic, roles.Reviewer);
        (ownership, worker, _, var continuations) = StartHost(store, discovery, roles, revalidator);
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(1500);
        if ((roles.Investigator, roles.Skeptic, roles.Reviewer) != before)
            return "RED: restart invoked a model role";

        // ---- explicit continuation.
        var continued = continuations.Continue(scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId,
            run, Guid.NewGuid(), first.Number, CancellationToken.None);
        if (continued.Outcome != ContinuationOutcome.Admitted)
        {
            await StopAsync(worker, ownership);
            return "RED: continuation refused: " + continued.Diagnostic;
        }
        watch.Restart();
        while (!GateC6AttemptTransitions.IsTerminal(store.GetAttempt(run)!.Status) &&
               watch.Elapsed < TimeSpan.FromMinutes(7))
            await Task.Delay(500);
        await StopAsync(worker, ownership);
        var second = store.GetAttempt(run)!;
        var detail = store.GetInvestigation(scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId, run)!;
        Console.WriteLine($"  calls: investigator={roles.Investigator} skeptic={roles.Skeptic} reviewer={roles.Reviewer}; " +
            $"attempt2={second.Status}/{second.TerminalCause}; run={detail.Summary.Status}");
        if (roles.Investigator != before.Investigator)
            return "RED: accepted Investigator checkpoint was re-run";
        if (!store.ListLineage(run).Any(l => l.AttemptId == second.AttemptId && l.Relation == "reused"))
            return "RED: continuation recorded no checkpoint reuse";
        if (second.Status != "completed" || detail.Report is null)
            return "RED-model: continuation ended " + second.Status + "/" + second.TerminalCause;
        return "GREEN";
    }

    private static (ExecutionOwnershipState, InvestigationWorker, InvestigationAdmissionService,
        InvestigationContinuationService) StartHost(WorkspaceStore store, GateCEvidenceDiscovery discovery,
        IGateCRoleStageRunner roles, GateC6CheckpointRevalidator revalidator)
    {
        var ownership = new ExecutionOwnershipState();
        if (!ownership.TryAcquire(store))
            throw new InvalidOperationException("Walkthrough could not acquire ownership: " + ownership.Diagnostic);
        var signal = new InvestigationWorkSignal();
        var worker = new InvestigationWorker(store, ownership, signal,
            new GateC6StageCoordinator(store, discovery, roles, revalidator),
            InvestigationWorkerOptions.Default, NullLogger<InvestigationWorker>.Instance);
        return (ownership, worker, new InvestigationAdmissionService(store, ownership, signal),
            new InvestigationContinuationService(store, ownership, revalidator, signal));
    }

    private static async Task StopAsync(InvestigationWorker worker, ExecutionOwnershipState ownership)
    {
        using var grace = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await worker.StopAsync(grace.Token);
        ownership.Dispose();
    }
}

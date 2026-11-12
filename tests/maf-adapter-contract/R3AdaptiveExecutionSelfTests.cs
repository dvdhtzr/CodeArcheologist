using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;

internal static class R3AdaptiveExecutionSelfTests
{
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Host-only fake role: exercises the REAL coordinator, fenced managed
    /// worker, immutable receipts and v3 checkpoint validator, with zero
    /// Ollama calls. Separate real-model acceptance is still required.
    /// </summary>
    private sealed class FakeAdaptiveRoles(WorkspaceStore store)
        : IAdaptiveRoleStageRunner
    {
        public int InvestigatorCalls;
        public int SkepticCalls;
        public bool FailSkepticOnce;
        public readonly List<(GateCRole Role, int Calls, int Handles)> EntryStates = [];

        public Task<GateC3RoleOutcome> RunAdaptiveAsync(
            GateCEvidenceSession session, InvestigationExecutionContext context,
            CancellationToken ct)
        {
            var role = session.Role;
            EntryStates.Add((role, session.ToolCallsUsed, session.HostCatalog().Count));
            if (role == GateCRole.Investigator) InvestigatorCalls++;
            else
            {
                SkepticCalls++;
                if (FailSkepticOnce)
                {
                    FailSkepticOnce = false;
                    return Task.FromResult(new GateC3RoleOutcome(role,
                        GateCRoleStatus.TimedOut, "model-deadline-exceeded",
                        null, null, session.ToolCallsUsed));
                }
            }
            var run = context.Run;
            var first = session.Request(GateCEvidenceRegistry.Relationships, ct);
            Expect(first.Check.Accepted && first.Response is not null,
                "Model-free adaptive initial read rejected.");
            store.RecordAdaptiveEvidenceReceipt(context.Fence, context.Attempt.AttemptId,
                session, first.Response!, "initial",
                "Host approved initial saved relationship view");

            if (role == GateCRole.Investigator)
            {
                var followup = session.Request(GateCEvidenceRegistry.EntryPoint, ct);
                Expect(followup.Check.Accepted && followup.Response is not null,
                    "Model-free adaptive follow-up rejected.");
                store.RecordAdaptiveEvidenceReceipt(context.Fence, context.Attempt.AttemptId,
                    session, followup.Response!, "follow-up",
                    "Check candidate's saved source annotation",
                    "unknown-entry-point-binding");
            }
            else
            {
                // The Skeptic's proposed gap maps to a capability already
                // served. It must create a durable denial, not another request.
                store.RecordAdaptiveDuplicateGap(context.Fence, context.Attempt.AttemptId,
                    session, "uncertain-source-relationships",
                    "Recheck the same previously served source relationships");
            }

            var handle = session.HostCatalog()[0].Handle;
            var assessment = role == GateCRole.Investigator
                ? new GateCIndependentAssessment(GateCContract.Version, run.RunId, run.Scope,
                    role, [handle], [new GateCHypothesis("H1",
                        "Attribute may annotate the written method",
                        [handle], "Runtime registration remains unknown")], [],
                    ["Effective route not proved"])
                : new GateCIndependentAssessment(GateCContract.Version, run.RunId, run.Scope,
                    role, [handle], [], [new GateCConcern("S1",
                        GateCConcernKind.InsufficientEvidence,
                        "Runtime dispatch cannot be established from the source",
                        [handle])], ["Effective route unknown"]);
            Expect(session.ValidateAssessment(assessment).Accepted,
                "Model-free adaptive assessment has invalid role-local citations.");
            return Task.FromResult(new GateC3RoleOutcome(role,
                GateCRoleStatus.CompletedValidated,
                "valid-structured-assessment-not-semantic-proof", assessment,
                GateCEvidenceRegistry.Relationships, session.ToolCallsUsed,
                session.ReportLinks(), session.GraphAnalyzerVersion));
        }
    }

    public static async Task RunAsync()
    {
        ProposalContract();

        await GateC6bSelfTests.ScenarioAsync(
            "R3 adaptive: genuine probe's evidence service graph resolves without Ollama",
            _ =>
            {
                // Build the EXACT same DI container as --r3-adaptive-run.
                // Previously this omitted CSharpHostCautionAnalyzer, which
                // passed check.ps1 but failed before the first model call.
                using var services = R3AdaptiveLocalRun.BuildEvidenceServices();
                if (services.GetRequiredService<SnapshotEvidenceTools>() is null ||
                    services.GetRequiredService<GateCEvidenceDiscovery>() is null)
                    throw new InvalidOperationException(
                        "Genuine adaptive probe evidence service graph is incomplete.");
                return Task.CompletedTask;
            });

        await GateC6bSelfTests.ScenarioAsync(
            "R3 adaptive: real managed worker commits v3 roles and gap decisions without Ollama",
            async fixture =>
            {
                var store = fixture.Store;
                store.Initialize();
                var scope = GateC6bSelfTests.SeedSnapshot(store);
                var evidence = new GateC6bCheckpointSelfTests.Evidence();
                var legacy = new GateC6bCheckpointSelfTests.Roles();
                var adaptive = new FakeAdaptiveRoles(store);
                var revalidator = new GateC6CheckpointRevalidator(evidence, store);
                var coordinator = new GateC6StageCoordinator(store, evidence,
                    legacy, revalidator, adaptive);
                using var host = GateC6bSelfTests.StartHost(store, coordinator);
                await host.Worker.StartAsync(CancellationToken.None);

                var key = Guid.NewGuid();
                var admitted = host.Admission.AdmitAdaptive(scope, key,
                    "http-endpoint-attribute");
                Expect(admitted.Outcome == InvestigationAdmissionOutcome.Admitted &&
                    admitted.Record is not null, "Adaptive host admission failed.");
                var runId = admitted.Record!.RunId;
                Expect(host.Admission.AdmitAdaptive(scope, key,
                        "http-endpoint-attribute").Outcome ==
                        InvestigationAdmissionOutcome.Replayed,
                    "Adaptive admission was not idempotent.");
                Expect(host.Admission.Admit(scope, key,
                        "http-endpoint-attribute").Outcome ==
                        InvestigationAdmissionOutcome.Conflict,
                    "Adaptive admission alias was accepted by stable v1.");

                await GateC6bSelfTests.Until(() => store.GetInvestigation(
                    scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId, runId)
                    ?.Summary.Status == "completed", "adaptive model-free completion");

                var checkpoints = store.ListCheckpoints(runId);
                Expect(checkpoints.Count == 3 &&
                    checkpoints.All(cp =>
                        cp.ContractVersion == R3AdaptiveCheckpointContract.Version),
                    "Adaptive execution did not produce three v3 checkpoints.");
                Expect(adaptive.InvestigatorCalls == 1 && adaptive.SkepticCalls == 1 &&
                    legacy.InvestigatorCalls == 0 && legacy.SkepticCalls == 0 &&
                    legacy.ReviewerCalls == 1,
                    "Adaptive profile incorrectly invoked legacy independent roles.");
                Expect(adaptive.EntryStates.Count == 2 &&
                    adaptive.EntryStates.All(e => e.Calls == 0 && e.Handles == 0),
                    "Independent roles did not start with fresh empty evidence.");
                var attempt = store.ListAttempts(runId).Single();
                Expect(store.ListAdaptiveEvidenceReceipts(runId, attempt.AttemptId,
                    GateCRole.Investigator).Count == 2 &&
                    store.ListAdaptiveEvidenceReceipts(runId, attempt.AttemptId,
                    GateCRole.Skeptic).Count == 1,
                    "Initial and follow-up receipts do not match the two role sessions.");
                var investigatorGaps = store.ListAdaptiveGapEvents(runId,
                    attempt.AttemptId, GateCRole.Investigator);
                var skepticGaps = store.ListAdaptiveGapEvents(runId,
                    attempt.AttemptId, GateCRole.Skeptic);
                Expect(investigatorGaps.Select(g => g.EventKind).SequenceEqual(
                        new[] { "proposed", "allowed", "no-new-evidence" }) &&
                    skepticGaps.Select(g => g.EventKind).SequenceEqual(
                        new[] { "proposed", "denied" }),
                    "Host gap allowance and duplicate denial were not durable.");
                var snapshot = store.GetSnapshot(scope.WorkspaceId, scope.RepositoryId,
                    scope.SnapshotId)!;
                var replay = revalidator.Revalidate(snapshot, admitted.Record!,
                    checkpoints, store.ListAcceptedCheckpointEventIds(runId),
                    CancellationToken.None);
                Expect(replay.Accepted && replay.Reviewer is not null &&
                    replay.Investigator is not null && replay.Skeptic is not null,
                    "Completed adaptive report has non-replayable checkpoints: " +
                    replay.Diagnostic);
                await host.Worker.StopAsync(CancellationToken.None);
            });

        await GateC6bSelfTests.ScenarioAsync(
            "R3 adaptive: explicit continuation reuses receipt-verified Investigator v3 checkpoint",
            async fixture =>
            {
                var store = fixture.Store;
                store.Initialize();
                var scope = GateC6bSelfTests.SeedSnapshot(store);
                var evidence = new GateC6bCheckpointSelfTests.Evidence();
                var legacy = new GateC6bCheckpointSelfTests.Roles();
                var adaptive = new FakeAdaptiveRoles(store) { FailSkepticOnce = true };
                var revalidator = new GateC6CheckpointRevalidator(evidence, store);
                var coordinator = new GateC6StageCoordinator(
                    store, evidence, legacy, revalidator, adaptive);
                Guid runId;
                using (var first = GateC6bSelfTests.StartHost(store, coordinator))
                {
                    await first.Worker.StartAsync(CancellationToken.None);
                    var result = first.Admission.AdmitAdaptive(scope, Guid.NewGuid(),
                        "http-endpoint-attribute");
                    Expect(result.Record is not null &&
                        result.Outcome == InvestigationAdmissionOutcome.Admitted,
                        "Initial adaptive admission rejected.");
                    runId = result.Record!.RunId;
                    await GateC6bSelfTests.Until(() => store.GetInvestigation(
                        scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId,
                        runId)?.Summary.Status == "interrupted",
                        "adaptive Skeptic role interruption");
                    Expect(store.ListCheckpoints(runId).Count == 1 &&
                        store.ListCheckpoints(runId)[0].Role == "investigator",
                        "Timed-out Skeptic must not acquire a checkpoint.");
                    await first.Worker.StopAsync(CancellationToken.None);
                }
                // A new owner never automatically resumes or calls Ollama.
                using (var second = GateC6bSelfTests.StartHost(store, coordinator))
                {
                    await second.Worker.StartAsync(CancellationToken.None);
                    Expect(adaptive.InvestigatorCalls == 1 && adaptive.SkepticCalls == 1,
                        "Restart invoked an agent before explicit continuation.");
                    var continuation = new InvestigationContinuationService(
                        store, second.Ownership, revalidator, second.Signal);
                    var current = store.GetInvestigation(scope.WorkspaceId,
                        scope.RepositoryId, scope.SnapshotId, runId)!.Summary;
                    Expect(continuation.Evaluate(current, CancellationToken.None).Eligible,
                        "Exact v3 receipt replay should permit manual continuation.");
                    var resumed = continuation.Continue(scope.WorkspaceId,
                        scope.RepositoryId, scope.SnapshotId, runId,
                        Guid.NewGuid(), 1, CancellationToken.None);
                    Expect(resumed.Outcome == ContinuationOutcome.Admitted,
                        "Host refused explicit adaptive continuation: " + resumed.Diagnostic);
                    await GateC6bSelfTests.Until(() => store.GetInvestigation(
                        scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId,
                        runId)?.Summary.Status == "completed",
                        "adaptive manual continuation completion");
                    Expect(adaptive.InvestigatorCalls == 1 &&
                        adaptive.SkepticCalls == 2 &&
                        legacy.ReviewerCalls == 1,
                        "Continuation reran a validated role or missed the Reviewer.");
                    var attempts = store.ListAttempts(runId);
                    var checkpoints = store.ListCheckpoints(runId);
                    Expect(attempts.Count == 2 && checkpoints.Count == 3 &&
                        store.ListLineage(runId).Any(l =>
                            l.AttemptId == attempts[1].AttemptId &&
                            l.Relation == "reused" &&
                            l.CheckpointId == checkpoints.Single(
                                cp => cp.Role == "investigator").CheckpointId),
                        "Adaptive v3 Investigator checkpoint was not explicitly reused.");
                    await second.Worker.StopAsync(CancellationToken.None);
                }
            });

        Console.WriteLine("GREEN: R3 adaptive schema parser, fenced worker dispatch, " +
            "independent role receipts, host gap denial, v3 checkpoint recovery and v1 isolation.");
    }

    private static void ProposalContract()
    {
        foreach (var accepted in new[]
        {
            "{\"kind\":\"none\",\"purpose\":\"No additional view can help\"}",
            "{\"kind\":\"unknown-entry-point-binding\",\"purpose\":\"Verify annotation\"}",
            "{\"kind\":\"missing-direct-invocations\",\"purpose\":\"Inspect direct source calls\"}"
        })
            Expect(AdaptiveGapProposal.TryParse(accepted, out var parsed) && parsed is not null,
                "Approved structured gap proposal rejected.");

        foreach (var rejected in new[]
        {
            "{}",
            "{\"kind\":\"read-arbitrary-file\",\"purpose\":\"Anything\"}",
            "{\"kind\":\"none\",\"purpose\":\"\"}",
            "{\"kind\":\"none\",\"purpose\":\"Fine\",\"path\":\"C:\\\\secrets\"}",
            "{\"kind\":\"unknown-entry-point-binding\",\"purpose\":\"A\\nB\"}",
            "{\"kind\":\"none\",\"purpose\":\"ok\",\"purpose\":\"duplicate\"}"
        })
            Expect(!AdaptiveGapProposal.TryParse(rejected, out _),
                "Unsupported, extra-property, duplicated or unbounded gap proposal accepted.");

        Expect(AdaptiveGapPolicy.CapabilityFor("unresolved-callee") ==
            GateCEvidenceRegistry.CandidateCallees &&
            AdaptiveGapPolicy.CapabilityFor("arbitrary-command") is null,
            "Host gap mappings allowed arbitrary capability.");
    }
}

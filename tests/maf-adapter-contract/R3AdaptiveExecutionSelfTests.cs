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
        public readonly List<(GateCRole Role, int Calls, int Handles)> EntryStates = [];

        public Task<GateC3RoleOutcome> RunAdaptiveAsync(
            GateCEvidenceSession session, InvestigationExecutionContext context,
            CancellationToken ct)
        {
            var role = session.Role;
            EntryStates.Add((role, session.ToolCallsUsed, session.HostCatalog().Count));
            if (role == GateCRole.Investigator) InvestigatorCalls++;
            else SkepticCalls++;
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

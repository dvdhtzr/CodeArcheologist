using CodeArcheologist.Api.Execution;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;

internal static class R3AdaptiveFaultSelfTests
{
    private static void Expect(bool condition, string why)
    {
        if (!condition) throw new InvalidOperationException(why);
    }

    // This is the *real* runner's model-facing boundary: unlike a fake role
    // result, it observes every actual prompt, and can check whether committed
    // receipts already exist when a source evidence prompt is delivered.
    private sealed class ObservingPromptClient : IAdaptivePromptClientFactory, IAdaptivePromptClient
    {
        public int EvidencePrompts;
        public int AssessmentPrompts;
        public string GapKind = AdaptiveGapProposal.NoGap;
        public Action<string, CancellationToken>? WhenEvidenceDelivered;
        public IAdaptivePromptClient Open(GateCRole role) => this;
        public void Dispose() { }

        public Task<string> AskAsync(string prompt, string schema, string schemaName,
            CancellationToken ct)
        {
            if (prompt.Contains("<untrusted-evidence-json>", StringComparison.Ordinal))
            {
                EvidencePrompts++;
                WhenEvidenceDelivered?.Invoke(schemaName, ct);
                ct.ThrowIfCancellationRequested();
            }
            return Task.FromResult(schemaName switch
            {
                "R3AdaptiveInitialSelection" =>
                    "{\"capabilities\":[\"inspect_source_relationships\"]}",
                "R3AdaptiveGapProposal" =>
                    "{\"kind\":\"" + GapKind + "\",\"purpose\":\"Inspect bounded host evidence\"}",
                "R3AdaptiveIndependentAssessment" => CountAssessment(),
                _ => throw new InvalidOperationException("Unexpected model stage.")
            });
        }

        private string CountAssessment()
        {
            AssessmentPrompts++;
            // Invalid shape is intentional: the test is for commit/delivery
            // boundaries, not a fabricated validated role assessment.
            return "{}";
        }
    }

    private static (GateCScope Scope, InvestigationSummary Run,
        InvestigationAttempt Attempt, GateCEvidenceSession Session)
        Seed(WorkspaceStore store, ExecutionFence fence,
            GateCRole role = GateCRole.Investigator)
    {
        store.Initialize();
        var scope = GateC6bSelfTests.SeedSnapshot(store);
        var run = store.CreateInvestigation(scope, Guid.NewGuid(), fence,
            adaptiveQuestionSubjectKind: "http-endpoint-attribute").Record!;
        var attempt = store.ClaimNextQueuedAttempt(fence, TimeSpan.FromMinutes(6))!.Attempt;
        var factory = new GateC6bCheckpointSelfTests.Evidence();
        var snapshot = store.GetSnapshot(scope.WorkspaceId, scope.RepositoryId,
            scope.SnapshotId)!;
        var session = factory.OpenVerified(snapshot, scope, run.RunId, role,
            CancellationToken.None);
        return (scope, run, attempt, session);
    }

    private static InvestigationExecutionContext Execution(WorkspaceStore store,
        GateCScope scope, InvestigationSummary run, InvestigationAttempt attempt,
        ExecutionFence fence) => new(
            store.GetSnapshot(scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId)!,
            run, attempt, fence);

    private static void AppendFollowup(WorkspaceStore store, ExecutionFence fence,
        InvestigationAttempt attempt, GateCEvidenceSession session)
    {
        var first = session.Request(GateCEvidenceRegistry.Relationships,
            CancellationToken.None).Response!;
        store.RecordAdaptiveEvidenceReceipt(fence, attempt.AttemptId, session, first,
            "initial", "Host-approved baseline view");
        var second = session.Request(GateCEvidenceRegistry.EntryPoint,
            CancellationToken.None).Response!;
        store.RecordAdaptiveEvidenceReceipt(fence, attempt.AttemptId, session, second,
            "follow-up", "Inspect candidate binding", "unknown-entry-point-binding");
    }

    private static void MustRejectProjection(WorkspaceStore store, GateCScope scope,
        Guid runId, string why)
    {
        try
        {
            _ = new AdaptiveActivityProjectionService(store,
                new GateC6bCheckpointSelfTests.Evidence()).Project(
                    scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId, runId,
                    CancellationToken.None);
        }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe adaptive activity projection: " + why);
    }

    public static async Task RunAsync()
    {
        await GateC6bSelfTests.ScenarioAsync(
            "R3 PR3d: injected receipt write error blocks real model-facing delivery",
            async f =>
            {
                f.Store.Initialize();
                using var owner = new ExecutionOwnershipState();
                Expect(owner.TryAcquire(f.Store), "Missing fenced owner.");
                var (scope, run, attempt, session) = Seed(f.Store, owner.Fence!);
                var prompts = new ObservingPromptClient();
                GateC6bSelfTests.Exec(f.Store, """
                    CREATE TRIGGER r3_fault_receipt BEFORE INSERT ON adaptive_evidence_receipts
                    BEGIN SELECT RAISE(ABORT, 'injected receipt failure'); END;
                    """);
                try
                {
                    await new AdaptiveMafRoleStageRunner(f.Store, prompts)
                        .RunAdaptiveAsync(session, Execution(f.Store, scope, run,
                            attempt, owner.Fence!), CancellationToken.None);
                    throw new InvalidOperationException("Failed receipt was delivered.");
                }
                catch (StagePersistenceException) { }
                Expect(prompts.EvidencePrompts == 0 &&
                    f.Store.ListAdaptiveEvidenceReceipts(run.RunId, attempt.AttemptId,
                        GateCRole.Investigator).Count == 0 &&
                    f.Store.ListCheckpoints(run.RunId).Count == 0,
                    "Receipt failure leaked evidence to the actual prompt boundary.");
            });

        await GateC6bSelfTests.ScenarioAsync(
            "R3 PR3d: gap transaction rollback prevents follow-up prompt and receipt",
            async f =>
            {
                f.Store.Initialize();
                using var owner = new ExecutionOwnershipState();
                Expect(owner.TryAcquire(f.Store), "Missing fenced owner.");
                var (scope, run, attempt, session) = Seed(f.Store, owner.Fence!);
                var prompts = new ObservingPromptClient
                { GapKind = "unknown-entry-point-binding" };
                prompts.WhenEvidenceDelivered = (stage, _) =>
                {
                    Expect(f.Store.ListAdaptiveEvidenceReceipts(run.RunId,
                        attempt.AttemptId, GateCRole.Investigator).Count == 1,
                        "The model saw initial evidence before the first commit.");
                };
                GateC6bSelfTests.Exec(f.Store, """
                    CREATE TRIGGER r3_fault_gap BEFORE INSERT ON adaptive_gap_events
                    BEGIN SELECT RAISE(ABORT, 'injected gap rollback'); END;
                    """);
                try
                {
                    await new AdaptiveMafRoleStageRunner(f.Store, prompts)
                        .RunAdaptiveAsync(session, Execution(f.Store, scope, run,
                            attempt, owner.Fence!), CancellationToken.None);
                    throw new InvalidOperationException("Rolled back gap reached final model.");
                }
                catch (StagePersistenceException) { }
                Expect(prompts.EvidencePrompts == 1 && prompts.AssessmentPrompts == 0 &&
                    f.Store.ListAdaptiveEvidenceReceipts(run.RunId, attempt.AttemptId,
                        GateCRole.Investigator).Count == 1 &&
                    f.Store.ListAdaptiveGapEvents(run.RunId, attempt.AttemptId,
                        GateCRole.Investigator).Count == 0 &&
                    f.Store.ListCheckpoints(run.RunId).Count == 0,
                    "Rolled-back follow-up was delivered or checkpointed.");
            });

        await GateC6bSelfTests.ScenarioAsync(
            "R3 PR3d: stale owner cannot deliver evidence or create a checkpoint",
            async f =>
            {
                f.Store.Initialize();
                using var owner = new ExecutionOwnershipState();
                Expect(owner.TryAcquire(f.Store), "Missing fenced owner.");
                var (scope, run, attempt, session) = Seed(f.Store, owner.Fence!);
                var prompts = new ObservingPromptClient();
                try
                {
                    await new AdaptiveMafRoleStageRunner(f.Store, prompts)
                        .RunAdaptiveAsync(session, Execution(f.Store, scope, run,
                            attempt, new ExecutionFence(Guid.NewGuid(), owner.Fence!.Epoch)),
                            CancellationToken.None);
                    throw new InvalidOperationException("Stale owner accepted.");
                }
                catch (ExecutionFenceException) { }
                Expect(prompts.EvidencePrompts == 0 &&
                    f.Store.ListCheckpoints(run.RunId).Count == 0 &&
                    f.Store.ListAdaptiveEvidenceReceipts(run.RunId,
                        attempt.AttemptId, GateCRole.Investigator).Count == 0,
                    "Stale fenced attempt advanced after failure.");
            });

        await GateC6bSelfTests.ScenarioAsync(
            "R3 PR3d: cancelled active model stage leaves receipts but no checkpoint",
            async f =>
            {
                f.Store.Initialize();
                using var owner = new ExecutionOwnershipState();
                Expect(owner.TryAcquire(f.Store), "Missing fenced owner.");
                var (scope, run, attempt, session) = Seed(f.Store, owner.Fence!);
                using var cancellation = new CancellationTokenSource();
                var prompts = new ObservingPromptClient();
                prompts.WhenEvidenceDelivered = (_, _) => cancellation.Cancel();
                var result = await new AdaptiveMafRoleStageRunner(f.Store, prompts)
                    .RunAdaptiveAsync(session, Execution(f.Store, scope, run,
                        attempt, owner.Fence!), cancellation.Token);
                Expect(result.Status == GateCRoleStatus.Cancelled &&
                    prompts.EvidencePrompts == 1 &&
                    f.Store.ListAdaptiveEvidenceReceipts(run.RunId, attempt.AttemptId,
                        GateCRole.Investigator).Count == 1 &&
                    f.Store.ListCheckpoints(run.RunId).Count == 0,
                    "Cancellation after committed initial evidence fabricated a checkpoint.");
            });

        await GateC6bSelfTests.ScenarioAsync(
            "R3 PR3d: unrecorded gap is not inferred model none; foreign scope is hidden",
            f =>
            {
                f.Store.Initialize();
                using var owner = new ExecutionOwnershipState();
                Expect(owner.TryAcquire(f.Store), "Missing fenced owner.");
                var (scope, run, attempt, session) = Seed(f.Store, owner.Fence!);
                var first = session.Request(GateCEvidenceRegistry.Relationships,
                    CancellationToken.None).Response!;
                f.Store.RecordAdaptiveEvidenceReceipt(owner.Fence!, attempt.AttemptId,
                    session, first, "initial", "Host-approved initial view");
                var projection = new AdaptiveActivityProjectionService(f.Store,
                    new GateC6bCheckpointSelfTests.Evidence());
                var view = projection.Project(scope.WorkspaceId, scope.RepositoryId,
                    scope.SnapshotId, run.RunId, CancellationToken.None)!;
                var role = view.Attempts.Single().Roles.First();
                Expect(role.Decision == "unrecorded" && role.ProposedGapKind is null &&
                    role.Requests.Single().ReceiptId != Guid.Empty &&
                    role.Requests.Single().NewItemCount >= 0 &&
                    !role.CheckpointAccepted,
                    "Absent gap event was promoted into a model decision.");
                Expect(projection.Project(scope.WorkspaceId, scope.RepositoryId,
                    Guid.NewGuid(), run.RunId, CancellationToken.None) is null,
                    "Foreign snapshot received a run history.");
                return Task.CompletedTask;
            });

        await GateC6bSelfTests.ScenarioAsync(
            "R3 PR3d: missing first persisted receipt fails closed",
            f =>
            {
                f.Store.Initialize();
                using var owner = new ExecutionOwnershipState();
                Expect(owner.TryAcquire(f.Store), "Missing fenced owner.");
                var (scope, run, attempt, session) = Seed(f.Store, owner.Fence!);
                AppendFollowup(f.Store, owner.Fence!, attempt, session);
                GateC6bSelfTests.Exec(f.Store,
                    "DROP TRIGGER adaptive_evidence_receipts_no_delete;");
                GateC6bSelfTests.Exec(f.Store,
                    "DELETE FROM adaptive_evidence_receipts WHERE ordinal=1;");
                MustRejectProjection(f.Store, scope, run.RunId,
                    "missing initial receipt with surviving follow-up");
                return Task.CompletedTask;
            });

        await GateC6bSelfTests.ScenarioAsync(
            "R3 PR3d: modified receipt digest fails closed",
            f =>
            {
                f.Store.Initialize();
                using var owner = new ExecutionOwnershipState();
                Expect(owner.TryAcquire(f.Store), "Missing fenced owner.");
                var (scope, run, attempt, session) = Seed(f.Store, owner.Fence!);
                AppendFollowup(f.Store, owner.Fence!, attempt, session);
                GateC6bSelfTests.Exec(f.Store,
                    "DROP TRIGGER adaptive_evidence_receipts_no_update;");
                GateC6bSelfTests.Exec(f.Store,
                    "UPDATE adaptive_evidence_receipts SET content_sha256=$x WHERE ordinal=1;",
                    ("$x", new string('F', 64)));
                MustRejectProjection(f.Store, scope, run.RunId, "modified receipt digest");
                return Task.CompletedTask;
            });

        await GateC6bSelfTests.ScenarioAsync(
            "R3 PR3d: missing allowed gap event fails closed",
            f =>
            {
                f.Store.Initialize();
                using var owner = new ExecutionOwnershipState();
                Expect(owner.TryAcquire(f.Store), "Missing fenced owner.");
                var (scope, run, attempt, session) = Seed(f.Store, owner.Fence!);
                AppendFollowup(f.Store, owner.Fence!, attempt, session);
                GateC6bSelfTests.Exec(f.Store,
                    "DROP TRIGGER adaptive_gap_events_no_delete;");
                GateC6bSelfTests.Exec(f.Store,
                    "DELETE FROM adaptive_gap_events WHERE event_kind='allowed';");
                MustRejectProjection(f.Store, scope, run.RunId, "missing gap decision event");
                return Task.CompletedTask;
            });

        await GateC6bSelfTests.ScenarioAsync(
            "R3 PR3d: reordered gap with recomputed digest fails closed",
            f =>
            {
                f.Store.Initialize();
                using var owner = new ExecutionOwnershipState();
                Expect(owner.TryAcquire(f.Store), "Missing fenced owner.");
                var (scope, run, attempt, session) = Seed(f.Store, owner.Fence!);
                AppendFollowup(f.Store, owner.Fence!, attempt, session);
                var proposed = f.Store.ListAdaptiveGapEvents(run.RunId,
                    attempt.AttemptId, GateCRole.Investigator)
                    .Single(e => e.EventKind == "proposed");
                var moved = proposed with { CreatedAtUnixMs = proposed.CreatedAtUnixMs + 10000 };
                moved = moved with { ContentSha256 = moved.ComputeDigest() };
                GateC6bSelfTests.Exec(f.Store,
                    "DROP TRIGGER adaptive_gap_events_no_update;");
                GateC6bSelfTests.Exec(f.Store, """
                    UPDATE adaptive_gap_events SET created_at_unix_ms=$at,
                        content_sha256=$digest WHERE event_id=$id;
                    """, ("$at", moved.CreatedAtUnixMs),
                    ("$digest", moved.ContentSha256),
                    ("$id", proposed.EventId.ToString("D")));
                MustRejectProjection(f.Store, scope, run.RunId,
                    "semantically reordered gap events with valid row digests");
                return Task.CompletedTask;
            });

        Console.WriteLine("GREEN: R3 PR3d observable prompt boundary, receipt/gap rollback, " +
            "stale fencing, active cancellation, unrecorded decisions and corrupted history.");
    }
}

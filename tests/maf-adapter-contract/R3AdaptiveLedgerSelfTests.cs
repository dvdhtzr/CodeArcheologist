using CodeArcheologist.Api.Execution;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;

internal static class R3AdaptiveLedgerSelfTests
{
    private static void Expect(bool value, string explanation)
    {
        if (!value) throw new InvalidOperationException(explanation);
    }

    public static async Task RunAsync()
    {
        await GateC6bSelfTests.ScenarioAsync(
            "R3 ledger: fenced receipt-before-delivery and immutable gap lifecycle",
            f =>
            {
                f.Store.Initialize();
                Expect(GateC6bSelfTests.Scalar(f.Store, "PRAGMA user_version;") == 8,
                    "Additive v8 migration not applied.");
                var scope = GateC6bSelfTests.SeedSnapshot(f.Store);
                using var owner = new ExecutionOwnershipState();
                Expect(owner.TryAcquire(f.Store), "Test must own execution lock and epoch.");
                var fence = owner.Fence!;
                var key = Guid.NewGuid();
                var admitted = f.Store.CreateInvestigation(scope, key, fence,
                    adaptiveQuestionSubjectKind: "http-endpoint-attribute");
                var runId = admitted.Record!.RunId;
                var replay = f.Store.CreateInvestigation(scope, key, fence,
                    adaptiveQuestionSubjectKind: "http-endpoint-attribute");
                Expect(!replay.Created && !replay.Conflict && replay.Record?.RunId == runId,
                    "Adaptive request retries must retain their original run identity.");
                Expect(f.Store.CreateInvestigation(scope, key, fence,
                    "http-endpoint-attribute").Conflict &&
                    f.Store.CreateInvestigation(scope, key, fence).Conflict,
                    "V1/legacy idempotency keys must never alias an adaptive run.");
                var savedContext = f.Store.GetAdaptiveContext(
                    scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId, runId);
                Expect(AdaptiveQuestionContract.Verify(savedContext) &&
                    savedContext!.QuestionId != runId &&
                    f.Store.GetQuestionContext(scope.WorkspaceId, scope.RepositoryId,
                        scope.SnapshotId, runId) is null,
                    "Adaptive v2 must be separately admitted and never masquerade as v1.");
                var claim = f.Store.ClaimNextQueuedAttempt(fence, TimeSpan.FromMinutes(6))
                    ?? throw new InvalidOperationException("Test run was not claimed.");
                var attemptId = claim.Attempt.AttemptId;
                var factory = new GateC6bCheckpointSelfTests.Evidence();
                var investigator = factory.OpenVerified(f.Store.GetSnapshot(
                    scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId)!, scope, runId,
                    GateCRole.Investigator, CancellationToken.None);
                var first = investigator.Request(GateCEvidenceRegistry.Relationships,
                    CancellationToken.None).Response!;
                var initial = f.Store.RecordAdaptiveEvidenceReceipt(fence, attemptId,
                    investigator, first, "initial", "Initial bounded candidate relationship view");
                Expect(initial.Ordinal == 1 && initial.NewItemCount > 0 &&
                    initial.GapId is null && initial.ComputeDigest() == initial.ContentSha256,
                    "Initial receipt was not durably bound to the host-issued evidence.");
                var secondView = investigator.Request(GateCEvidenceRegistry.EntryPoint,
                    CancellationToken.None).Response!;
                var followup = f.Store.RecordAdaptiveEvidenceReceipt(fence, attemptId,
                    investigator, secondView, "follow-up",
                    "Check whether the candidate has another recorded relation",
                    "unknown-entry-point-binding");
                Expect(followup.Ordinal == 2 && followup.NewItemCount == 0 &&
                    followup.GapId.HasValue, "Overlap must be a charged, explicit no-new-evidence.");
                var persisted = f.Store.ListAdaptiveEvidenceReceipts(runId, attemptId,
                    GateCRole.Investigator);
                Expect(persisted.Count == 2 && persisted[0] == initial &&
                    persisted[1] == followup, "Committed receipt sequence changed.");
                Expect(GateC6bSelfTests.Scalar(f.Store,
                    "SELECT COUNT(*) FROM adaptive_gap_events;") == 3,
                    "Follow-up proposal, approval and terminal status must all be durable.");
                Expect(GateC6bSelfTests.Scalar(f.Store,
                    "SELECT COUNT(*) FROM adaptive_gap_events WHERE event_kind='no-new-evidence';") == 1,
                    "No-new-evidence outcome was not recorded.");

                // The same result cannot be stored twice to manufacture evidence.
                try
                {
                    f.Store.RecordAdaptiveEvidenceReceipt(fence, attemptId,
                        investigator, secondView, "follow-up", "duplicate",
                        "unknown-entry-point-binding");
                    throw new InvalidOperationException("Duplicate receipt was accepted.");
                }
                catch (InvalidOperationException error)
                    when (error.Message.StartsWith("Out of order", StringComparison.Ordinal)) { }
                Expect(f.Store.ListAdaptiveEvidenceReceipts(runId, attemptId,
                    GateCRole.Skeptic).Count == 0, "Skeptic inherited Investigator receipts.");

                var skeptic = factory.OpenVerified(f.Store.GetSnapshot(
                    scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId)!, scope, runId,
                    GateCRole.Skeptic, CancellationToken.None);
                var independent = skeptic.Request(GateCEvidenceRegistry.Relationships,
                    CancellationToken.None).Response!;
                var skepticReceipt = f.Store.RecordAdaptiveEvidenceReceipt(fence, attemptId,
                    skeptic, independent, "initial", "Independent Skeptic view");
                Expect(skepticReceipt.Ordinal == 1 &&
                    skepticReceipt.Role == GateCRole.Skeptic,
                    "Skeptic must have a separate, independently numbered request ledger.");

                GateC6bSelfTests.ExpectSqliteAbort(f.Store,
                    "UPDATE adaptive_evidence_receipts SET purpose='tampered';");
                GateC6bSelfTests.ExpectSqliteAbort(f.Store,
                    "DELETE FROM adaptive_evidence_receipts;");
                GateC6bSelfTests.ExpectSqliteAbort(f.Store,
                    "UPDATE adaptive_gap_events SET diagnostic='tampered';");
                GateC6bSelfTests.ExpectSqliteAbort(f.Store,
                    "DELETE FROM adaptive_gap_events;");
                return Task.CompletedTask;
            });

        await GateC6bSelfTests.ScenarioAsync(
            "R3 ledger: v7 historical investigation and question preserved",
            f =>
            {
                f.Store.InitializeTo(7);
                var scope = GateC6bSelfTests.SeedSnapshot(f.Store);
                using var owner = new ExecutionOwnershipState();
                Expect(owner.TryAcquire(f.Store), "v7 fixture ownership required.");
                var run = f.Store.CreateInvestigation(scope, Guid.NewGuid(), owner.Fence!,
                    "http-endpoint-attribute").Record!.RunId;
                f.Store.Initialize();
                f.Store.Initialize();
                Expect(f.Store.GetAdaptiveContext(scope.WorkspaceId, scope.RepositoryId,
                    scope.SnapshotId, run) is null,
                    "Historical v1 question gained an adaptive execution identity.");
                Expect(GateC6bSelfTests.Scalar(f.Store, "PRAGMA user_version;") == 8,
                    "v7-to-v8 migration failed.");
                Expect(f.Store.GetQuestionContext(scope.WorkspaceId, scope.RepositoryId,
                    scope.SnapshotId, run) is not null,
                    "Historical v1 question context was mutated.");
                Expect(f.Store.GetAttempt(run)?.Status == "queued",
                    "Migration performed forbidden recovery.");
                Expect(GateC6bSelfTests.Scalar(f.Store,
                    "SELECT COUNT(*) FROM adaptive_evidence_receipts;") == 0 &&
                    GateC6bSelfTests.Scalar(f.Store,
                    "SELECT COUNT(*) FROM adaptive_gap_events;") == 0,
                    "Migration fabricated adaptive evidence.");
                return Task.CompletedTask;
            });
        Console.WriteLine("GREEN: R3 additive v8 receipt/gap ledger, fencing, role independence, " +
            "overlap budget, migration preservation and immutable history.");
    }
}

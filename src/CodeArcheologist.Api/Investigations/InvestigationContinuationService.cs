using CodeArcheologist.Api.Execution;
using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Investigations;

public enum ContinuationOutcome
{
    Admitted, Replayed, IdempotencyConflict, Busy, StaleAttempt, NotFound, NotRecoverable,
    AlreadyCompleted, CheckpointRejected, ExecutionUnavailable
}

public sealed record ContinuationResult(ContinuationOutcome Outcome, string Diagnostic,
    InvestigationAttempt? Attempt);

/// <summary>
/// Explicit, user-authorized continuation (decision D2). The endpoint has
/// already authenticated the user and checked CSRF and scope. This service:
/// replays idempotently, refuses legacy, completed, active or stale runs,
/// verifies the original immutable snapshot and every accepted checkpoint
/// (fail closed, D5), and only then admits a NEW attempt under the same run.
/// It never executes a role itself: the managed worker does.
/// </summary>
public sealed class InvestigationContinuationService(WorkspaceStore store,
    ExecutionOwnershipState ownership, GateC6CheckpointRevalidator revalidator,
    InvestigationWorkSignal signal)
{
    public ContinuationResult Continue(Guid workspaceId, Guid repositoryId, Guid snapshotId,
        Guid runId, Guid idempotencyKey, int expectedAttemptNumber, CancellationToken ct)
    {
        var detail = store.GetInvestigation(workspaceId, repositoryId, snapshotId, runId);
        if (detail is null) return new(ContinuationOutcome.NotFound, "run-not-found", null);

        // An exact replay returns its original attempt before any other check.
        var prior = store.FindExecutionRequest(idempotencyKey);
        if (prior is not null)
        {
            if (prior.Kind != "continue" || prior.RunId != runId ||
                prior.RequestDigest != WorkspaceStore.ContinuationDigest(runId, expectedAttemptNumber))
                return new(ContinuationOutcome.IdempotencyConflict, "idempotency-key-reused", null);
        }
        var fence = ownership.Fence;
        if (fence is null)
            return new(ContinuationOutcome.ExecutionUnavailable, "execution-owner-unavailable", null);

        if (prior is null)
        {
            var summary = detail.Summary;
            if (summary.Status == "completed")
                return new(ContinuationOutcome.AlreadyCompleted, "run-already-completed", null);
            var latest = store.GetAttempt(runId);
            if (latest is null || latest.ContractVersion != GateC6ExecutionContract.AttemptVersion)
                return new(ContinuationOutcome.NotRecoverable, "legacy-run-not-resumable", null);
            if (latest.Number != expectedAttemptNumber)
                return new(ContinuationOutcome.StaleAttempt, "expected-attempt-is-stale", null);
            if (!GateC6AttemptTransitions.IsTerminal(latest.Status))
                return new(ContinuationOutcome.Busy, "another-execution-active", null);
            if (summary.Status != "interrupted")
                return new(ContinuationOutcome.NotRecoverable, "run-not-interrupted", null);

            var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
            if (snapshot is null)
                return new(ContinuationOutcome.CheckpointRejected, "snapshot-missing", null);
            var checkpoints = store.ListCheckpoints(runId);
            var revalidated = revalidator.Revalidate(snapshot, summary, checkpoints,
                store.ListAcceptedCheckpointEventIds(runId), ct);
            if (!revalidated.Accepted)
                return new(ContinuationOutcome.CheckpointRejected, revalidated.Diagnostic, null);

            return Admit(fence, runId, idempotencyKey, expectedAttemptNumber,
                revalidated.CheckpointIds);
        }
        return Admit(fence, runId, idempotencyKey, expectedAttemptNumber, []);
    }

    private ContinuationResult Admit(ExecutionFence fence, Guid runId, Guid key, int expected,
        IReadOnlyCollection<Guid> checkpointIds)
    {
        ContinuationAdmission admission;
        try
        {
            admission = store.AdmitContinuation(fence, runId, key, expected, checkpointIds);
        }
        catch (ExecutionFenceException error)
        {
            ownership.MarkLost(error.Diagnostic);
            return new(ContinuationOutcome.ExecutionUnavailable, "execution-owner-unavailable", null);
        }
        var result = admission.Outcome switch
        {
            ContinuationAdmissionOutcome.Admitted => new ContinuationResult(
                ContinuationOutcome.Admitted, "continuation-admitted", admission.Attempt),
            ContinuationAdmissionOutcome.Replayed => new(ContinuationOutcome.Replayed,
                "continuation-replayed", admission.Attempt),
            ContinuationAdmissionOutcome.Conflict => new(ContinuationOutcome.IdempotencyConflict,
                "idempotency-key-reused", null),
            ContinuationAdmissionOutcome.Busy => new(ContinuationOutcome.Busy,
                "another-execution-active", null),
            ContinuationAdmissionOutcome.StaleAttempt => new(ContinuationOutcome.StaleAttempt,
                "expected-attempt-is-stale", null),
            ContinuationAdmissionOutcome.AlreadyCompleted => new(ContinuationOutcome.AlreadyCompleted,
                "run-already-completed", null),
            ContinuationAdmissionOutcome.CheckpointsChanged => new(ContinuationOutcome.CheckpointRejected,
                "checkpoint-set-changed-during-validation", null),
            _ => new(ContinuationOutcome.NotRecoverable, "run-not-recoverable", null)
        };
        if (result.Outcome == ContinuationOutcome.Admitted) signal.Notify();
        return result;
    }
}

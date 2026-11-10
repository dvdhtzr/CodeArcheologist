using CodeArcheologist.Api.Execution;
using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Investigations;

public enum CancellationOutcome
{
    Requested, Replayed, IdempotencyConflict, StaleAttempt, NoActiveAttempt, NotFound,
    ExecutionUnavailable
}

public sealed record CancellationResult(CancellationOutcome Outcome, string Diagnostic,
    InvestigationAttempt? Attempt);

/// <summary>
/// Cancellation = (1) endpoint authorization/CSRF/scope, (2) durable fenced
/// intent with idempotency, (3) notification of the worker. Terminal
/// cancellation and slot release are performed ONLY by the worker once the
/// in-flight model call has returned (or at the queued claim boundary).
/// </summary>
public sealed class InvestigationCancellationService(WorkspaceStore store,
    ExecutionOwnershipState ownership, InvestigationWorker worker)
{
    public CancellationResult Cancel(Guid workspaceId, Guid repositoryId, Guid snapshotId,
        Guid runId, Guid idempotencyKey, int expectedAttemptNumber)
    {
        if (store.GetInvestigation(workspaceId, repositoryId, snapshotId, runId) is null)
            return new(CancellationOutcome.NotFound, "run-not-found", null);
        var fence = ownership.Fence;
        if (fence is null)
            return new(CancellationOutcome.ExecutionUnavailable, "execution-owner-unavailable", null);
        CancellationRequestResult result;
        try
        {
            result = store.RequestCancellation(fence, runId, idempotencyKey, expectedAttemptNumber);
        }
        catch (ExecutionFenceException error)
        {
            ownership.MarkLost(error.Diagnostic);
            return new(CancellationOutcome.ExecutionUnavailable, "execution-owner-unavailable", null);
        }
        if (result.Outcome is CancellationRequestOutcome.Requested or CancellationRequestOutcome.Replayed &&
            result.Attempt is { } attempt && !GateC6AttemptTransitions.IsTerminal(attempt.Status))
            worker.NotifyCancellation(attempt.AttemptId);
        return result.Outcome switch
        {
            CancellationRequestOutcome.Requested => new(CancellationOutcome.Requested,
                "cancellation-intent-recorded-awaiting-safe-boundary", result.Attempt),
            CancellationRequestOutcome.Replayed => new(CancellationOutcome.Replayed,
                "cancellation-replayed", result.Attempt),
            CancellationRequestOutcome.Conflict => new(CancellationOutcome.IdempotencyConflict,
                "idempotency-key-reused", null),
            CancellationRequestOutcome.StaleAttempt => new(CancellationOutcome.StaleAttempt,
                "expected-attempt-is-stale", null),
            CancellationRequestOutcome.NotFound => new(CancellationOutcome.NotFound, "run-not-found", null),
            _ => new(CancellationOutcome.NoActiveAttempt, "no-active-attempt", null)
        };
    }
}

public static class GateC6ExecutionProjectionContract
{
    public const string Version = "gate-c6b-execution-projection-v1";
}

public sealed record ExecutionCheckpointView(Guid CheckpointId, string ContentSha256,
    DateTimeOffset AcceptedAtUtc, int AcceptedInAttemptNumber, bool ReusedByCurrentAttempt);

public sealed record ExecutionStageView(string Role, string Status, ExecutionCheckpointView? Checkpoint);

public sealed record ExecutionAttemptView(Guid AttemptId, int Number, string Status,
    string? TerminalCause, string Diagnostic, string ContractVersion, Guid? ContinuesAttemptId,
    long? OwnershipEpoch, DateTimeOffset? DeadlineAtUtc, DateTimeOffset? CancelRequestedAtUtc,
    bool Legacy);

public sealed record ExecutionProjection(string ProjectionVersion, Guid RunId, string RunStatus,
    string RunDiagnostic, bool Legacy, ExecutionAttemptView? CurrentAttempt,
    IReadOnlyList<ExecutionAttemptView> Attempts, IReadOnlyList<ExecutionStageView> Stages,
    ContinuationEligibility Recovery, bool Cancellable, bool ExecutionOwner,
    string OwnershipDiagnostic, string Note);

/// <summary>
/// Versioned, read-only operational view derived ONLY from host-recorded
/// attempts, events, checkpoints and lineage. It never rewrites C5 report
/// rows, never infers progress from model prose, and exposes only fixed
/// host diagnostic codes.
/// </summary>
public sealed class InvestigationExecutionProjectionService(WorkspaceStore store,
    ExecutionOwnershipState ownership, InvestigationContinuationService continuations)
{
    private static readonly string[] Roles = ["investigator", "skeptic", "reviewer"];

    public ExecutionProjection? Project(Guid workspaceId, Guid repositoryId, Guid snapshotId,
        Guid runId, CancellationToken ct)
    {
        var detail = store.GetInvestigation(workspaceId, repositoryId, snapshotId, runId);
        if (detail is null) return null;
        var attempts = store.ListAttempts(runId).Select(a => new ExecutionAttemptView(a.AttemptId,
            a.Number, a.Status, a.TerminalCause, a.Diagnostic, a.ContractVersion, a.ContinuesAttemptId,
            a.OwnershipEpoch, a.DeadlineAtUtc, a.CancelRequestedAtUtc,
            a.ContractVersion != GateC6ExecutionContract.AttemptVersion)).ToArray();
        var current = attempts.LastOrDefault();
        var legacy = current is null || current.Legacy;
        var events = store.ListEvents(workspaceId, repositoryId, snapshotId, runId);
        var checkpoints = store.ListCheckpoints(runId);
        var lineage = store.ListLineage(runId);
        var numberOf = attempts.ToDictionary(a => a.AttemptId, a => a.Number);

        var stages = Roles.Select(role =>
        {
            var cp = checkpoints.SingleOrDefault(c => c.Role == role);
            ExecutionCheckpointView? view = cp is null ? null : new(cp.CheckpointId, cp.ContentSha256,
                DateTimeOffset.FromUnixTimeMilliseconds(cp.AcceptedAtUnixMs),
                numberOf.GetValueOrDefault(cp.AttemptId),
                current is not null && lineage.Any(l => l.AttemptId == current.AttemptId &&
                    l.CheckpointId == cp.CheckpointId && l.Relation == "reused"));
            if (legacy) return new ExecutionStageView(role, "legacy-unavailable", view);
            if (cp is not null)
                return new ExecutionStageView(role, view!.ReusedByCurrentAttempt ? "reused-checkpoint"
                    : view.AcceptedInAttemptNumber == current!.Number ? "accepted" : "accepted-earlier", view);
            var last = events.LastOrDefault(e => e.AttemptId == current!.AttemptId && e.Stage == role &&
                e.EventType.StartsWith("role-", StringComparison.Ordinal));
            var status = last?.EventType switch
            {
                null => "not-started",
                "role-started" => GateC6AttemptTransitions.IsTerminal(current!.Status) ? "abandoned" : "running",
                "role-assessment-rejected" => "rejected",
                "role-timed-out" => "timed-out",
                "role-cancelled" => "cancelled",
                _ => "failed"
            };
            return new ExecutionStageView(role, status, null);
        }).ToArray();

        var recovery = continuations.Evaluate(detail.Summary, ct);
        var cancellable = current is { Legacy: false } &&
            !GateC6AttemptTransitions.IsTerminal(current.Status) && current.CancelRequestedAtUtc is null &&
            ownership.Fence is not null;
        return new(GateC6ExecutionProjectionContract.Version, runId, detail.Summary.Status,
            detail.Summary.Diagnostic, legacy, current, attempts, stages, recovery, cancellable,
            ownership.Fence is not null, ownership.Diagnostic,
            "Host-observed execution state only; stage acceptance is contract validation, not semantic proof.");
    }
}

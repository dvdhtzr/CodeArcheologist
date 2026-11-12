using CodeArcheologist.Api.Execution;
using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Investigations;

public enum InvestigationAdmissionOutcome { Admitted, Replayed, Conflict, Busy, ExecutionUnavailable }

public sealed record InvestigationAdmission(InvestigationAdmissionOutcome Outcome,
    InvestigationSummary? Record);

/// <summary>
/// C6b responsibility 1 — admission only: authorization and request
/// validation happen at the endpoint; this service performs the durable,
/// idempotent, fenced admission and then merely NUDGES the managed worker.
/// It never runs MAF and never starts a task.
/// </summary>
public sealed class InvestigationAdmissionService(WorkspaceStore store,
    ExecutionOwnershipState ownership, InvestigationWorkSignal signal)
{
    public InvestigationAdmission Admit(GateCScope scope, Guid idempotencyKey,
        string? questionSubjectKind = null) =>
        AdmitCore(scope, idempotencyKey, questionSubjectKind, null);

    /// <summary>Explicit experimental profile. Never inferred from a legacy
    /// request or from model output; has a separate idempotency digest.</summary>
    public InvestigationAdmission AdmitAdaptive(GateCScope scope, Guid idempotencyKey,
        string verifiedSubjectKind) =>
        AdmitCore(scope, idempotencyKey, null, verifiedSubjectKind);

    private InvestigationAdmission AdmitCore(GateCScope scope, Guid idempotencyKey,
        string? questionSubjectKind, string? adaptiveQuestionSubjectKind)
    {
        var fence = ownership.Fence;
        if (fence is null)
            return new(InvestigationAdmissionOutcome.ExecutionUnavailable, null);
        InvestigationCreateResult result;
        try
        {
            result = store.CreateInvestigation(scope, idempotencyKey, fence,
                questionSubjectKind, adaptiveQuestionSubjectKind);
        }
        catch (ExecutionFenceException error)
        {
            ownership.MarkLost(error.Diagnostic);
            return new(InvestigationAdmissionOutcome.ExecutionUnavailable, null);
        }
        if (result.Busy) return new(InvestigationAdmissionOutcome.Busy, null);
        if (result.Conflict) return new(InvestigationAdmissionOutcome.Conflict, result.Record);
        if (!result.Created) return new(InvestigationAdmissionOutcome.Replayed, result.Record);
        signal.Notify();
        return new(InvestigationAdmissionOutcome.Admitted, result.Record);
    }
}

/// <summary>Coalescing wake-up signal; the database remains the source of truth.</summary>
public sealed class InvestigationWorkSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        _signal.WaitAsync(timeout, ct);
}

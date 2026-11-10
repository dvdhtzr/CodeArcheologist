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
    public InvestigationAdmission Admit(GateCScope scope, Guid idempotencyKey)
    {
        var fence = ownership.Fence;
        if (fence is null)
            return new(InvestigationAdmissionOutcome.ExecutionUnavailable, null);
        InvestigationCreateResult result;
        try
        {
            result = store.CreateInvestigation(scope, idempotencyKey, fence);
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

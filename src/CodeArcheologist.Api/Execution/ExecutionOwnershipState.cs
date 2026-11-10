using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Execution;

public enum ExecutionOwnershipStatus { NotAcquired, Owner, Lost }

/// <summary>
/// Process-wide execution authority: the held OS lock plus the fenced
/// database epoch established under it. Implements the C6b startup protocol
/// steps 2–7. A process that cannot acquire the lock performs NO recovery and
/// NO execution; it only serves read-only data.
/// </summary>
public sealed class ExecutionOwnershipState : IDisposable
{
    private readonly object _gate = new();
    private ExecutionProcessLock? _lock;
    private ExecutionFence? _fence;

    public ExecutionOwnershipStatus Status { get; private set; } =
        ExecutionOwnershipStatus.NotAcquired;

    public string Diagnostic { get; private set; } = "ownership-not-attempted";

    public ReconciliationResult? Reconciliation { get; private set; }

    /// <summary>Current fence, or null when this process must not execute.</summary>
    public ExecutionFence? Fence
    {
        get { lock (_gate) return Status == ExecutionOwnershipStatus.Owner ? _fence : null; }
    }

    /// <summary>
    /// Call once at startup after <see cref="WorkspaceStore.Initialize"/>.
    /// Never blocks waiting for the lock and never steals it.
    /// </summary>
    public bool TryAcquire(WorkspaceStore store)
    {
        lock (_gate)
        {
            if (Status != ExecutionOwnershipStatus.NotAcquired)
                throw new InvalidOperationException("Ownership acquisition is single-shot per process.");
            var held = ExecutionProcessLock.TryAcquire(store.DataDirectory, out var diagnostic);
            Diagnostic = diagnostic;
            if (held is null) return false;
            try
            {
                var fence = store.EstablishOwnership(held);
                held.WriteDiagnostic(fence.OwnerId, fence.Epoch);
                Reconciliation = store.ReconcileAbandoned(fence);
                _lock = held;
                _fence = fence;
                Status = ExecutionOwnershipStatus.Owner;
                Diagnostic = "execution-owner";
                return true;
            }
            catch
            {
                held.Dispose();
                Diagnostic = "ownership-establishment-failed";
                throw;
            }
        }
    }

    /// <summary>
    /// A fenced write was rejected: some other epoch is authoritative. Stop
    /// executing permanently in this process. The OS lock is intentionally
    /// kept so that no further process races an in-flight model call here.
    /// </summary>
    public void MarkLost(string diagnostic)
    {
        lock (_gate)
        {
            if (Status != ExecutionOwnershipStatus.Owner) return;
            Status = ExecutionOwnershipStatus.Lost;
            Diagnostic = diagnostic;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _lock?.Dispose();
            _lock = null;
        }
    }
}

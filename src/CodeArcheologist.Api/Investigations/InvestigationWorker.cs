using CodeArcheologist.Api.Execution;
using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Investigations;

/// <summary>
/// The model-executing stage sequence for one claimed attempt. Production
/// binds <see cref="GateC6StageCoordinator"/> (checkpoint-aware, genuine
/// MAF roles); deterministic tests bind model-free fixtures.
/// </summary>
public interface IInvestigationExecutor
{
    Task<GateC4RunOutcome> ExecuteAsync(InvestigationExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed record InvestigationWorkerOptions(TimeSpan AttemptDeadline,
    TimeSpan HeartbeatInterval)
{
    public static InvestigationWorkerOptions Default { get; } =
        new(GateC6ExecutionContract.AttemptDeadline, TimeSpan.FromSeconds(10));
}

public enum InvestigationWorkerState { Idle, Executing, Halted, NotOwner }

/// <summary>
/// C6b responsibility 2 — the single managed hosted worker. It owns
/// scheduling, the per-attempt deadline, cancellation and lifetime.
///
/// Invariants:
/// - Executes only while this process holds the OS lock and fenced epoch.
/// - Never claims work at startup by itself: only attempts admitted by THIS
///   owner are claimable, and reconciliation has already finalized others.
/// - The execution slot is released only by the single terminal write that
///   follows the executor actually returning. An uncooperative model call
///   keeps the slot held (fail closed) rather than allowing overlap.
/// - A terminal write is attempted exactly once. If it fails, the worker
///   halts; it never tries a second, different finalization.
/// </summary>
public sealed class InvestigationWorker(WorkspaceStore store,
    ExecutionOwnershipState ownership, InvestigationWorkSignal signal,
    IInvestigationExecutor executor, InvestigationWorkerOptions options,
    ILogger<InvestigationWorker> logger) : BackgroundService
{
    private volatile InvestigationWorkerState _state = InvestigationWorkerState.Idle;
    private int _executions;
    private readonly object _currentGate = new();
    private (Guid AttemptId, CancellationTokenSource UserCancel)? _current;

    /// <summary>
    /// Notification half of cancellation: signal the in-flight attempt. The
    /// durable intent is already committed by the caller; the heartbeat also
    /// re-reads it, so a lost notification cannot lose the intent. This never
    /// writes a terminal state or releases the slot.
    /// </summary>
    public void NotifyCancellation(Guid attemptId)
    {
        lock (_currentGate)
            if (_current is { } current && current.AttemptId == attemptId)
                current.UserCancel.Cancel();
        signal.Notify(); // a queued attempt is finalized at the claim boundary
    }

    public InvestigationWorkerState State => _state;

    /// <summary>Number of executor invocations by this worker (diagnostics/tests).</summary>
    public int Executions => Volatile.Read(ref _executions);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var fence = ownership.Fence;
        if (fence is null)
        {
            _state = InvestigationWorkerState.NotOwner;
            logger.LogWarning("Investigation execution disabled: {Diagnostic}", ownership.Diagnostic);
            return;
        }

        // Ownership loss is deliberately NOT linked to shutdown, so the two
        // causes remain distinguishable when classifying the terminal outcome.
        using var ownershipLost = new CancellationTokenSource();
        using var stopOrLost = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken, ownershipLost.Token);
        var heartbeat = HeartbeatAsync(fence, ownershipLost, stopOrLost.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested &&
                   !ownershipLost.IsCancellationRequested &&
                   _state != InvestigationWorkerState.Halted)
            {
                ClaimedAttempt? claim;
                try
                {
                    claim = store.ClaimNextQueuedAttempt(fence, options.AttemptDeadline);
                }
                catch (ExecutionFenceException error)
                {
                    LoseOwnership(error.Diagnostic, ownershipLost);
                    break;
                }

                if (claim is null)
                {
                    try { await signal.WaitAsync(options.HeartbeatInterval, stopOrLost.Token); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                _state = InvestigationWorkerState.Executing;
                await RunAttemptAsync(fence, claim, stoppingToken, ownershipLost);
                if (_state == InvestigationWorkerState.Executing)
                    _state = InvestigationWorkerState.Idle;
            }
        }
        finally
        {
            await stopOrLost.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { }
            if (_state != InvestigationWorkerState.Halted &&
                ownership.Status == ExecutionOwnershipStatus.Owner)
            {
                try { store.TryReleaseOwnership(fence); }
                catch (ExecutionFenceException) { }
            }
        }
    }

    private async Task HeartbeatAsync(ExecutionFence fence,
        CancellationTokenSource ownershipLost, CancellationToken stopOrLost)
    {
        using var timer = new PeriodicTimer(options.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(stopOrLost))
        {
            try
            {
                store.Heartbeat(fence);
                Guid? active;
                lock (_currentGate) active = _current?.AttemptId;
                if (active is Guid id && store.IsCancellationRequested(id))
                    NotifyCancellation(id);
            }
            catch (ExecutionFenceException error)
            {
                LoseOwnership(error.Diagnostic, ownershipLost);
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A transient write failure is not ownership loss; liveness is diagnostic only.
                logger.LogWarning("Execution heartbeat write failed ({Type}).", error.GetType().Name);
            }
        }
    }

    private void LoseOwnership(string diagnostic, CancellationTokenSource ownershipLost)
    {
        ownership.MarkLost(diagnostic);
        logger.LogError("Execution ownership lost: {Diagnostic}. Worker stops executing.", diagnostic);
        ownershipLost.Cancel();
    }

    private async Task RunAttemptAsync(ExecutionFence fence, ClaimedAttempt claim,
        CancellationToken stoppingToken, CancellationTokenSource ownershipLost)
    {
        var run = claim.Run;
        var attemptId = claim.Attempt.AttemptId;
        using var deadline = new CancellationTokenSource();
        var remaining = claim.DeadlineAtUtc - DateTimeOffset.UtcNow;
        deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        using var userCancel = new CancellationTokenSource();
        using var attemptToken = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken, ownershipLost.Token, deadline.Token, userCancel.Token);
        lock (_currentGate) _current = (attemptId, userCancel);
        try
        {
            await RunClaimedAsync(fence, claim, stoppingToken, ownershipLost, deadline,
                userCancel, attemptToken.Token);
        }
        finally
        {
            lock (_currentGate) _current = null;
        }
    }

    private async Task RunClaimedAsync(ExecutionFence fence, ClaimedAttempt claim,
        CancellationToken stoppingToken, CancellationTokenSource ownershipLost,
        CancellationTokenSource deadline, CancellationTokenSource userCancel,
        CancellationToken attemptToken)
    {
        var run = claim.Run;
        var attemptId = claim.Attempt.AttemptId;

        TerminalDecision decision;
        var snapshot = store.GetSnapshot(run.Scope.WorkspaceId,
            run.Scope.RepositoryId, run.Scope.SnapshotId);
        if (snapshot is null || snapshot.FingerprintSha256 != run.Scope.FingerprintSha256)
        {
            decision = TerminalDecision.Fail("host-execution-rejected", "evidence-rejected");
        }
        else
        {
            GateC4RunOutcome? outcome = null;
            Exception? fault = null;
            Interlocked.Increment(ref _executions);
            try
            {
                // The ONLY model-executing call. Awaited to completion even if
                // cancellation was requested: the slot is not released while it runs.
                outcome = await executor.ExecuteAsync(
                    new InvestigationExecutionContext(snapshot, run, claim.Attempt, fence),
                    attemptToken);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                fault = error;
                if (error is ExecutionFenceException lost)
                    LoseOwnership(lost.Diagnostic, ownershipLost);
            }
            // Durable intent is re-read: cancellation counts even if the
            // in-memory notification never arrived.
            var userCancelled = userCancel.IsCancellationRequested ||
                SafeCancellationRead(attemptId);
            decision = Classify(outcome, fault, run, stoppingToken,
                ownershipLost.IsCancellationRequested, deadline.IsCancellationRequested,
                userCancelled);
        }

        if (decision.Kind == TerminalKind.Halt)
        {
            // A checkpoint/stage write failed mid-attempt: behave like a crash
            // before commit. Write nothing more; restart reconciles explicitly.
            logger.LogError("Stage persistence failed; worker halted without terminal write.");
            _state = InvestigationWorkerState.Halted;
            return;
        }
        if (decision.Kind == TerminalKind.None)
        {
            // Ownership was lost: any write would be rejected by the fence.
            // The new owner (after this process exits) reconciles the attempt.
            _state = InvestigationWorkerState.Halted;
            return;
        }

        try
        {
            switch (decision.Kind)
            {
                case TerminalKind.Report:
                    store.FinishInvestigation(fence, attemptId, run.RunId,
                        decision.Report!, decision.Cause);
                    break;
                case TerminalKind.Interrupt:
                    store.InterruptAttempt(fence, attemptId, run.RunId, decision.Cause);
                    break;
                default:
                    store.FailInvestigation(fence, attemptId, run.RunId,
                        decision.Diagnostic!, decision.Cause);
                    break;
            }
        }
        catch (ExecutionFenceException error)
        {
            LoseOwnership(error.Diagnostic, ownershipLost);
            _state = InvestigationWorkerState.Halted;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Do NOT attempt a second finalization of the same record. The
            // attempt stays active and the slot stays held: fail closed until
            // an explicit restart reconciles it as interrupted.
            logger.LogError("Terminal persistence failed ({Type}); worker halted.",
                error.GetType().Name);
            _state = InvestigationWorkerState.Halted;
        }
    }

    internal enum TerminalKind { None, Halt, Report, Interrupt, Fail }

    internal sealed record TerminalDecision(TerminalKind Kind, string Cause,
        GateC5Report? Report = null, string? Diagnostic = null)
    {
        public static TerminalDecision Fail(string diagnostic, string cause) =>
            new(TerminalKind.Fail, cause, null, diagnostic);
    }

    /// <summary>
    /// Host-observed classification. Which host token fired takes precedence
    /// over what the (cancellation-aware) runner reported, so a shutdown or
    /// deadline is never mislabelled as user cancellation.
    /// </summary>
    private bool SafeCancellationRead(Guid attemptId)
    {
        try { return store.IsCancellationRequested(attemptId); }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    internal static TerminalDecision Classify(GateC4RunOutcome? outcome, Exception? fault,
        InvestigationSummary run, CancellationToken stopping, bool ownershipLost,
        bool deadlineExpired, bool userCancelled = false)
    {
        // A failed stage/checkpoint write is treated as a crash before commit.
        if (fault is StagePersistenceException) return new(TerminalKind.Halt, "host-failure");
        if (ownershipLost || fault is ExecutionFenceException)
            return new(TerminalKind.None, "ownership-lost");
        // Explicit, durable user intent outranks coincident shutdown/deadline.
        if (userCancelled) return TerminalDecision.Fail("host-cancelled", "user-cancelled");
        if (stopping.IsCancellationRequested)
            return new(TerminalKind.Interrupt, "application-shutdown");
        if (deadlineExpired) return new(TerminalKind.Interrupt, "attempt-deadline");
        if (fault is CheckpointRejectedException)
            return new(TerminalKind.Interrupt, "checkpoint-rejected");
        if (fault is not null || outcome is null)
            return TerminalDecision.Fail("host-execution-failure", "host-failure");

        GateC5Report report;
        try
        {
            report = GateC5ReportFactory.Create(outcome, run.StartedAtUtc, DateTimeOffset.UtcNow);
        }
        catch (InvalidOperationException)
        {
            return TerminalDecision.Fail("host-report-validation-failed", "report-validation-failed");
        }

        var roles = new[] { outcome.Independent.Investigator.Status,
            outcome.Independent.Skeptic.Status, outcome.Reviewer.Status };
        var cause = outcome.Progress.Status switch
        {
            GateCRunStatus.Completed => "completed",
            GateCRunStatus.Rejected => "evidence-rejected",
            // No user cancellation exists in this phase: a role-level Cancelled
            // without a host token is not trustworthy as a user action.
            GateCRunStatus.Cancelled => "host-failure",
            _ when roles.Contains(GateCRoleStatus.TimedOut) => "role-timeout",
            _ when roles.Contains(GateCRoleStatus.Failed) => "provider-error",
            _ when roles.Contains(GateCRoleStatus.ValidationRejected) => "validation-rejected",
            _ => "host-failure"
        };
        if (outcome.Progress.Status == GateCRunStatus.Cancelled)
            return TerminalDecision.Fail("host-execution-failure", cause);
        // Transient role failures leave the RUN recoverable (no report); accepted
        // checkpoints stay reusable by an explicit continuation.
        if (cause is "role-timeout" or "provider-error")
            return new(TerminalKind.Interrupt, cause);
        return new(TerminalKind.Report, cause, report);
    }
}

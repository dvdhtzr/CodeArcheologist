using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;
using static GateC6bCheckpointSelfTests;
using static GateC6bSelfTests;

/// <summary>
/// Gate C6b Phase 4, model-free: durable cancellation intent, worker-owned
/// terminal cancellation, claim-boundary cancellation, heartbeat re-read of
/// intent, and the versioned execution projection with recovery eligibility.
/// </summary>
internal static class GateC6bControlSelfTests
{
    public static async Task RunAsync()
    {
        await ScenarioAsync("cancellation keeps slot until the uncooperative call returns", CancelUncooperative);
        await ScenarioAsync("cooperative cancellation is recorded as user-cancelled", CancelCooperative);
        await ScenarioAsync("queued attempt is cancelled at the claim boundary without a model call", CancelQueued);
        await ScenarioAsync("durable cancellation intent survives a lost notification", CancelIntentWithoutNotification);
        await ScenarioAsync("execution projection: stages, checkpoints and recovery eligibility", Projection);
    }

    private static int CallsOf(Env env) =>
        env.Roles.InvestigatorCalls + env.Roles.SkepticCalls + env.Roles.ReviewerCalls;

    private static async Task CancelUncooperative(Fixture f)
    {
        var env = new Env(f);
        using var _ = env;
        env.Store.Initialize();
        env.Scope = SeedSnapshot(env.Store);
        env.Roles.Skeptic = Mode.BlockIgnoringCancel;
        await env.StartAsync();
        var run = env.Host.Admission.Admit(env.Scope, Guid.NewGuid()).Record!.RunId;
        await env.Roles.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var key = Guid.NewGuid();
        var requested = env.Cancel(run, 1, key);
        Expect(requested.Outcome == CancellationOutcome.Requested, "Cancellation not accepted: " + requested.Diagnostic);
        await Task.Delay(400);
        var attempt = env.Store.GetAttempt(run)!;
        Expect(attempt.Status == "running" && attempt.CancelRequestedAtUtc is not null,
            "Cancellation intent not durable, or attempt finalized while the call is active.");
        Expect(SlotHeld(env.Store) == 1, "Slot released while the model call is still active.");
        Expect(env.Host.Admission.Admit(env.Scope, Guid.NewGuid()).Outcome ==
            InvestigationAdmissionOutcome.Busy, "New work admitted during pending cancellation.");
        Expect(env.Events(run).Count(e => e.EventType == "cancellation-requested") == 1, "Intent event missing.");
        Expect(env.Cancel(run, 1, key).Outcome == CancellationOutcome.Replayed, "Cancel replay not idempotent.");
        Expect(env.Cancel(run, 2, key).Outcome == CancellationOutcome.IdempotencyConflict, "Reused key accepted.");
        Expect(env.Cancel(run, 9).Outcome == CancellationOutcome.StaleAttempt, "Stale cancellation accepted.");
        Expect(env.Events(run).Count(e => e.EventType == "cancellation-requested") == 1, "Intent duplicated.");
        Expect(!env.Project(run).Cancellable, "Projection offers a second cancellation.");

        env.Roles.Gate.SetResult();
        await WaitTerminal(env, run);
        attempt = env.Store.GetAttempt(run)!;
        Expect(attempt is { Status: "cancelled", TerminalCause: "user-cancelled" }, "Wrong terminal cancellation.");
        Expect(env.RunStatus(run) == "cancelled" && SlotHeld(env.Store) == 0, "Run/slot state wrong after cancel.");
        var types = env.Events(run).Select(e => e.EventType).ToArray();
        Expect(Array.IndexOf(types, "cancellation-requested") < Array.IndexOf(types, "attempt-cancelled"),
            "Cancellation chronology wrong.");
        Expect(env.Cancel(run, 1).Outcome == CancellationOutcome.NoActiveAttempt, "Cancelled a terminal attempt.");
        Expect(env.Continue(run, 1).Outcome == ContinuationOutcome.NotRecoverable, "Cancelled run continued.");
        await env.Host.Worker.StopAsync(CancellationToken.None);
    }

    private static async Task CancelCooperative(Fixture f)
    {
        var env = new Env(f);
        using var _ = env;
        env.Store.Initialize();
        env.Scope = SeedSnapshot(env.Store);
        env.Roles.Investigator = Mode.Block;
        await env.StartAsync();
        var run = env.Host.Admission.Admit(env.Scope, Guid.NewGuid()).Record!.RunId;
        await env.Roles.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Expect(env.Cancel(run, 1).Outcome == CancellationOutcome.Requested, "Cancellation refused.");
        await WaitTerminal(env, run);
        Expect(env.Store.GetAttempt(run) is { Status: "cancelled", TerminalCause: "user-cancelled" },
            "User cancellation misclassified (e.g. as shutdown or deadline).");
        Expect(env.Events(run).Any(e => e.EventType == "role-cancelled" && e.Stage == "investigator"),
            "Role cancellation not observed.");
        Expect(env.Store.ListCheckpoints(run).Count == 0, "Cancelled role produced a checkpoint.");
        await env.Host.Worker.StopAsync(CancellationToken.None);
    }

    private static async Task CancelQueued(Fixture f)
    {
        var env = new Env(f) { StartWorker = false };
        using var _ = env;
        env.Store.Initialize();
        env.Scope = SeedSnapshot(env.Store);
        await env.StartAsync();
        var run = env.Host.Admission.Admit(env.Scope, Guid.NewGuid()).Record!.RunId;
        Expect(env.Cancel(run, 1).Outcome == CancellationOutcome.Requested, "Queued cancellation refused.");
        Expect(env.Store.GetAttempt(run)!.Status == "queued" && SlotHeld(env.Store) == 1,
            "Cancellation request itself finalized the attempt.");
        await env.Host.Worker.StartAsync(CancellationToken.None);
        await WaitTerminal(env, run);
        Expect(env.Store.GetAttempt(run) is { Status: "cancelled", TerminalCause: "user-cancelled" },
            "Queued attempt not cancelled at claim boundary.");
        Expect(CallsOf(env) == 0 && env.Evidence.Opens == 0, "Cancelled queued attempt executed work.");
        Expect(env.RunStatus(run) == "cancelled" && SlotHeld(env.Store) == 0, "Run/slot state wrong.");
        await env.Host.Worker.StopAsync(CancellationToken.None);
    }

    private static async Task CancelIntentWithoutNotification(Fixture f)
    {
        var (env, run) = await Begin(f);
        using var _ = env;
        // First run completes normally; the second blocks so intent can be injected.
        await WaitTerminal(env, run);
        env.Roles.Investigator = Mode.Block;
        env.Roles.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = env.Host.Admission.Admit(env.Scope, Guid.NewGuid()).Record!.RunId;
        await env.Roles.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Intent written directly, bypassing the worker notification.
        var stored = env.Store.RequestCancellation(env.Host.Ownership.Fence!, second, Guid.NewGuid(), 1);
        Expect(stored.Outcome == CancellationRequestOutcome.Requested, "Intent not stored.");
        await WaitTerminal(env, second);
        Expect(env.Store.GetAttempt(second)!.TerminalCause == "user-cancelled",
            "Durable intent was not honored without notification.");
        await env.Host.Worker.StopAsync(CancellationToken.None);
    }

    private static async Task Projection(Fixture f)
    {
        var env = new Env(f);
        using var _ = env;
        env.Store.Initialize();
        env.Scope = SeedSnapshot(env.Store);
        env.Roles.Skeptic = Mode.Block;
        await env.StartAsync();
        var run = env.Host.Admission.Admit(env.Scope, Guid.NewGuid()).Record!.RunId;
        await env.Roles.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var live = env.Project(run);
        Expect(live.ProjectionVersion == GateC6ExecutionProjectionContract.Version && !live.Legacy &&
            live.CurrentAttempt?.Status == "running" && live.Cancellable && live.ExecutionOwner,
            "Live projection header wrong.");
        Expect(live.Stages.Select(s => s.Status).SequenceEqual(["accepted", "running", "not-started"]) &&
            live.Stages[0].Checkpoint is { AcceptedInAttemptNumber: 1 } &&
            live.Stages[0].Checkpoint!.ContentSha256.Length == 64, "Live stage view wrong: " +
            string.Join(",", live.Stages.Select(s => s.Status)));
        Expect(!live.Recovery.Eligible && live.Recovery.Diagnostic == "another-execution-active",
            "Active run reported as recoverable.");

        // Shutdown mid-Skeptic -> interrupted, recoverable.
        await env.RestartAsync();
        var interrupted = env.Project(run);
        Expect(interrupted.RunStatus == "interrupted" && interrupted.Recovery.Eligible &&
            interrupted.Recovery.ExpectedAttemptNumber == 1 && !interrupted.Cancellable,
            "Interrupted run not reported eligible: " + interrupted.Recovery.Diagnostic);
        Expect(interrupted.Stages.Select(s => s.Status).SequenceEqual(["accepted", "cancelled", "not-started"]),
            "Interrupted stage view wrong: " + string.Join(",", interrupted.Stages.Select(s => s.Status)));
        Expect(interrupted.CurrentAttempt?.TerminalCause == "application-shutdown", "Cause missing.");

        // Interrupted and eligible are separate: a changed snapshot makes it ineligible.
        env.Evidence.SnapshotChanged = true;
        var blocked = env.Project(run);
        Expect(blocked.RunStatus == "interrupted" && !blocked.Recovery.Eligible &&
            blocked.Recovery.Diagnostic == "snapshot-changed-or-invalid", "Ineligibility not reported.");
        env.Evidence.SnapshotChanged = false;

        env.Roles.Skeptic = Mode.Succeed;
        Expect(env.Continue(run, 1).Outcome == ContinuationOutcome.Admitted, "Continuation refused.");
        await Until(() => env.RunStatus(run) == "completed", "completion");
        var done = env.Project(run);
        Expect(done.Attempts.Count == 2 && done.Attempts[1].ContinuesAttemptId == done.Attempts[0].AttemptId &&
            done.Stages.Select(s => s.Status).SequenceEqual(["reused-checkpoint", "accepted", "accepted"]) &&
            !done.Recovery.Eligible && done.Recovery.Diagnostic == "run-already-completed",
            "Completed projection wrong: " + string.Join(",", done.Stages.Select(s => s.Status)));
        Expect(env.Projections.Project(env.Scope.WorkspaceId, env.Scope.RepositoryId, Guid.NewGuid(), run,
            CancellationToken.None) is null, "Projection leaked across snapshot scope.");
        await env.Host.Worker.StopAsync(CancellationToken.None);
    }
}

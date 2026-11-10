using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Investigations;

/// <summary>
/// Role stage execution. Production binds the genuine MAF roles
/// (<see cref="MafRoleStageRunner"/>); deterministic tests bind model-free
/// fixtures. An independent role receives ONLY its own fresh evidence
/// session: there is no parameter through which another role's output can flow.
/// </summary>
public interface IGateCRoleStageRunner
{
    Task<GateC3RoleOutcome> RunIndependentAsync(GateCEvidenceSession session, Guid runId,
        GateCScope scope, CancellationToken ct);

    Task<GateC4ReviewerOutcome> ReviewAsync(GateCIndependentAssessment investigator,
        GateCIndependentAssessment skeptic, Guid runId, GateCScope scope, CancellationToken ct);
}

public sealed class MafRoleStageRunner : IGateCRoleStageRunner
{
    public Task<GateC3RoleOutcome> RunIndependentAsync(GateCEvidenceSession session, Guid runId,
        GateCScope scope, CancellationToken ct) =>
        GateC3IndependentRunner.RunRoleAsync(session, runId, scope, session.Role, ct);

    public Task<GateC4ReviewerOutcome> ReviewAsync(GateCIndependentAssessment investigator,
        GateCIndependentAssessment skeptic, Guid runId, GateCScope scope, CancellationToken ct) =>
        GateC4ReconciliationRunner.ReviewAsync(investigator, skeptic, runId, scope, ct);
}

/// <summary>Everything the worker hands to the coordinator for one claimed attempt.</summary>
public sealed record InvestigationExecutionContext(SnapshotRecord Snapshot,
    InvestigationSummary Run, InvestigationAttempt Attempt, ExecutionFence Fence);

/// <summary>Accepted checkpoints failed revalidation at execution time (fail closed).</summary>
public sealed class CheckpointRejectedException(string diagnostic) : Exception(diagnostic)
{
    public string Diagnostic { get; } = diagnostic;
}

/// <summary>
/// A fenced checkpoint/stage write failed. Treated like a crash before
/// commit: the worker writes nothing further and halts (fail closed).
/// </summary>
public sealed class StagePersistenceException(Exception inner)
    : Exception("Stage persistence failed.", inner);

/// <summary>
/// C6b responsibility 3 — checkpoint-aware coordinator. Sequences
/// Investigator → Skeptic → Reviewer through the unchanged host state
/// machine, reuses only independently revalidated checkpoints, executes only
/// unfinished stages, and persists a checkpoint ONLY after host acceptance.
/// It owns no state or authorization: every write goes through the fenced store.
/// </summary>
public sealed class GateC6StageCoordinator(WorkspaceStore store,
    IGateCEvidenceSessionFactory evidence, IGateCRoleStageRunner roles,
    GateC6CheckpointRevalidator revalidator) : IInvestigationExecutor
{
    public async Task<GateC4RunOutcome> ExecuteAsync(InvestigationExecutionContext context,
        CancellationToken ct)
    {
        var (snapshot, run, attempt, fence) = context;
        var runId = run.RunId;
        var scope = run.Scope;

        // Re-verify accepted checkpoints for THIS execution even though the
        // continuation request already did: the snapshot may have changed since.
        var reusable = revalidator.Revalidate(snapshot, run, store.ListCheckpoints(runId),
            store.ListAcceptedCheckpointEventIds(runId), ct);
        if (!reusable.Accepted)
        {
            Persist(() => store.RecordCheckpointRejection(fence, attempt.AttemptId, reusable.Diagnostic));
            throw new CheckpointRejectedException(reusable.Diagnostic);
        }

        var progress = GateCRunProgress.New(runId);
        var investigator = Missing(GateCRole.Investigator);
        var skeptic = Missing(GateCRole.Skeptic);
        var notStarted = new GateC4ReviewerOutcome(GateCRoleStatus.NotStarted,
            "independent-gate-incomplete", null, null);

        // Fresh, independently verified sessions only for roles that still run.
        GateCEvidenceSession? investigatorSession = null, skepticSession = null;
        Persist(() => store.AppendStageEvent(fence, attempt.AttemptId,
            "evidence-verification-started", null, "verifying-saved-snapshot"));
        try
        {
            if (reusable.Investigator is null)
                investigatorSession = evidence.OpenVerified(snapshot, scope, runId, GateCRole.Investigator, ct);
            if (reusable.Skeptic is null)
                skepticSession = evidence.OpenVerified(snapshot, scope, runId, GateCRole.Skeptic, ct);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Persist(() => store.AppendStageEvent(fence, attempt.AttemptId,
                "evidence-verification-rejected", null, "evidence-scope-or-integrity-rejected"));
            if (error is OperationCanceledException) throw;
            progress = Advance(progress, GateCRunEvent.Reject);
            var category = "evidence-scope-or-integrity-rejected";
            var rejected = new GateC3RunOutcome(runId, scope, progress,
                investigator with { Diagnostic = category }, skeptic with { Diagnostic = category });
            return new(rejected, progress, notStarted);
        }
        Persist(() => store.AppendStageEvent(fence, attempt.AttemptId,
            "evidence-verification-accepted", null, "saved-snapshot-verified"));
        progress = Advance(progress, GateCRunEvent.EvidenceVerified);
        progress = Advance(progress, GateCRunEvent.BeginIndependentAssessments);

        // ---- Investigator
        progress = Advance(progress, GateCRunEvent.StartInvestigator);
        if (reusable.Investigator is { } reusedInvestigator)
        {
            Persist(() => store.RecordCheckpointReuse(fence, attempt.AttemptId, reusedInvestigator.Checkpoint));
            investigator = reusedInvestigator.Outcome;
        }
        else
        {
            investigator = await RunIndependentStageAsync(context, investigatorSession!, ct);
        }
        if (investigator.Status != GateCRoleStatus.CompletedValidated)
        {
            progress = Advance(progress, OutcomeEvent(GateCRole.Investigator, investigator.Status));
            return new(new(runId, scope, progress, investigator, skeptic), progress, notStarted);
        }
        progress = Advance(progress, GateCRunEvent.AcceptInvestigator);

        // ---- Skeptic: new independent session; never sees Investigator output.
        progress = Advance(progress, GateCRunEvent.StartSkeptic);
        if (reusable.Skeptic is { } reusedSkeptic)
        {
            Persist(() => store.RecordCheckpointReuse(fence, attempt.AttemptId, reusedSkeptic.Checkpoint));
            skeptic = reusedSkeptic.Outcome;
        }
        else
        {
            skeptic = await RunIndependentStageAsync(context, skepticSession!, ct);
        }
        progress = Advance(progress, OutcomeEvent(GateCRole.Skeptic, skeptic.Status));
        var independent = new GateC3RunOutcome(runId, scope, progress, investigator, skeptic);
        if (skeptic.Status != GateCRoleStatus.CompletedValidated)
            return new(independent, progress, notStarted);

        // ---- Reviewer: host gate requires both validated independent roles.
        progress = Advance(progress, GateCRunEvent.BeginReviewer);
        GateC4ReviewerOutcome reviewer;
        if (reusable.Reviewer is { } reusedReview)
        {
            Persist(() => store.RecordCheckpointReuse(fence, attempt.AttemptId, reusedReview.Checkpoint));
            reviewer = reusedReview.Outcome;
        }
        else
        {
            Persist(() => store.AppendStageEvent(fence, attempt.AttemptId, "role-started", "reviewer",
                "reviewer-started"));
            reviewer = await roles.ReviewAsync(investigator.Assessment!, skeptic.Assessment!,
                runId, scope, ct);
            if (reviewer.Status == GateCRoleStatus.CompletedValidated)
            {
                // Independently re-run the host gate; never trust the runner's verdict alone.
                var check = GateCContractValidator.ValidateReview(reviewer.Assessment, runId, scope,
                    investigator.Assessment!, skeptic.Assessment!);
                if (!check.Check.Accepted || check.Dispositions.Any(d => d.IsVerifiedFact))
                    reviewer = new(GateCRoleStatus.ValidationRejected,
                        "review-host-revalidation-failed", null, null);
            }
            if (reviewer.Status == GateCRoleStatus.CompletedValidated)
            {
                var deps = new[] { CheckpointOf(GateCRole.Investigator, reusable, context),
                    CheckpointOf(GateCRole.Skeptic, reusable, context) };
                Persist(() => store.AcceptCheckpoint(fence, attempt.AttemptId, new(runId,
                    GateCRole.Reviewer, scope, GateCEvidenceRegistry.Version,
                    GateC6CheckpointContract.ReviewerAnalyzer, GateC4ReconciliationRunner.ModelId,
                    GateC6CheckpointRevalidator.Canonical(reviewer.Assessment!), "[]",
                    GateC6CheckpointRevalidator.Canonical(new GateC6CheckpointValidation(true,
                        reviewer.Reconciliation!.Check.Diagnostic, reviewer.Diagnostic, null, 0)),
                    deps)));
            }
            else
            {
                Persist(() => store.AppendStageEvent(fence, attempt.AttemptId,
                    RoleEndEvent(reviewer.Status), "reviewer", SafeDiagnostic(reviewer.Diagnostic)));
            }
        }
        progress = reviewer.Status switch
        {
            GateCRoleStatus.CompletedValidated =>
                Advance(Advance(progress, GateCRunEvent.AcceptReviewer), GateCRunEvent.Complete),
            GateCRoleStatus.Cancelled => Advance(progress, GateCRunEvent.CancelReviewer),
            GateCRoleStatus.TimedOut => Advance(progress, GateCRunEvent.TimeoutReviewer),
            GateCRoleStatus.Failed => Advance(progress, GateCRunEvent.FailReviewer),
            _ => Advance(progress, GateCRunEvent.RejectReviewer)
        };
        return new(independent, progress, reviewer);
    }

    private async Task<GateC3RoleOutcome> RunIndependentStageAsync(
        InvestigationExecutionContext context, GateCEvidenceSession session, CancellationToken ct)
    {
        var role = session.Role;
        var stage = GateC6CheckpointContract.RoleName(role);
        var (_, run, attempt, fence) = context;
        Persist(() => store.AppendStageEvent(fence, attempt.AttemptId, "role-started", stage,
            stage + "-started"));
        var outcome = await roles.RunIndependentAsync(session, run.RunId, run.Scope, ct);
        if (outcome.Status == GateCRoleStatus.CompletedValidated)
        {
            // Host acceptance: the issuing session revalidates the assessment
            // against its own handle ledger before ANY checkpoint is written.
            var check = session.ValidateAssessment(outcome.Assessment);
            var links = session.ReportLinks();
            if (outcome.Role != role || !check.Accepted ||
                outcome.EvidenceLinks is null || !links.SequenceEqual(outcome.EvidenceLinks))
                outcome = new(role, GateCRoleStatus.ValidationRejected,
                    "host-acceptance-revalidation-failed", null, outcome.SelectedCapability,
                    outcome.ToolCallsUsed);
        }
        if (outcome.Status == GateCRoleStatus.CompletedValidated)
        {
            var accepted = outcome;
            Persist(() => store.AcceptCheckpoint(fence, attempt.AttemptId, new(run.RunId, role,
                run.Scope, GateCEvidenceRegistry.Version, session.GraphAnalyzerVersion,
                GateC3IndependentRunner.ModelId,
                GateC6CheckpointRevalidator.Canonical(accepted.Assessment!),
                GateC6CheckpointRevalidator.Canonical(accepted.EvidenceLinks!.ToArray()),
                GateC6CheckpointRevalidator.Canonical(new GateC6CheckpointValidation(true,
                    "contract-valid-not-semantic-proof", accepted.Diagnostic,
                    accepted.SelectedCapability, accepted.ToolCallsUsed)),
                [])));
        }
        else
        {
            Persist(() => store.AppendStageEvent(fence, attempt.AttemptId,
                RoleEndEvent(outcome.Status), stage, SafeDiagnostic(outcome.Diagnostic)));
        }
        return outcome;
    }

    private Guid CheckpointOf(GateCRole role, GateC6Revalidation reusable,
        InvestigationExecutionContext context)
    {
        var reused = role == GateCRole.Investigator ? reusable.Investigator : reusable.Skeptic;
        if (reused is not null) return reused.Checkpoint.CheckpointId;
        var name = GateC6CheckpointContract.RoleName(role);
        return store.ListCheckpoints(context.Run.RunId).Single(c => c.Role == name).CheckpointId;
    }

    private static string RoleEndEvent(GateCRoleStatus status) => status switch
    {
        GateCRoleStatus.ValidationRejected => "role-assessment-rejected",
        GateCRoleStatus.TimedOut => "role-timed-out",
        GateCRoleStatus.Cancelled => "role-cancelled",
        _ => "role-failed"
    };

    // Role diagnostics are already host-fixed reason codes; bound them anyway.
    private static string SafeDiagnostic(string diagnostic) =>
        diagnostic.Length <= 80 && diagnostic.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? diagnostic : "role-diagnostic-withheld";

    private static void Persist(Action write)
    {
        try { write(); }
        catch (ExecutionFenceException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new StagePersistenceException(error);
        }
    }

    private static GateC3RoleOutcome Missing(GateCRole role) =>
        new(role, GateCRoleStatus.NotStarted, "not-started", null, null, 0);

    private static GateCRunProgress Advance(GateCRunProgress state, GateCRunEvent action)
    {
        var transition = GateCStateTransitions.Apply(state, action);
        if (!transition.Check.Accepted)
            throw new InvalidOperationException("Illegal host stage transition.");
        return transition.Progress;
    }

    private static GateCRunEvent OutcomeEvent(GateCRole role, GateCRoleStatus status) =>
        (role, status) switch
        {
            (GateCRole.Investigator, GateCRoleStatus.CompletedValidated) => GateCRunEvent.AcceptInvestigator,
            (GateCRole.Investigator, GateCRoleStatus.ValidationRejected) => GateCRunEvent.RejectInvestigator,
            (GateCRole.Investigator, GateCRoleStatus.TimedOut) => GateCRunEvent.TimeoutInvestigator,
            (GateCRole.Investigator, GateCRoleStatus.Cancelled) => GateCRunEvent.CancelInvestigator,
            (GateCRole.Investigator, _) => GateCRunEvent.FailInvestigator,
            (GateCRole.Skeptic, GateCRoleStatus.CompletedValidated) => GateCRunEvent.AcceptSkeptic,
            (GateCRole.Skeptic, GateCRoleStatus.ValidationRejected) => GateCRunEvent.RejectSkeptic,
            (GateCRole.Skeptic, GateCRoleStatus.TimedOut) => GateCRunEvent.TimeoutSkeptic,
            (GateCRole.Skeptic, GateCRoleStatus.Cancelled) => GateCRunEvent.CancelSkeptic,
            (GateCRole.Skeptic, _) => GateCRunEvent.FailSkeptic,
            _ => throw new InvalidOperationException("Unsupported role outcome transition.")
        };
}

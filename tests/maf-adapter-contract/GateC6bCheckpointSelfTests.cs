using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;
using static GateC6bSelfTests;

/// <summary>
/// Gate C6b Phase 3, model-free: the REAL checkpoint-aware coordinator,
/// fenced checkpoint store, revalidator and continuation service, with
/// fixture role runners and a fixture evidence package in place of MAF/Ollama
/// and Roslyn. Each scenario uses its own disposable data directory.
/// </summary>
internal static class GateC6bCheckpointSelfTests
{
    public static async Task RunAsync()
    {
        await ScenarioAsync("three roles checkpointed only after host acceptance", AllRolesCheckpointed);
        await ScenarioAsync("host rejects forged role output; no checkpoint", ForgedOutputRejected);
        await ScenarioAsync("crash before Skeptic commit: continue reuses Investigator, fresh Skeptic", CrashBeforeSkepticCommit);
        await ScenarioAsync("role timeout leaves run recoverable; continuation completes", RoleTimeoutRecoverable);
        await ScenarioAsync("three valid checkpoints complete the report without a model call", ModelFreeCompletion);
        await ScenarioAsync("continuation conflicts, busy, stale and stage-dependency guards", ContinuationGuards);
        await GateC6bControlSelfTests.RunAsync();
        foreach (var (name, tamper, diagnostic) in TamperCases())
            await ScenarioAsync("fail closed: " + name, f => TamperedCheckpointRejected(f, tamper, diagnostic));
    }

    // ---------------------------------------------------------------- fixtures

    internal sealed class Evidence : IGateCEvidenceSessionFactory
    {
        public string Analyzer = "graph-v8";
        public bool SnapshotChanged;
        public int Opens;

        public GateCEvidenceSession OpenVerified(SnapshotRecord snapshot, GateCScope scope,
            Guid runId, GateCRole role, CancellationToken ct)
        {
            Interlocked.Increment(ref Opens);
            if (SnapshotChanged) throw new IOException("saved bytes changed");
            var package = new AgentEvidencePackage(SnapshotEvidenceTools.Version, scope.WorkspaceId,
                scope.RepositoryId, scope.SnapshotId, scope.FingerprintSha256, Analyzer,
                scope.EntryPointId, "http-endpoint-attribute", "[HttpGet] void F() { G(); }", false, 1, 1, 0,
                [
                    new(scope.EntryPointId, "http-endpoint-attribute", "candidate", "entry", "A.cs", new string('b', 64), 1, 1, null),
                    new("method", "syntax-declaration", "observed-syntax", "F", "A.cs", new string('b', 64), 2, 1, null),
                    new("call", "invocation-site", "observed-syntax", "G()", "A.cs", new string('b', 64), 2, 20, null),
                ],
                [
                    new("annotation", "annotates-method", "candidate", scope.EntryPointId, "method",
                        "A.cs", new string('b', 64), 1, 1, "attribute annotates method", null),
                    new("contains", "contains-invocation", "observed-syntax", "method", "call",
                        "A.cs", new string('b', 64), 2, 20, "method contains invocation", null),
                ],
                ["Runtime unknown."], ["Source only."]);
            return new GateCEvidenceSession(package, scope, runId, role);
        }
    }

    internal enum Mode { Succeed, Forge, TimeOut, Block, BlockIgnoringCancel }

    internal sealed class Roles : IGateCRoleStageRunner
    {
        public Mode Investigator = Mode.Succeed, Skeptic = Mode.Succeed, Reviewer = Mode.Succeed;
        public int InvestigatorCalls, SkepticCalls, ReviewerCalls;
        public readonly List<(int Handles, int Calls)> SkepticEntryState = [];
        public TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GateC3RoleOutcome> RunIndependentAsync(GateCEvidenceSession session,
            Guid runId, GateCScope scope, CancellationToken ct)
        {
            var role = session.Role;
            Mode mode;
            if (role == GateCRole.Investigator) { InvestigatorCalls++; mode = Investigator; }
            else
            {
                SkepticCalls++; mode = Skeptic;
                // The Skeptic must start from an empty, independent evidence context.
                SkepticEntryState.Add((session.HostCatalog().Count, session.ToolCallsUsed));
            }
            if (mode == Mode.TimeOut)
                return new(role, GateCRoleStatus.TimedOut, "model-deadline-exceeded", null, null, 0);
            if (mode == Mode.Block)
            {
                Entered.TrySetResult();
                try { await Gate.Task.WaitAsync(ct); }
                catch (OperationCanceledException)
                {
                    return new(role, GateCRoleStatus.Cancelled, "caller-cancelled", null, null, 0);
                }
            }
            if (mode == Mode.BlockIgnoringCancel)
            {
                Entered.TrySetResult();
                await Gate.Task; // like an uncooperative in-flight provider call
            }
            var view = session.Request(GateCEvidenceRegistry.Relationships, ct).Response!;
            var handle = mode == Mode.Forge ? "E9" : view.Items[0].Handle;
            var assessment = role == GateCRole.Investigator
                ? new GateCIndependentAssessment(GateCContract.Version, runId, scope, role, [handle],
                    [new GateCHypothesis("H1", "Attribute may annotate F", [handle], "Not runtime proof")],
                    [], ["Binding unknown"])
                : new GateCIndependentAssessment(GateCContract.Version, runId, scope, role, [handle], [],
                    [new GateCConcern("S1", GateCConcernKind.InsufficientEvidence, "Runtime not proven", [handle])],
                    ["Runtime unknown"]);
            return new(role, GateCRoleStatus.CompletedValidated, "valid-structured-assessment-not-semantic-proof",
                assessment, GateCEvidenceRegistry.Relationships, session.ToolCallsUsed,
                session.ReportLinks(), session.GraphAnalyzerVersion);
        }

        public Task<GateC4ReviewerOutcome> ReviewAsync(GateCIndependentAssessment investigator,
            GateCIndependentAssessment skeptic, Guid runId, GateCScope scope, CancellationToken ct)
        {
            ReviewerCalls++;
            if (Reviewer == Mode.TimeOut)
                return Task.FromResult(new GateC4ReviewerOutcome(GateCRoleStatus.TimedOut,
                    "reviewer-deadline-exceeded", null, null));
            var review = new GateCReviewerAssessment(GateCContract.Version, runId, scope, GateCRole.Reviewer,
                [new GateCReviewDecision("H1", GateCRecommendation.ContestedOrInsufficient, ["S1"],
                    "Insufficient evidence")], []);
            var check = GateCContractValidator.ValidateReview(review, runId, scope, investigator, skeptic);
            return Task.FromResult(new GateC4ReviewerOutcome(GateCRoleStatus.CompletedValidated,
                "review-validated-not-semantic-proof", review, check));
        }
    }

    internal sealed class Env(Fixture f) : IDisposable
    {
        public readonly WorkspaceStore Store = f.Store;
        public readonly Evidence Evidence = new();
        public readonly Roles Roles = new();
        public GateCScope Scope = default!;
        public Host Host = default!;
        public InvestigationContinuationService Continuations = default!;
        public InvestigationCancellationService Cancellations = default!;
        public InvestigationExecutionProjectionService Projections = default!;

        public async Task StartAsync()
        {
            var revalidator = new GateC6CheckpointRevalidator(Evidence);
            Host = StartHost(Store, new GateC6StageCoordinator(Store, Evidence, Roles, revalidator));
            Continuations = new(Store, Host.Ownership, revalidator, Host.Signal);
            Cancellations = new(Store, Host.Ownership, Host.Worker);
            Projections = new(Store, Host.Ownership, Continuations);
            if (StartWorker) await Host.Worker.StartAsync(CancellationToken.None);
        }

        public bool StartWorker = true;

        public CancellationResult Cancel(Guid run, int expected, Guid? key = null) =>
            Cancellations.Cancel(Scope.WorkspaceId, Scope.RepositoryId, Scope.SnapshotId, run,
                key ?? Guid.NewGuid(), expected);

        public ExecutionProjection Project(Guid run) =>
            Projections.Project(Scope.WorkspaceId, Scope.RepositoryId, Scope.SnapshotId, run,
                CancellationToken.None)!;

        /// <summary>Explicit restart: stop worker, release the lock, start a new owner.</summary>
        public async Task RestartAsync()
        {
            await Host.Worker.StopAsync(CancellationToken.None);
            Host.Dispose();
            await StartAsync();
        }

        public ContinuationResult Continue(Guid run, int expected, Guid? key = null) =>
            Continuations.Continue(Scope.WorkspaceId, Scope.RepositoryId, Scope.SnapshotId, run,
                key ?? Guid.NewGuid(), expected, CancellationToken.None);

        public string RunStatus(Guid run) => Store.GetInvestigation(Scope.WorkspaceId,
            Scope.RepositoryId, Scope.SnapshotId, run)!.Summary.Status;

        public IReadOnlyList<InvestigationEvent> Events(Guid run) =>
            Store.ListEvents(Scope.WorkspaceId, Scope.RepositoryId, Scope.SnapshotId, run);

        public void Dispose() => Host?.Dispose();
    }

    internal static Task<(Env Env, Guid Run)> Begin(Fixture f) => BeginAsync(f);
    internal static Task WaitTerminal(Env env, Guid run) => Terminal(env, run);

    private static async Task<(Env Env, Guid Run)> BeginAsync(Fixture f)
    {
        var env = new Env(f);
        env.Store.Initialize();
        env.Scope = SeedSnapshot(env.Store);
        await env.StartAsync();
        var admitted = env.Host.Admission.Admit(env.Scope, Guid.NewGuid());
        Expect(admitted.Outcome == InvestigationAdmissionOutcome.Admitted, "Not admitted.");
        return (env, admitted.Record!.RunId);
    }

    private static Task Terminal(Env env, Guid run) =>
        Until(() => env.Store.GetAttempt(run) is { } a && GateC6AttemptTransitions.IsTerminal(a.Status),
            "terminal attempt");

    // ---------------------------------------------------------------- scenarios

    private static async Task AllRolesCheckpointed(Fixture f)
    {
        var (env, run) = await BeginAsync(f);
        using var _ = env;
        await Terminal(env, run);
        Expect(env.RunStatus(run) == "completed", "Run did not complete: " + env.RunStatus(run));
        var cps = env.Store.ListCheckpoints(run);
        Expect(cps.Select(c => c.Role).OrderBy(x => x).SequenceEqual(["investigator", "reviewer", "skeptic"]),
            "Expected exactly one checkpoint per role.");
        Expect(cps.All(c => c.ComputeDigest() == c.ContentSha256 && c.Scope == env.Scope &&
            c.ContractVersion == GateC6CheckpointContract.Version), "Checkpoint binding or digest invalid.");
        var inv = cps.Single(c => c.Role == "investigator");
        var sk = cps.Single(c => c.Role == "skeptic");
        var rv = cps.Single(c => c.Role == "reviewer");
        Expect(inv.DependsOnJson == "[]" && sk.DependsOnJson == "[]", "Independent role depends on another role.");
        Expect(rv.DependsOnJson.Contains(inv.CheckpointId.ToString("D")) &&
            rv.DependsOnJson.Contains(sk.CheckpointId.ToString("D")), "Reviewer lineage missing.");
        var types = env.Events(run).Select(e => (e.EventType, e.Stage)).ToArray();
        Expect(types.SequenceEqual([
            ("attempt-admitted", null), ("attempt-started", null),
            ("evidence-verification-started", null), ("evidence-verification-accepted", null),
            ("role-started", "investigator"), ("checkpoint-accepted", "investigator"),
            ("role-started", "skeptic"), ("checkpoint-accepted", "skeptic"),
            ("role-started", "reviewer"), ("checkpoint-accepted", "reviewer"),
            ("attempt-completed", null)]), "Host event chronology is wrong: " +
            string.Join(",", types.Select(t => t.EventType + "/" + t.Stage)));
        Expect(env.Events(run).Where(e => e.EventType == "checkpoint-accepted")
            .Select(e => e.CheckpointId).ToHashSet().SetEquals(cps.Select(c => c.CheckpointId.ToString("D"))),
            "Checkpoint events do not name their checkpoints.");
        Expect(env.Store.ListLineage(run).Count(l => l.Relation == "accepted") == 3, "Lineage missing.");
        ExpectSqliteAbort(env.Store, "UPDATE investigation_checkpoints SET model_id='x';");
        ExpectSqliteAbort(env.Store, "DELETE FROM investigation_checkpoints;");
        ExpectSqliteAbort(env.Store, "DELETE FROM attempt_checkpoint_lineage;");
        var report = env.Store.GetInvestigation(env.Scope.WorkspaceId, env.Scope.RepositoryId,
            env.Scope.SnapshotId, run)!.Report!;
        Expect(report.EvidenceLinks.Count == 4 &&
            report.Reviewer.Reconciliation!.Dispositions.All(d => !d.IsVerifiedFact), "Report invalid.");
        Expect(env.Continue(run, 1).Outcome == ContinuationOutcome.AlreadyCompleted,
            "Completed run accepted continuation.");
        await env.Host.Worker.StopAsync(CancellationToken.None);
    }

    private static async Task ForgedOutputRejected(Fixture f)
    {
        var env = new Env(f);
        using var _ = env;
        env.Store.Initialize();
        env.Scope = SeedSnapshot(env.Store);
        env.Roles.Investigator = Mode.Forge;
        await env.StartAsync();
        var run = env.Host.Admission.Admit(env.Scope, Guid.NewGuid()).Record!.RunId;
        await Terminal(env, run);
        Expect(env.Store.ListCheckpoints(run).Count == 0, "Forged output was checkpointed.");
        Expect(env.Events(run).Any(e => e.EventType == "role-assessment-rejected" && e.Stage == "investigator"),
            "Rejection not recorded.");
        Expect(env.RunStatus(run) == "incomplete" &&
            env.Store.GetAttempt(run)!.TerminalCause == "validation-rejected", "Validation rejection misrecorded.");
        Expect(env.Roles.SkepticCalls == 0 && env.Roles.ReviewerCalls == 0, "Later stages ran after rejection.");
        await env.Host.Worker.StopAsync(CancellationToken.None);
    }

    private static async Task CrashBeforeSkepticCommit(Fixture f)
    {
        var env = new Env(f);
        using var _ = env;
        env.Store.Initialize();
        env.Scope = SeedSnapshot(env.Store);
        Exec(env.Store, """
            CREATE TRIGGER test_crash_skeptic BEFORE INSERT ON investigation_events
            WHEN NEW.event_type='checkpoint-accepted' AND NEW.stage='skeptic'
            BEGIN SELECT RAISE(ABORT, 'crash before commit'); END;
            """);
        await env.StartAsync();
        var run = env.Host.Admission.Admit(env.Scope, Guid.NewGuid()).Record!.RunId;
        await Until(() => env.Host.Worker.State == InvestigationWorkerState.Halted, "halt");
        var first = env.Store.GetAttempt(run)!;
        Expect(first.Status == "running", "Attempt finalized after stage persistence failure.");
        Expect(env.Store.ListCheckpoints(run).Select(c => c.Role).SequenceEqual(["investigator"]),
            "Crash-before-commit left a Skeptic checkpoint (or lost the Investigator one).");
        Exec(env.Store, "DROP TRIGGER test_crash_skeptic;");

        await env.RestartAsync();
        Expect(env.Store.GetAttempt(run) is { Status: "interrupted", TerminalCause: "process-interrupted" },
            "Restart did not record interruption.");
        Expect(env.Roles.InvestigatorCalls == 1 && env.Roles.SkepticCalls == 1, "Restart ran a model.");

        var key = Guid.NewGuid();
        var continued = env.Continue(run, 1, key);
        Expect(continued.Outcome == ContinuationOutcome.Admitted, "Continuation refused: " + continued.Diagnostic);
        await Until(() => env.RunStatus(run) == "completed", "continued completion");
        Expect(env.Roles.InvestigatorCalls == 1, "Accepted Investigator checkpoint was re-run.");
        Expect(env.Roles.SkepticCalls == 2 && env.Roles.ReviewerCalls == 1, "Unfinished stages not executed once.");
        Expect(env.Roles.SkepticEntryState.All(s => s == (0, 0)), "Skeptic did not start independent.");

        var attempts = env.Store.ListAttempts(run);
        Expect(attempts.Count == 2 && attempts[1].Number == 2 && attempts[1].ContinuesAttemptId == first.AttemptId &&
            attempts[0].Status == "interrupted" && attempts[0].StateVersion == first.StateVersion + 1,
            "Attempt lineage wrong or historical attempt rewritten.");
        var lineage = env.Store.ListLineage(run);
        var inv = env.Store.ListCheckpoints(run).Single(c => c.Role == "investigator");
        Expect(inv.AttemptId == first.AttemptId, "Original checkpoint provenance was rewritten.");
        Expect(lineage.Count(l => l.AttemptId == attempts[1].AttemptId && l.Relation == "reused" &&
            l.CheckpointId == inv.CheckpointId) == 1 &&
            lineage.Count(l => l.AttemptId == attempts[1].AttemptId && l.Relation == "accepted") == 2,
            "Continuation lineage wrong.");
        var secondEvents = env.Events(run).Where(e => e.AttemptId == attempts[1].AttemptId)
            .Select(e => e.EventType).ToArray();
        Expect(secondEvents[0] == "continuation-admitted" && secondEvents.Contains("checkpoint-reused") &&
            secondEvents[^1] == "attempt-completed", "Continuation events wrong.");

        // Idempotent continuation does not duplicate execution.
        var replay = env.Continue(run, 1, key);
        Expect(replay.Outcome == ContinuationOutcome.Replayed && replay.Attempt?.AttemptId == attempts[1].AttemptId,
            "Continuation replay not idempotent.");
        Expect(env.Continue(run, 2, key).Outcome == ContinuationOutcome.IdempotencyConflict,
            "Reused key with different request accepted.");
        Expect(env.Store.ListAttempts(run).Count == 2 && env.Roles.SkepticCalls == 2, "Replay duplicated work.");
        await env.Host.Worker.StopAsync(CancellationToken.None);
    }

    private static async Task RoleTimeoutRecoverable(Fixture f)
    {
        var env = new Env(f);
        using var _ = env;
        env.Store.Initialize();
        env.Scope = SeedSnapshot(env.Store);
        env.Roles.Skeptic = Mode.TimeOut;
        await env.StartAsync();
        var run = env.Host.Admission.Admit(env.Scope, Guid.NewGuid()).Record!.RunId;
        await Terminal(env, run);
        Expect(env.Store.GetAttempt(run) is { Status: "incomplete", TerminalCause: "role-timeout" },
            "Role timeout not recorded distinctly.");
        Expect(env.RunStatus(run) == "interrupted", "Role timeout did not leave the run recoverable.");
        Expect(env.Events(run).Any(e => e.EventType == "role-timed-out" && e.Stage == "skeptic"), "No timeout event.");
        await Task.Delay(300);
        Expect(env.Roles.SkepticCalls == 1, "Timed-out role was retried automatically.");
        env.Roles.Skeptic = Mode.Succeed;
        Expect(env.Continue(run, 1).Outcome == ContinuationOutcome.Admitted, "Continuation refused.");
        await Until(() => env.RunStatus(run) == "completed", "completion");
        Expect(env.Roles.InvestigatorCalls == 1 && env.Roles.SkepticCalls == 2, "Wrong stages executed.");
        await env.Host.Worker.StopAsync(CancellationToken.None);
    }

    private static async Task ModelFreeCompletion(Fixture f)
    {
        var env = new Env(f);
        using var _ = env;
        env.Store.Initialize();
        env.Scope = SeedSnapshot(env.Store);
        Exec(env.Store, """
            CREATE TRIGGER test_crash_report BEFORE INSERT ON investigation_events
            WHEN NEW.event_type='attempt-completed'
            BEGIN SELECT RAISE(ABORT, 'crash before report'); END;
            """);
        await env.StartAsync();
        var run = env.Host.Admission.Admit(env.Scope, Guid.NewGuid()).Record!.RunId;
        await Until(() => env.Host.Worker.State == InvestigationWorkerState.Halted, "halt");
        Expect(env.Store.ListCheckpoints(run).Count == 3, "Checkpoints not durable after commit.");
        Exec(env.Store, "DROP TRIGGER test_crash_report;");
        await env.RestartAsync();
        Expect(env.RunStatus(run) == "interrupted", "Run not interrupted after restart.");
        var calls = (env.Roles.InvestigatorCalls, env.Roles.SkepticCalls, env.Roles.ReviewerCalls);
        Expect(env.Continue(run, 1).Outcome == ContinuationOutcome.Admitted, "Continuation refused.");
        await Until(() => env.RunStatus(run) == "completed", "model-free completion");
        Expect((env.Roles.InvestigatorCalls, env.Roles.SkepticCalls, env.Roles.ReviewerCalls) == calls,
            "A model role was invoked despite three valid checkpoints.");
        var second = env.Store.ListAttempts(run)[1];
        Expect(env.Store.ListLineage(run).Count(l => l.AttemptId == second.AttemptId && l.Relation == "reused") == 3,
            "Report not completed from all three reused checkpoints.");
        await env.Host.Worker.StopAsync(CancellationToken.None);
    }

    private static async Task ContinuationGuards(Fixture f)
    {
        var env = new Env(f);
        using var _ = env;
        env.Store.Initialize();
        env.Scope = SeedSnapshot(env.Store);
        env.Roles.Skeptic = Mode.TimeOut;
        await env.StartAsync();
        var run = env.Host.Admission.Admit(env.Scope, Guid.NewGuid()).Record!.RunId;
        await Terminal(env, run);
        Expect(env.Continue(run, 7).Outcome == ContinuationOutcome.StaleAttempt, "Stale expected attempt accepted.");
        Expect(env.Continuations.Continue(env.Scope.WorkspaceId, env.Scope.RepositoryId, Guid.NewGuid(), run,
            Guid.NewGuid(), 1, CancellationToken.None).Outcome == ContinuationOutcome.NotFound, "Cross-scope continue.");

        env.Roles.Skeptic = Mode.Block;
        Expect(env.Continue(run, 1).Outcome == ContinuationOutcome.Admitted, "Continuation refused.");
        await env.Roles.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var active = env.Store.ListAttempts(run)[1];
        Expect(env.Continue(run, 1).Outcome == ContinuationOutcome.StaleAttempt, "Concurrent continuation accepted.");
        Expect(env.Continue(run, 2).Outcome == ContinuationOutcome.Busy, "Continuation accepted while active.");
        var fence = env.Host.Ownership.Fence!;
        var inv = env.Store.ListCheckpoints(run).Single();
        // Reviewer admission requires BOTH validated independent checkpoints.
        try
        {
            env.Store.AcceptCheckpoint(fence, active.AttemptId, new(run, GateCRole.Reviewer, env.Scope,
                GateCEvidenceRegistry.Version, GateC6CheckpointContract.ReviewerAnalyzer,
                GateC4ReconciliationRunner.ModelId, "{}", "[]", "{}", [inv.CheckpointId]));
            throw new InvalidOperationException("Reviewer checkpoint accepted without a Skeptic checkpoint.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("requires both")) { }
        try
        {
            env.Store.AcceptCheckpoint(fence, active.AttemptId, new(run, GateCRole.Skeptic, env.Scope,
                GateCEvidenceRegistry.Version, "graph-v8", GateC3IndependentRunner.ModelId,
                "{}", "[]", "{}", [inv.CheckpointId]));
            throw new InvalidOperationException("Skeptic checkpoint accepted with an Investigator dependency.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("cannot depend")) { }
        env.Roles.Gate.SetResult();
        await Until(() => env.RunStatus(run) == "completed", "completion");
        await env.Host.Worker.StopAsync(CancellationToken.None);

        // A process without the execution lock cannot continue anything.
        var nonOwner = new InvestigationContinuationService(env.Store,
            new CodeArcheologist.Api.Execution.ExecutionOwnershipState(),
            new GateC6CheckpointRevalidator(env.Evidence), new InvestigationWorkSignal());
        Expect(nonOwner.Continue(env.Scope.WorkspaceId, env.Scope.RepositoryId, env.Scope.SnapshotId, run,
            Guid.NewGuid(), 2, CancellationToken.None).Outcome == ContinuationOutcome.ExecutionUnavailable,
            "Non-owner admitted a continuation.");
    }

    private static IEnumerable<(string, Action<Env, Guid>, string)> TamperCases()
    {
        yield return ("damaged digest", (env, run) => Rewrite(env, run, "assessment_json",
            c => c.AssessmentJson.Replace("Attribute", "Atribute"), recomputeDigest: false),
            "checkpoint-digest-mismatch");
        yield return ("foreign scope", (env, run) => Rewrite(env, run, "snapshot_id",
            _ => Guid.NewGuid().ToString("D"), recomputeDigest: true), "checkpoint-foreign-scope");
        yield return ("altered evidence mapping", (env, run) => Rewrite(env, run, "evidence_links_json",
            c => c.EvidenceLinksJson.Replace("\"annotation\"", "\"contains\""), recomputeDigest: true),
            "foreign-or-altered-evidence-mapping");
        yield return ("incompatible validator", (env, run) => Rewrite(env, run, "validator_version",
            _ => "gate-c-host-validator-v0", recomputeDigest: true), "checkpoint-validator-incompatible");
        yield return ("incompatible evidence contract", (env, run) => Rewrite(env, run, "capability_contract_version",
            _ => "gate-c-evidence-capabilities-v0", recomputeDigest: true), "checkpoint-contract-incompatible");
        yield return ("incompatible model provenance", (env, run) => Rewrite(env, run, "model_id",
            _ => "other-model:1b", recomputeDigest: true), "checkpoint-model-provenance-incompatible");
        yield return ("incompatible analyzer", (env, _) => env.Evidence.Analyzer = "graph-v9",
            "checkpoint-analyzer-incompatible");
        yield return ("changed snapshot", (env, _) => env.Evidence.SnapshotChanged = true,
            "snapshot-changed-or-invalid");
        yield return ("missing accepted checkpoint", (env, _) => Exec(env.Store, """
            PRAGMA foreign_keys = OFF;
            DROP TRIGGER investigation_checkpoints_no_delete;
            DELETE FROM investigation_checkpoints;
            """), "checkpoint-missing-or-unrecorded");
    }

    /// <summary>
    /// Simulates corruption or an adversarial local edit, bypassing the
    /// immutability trigger. With recomputeDigest the digest is made
    /// consistent so the deeper semantic checks are what must reject it.
    /// </summary>
    private static void Rewrite(Env env, Guid run, string column,
        Func<GateC6StoredCheckpoint, string> value, bool recomputeDigest)
    {
        var cp = env.Store.ListCheckpoints(run).Single(c => c.Role == "investigator");
        var newValue = value(cp);
        var changed = column switch
        {
            "assessment_json" => cp with { AssessmentJson = newValue },
            "snapshot_id" => cp with { Scope = cp.Scope with { SnapshotId = Guid.Parse(newValue) } },
            "evidence_links_json" => cp with { EvidenceLinksJson = newValue },
            "validator_version" => cp with { ValidatorVersion = newValue },
            "capability_contract_version" => cp with { CapabilityContractVersion = newValue },
            "model_id" => cp with { ModelId = newValue },
            _ => throw new ArgumentException(column)
        };
        Expect(newValue != (column switch
        {
            "assessment_json" => cp.AssessmentJson, "evidence_links_json" => cp.EvidenceLinksJson,
            _ => "\0"
        }), "Tamper fixture did not change anything.");
        var digest = recomputeDigest ? changed.ComputeDigest() : cp.ContentSha256;
        Exec(env.Store, $"""
            DROP TRIGGER investigation_checkpoints_no_update;
            UPDATE investigation_checkpoints SET {column}=$v, content_sha256=$d WHERE checkpoint_id=$id;
            """, ("$v", newValue), ("$d", digest), ("$id", cp.CheckpointId.ToString("D")));
    }

    private static async Task TamperedCheckpointRejected(Fixture f, Action<Env, Guid> tamper, string diagnostic)
    {
        var env = new Env(f);
        using var _ = env;
        env.Store.Initialize();
        env.Scope = SeedSnapshot(env.Store);
        env.Roles.Skeptic = Mode.TimeOut;
        await env.StartAsync();
        var run = env.Host.Admission.Admit(env.Scope, Guid.NewGuid()).Record!.RunId;
        await Terminal(env, run);
        Expect(env.RunStatus(run) == "interrupted" && env.Store.ListCheckpoints(run).Count == 1,
            "Fixture did not produce an interrupted run with one checkpoint.");
        tamper(env, run);
        env.Roles.Skeptic = Mode.Succeed;
        var result = env.Continue(run, 1);
        Expect(result.Outcome == ContinuationOutcome.CheckpointRejected && result.Diagnostic == diagnostic,
            $"Expected {diagnostic}, got {result.Outcome}/{result.Diagnostic}.");
        await Task.Delay(200);
        Expect(env.Store.ListAttempts(run).Count == 1, "A rejected continuation created an attempt.");
        Expect(env.RunStatus(run) == "interrupted", "Rejected continuation changed run state.");
        Expect(env.Roles.InvestigatorCalls == 1 && env.Roles.SkepticCalls == 1,
            "Damaged checkpoint caused the affected role to be redone.");
        await env.Host.Worker.StopAsync(CancellationToken.None);
    }
}

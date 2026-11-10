using System.Diagnostics;
using CodeArcheologist.Api.Execution;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Gate C6b Phase 1–2 deterministic, model-free checks: schema v6 migration,
/// OS execution lock, fenced ownership epochs, startup reconciliation only
/// after ownership, real cross-process exclusion, stale-write rejection, and
/// the managed hosted worker lifecycle with fault injection. Every scenario
/// uses its own disposable data directory; no real user workspace is touched
/// and no imported repository code or model provider is ever executed.
/// </summary>
internal static class GateC6bSelfTests
{
    public static async Task RunAsync()
    {
        Scenario("v6 forward migration preserves history and recovers nothing", MigrationPreservesHistory);
        Scenario("v6 migration refuses unknown-provenance v5 checkpoints", MigrationRefusesV5Checkpoints);
        Scenario("in-process OS lock exclusion and release", InProcessLockExclusion);
        Scenario("ownership epoch triggers are monotonic and permanent", EpochTriggers);
        Scenario("cross-process ownership, heartbeat non-theft, stale-write fencing", CrossProcessOwnership);
        await ScenarioAsync("worker never auto-invokes a model at startup", WorkerNoStartupInvocation);
        await ScenarioAsync("worker executes admitted work exactly once", WorkerCompletes);
        await ScenarioAsync("non-owner process refuses admission and execution", NonOwnerRefuses);
        await ScenarioAsync("shutdown keeps slot until uncooperative call returns", WorkerShutdownUncooperative);
        await ScenarioAsync("attempt deadline is recorded distinctly", WorkerDeadline);
        await ScenarioAsync("failed terminal write is never retried as another outcome", WorkerTerminalWriteFailure);
        ClassificationIsHostObserved();
        Console.WriteLine("GREEN: C6b Phase 1-2 schema v6, OS lock, fenced epochs, cross-process ownership, reconciliation-after-ownership, stale-write rejection, managed worker lifecycle and fault injection.");
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Fixture(string Directory, WorkspaceStore Store, GateCScope Scope);

    private static void Scenario(string name, Action<Fixture> body) =>
        ScenarioAsync(name, f => { body(f); return Task.CompletedTask; }).GetAwaiter().GetResult();

    private static async Task ScenarioAsync(string name, Func<Fixture, Task> body)
    {
        var original = Environment.GetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR");
        var dir = Path.Combine(Path.GetTempPath(), "CodeArcheologist-C6b-" + Guid.NewGuid().ToString("N"));
        var failed = false;
        try
        {
            Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", dir);
            var store = new WorkspaceStore();
            if (!store.DataDirectory.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.Ordinal))
                throw new InvalidOperationException("Test data directory is not disposable.");
            await body(new Fixture(dir, store, default!));
            Console.WriteLine("  ok: " + name);
        }
        catch
        {
            failed = true;
            Console.Error.WriteLine("  FAILED: " + name);
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", original);
            SqliteConnection.ClearAllPools();
            try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
            catch (IOException) when (failed) { Console.Error.WriteLine("  (cleanup skipped)"); }
            catch (UnauthorizedAccessException) when (failed) { Console.Error.WriteLine("  (cleanup skipped)"); }
        }
    }

    private static SqliteConnection Raw(WorkspaceStore store)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = store.DatabasePath, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static long Scalar(WorkspaceStore store, string sql, params (string, object)[] args)
    {
        using var db = Raw(store);
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Exec(WorkspaceStore store, string sql, params (string, object)[] args)
    {
        using var db = Raw(store);
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    private static void ExpectSqliteAbort(WorkspaceStore store, string sql)
    {
        try { Exec(store, sql); }
        catch (SqliteException) { return; }
        throw new InvalidOperationException("Expected SQLite to abort: " + sql);
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectFence(Action action, string what)
    {
        try { action(); }
        catch (ExecutionFenceException) { return; }
        throw new InvalidOperationException("Stale owner write accepted: " + what);
    }

    /// <summary>Trusted metadata only; inserts a fixture snapshot row.</summary>
    private static GateCScope SeedSnapshot(WorkspaceStore store)
    {
        var workspace = store.Create("C6b test", Guid.NewGuid()).Workspace;
        var repositoryId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var scope = new GateCScope(workspace.Id, repositoryId, snapshotId,
            new string('a', 64), "http-candidate:1", SnapshotEvidenceTools.Version);
        Exec(store, """
            INSERT INTO repository_sources
                (id,workspace_id,local_path,path_key,source_kind,added_at_unix_ms,
                 version,idempotency_key,request_digest)
            VALUES ($repo,$ws,'/test','/test','folder',1,1,$rk,'fixture');
            INSERT INTO repository_snapshots
                (id,workspace_id,repository_id,fingerprint_sha256,manifest_sha256,
                 captured_at_unix_ms,file_count,total_bytes,exclusion_count,policy_version)
            VALUES ($snap,$ws,$repo,$fp,$fp,1,0,0,0,'test');
            """, ("$repo", repositoryId.ToString("D")), ("$ws", workspace.Id.ToString("D")),
            ("$snap", snapshotId.ToString("D")), ("$rk", Guid.NewGuid().ToString("D")),
            ("$fp", scope.FingerprintSha256));
        return scope;
    }

    private static string ScopeArg(GateCScope s) =>
        $"{s.WorkspaceId:D}/{s.RepositoryId:D}/{s.SnapshotId:D}";

    /// <summary>A host-valid completed outcome built without any model.</summary>
    internal static GateC4RunOutcome CompletedOutcome(Guid run, GateCScope scope)
    {
        var investigator = new GateC3RoleOutcome(GateCRole.Investigator,
            GateCRoleStatus.CompletedValidated, "valid", new(
                GateCContract.Version, run, scope, GateCRole.Investigator,
                ["E1"], [new GateCHypothesis("H1", "Possible call", ["E1"], "Not runtime proof")],
                [], ["Binding unknown"]),
            "inspect_entry_point", 1,
            [new GateC5EvidenceLink(GateCRole.Investigator, "E1", "canonical-A",
                GateCEvidenceKind.Candidate, "http-endpoint", "candidate", "A.cs",
                new string('b', 64), 4, 5)], "test-analyzer-v1");
        var skeptic = new GateC3RoleOutcome(GateCRole.Skeptic,
            GateCRoleStatus.CompletedValidated, "valid", new(
                GateCContract.Version, run, scope, GateCRole.Skeptic,
                ["E1"], [], [new GateCConcern("S1", GateCConcernKind.InsufficientEvidence,
                    "Runtime not proven", ["E1"])], ["Runtime unknown"]),
            "inspect_candidate_callees", 1,
            [new GateC5EvidenceLink(GateCRole.Skeptic, "E1", "canonical-B",
                GateCEvidenceKind.ProvisionalLocal, "callee", "provisional-local", "B.cs",
                new string('c', 64), 8, 2)], "test-analyzer-v1");
        var review = new GateCReviewerAssessment(GateCContract.Version, run, scope,
            GateCRole.Reviewer, [new GateCReviewDecision("H1",
                GateCRecommendation.ContestedOrInsufficient, ["S1"], "Insufficient evidence")], []);
        var reconciled = GateCContractValidator.ValidateReview(review, run, scope,
            investigator.Assessment!, skeptic.Assessment!);
        var progress = GateCRunProgress.New(run);
        foreach (var evt in new[] { GateCRunEvent.EvidenceVerified,
            GateCRunEvent.BeginIndependentAssessments, GateCRunEvent.StartInvestigator,
            GateCRunEvent.AcceptInvestigator, GateCRunEvent.StartSkeptic,
            GateCRunEvent.AcceptSkeptic, GateCRunEvent.BeginReviewer,
            GateCRunEvent.AcceptReviewer, GateCRunEvent.Complete })
            progress = GateCStateTransitions.Apply(progress, evt).Progress;
        return new(new GateC3RunOutcome(run, scope, progress, investigator, skeptic),
            progress, new GateC4ReviewerOutcome(GateCRoleStatus.CompletedValidated,
                "valid", review, reconciled));
    }

    private sealed class FixtureExecutor(Func<SnapshotRecord, GateCScope, Guid,
        CancellationToken, Task<GateC4RunOutcome>> body) : IInvestigationExecutor
    {
        public int Calls;
        public Task<GateC4RunOutcome> ExecuteAsync(SnapshotRecord snapshot, GateCScope scope,
            Guid runId, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return body(snapshot, scope, runId, ct);
        }
    }

    private sealed record Host(ExecutionOwnershipState Ownership, InvestigationWorker Worker,
        InvestigationAdmissionService Admission, FixtureExecutor Executor) : IDisposable
    {
        public void Dispose() => Ownership.Dispose();
    }

    private static Host StartHost(WorkspaceStore store, FixtureExecutor executor,
        InvestigationWorkerOptions? options = null, bool acquire = true)
    {
        store.Initialize();
        var ownership = new ExecutionOwnershipState();
        if (ownership.TryAcquire(store) != acquire)
            throw new InvalidOperationException("Unexpected ownership outcome: " + ownership.Diagnostic);
        var signal = new InvestigationWorkSignal();
        var worker = new InvestigationWorker(store, ownership, signal, executor,
            options ?? new InvestigationWorkerOptions(TimeSpan.FromMinutes(6),
                TimeSpan.FromMilliseconds(100)),
            NullLogger<InvestigationWorker>.Instance);
        return new(ownership, worker, new InvestigationAdmissionService(store, ownership, signal), executor);
    }

    private static async Task Until(Func<bool> condition, string what, int timeoutMs = 10000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
                throw new InvalidOperationException("Timed out waiting for: " + what);
            await Task.Delay(25);
        }
    }

    private static long SlotHeld(WorkspaceStore store) => Scalar(store,
        "SELECT COUNT(*) FROM investigation_execution_slot WHERE active_attempt_id IS NOT NULL;");

    // -------------------------------------------------------------- migration

    private static void MigrationPreservesHistory(Fixture f)
    {
        var store = f.Store;
        store.InitializeTo(5);
        Expect(Scalar(store, "PRAGMA user_version;") == 5, "Fixture is not v5.");
        var scope = SeedSnapshot(store);
        var completedRun = Guid.NewGuid(); var legacyRunning = Guid.NewGuid(); var preC6 = Guid.NewGuid();
        var completedAttempt = Guid.NewGuid(); var runningAttempt = Guid.NewGuid();
        const string reportJson = "{\"legacy\":true}";
        foreach (var (run, status, json) in new[] {
            (completedRun, "completed", (object)reportJson), (legacyRunning, "running", DBNull.Value),
            (preC6, "running", DBNull.Value) })
            Exec(store, """
                INSERT INTO investigation_reports (run_id,workspace_id,repository_id,snapshot_id,
                    fingerprint_sha256,entry_point_id,evidence_contract_version,
                    report_contract_version,started_at_unix_ms,status,diagnostic,report_json,
                    idempotency_key,request_digest)
                VALUES ($run,$ws,$repo,$snap,$fp,'e',$ev,'gate-c5-investigation-report-v1',1,
                    $status,'d',$json,$key,'digest');
                """, ("$run", run.ToString("D")), ("$ws", scope.WorkspaceId.ToString("D")),
                ("$repo", scope.RepositoryId.ToString("D")), ("$snap", scope.SnapshotId.ToString("D")),
                ("$fp", scope.FingerprintSha256), ("$ev", scope.EvidenceContractVersion),
                ("$status", status), ("$json", json), ("$key", Guid.NewGuid().ToString("D")));
        foreach (var (attempt, run, status) in new[] {
            (completedAttempt, completedRun, "completed"), (runningAttempt, legacyRunning, "running") })
            Exec(store, """
                INSERT INTO investigation_attempts (attempt_id,run_id,attempt_number,
                    contract_version,status,diagnostic,created_at_unix_ms,last_event_sequence)
                VALUES ($a,$run,1,'gate-c6-execution-attempt-v1',$status,'d',1,1);
                INSERT INTO investigation_events (event_id,attempt_id,sequence_number,
                    event_contract_version,occurred_at_unix_ms,event_type,diagnostic,payload_json)
                VALUES ($e,$a,1,'gate-c6-investigation-event-v1',1,'attempt-admitted','queued','{}');
                """, ("$a", attempt.ToString("D")), ("$run", run.ToString("D")),
                ("$status", status), ("$e", Guid.NewGuid().ToString("D")));
        Exec(store, "UPDATE investigation_execution_slot SET active_attempt_id=$a;",
            ("$a", runningAttempt.ToString("D")));

        store.Initialize();
        Expect(Scalar(store, "PRAGMA user_version;") == 6, "Not migrated to v6.");
        Expect(Scalar(store, "SELECT COUNT(*) FROM investigation_reports;") == 3, "Reports lost.");
        Expect(Scalar(store, "SELECT COUNT(*) FROM investigation_events;") == 2, "Events lost.");
        Expect(Scalar(store, "SELECT COUNT(*) FROM investigation_events WHERE event_contract_version='gate-c6-investigation-event-v1' AND ownership_epoch IS NULL;") == 2,
            "Historical events were rewritten.");
        // Migration (and a re-run of Initialize) must not recover active work.
        store.Initialize();
        Expect(Scalar(store, "SELECT COUNT(*) FROM investigation_reports WHERE status='running';") == 2,
            "Initialize() recovered active work without ownership.");
        Expect(store.GetAttempt(legacyRunning)?.Status == "running", "Legacy attempt touched by migration.");
        Expect(SlotHeld(store) == 1, "Slot released by migration.");
        ExpectSqliteAbort(store, "UPDATE investigation_events SET diagnostic='x';");
        ExpectSqliteAbort(store, "DELETE FROM investigation_events;");
        Expect(Scalar(store, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('investigation_checkpoints_no_update','investigation_checkpoints_no_delete','attempt_checkpoint_lineage_no_update','execution_owner_epoch_monotonic');") == 4,
            "Immutability triggers missing.");
        Expect(store.GetExecutionOwner() is { Epoch: 0, Status: "unowned" }, "Owner row not initialized.");

        using var held = ExecutionProcessLock.TryAcquire(store.DataDirectory, out _)
            ?? throw new InvalidOperationException("Lock unavailable.");
        var fence = store.EstablishOwnership(held);
        var result = store.ReconcileAbandoned(fence);
        Expect(result is { LegacyAttemptsFinalized: 1, LegacyReportsFinalized: 1, InterruptedAttempts: 0 },
            "Unexpected reconciliation counts.");
        var legacy = store.GetAttempt(legacyRunning)!;
        Expect(legacy.Status == "interrupted" && legacy.TerminalCause == "legacy-c6a-restart",
            "Legacy C6a attempt not finalized with legacy semantics.");
        Expect(Scalar(store, "SELECT COUNT(*) FROM investigation_reports WHERE status='incomplete';") == 2,
            "Legacy reports not finalized as non-resumable incomplete.");
        Expect(Scalar(store, "SELECT COUNT(*) FROM investigation_reports WHERE run_id=$r AND status='completed' AND report_json=$j;",
            ("$r", completedRun.ToString("D")), ("$j", reportJson)) == 1, "Completed C5 report changed.");
        Expect(SlotHeld(store) == 0, "Slot not released by owner reconciliation.");
    }

    private static void MigrationRefusesV5Checkpoints(Fixture f)
    {
        f.Store.InitializeTo(5);
        Exec(f.Store, """
            PRAGMA foreign_keys = OFF;
            INSERT INTO investigation_checkpoints VALUES ('c','a','investigator','v','v','{}','{}',
                '{}','{}','[]','{}',$sha,1);
            """, ("$sha", new string('0', 64)));
        try
        {
            f.Store.Initialize();
            throw new InvalidOperationException("Migration accepted unknown-provenance checkpoint.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("unknown provenance")) { }
        Expect(Scalar(f.Store, "PRAGMA user_version;") == 5, "Failed migration was not rolled back.");
        Expect(Scalar(f.Store, "SELECT COUNT(*) FROM investigation_checkpoints;") == 1, "Checkpoint discarded.");
    }

    // -------------------------------------------------------------- ownership

    private static void InProcessLockExclusion(Fixture f)
    {
        var first = ExecutionProcessLock.TryAcquire(f.Store.DataDirectory, out var d1)
            ?? throw new InvalidOperationException("First lock failed: " + d1);
        var second = ExecutionProcessLock.TryAcquire(f.Store.DataDirectory, out var d2);
        Expect(second is null && d2 == "execution-lock-held-by-another-process",
            "Second exclusive lock was granted: " + d2);
        first.WriteDiagnostic(Guid.NewGuid(), 1);
        first.Dispose();
        Expect(File.Exists(Path.Combine(f.Store.DataDirectory, ExecutionProcessLock.FileName)),
            "Lock file should persist; its existence is not authority.");
        using var third = ExecutionProcessLock.TryAcquire(f.Store.DataDirectory, out var d3)
            ?? throw new InvalidOperationException("Lock not reacquirable after release: " + d3);
    }

    private static void EpochTriggers(Fixture f)
    {
        var store = f.Store;
        store.Initialize();
        using var held = ExecutionProcessLock.TryAcquire(store.DataDirectory, out _)!;
        var a = store.EstablishOwnership(held);
        var b = store.EstablishOwnership(held);
        Expect(a.Epoch == 1 && b.Epoch == 2 && a.OwnerId != b.OwnerId, "Epochs not monotonic.");
        ExpectFence(() => store.Heartbeat(a), "heartbeat by superseded epoch");
        store.Heartbeat(b);
        ExpectSqliteAbort(store, "UPDATE execution_owner SET ownership_epoch=ownership_epoch-1;");
        ExpectSqliteAbort(store, "UPDATE execution_owner SET ownership_epoch=ownership_epoch+2;");
        ExpectSqliteAbort(store, $"UPDATE execution_owner SET owner_id='{Guid.NewGuid():D}';");
        ExpectSqliteAbort(store, "DELETE FROM execution_owner;");
        ExpectSqliteAbort(store, "UPDATE execution_ownership_history SET process_id=0;");
        Expect(Scalar(store, "SELECT COUNT(*) FROM execution_ownership_history;") == 2, "History incomplete.");
    }

    private static void CrossProcessOwnership(Fixture f)
    {
        var store = f.Store;
        store.Initialize();
        var scope = SeedSnapshot(store);
        using var child = StartLockHolder(f.Directory, scope);
        var ready = child.StandardOutput.ReadLine() ?? "";
        if (!ready.StartsWith("READY ", StringComparison.Ordinal))
            throw new InvalidOperationException("Lock holder failed: " + ready + child.StandardError.ReadToEnd());
        var parts = ready.Split(' ');
        var childEpoch = long.Parse(parts[1]);
        var childRun = Guid.Parse(parts[2]);
        var childOwner = store.GetExecutionOwner().OwnerId!.Value;
        var childFence = new ExecutionFence(childOwner, childEpoch);

        // A second application process starts while the first executes.
        store.Initialize();
        var second = new ExecutionOwnershipState();
        Expect(!second.TryAcquire(store), "Second process acquired a held execution lock.");
        Expect(second.Fence is null && second.Reconciliation is null, "Non-owner reconciled work.");
        Expect(store.GetAttempt(childRun)?.Status == "running", "Live attempt was interrupted by a second process.");
        Expect(SlotHeld(store) == 1, "Live slot released by a second process.");

        // Heartbeat expiry is diagnostic only: an ancient heartbeat steals nothing.
        Exec(store, "UPDATE execution_owner SET heartbeat_at_unix_ms=0;");
        Expect(ExecutionProcessLock.TryAcquire(store.DataDirectory, out _) is null,
            "Expired heartbeat allowed lock acquisition.");
        Expect(store.GetExecutionOwner().Epoch == childEpoch, "Epoch changed without the lock.");

        // Unexpected crash of the owning process: the OS releases the lock.
        child.Kill(entireProcessTree: true);
        child.WaitForExit();
        var third = new ExecutionOwnershipState();
        Expect(third.TryAcquire(store), "Lock not acquirable after owner crash: " + third.Diagnostic);
        var fence = third.Fence!;
        Expect(fence.Epoch == childEpoch + 1, "Takeover epoch not monotonic.");
        Expect(third.Reconciliation?.InterruptedAttempts == 1, "Abandoned attempt not reconciled.");
        var attempt = store.GetAttempt(childRun)!;
        Expect(attempt is { Status: "interrupted", TerminalCause: "process-interrupted" },
            "Crashed attempt not recorded as interrupted.");
        Expect(store.GetInvestigation(scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId,
            childRun)?.Summary.Status == "interrupted", "Run not left in recoverable interrupted state.");
        var events = store.ListEvents(scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId, childRun);
        Expect(events.Select(e => e.EventType).SequenceEqual(
                ["attempt-admitted", "attempt-started", "attempt-interrupted"]) &&
            events[0].OwnershipEpoch == childEpoch && events[2].OwnershipEpoch == fence.Epoch,
            "Event chronology across owners is wrong.");
        Expect(SlotHeld(store) == 0, "Slot not released after reconciliation.");

        // Every stale-owner mutation is rejected inside its transaction.
        var eventCount = events.Count;
        ExpectFence(() => store.Heartbeat(childFence), "heartbeat");
        ExpectFence(() => store.ClaimNextQueuedAttempt(childFence, TimeSpan.FromMinutes(1)), "claim");
        ExpectFence(() => store.CreateInvestigation(scope, Guid.NewGuid(), childFence), "admission");
        ExpectFence(() => store.AppendStageEvent(childFence, attempt.AttemptId, "role-started",
            "investigator", "stale"), "stage event");
        ExpectFence(() => store.InterruptAttempt(childFence, attempt.AttemptId, childRun,
            "application-shutdown"), "interrupt");
        ExpectFence(() => store.FailInvestigation(childFence, attempt.AttemptId, childRun,
            "host-execution-failure", "host-failure"), "failure");
        ExpectFence(() => store.FinishInvestigation(childFence, attempt.AttemptId, childRun,
            GateC5ReportFactory.Create(CompletedOutcome(childRun, scope), DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow), "completed"), "report completion");
        ExpectFence(() => store.TryReleaseOwnership(childFence), "release");
        Expect(store.ListEvents(scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId, childRun).Count == eventCount,
            "A stale write appended events.");

        // A current owner also cannot write to an attempt it does not own.
        var admitted = store.CreateInvestigation(scope, Guid.NewGuid(), fence);
        var claim = store.ClaimNextQueuedAttempt(fence, TimeSpan.FromMinutes(1))!;
        ExpectFence(() => store.AppendStageEvent(childFence, claim.Attempt.AttemptId,
            "role-started", "investigator", "stale"), "foreign attempt event");
        Expect(claim.Run.RunId == admitted.Record!.RunId, "Wrong attempt claimed.");
        third.Dispose();
    }

    private static Process StartLockHolder(string dataDirectory, GateCScope scope)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var info = new ProcessStartInfo(self)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, UseShellExecute = false
        };
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(GateC6bSelfTests).Assembly.Location);
        foreach (var arg in new[] { "--c6b-lock-holder", dataDirectory, ScopeArg(scope), "hold-running" })
            info.ArgumentList.Add(arg);
        info.Environment["CODE_ARCHEOLOGIST_DATA_DIR"] = dataDirectory;
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start lock holder.");
    }

    /// <summary>
    /// Child process: acquire the OS lock, establish an epoch, admit and
    /// start one attempt, then hold everything until killed.
    /// </summary>
    public static int RunLockHolder(string dataDirectory, string scopeArg, string mode)
    {
        if (mode != "hold-running") return 2;
        Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", dataDirectory);
        var store = new WorkspaceStore();
        store.Initialize();
        var ownership = new ExecutionOwnershipState();
        if (!ownership.TryAcquire(store)) { Console.WriteLine("NOLOCK " + ownership.Diagnostic); return 3; }
        var ids = scopeArg.Split('/').Select(Guid.Parse).ToArray();
        var snapshot = store.GetSnapshot(ids[0], ids[1], ids[2])
            ?? throw new InvalidOperationException("Fixture snapshot missing.");
        var scope = new GateCScope(ids[0], ids[1], ids[2], snapshot.FingerprintSha256,
            "http-candidate:1", SnapshotEvidenceTools.Version);
        var fence = ownership.Fence!;
        var created = store.CreateInvestigation(scope, Guid.NewGuid(), fence);
        store.ClaimNextQueuedAttempt(fence, TimeSpan.FromMinutes(6));
        Console.WriteLine($"READY {fence.Epoch} {created.Record!.RunId:D}");
        Console.Out.Flush();
        Console.In.ReadLine();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    // ----------------------------------------------------------------- worker

    private static async Task WorkerNoStartupInvocation(Fixture f)
    {
        var store = f.Store;
        store.Initialize();
        var scope = SeedSnapshot(store);
        Guid run;
        using (var held = ExecutionProcessLock.TryAcquire(store.DataDirectory, out _)!)
        {
            var old = store.EstablishOwnership(held);
            run = store.CreateInvestigation(scope, Guid.NewGuid(), old).Record!.RunId;
        } // previous process died with work queued
        var executor = new FixtureExecutor((_, s, r, _) => Task.FromResult(CompletedOutcome(r, s)));
        using var host = StartHost(store, executor);
        await host.Worker.StartAsync(CancellationToken.None);
        await Task.Delay(600);
        Expect(executor.Calls == 0 && host.Worker.Executions == 0, "Startup invoked a model.");
        Expect(store.GetAttempt(run) is { Status: "interrupted", TerminalCause: "process-interrupted" },
            "Abandoned queued attempt not reconciled to interrupted.");
        Expect(store.GetInvestigation(scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId, run)
            ?.Summary.Status == "interrupted", "Run state wrong.");
        await host.Worker.StopAsync(CancellationToken.None);
        Expect(executor.Calls == 0, "Shutdown invoked a model.");
    }

    private static async Task WorkerCompletes(Fixture f)
    {
        var store = f.Store;
        store.Initialize();
        var scope = SeedSnapshot(store);
        var executor = new FixtureExecutor((_, s, r, _) => Task.FromResult(CompletedOutcome(r, s)));
        using var host = StartHost(store, executor);
        await host.Worker.StartAsync(CancellationToken.None);
        var key = Guid.NewGuid();
        var admitted = host.Admission.Admit(scope, key);
        Expect(admitted.Outcome == InvestigationAdmissionOutcome.Admitted, "Not admitted.");
        var run = admitted.Record!.RunId;
        await Until(() => store.GetAttempt(run)?.Status == "completed", "completion");
        Expect(host.Admission.Admit(scope, key).Outcome == InvestigationAdmissionOutcome.Replayed,
            "Idempotent replay not returned.");
        await Task.Delay(300);
        Expect(executor.Calls == 1, "Admitted work executed more than once.");
        var detail = store.GetInvestigation(scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId, run);
        Expect(detail?.Summary.Status == "completed" && detail.Report is not null, "Report not persisted.");
        Expect(store.GetAttempt(run)?.TerminalCause == "completed", "Terminal cause missing.");
        var events = store.ListEvents(scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId, run);
        Expect(events.Select(e => e.EventType).SequenceEqual(
            ["attempt-admitted", "attempt-started", "attempt-completed"]), "Event chronology wrong.");
        Expect(SlotHeld(store) == 0, "Slot not released after completion.");
        Expect(store.GetExecutionOwner().HeartbeatAtUtc is not null, "No heartbeat recorded.");
        await host.Worker.StopAsync(CancellationToken.None);
        Expect(store.GetExecutionOwner().Status == "released", "Idle owner not released on shutdown.");
    }

    private static async Task NonOwnerRefuses(Fixture f)
    {
        var store = f.Store;
        store.Initialize();
        var scope = SeedSnapshot(store);
        using var other = ExecutionProcessLock.TryAcquire(store.DataDirectory, out _)!;
        var executor = new FixtureExecutor((_, s, r, _) => Task.FromResult(CompletedOutcome(r, s)));
        using var host = StartHost(store, executor, acquire: false);
        Expect(host.Ownership.Diagnostic == "execution-lock-held-by-another-process",
            "Unexpected non-owner diagnostic.");
        Expect(host.Admission.Admit(scope, Guid.NewGuid()).Outcome ==
            InvestigationAdmissionOutcome.ExecutionUnavailable, "Non-owner admitted work.");
        await host.Worker.StartAsync(CancellationToken.None);
        await Until(() => host.Worker.State == InvestigationWorkerState.NotOwner, "non-owner state");
        Expect(executor.Calls == 0 && store.GetExecutionOwner().Epoch == 0,
            "Non-owner executed or established an epoch.");
        Expect(Scalar(store, "SELECT COUNT(*) FROM investigation_reports;") == 0, "Ghost report.");
        await host.Worker.StopAsync(CancellationToken.None);
    }

    private static async Task WorkerShutdownUncooperative(Fixture f)
    {
        var store = f.Store;
        store.Initialize();
        var scope = SeedSnapshot(store);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new FixtureExecutor(async (_, s, r, _) =>
        {
            entered.TrySetResult();
            await release.Task; // deliberately ignores cancellation, like a stuck provider call
            return CompletedOutcome(r, s);
        });
        using var host = StartHost(store, executor);
        await host.Worker.StartAsync(CancellationToken.None);
        var run = host.Admission.Admit(scope, Guid.NewGuid()).Record!.RunId;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var shutdownGrace = new CancellationTokenSource(TimeSpan.FromMilliseconds(400)))
            await host.Worker.StopAsync(shutdownGrace.Token);
        Expect(store.GetAttempt(run)?.Status == "running", "Attempt finalized while model call active.");
        Expect(SlotHeld(store) == 1, "Slot released while model call active.");
        Expect(host.Admission.Admit(scope, Guid.NewGuid()).Outcome == InvestigationAdmissionOutcome.Busy,
            "New work admitted while model call active.");
        release.SetResult();
        await Until(() => store.GetAttempt(run)?.Status == "interrupted", "shutdown terminalization");
        Expect(store.GetAttempt(run)?.TerminalCause == "application-shutdown", "Shutdown cause wrong.");
        Expect(store.GetInvestigation(scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId, run)
            ?.Summary.Status == "interrupted", "Shutdown must not complete or cancel the run.");
        Expect(SlotHeld(store) == 0, "Slot not released after the call returned.");
    }

    private static async Task WorkerDeadline(Fixture f)
    {
        var store = f.Store;
        store.Initialize();
        var scope = SeedSnapshot(store);
        var executor = new FixtureExecutor(async (_, s, r, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return CompletedOutcome(r, s);
        });
        using var host = StartHost(store, executor,
            new InvestigationWorkerOptions(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100)));
        await host.Worker.StartAsync(CancellationToken.None);
        var run = host.Admission.Admit(scope, Guid.NewGuid()).Record!.RunId;
        await Until(() => store.GetAttempt(run)?.Status == "interrupted", "deadline terminalization");
        Expect(store.GetAttempt(run)?.TerminalCause == "attempt-deadline", "Deadline cause wrong.");
        await host.Worker.StopAsync(CancellationToken.None);
    }

    private static async Task WorkerTerminalWriteFailure(Fixture f)
    {
        var store = f.Store;
        store.Initialize();
        var scope = SeedSnapshot(store);
        Exec(store, """
            CREATE TRIGGER test_fail_completion BEFORE INSERT ON investigation_events
            WHEN NEW.event_type='attempt-completed'
            BEGIN SELECT RAISE(ABORT, 'fault injected'); END;
            """);
        var executor = new FixtureExecutor((_, s, r, _) => Task.FromResult(CompletedOutcome(r, s)));
        Guid run;
        using (var host = StartHost(store, executor))
        {
            await host.Worker.StartAsync(CancellationToken.None);
            run = host.Admission.Admit(scope, Guid.NewGuid()).Record!.RunId;
            await Until(() => host.Worker.State == InvestigationWorkerState.Halted, "worker halt");
            Expect(store.GetAttempt(run)?.Status == "running", "Failed terminal write changed attempt.");
            Expect(store.GetInvestigation(scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId, run)
                ?.Summary.Status == "running", "Failed terminal write changed report.");
            Expect(Scalar(store, "SELECT COUNT(*) FROM investigation_events WHERE event_type IN ('attempt-failed','attempt-rejected','attempt-incomplete');") == 0,
                "A second, different finalization was attempted.");
            Expect(SlotHeld(store) == 1, "Slot released after failed terminal write.");
            Expect(host.Admission.Admit(scope, Guid.NewGuid()).Outcome == InvestigationAdmissionOutcome.Busy,
                "Admission succeeded while slot is unsafely held.");
            Expect(executor.Calls == 1, "Executor re-invoked.");
            await host.Worker.StopAsync(CancellationToken.None);
            Expect(store.GetExecutionOwner().Status == "active", "Halted owner released while attempt active.");
        }
        Exec(store, "DROP TRIGGER test_fail_completion;");
        // Explicit restart reconciles the stuck attempt; it is NOT reported completed.
        var restart = new ExecutionOwnershipState();
        Expect(restart.TryAcquire(store), "Restart could not acquire ownership.");
        Expect(store.GetAttempt(run) is { Status: "interrupted", TerminalCause: "process-interrupted" },
            "Stuck attempt not reconciled after restart.");
        Expect(store.GetInvestigation(scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId, run)
            ?.Report is null, "An incorrect completed report appeared after worker failure.");
        restart.Dispose();
    }

    private static void ClassificationIsHostObserved()
    {
        var run = Guid.NewGuid();
        var scope = new GateCScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new string('a', 64), "e", SnapshotEvidenceTools.Version);
        var summary = new InvestigationSummary(run, scope, GateC5ReportContract.Version,
            "running", "queued", DateTimeOffset.UtcNow, null);
        var outcome = CompletedOutcome(run, scope);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Expect(InvestigationWorker.Classify(outcome, null, summary, stopped.Token, false, false).Cause
            == "application-shutdown", "Shutdown misclassified.");
        Expect(InvestigationWorker.Classify(outcome, null, summary, CancellationToken.None, false, true).Cause
            == "attempt-deadline", "Deadline misclassified.");
        Expect(InvestigationWorker.Classify(outcome, null, summary, CancellationToken.None, true, false).Kind
            == InvestigationWorker.TerminalKind.None, "Ownership loss must not write.");
        Expect(InvestigationWorker.Classify(null, new Exception(), summary, CancellationToken.None, false, false).Cause
            == "host-failure", "Fault misclassified.");
        Expect(InvestigationWorker.Classify(outcome, null, summary, CancellationToken.None, false, false).Cause
            == "completed", "Completion misclassified.");
        var timedOut = outcome with
        {
            Progress = outcome.Progress with { Status = GateCRunStatus.Incomplete,
                ReviewerStatus = GateCRoleStatus.TimedOut },
            Reviewer = new GateC4ReviewerOutcome(GateCRoleStatus.TimedOut, "t", null, null)
        };
        Expect(InvestigationWorker.Classify(timedOut, null, summary, CancellationToken.None, false, false).Cause
            == "role-timeout", "Role timeout misclassified.");
        Console.WriteLine("  ok: terminal causes are host-observed and distinct");
    }
}

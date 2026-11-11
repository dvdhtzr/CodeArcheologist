using CodeArcheologist.Api.Execution;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;
using Microsoft.Data.Sqlite;

internal static class GateC5SelfTests
{
    public static void Run()
    {
        var original = Environment.GetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR");
        var dir = Path.Combine(Path.GetTempPath(), "CodeArcheologist-C5-" +
            Guid.NewGuid().ToString("N"));
        var testFailed = false;
        try
        {
            Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", dir);
            var store = new WorkspaceStore();
            store.Initialize();
            var processLock = ExecutionProcessLock.TryAcquire(store.DataDirectory, out _)
                ?? throw new InvalidOperationException("Test could not acquire execution lock.");
            var fence = store.EstablishOwnership(processLock);
            store.ReconcileAbandoned(fence);
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA user_version;";
                if (Convert.ToInt32(cmd.ExecuteScalar()) != 6)
                    throw new InvalidOperationException("C6 schema was not migrated.");
            }

            // Execution state and workflow state are intentionally independent.
            if (!GateC6AttemptTransitions.Allowed("queued", "running") ||
                !GateC6AttemptTransitions.Allowed("running", "interrupted") ||
                GateC6AttemptTransitions.Allowed("completed", "running") ||
                GateC6AttemptTransitions.Allowed("queued", "completed") ||
                GateC6AttemptTransitions.Allowed("running", "queued"))
                throw new InvalidOperationException("C6 illegal execution transition accepted.");

            var workspace = store.Create("C5 test", Guid.NewGuid()).Workspace;
            var repositoryId = Guid.NewGuid();
            var snapshotId = Guid.NewGuid();
            var scope = new GateCScope(workspace.Id, repositoryId, snapshotId,
                new string('a', 64), "http-candidate:1",
                SnapshotEvidenceTools.Version);
            // Test fixture inserts only trusted metadata into the disposable database.
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO repository_sources
                        (id,workspace_id,local_path,path_key,source_kind,added_at_unix_ms,
                         version,idempotency_key,request_digest)
                    VALUES ($repo,$ws,'/test','/test','folder',1,1,$rk,'fixture');
                    INSERT INTO repository_snapshots
                        (id,workspace_id,repository_id,fingerprint_sha256,manifest_sha256,
                         captured_at_unix_ms,file_count,total_bytes,exclusion_count,policy_version)
                    VALUES ($snap,$ws,$repo,$fp,$fp,1,0,0,0,'test');
                    """;
                cmd.Parameters.AddWithValue("$repo", repositoryId.ToString("D"));
                cmd.Parameters.AddWithValue("$ws", workspace.Id.ToString("D"));
                cmd.Parameters.AddWithValue("$snap", snapshotId.ToString("D"));
                cmd.Parameters.AddWithValue("$rk", Guid.NewGuid().ToString("D"));
                cmd.Parameters.AddWithValue("$fp", scope.FingerprintSha256);
                cmd.ExecuteNonQuery();
            }

            var key = Guid.NewGuid();
            var create = store.CreateInvestigation(scope, key, fence);
            if (!create.Created || create.Conflict || create.Record is null)
                throw new InvalidOperationException("C5 new request was not persisted.");
            var admitted = store.GetAttempt(create.Record.RunId);
            if (admitted?.Status != "queued" || admitted.LastEventSequence != 1)
                throw new InvalidOperationException("C6 attempt admission was not atomic.");
            var admissionEvents = store.ListEvents(workspace.Id, repositoryId, snapshotId,
                create.Record.RunId);
            if (admissionEvents.Count != 1 ||
                admissionEvents[0].EventType != "attempt-admitted" ||
                admissionEvents[0].SequenceNumber != 1)
                throw new InvalidOperationException("C6 admission event is invalid.");
            if (!store.CreateInvestigation(scope, Guid.NewGuid(), fence).Busy)
                throw new InvalidOperationException("C6 busy admission created a record.");
            if (store.ListInvestigations(workspace.Id, repositoryId, snapshotId).TotalCount != 1)
                throw new InvalidOperationException("Busy admission left a ghost report.");
            var replay = store.CreateInvestigation(scope, key, fence);
            if (replay.Created || replay.Conflict || replay.Record?.RunId != create.Record.RunId)
                throw new InvalidOperationException("C5 idempotent replay failed.");
            if (!store.CreateInvestigation(scope with { EntryPointId = "other" }, key, fence).Conflict)
                throw new InvalidOperationException("C5 conflicting idempotency was accepted.");
            if (store.GetInvestigation(workspace.Id, repositoryId,
                    Guid.NewGuid(), create.Record.RunId) is not null)
                throw new InvalidOperationException("C5 cross-snapshot lookup leaked report.");

            var effectiveRun = create.Record.RunId;
            var investigatorLink = new GateC5EvidenceLink(GateCRole.Investigator,
                "E1", "canonical-A", GateCEvidenceKind.Candidate,
                "http-endpoint", "candidate", "A.cs", new string('b',64), 4, 5);
            var skepticLink = new GateC5EvidenceLink(GateCRole.Skeptic,
                "E1", "canonical-B", GateCEvidenceKind.ProvisionalLocal,
                "callee", "provisional-local", "B.cs", new string('c',64), 8, 2);
            var investigator = new GateC3RoleOutcome(GateCRole.Investigator,
                GateCRoleStatus.CompletedValidated, "valid", new(
                    GateCContract.Version, effectiveRun, scope, GateCRole.Investigator,
                    ["E1"], [new GateCHypothesis("H1", "Possible call", ["E1"],
                        "Not runtime proof")], [], ["Binding unknown"]),
                "inspect_entry_point", 1, [investigatorLink], "test-analyzer-v1");
            var skeptic = new GateC3RoleOutcome(GateCRole.Skeptic,
                GateCRoleStatus.CompletedValidated, "valid", new(
                    GateCContract.Version, effectiveRun, scope, GateCRole.Skeptic,
                    ["E1"], [], [new GateCConcern("S1",
                        GateCConcernKind.InsufficientEvidence,
                        "Runtime not proven", ["E1"])], ["Runtime unknown"]),
                "inspect_candidate_callees", 1, [skepticLink], "test-analyzer-v1");

            var review = new GateCReviewerAssessment(GateCContract.Version,
                effectiveRun, scope, GateCRole.Reviewer,
                [new GateCReviewDecision("H1",
                    GateCRecommendation.ContestedOrInsufficient, ["S1"],
                    "Insufficient evidence")], []);
            var reconciled = GateCContractValidator.ValidateReview(
                review, effectiveRun, scope, investigator.Assessment!, skeptic.Assessment!);
            if (!reconciled.Check.Accepted)
                throw new InvalidOperationException("Test review is invalid.");

            var progress = GateCRunProgress.New(effectiveRun);
            foreach (var evt in new[] {
                GateCRunEvent.EvidenceVerified,
                GateCRunEvent.BeginIndependentAssessments,
                GateCRunEvent.StartInvestigator, GateCRunEvent.AcceptInvestigator,
                GateCRunEvent.StartSkeptic, GateCRunEvent.AcceptSkeptic,
                GateCRunEvent.BeginReviewer, GateCRunEvent.AcceptReviewer,
                GateCRunEvent.Complete })
            {
                var next = GateCStateTransitions.Apply(progress, evt);
                if (!next.Check.Accepted) throw new InvalidOperationException("Invalid test progress.");
                progress = next.Progress;
            }

            var outcome = new GateC4RunOutcome(
                new GateC3RunOutcome(effectiveRun, scope, progress,
                    investigator, skeptic), progress,
                new GateC4ReviewerOutcome(GateCRoleStatus.CompletedValidated,
                    "valid", review, reconciled));
            var report = GateC5ReportFactory.Create(outcome,
                create.Record.StartedAtUtc, DateTimeOffset.UtcNow);
            if (report.HostCautionStatus != "not-evaluated" ||
                report.HostCautions is { Count: > 0 })
                throw new InvalidOperationException("Uninspected report presented as verified.");

            // The disposable contract fixture supplies trusted host provenance;
            // full snapshot/source anchoring is verified by the lab and API tests.
            var hostCaution = new HostSyntaxCaution(
                "host-caution:conditional-invocation:" + scope.EntryPointId,
                "conditional-invocation", CSharpHostCautionAnalyzer.Status,
                "A written direct call is inside a conditional branch; no runtime execution is proven.",
                CSharpHostCautionAnalyzer.Version,
                [new HostCautionAnchor("Main.cs", new string('b', 64), 1, 1, 0, 5)]);
            var hostPackage = new AgentEvidencePackage(
                SnapshotEvidenceTools.Version, scope.WorkspaceId, scope.RepositoryId,
                scope.SnapshotId, scope.FingerprintSha256, "graph-v8", scope.EntryPointId,
                "http-endpoint-attribute", "source-only", false, 0, 0, 0,
                [], [], [], [], [hostCaution], CSharpHostCautionAnalyzer.Version);

            var noWarningReport = GateC5ReportFactory.AttachVerifiedCautions(
                report, hostPackage with { HostCautions = [] });
            if (noWarningReport.HostCautionStatus != "verified" ||
                noWarningReport.HostCautions?.Count != 0)
                throw new InvalidOperationException("Verified zero cautions were not distinguished from uninspected.");
            ExpectRejected(() => GateC5ReportFactory.AttachVerifiedCautions(report,
                hostPackage with { SnapshotId = Guid.NewGuid() }));
            ExpectRejected(() => GateC5ReportFactory.AttachVerifiedCautions(report,
                hostPackage with { HostCautions = [hostCaution with { Status = "AI-claim" }] }));
            ExpectRejected(() => GateC5ReportFactory.AttachVerifiedCautions(report,
                hostPackage with { HostCautions = [hostCaution with { Anchors = [] }] }));
            report = GateC5ReportFactory.AttachVerifiedCautions(report, hostPackage);
            ExpectRejected(() => GateC5ReportFactory.AttachVerifiedCautions(report, hostPackage));

            // Host evidence survives an incomplete AI investigation without
            // turning the incomplete review into a verified agent outcome.
            var incompleteOutcome = outcome with
            {
                Progress = progress with { Status = GateCRunStatus.Incomplete }
            };
            var incompleteReport = GateC5ReportFactory.AttachVerifiedCautions(
                GateC5ReportFactory.Create(incompleteOutcome,
                    create.Record.StartedAtUtc, DateTimeOffset.UtcNow),
                hostPackage);
            if (incompleteReport.Progress.Status != GateCRunStatus.Incomplete ||
                incompleteReport.HostCautionStatus != "verified" ||
                incompleteReport.HostCautions?.Count != 1 ||
                incompleteReport.Diagnostic != "investigation-incomplete")
                throw new InvalidOperationException(
                    "Host warnings were suppressed or promoted by incomplete AI status.");

            if (report.EvidenceLinks.Count != 2 ||
                report.EvidenceLinks[0].CanonicalId == report.EvidenceLinks[1].CanonicalId)
                throw new InvalidOperationException("Role-local evidence identities were conflated.");
            var claimed = store.ClaimNextQueuedAttempt(fence, TimeSpan.FromMinutes(6))
                ?? throw new InvalidOperationException("Admitted attempt was not claimable.");
            if (claimed.Run.RunId != effectiveRun)
                throw new InvalidOperationException("Claimed a foreign attempt.");
            store.FinishInvestigation(fence, claimed.Attempt.AttemptId, effectiveRun,
                report, "completed");
            var completedAttempt = store.GetAttempt(effectiveRun);
            var executionEvents = store.ListEvents(workspace.Id, repositoryId,
                snapshotId, effectiveRun);
            if (completedAttempt?.Status != "completed" ||
                completedAttempt.LastEventSequence != 3 ||
                executionEvents.Count != 3 ||
                executionEvents[1].EventType != "attempt-started" ||
                executionEvents[2].EventType != "attempt-completed" ||
                executionEvents.Select(x => x.SequenceNumber)
                    .SequenceEqual(new long[] { 1, 2, 3 }) == false)
                throw new InvalidOperationException("C6 terminal event sequence is invalid.");
            using (var db = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
            {
                db.Open();
                using var bad = db.CreateCommand();
                bad.CommandText = "UPDATE investigation_events SET diagnostic='tampered' WHERE attempt_id=$id;";
                bad.Parameters.AddWithValue("$id", completedAttempt.AttemptId.ToString("D"));
                try
                {
                    bad.ExecuteNonQuery();
                    throw new InvalidOperationException("Event mutation succeeded.");
                }
                catch (SqliteException) { }
            }
            var loaded = store.GetInvestigation(workspace.Id, repositoryId,
                snapshotId, effectiveRun);
            if (loaded?.Summary.Status != "completed" ||
                loaded.Report?.EvidenceLinks.Count != 2 ||
                loaded.Report.Reviewer.Reconciliation?.Dispositions[0].IsVerifiedFact != false ||
                loaded.Report.HostCautionStatus != "verified" ||
                loaded.Report.HostCautionAnalyzerVersion != CSharpHostCautionAnalyzer.Version ||
                loaded.Report.HostCautions?.SingleOrDefault()?.Kind != "conditional-invocation" ||
                loaded.Report.HostCautions?.FirstOrDefault()?.Anchors.FirstOrDefault()?.FileSha256 != new string('b', 64))
                throw new InvalidOperationException("Durable C5 report could not round-trip.");
            if (store.ListInvestigations(workspace.Id, repositoryId, snapshotId).TotalCount != 1)
                throw new InvalidOperationException("C5 report listing failed.");
            try
            {
                store.FinishInvestigation(fence, claimed.Attempt.AttemptId, effectiveRun,
                    report, "completed");
                throw new InvalidOperationException("Double completion was accepted.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message == "Investigation completion scope or state mismatch.") { }

            var foreign = outcome with {
                Independent = outcome.Independent with {
                    Scope = scope with { SnapshotId = Guid.NewGuid() }
                }
            };
            ExpectRejected(() => GateC5ReportFactory.Create(foreign,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

            var invalidLink = outcome with {
                Independent = outcome.Independent with {
                    Investigator = investigator with { EvidenceLinks = [] }
                }
            };
            ExpectRejected(() => GateC5ReportFactory.Create(invalidLink,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

            // Fault injection: a rejected event insert must roll back the report,
            // attempt, and slot reservation together.
            using (var db = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
            {
                db.Open();
                using var trap = db.CreateCommand();
                trap.CommandText = """
                    CREATE TRIGGER test_reject_admission BEFORE INSERT ON investigation_events
                    WHEN NEW.event_type='attempt-admitted'
                    BEGIN SELECT RAISE(ABORT, 'fault injected'); END;
                    """;
                trap.ExecuteNonQuery();
            }
            var rejectedKey = Guid.NewGuid();
            try
            {
                store.CreateInvestigation(scope, rejectedKey, fence);
                throw new InvalidOperationException("Injected admission failure succeeded.");
            }
            catch (SqliteException) { }
            using (var db = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
            {
                db.Open();
                using var cleanup = db.CreateCommand();
                cleanup.CommandText = """
                    DROP TRIGGER test_reject_admission;
                    SELECT COUNT(*) FROM investigation_reports WHERE idempotency_key=$key;
                    """;
                cleanup.Parameters.AddWithValue("$key", rejectedKey.ToString("D"));
                if (Convert.ToInt32(cleanup.ExecuteScalar()) != 0)
                    throw new InvalidOperationException("Failed admission left a report.");
            }
            // Restart: Initialize() alone must NOT touch active work. Only a new
            // lock holder with a new epoch reconciles; completed reports survive.
            var interrupted = store.CreateInvestigation(scope, Guid.NewGuid(), fence);
            store.Initialize();
            if (store.GetAttempt(interrupted.Record!.RunId)?.Status != "queued")
                throw new InvalidOperationException("Initialize() recovered active work without ownership.");
            processLock.Dispose();
            using var restartLock = ExecutionProcessLock.TryAcquire(store.DataDirectory, out _)
                ?? throw new InvalidOperationException("Restart could not acquire execution lock.");
            var restartFence = store.EstablishOwnership(restartLock);
            if (restartFence.Epoch != fence.Epoch + 1)
                throw new InvalidOperationException("Ownership epoch is not monotonic.");
            store.ReconcileAbandoned(restartFence);
            var interruptedAttempt = store.GetAttempt(interrupted.Record.RunId);
            var interruptedEvents = store.ListEvents(workspace.Id, repositoryId,
                snapshotId, interrupted.Record.RunId);
            if (interruptedAttempt?.Status != "interrupted" ||
                interruptedAttempt.TerminalCause != "process-interrupted" ||
                interruptedEvents.Count != 2 ||
                interruptedEvents[1].EventType != "attempt-interrupted" ||
                interruptedEvents[1].OwnershipEpoch != restartFence.Epoch)
                throw new InvalidOperationException("C6 restart event was not recorded.");
            var restarted = store.GetInvestigation(workspace.Id, repositoryId,
                snapshotId, interrupted.Record!.RunId);
            if (restarted?.Summary.Status != "interrupted" ||
                restarted.Summary.Diagnostic != "host-interrupted-awaiting-explicit-action" ||
                restarted.Report is not null ||
                store.GetInvestigation(workspace.Id, repositoryId,
                    snapshotId, effectiveRun)?.Report is null)
                throw new InvalidOperationException("Restart recovery was inaccurate.");

            Console.WriteLine("GREEN: C5a SQLite migration, report provenance, scoped lookup, independent handles, persistence, and interrupted-run recovery.");
        }
        catch
        {
            testFailed = true;
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", original);
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (IOException) when (testFailed)
            {
                // Preserve the original test failure instead of masking it
                // with a Windows filesystem cleanup exception.
                Console.Error.WriteLine("C5 test cleanup could not remove its temporary database.");
            }
        }
    }

    private static void ExpectRejected(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid C5 report accepted.");
    }
}

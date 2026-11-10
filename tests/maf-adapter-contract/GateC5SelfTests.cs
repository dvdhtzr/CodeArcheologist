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
        try
        {
            Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", dir);
            var store = new WorkspaceStore();
            store.Initialize();
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = store.DatabasePath }.ToString()))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA user_version;";
                if (Convert.ToInt32(cmd.ExecuteScalar()) != 4)
                    throw new InvalidOperationException("C5 schema was not migrated.");
            }

            var workspace = store.Create("C5 test", Guid.NewGuid()).Workspace;
            var runId = Guid.NewGuid();
            var repositoryId = Guid.NewGuid();
            var snapshotId = Guid.NewGuid();
            var scope = new GateCScope(workspace.Id, repositoryId, snapshotId,
                new string('a', 64), "http-candidate:1",
                SnapshotEvidenceTools.Version);
            // Test fixture inserts only trusted metadata into the disposable database.
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = store.DatabasePath }.ToString()))
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
            var create = store.CreateInvestigation(scope, key);
            if (!create.Created || create.Conflict || create.Record is null)
                throw new InvalidOperationException("C5 new request was not persisted.");
            var replay = store.CreateInvestigation(scope, key);
            if (replay.Created || replay.Conflict || replay.Record?.RunId != create.Record.RunId)
                throw new InvalidOperationException("C5 idempotent replay failed.");
            if (!store.CreateInvestigation(scope with { EntryPointId = "other" }, key).Conflict)
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
            if (report.EvidenceLinks.Count != 2 ||
                report.EvidenceLinks[0].CanonicalId == report.EvidenceLinks[1].CanonicalId)
                throw new InvalidOperationException("Role-local evidence identities were conflated.");
            store.FinishInvestigation(effectiveRun, report);
            var loaded = store.GetInvestigation(workspace.Id, repositoryId,
                snapshotId, effectiveRun);
            if (loaded?.Summary.Status != "completed" ||
                loaded.Report?.EvidenceLinks.Count != 2 ||
                loaded.Report.Reviewer.Reconciliation?.Dispositions[0].IsVerifiedFact != false)
                throw new InvalidOperationException("Durable C5 report could not round-trip.");
            if (store.ListInvestigations(workspace.Id, repositoryId, snapshotId).TotalCount != 1)
                throw new InvalidOperationException("C5 report listing failed.");
            try
            {
                store.FinishInvestigation(effectiveRun, report);
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

            // Restart marks only active records incomplete; completed reports survive.
            var interrupted = store.CreateInvestigation(scope, Guid.NewGuid());
            store.Initialize();
            var restarted = store.GetInvestigation(workspace.Id, repositoryId,
                snapshotId, interrupted.Record!.RunId);
            if (restarted?.Summary.Status != "incomplete" ||
                restarted.Summary.Diagnostic != "host-restarted-before-completion" ||
                restarted.Report is not null ||
                store.GetInvestigation(workspace.Id, repositoryId,
                    snapshotId, effectiveRun)?.Report is null)
                throw new InvalidOperationException("Restart recovery was inaccurate.");

            Console.WriteLine("GREEN: C5a SQLite migration, report provenance, scoped lookup, independent handles, persistence, and interrupted-run recovery.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", original);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static void ExpectRejected(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid C5 report accepted.");
    }
}

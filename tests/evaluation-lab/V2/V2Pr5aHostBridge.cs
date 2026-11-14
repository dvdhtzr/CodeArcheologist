using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.EvaluationLab.V2;

/// <summary>Read-only witness of the host's actual persisted report.</summary>
public static class V2Pr5aContract
{
    public const string Version = "v2-pr5a-host-report-provenance-v1";
    public const string ReportFile = "PRIVATE-host-stored-report.json";
    public const string ProvenanceFile = "PRIVATE-host-provenance.json";
    public const string Qualification = "diagnostic-host-stored-not-provider-raw-output-not-scoreable";
}
public sealed record V2Pr5aAttempt(
    Guid AttemptId, int Number, string Status, string? TerminalCause,
    Guid? ContinuesAttemptId, long? OwnershipEpoch);
public sealed record V2Pr5aCheckpoint(
    Guid CheckpointId, Guid OriginalAttemptId, string Role, string ContractVersion,
    string AssessmentSha256, string EvidenceLinksSha256, string ContentSha256,
    IReadOnlyList<string> AttemptLineage);
public sealed record V2Pr5aHandoff(
    Guid CallId, Guid AttemptId, string Role, int Ordinal, string Stage,
    string PromptSha256, string TerminalState, string EvidenceLinksSha256,
    int CommittedAdaptiveReceipts);
public sealed record V2Pr5aHostReceipt(
    string SchemaVersion, Guid RunId, Guid WorkspaceId, Guid RepositoryId,
    Guid SnapshotId, string SnapshotFingerprint, string SnapshotManifestSha256,
    string SnapshotCapturePolicy, string ReportContractVersion, string RunStatus,
    Guid? TerminalAttemptId, string? OriginalReportSha256,
    bool HasHostStoredReport, bool V2ObservationEnabled,
    IReadOnlyList<V2Pr5aAttempt> Attempts,
    IReadOnlyList<V2Pr5aCheckpoint> Checkpoints,
    IReadOnlyList<V2Pr5aHandoff> Handoffs,
    IReadOnlyList<string> Blockers, string Qualification);

/// <summary>
/// PR5a intentionally does not alter a workspace, take the execution lock,
/// migrate SQLite, call models or invent semantic/claim-level provenance.
/// Opens an existing v9 DB as READ ONLY and reads one SQLite snapshot.
/// </summary>
public static class V2Pr5aHostBridge
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static string Sha(string value) =>
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(value))).ToLowerInvariant();
    private static string Sha(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static bool SameScope(GateCScope scope, Guid ws, Guid repo, Guid snapshot,
        string fingerprint, string entry, string contract) =>
        scope.WorkspaceId == ws && scope.RepositoryId == repo &&
        scope.SnapshotId == snapshot && scope.FingerprintSha256 == fingerprint &&
        scope.EntryPointId == entry && scope.EvidenceContractVersion == contract;

    public static int Export(string databaseFile, Guid runId, string output, TextWriter log)
    {
        try
        {
            V2Pr4bValidation.Require(runId != Guid.Empty &&
                File.Exists(databaseFile) &&
                (File.GetAttributes(databaseFile) & FileAttributes.ReparsePoint) == 0 &&
                !Directory.Exists(output) && !File.Exists(output) &&
                !V2Pr4bValidation.Overlap(Path.GetFullPath(databaseFile), output) &&
                !V2Pr4bValidation.Overlap(Path.GetDirectoryName(
                    Path.GetFullPath(databaseFile))!, output),
                "Run, source database or new output destination invalid.");
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databaseFile),
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            db.Open();
            using var tx = db.BeginTransaction(deferred: true);
            using (var version = Cmd(db, tx, "PRAGMA user_version;"))
                V2Pr4bValidation.Require(
                    Convert.ToInt32(version.ExecuteScalar()) == 9,
                    "Only existing v9 host databases can be observed; no migration is allowed.");
            Guid ws, repo, snapshot;
            string fingerprint, manifest, policy, entry, contract, reportContract,
                status, diagnostic;
            string? reportText;
            using (var cmd = Cmd(db, tx, """
                SELECT r.workspace_id,r.repository_id,r.snapshot_id,
                       r.fingerprint_sha256,r.entry_point_id,r.evidence_contract_version,
                       r.report_contract_version,r.status,r.diagnostic,r.report_json,
                       s.manifest_sha256,s.policy_version
                FROM investigation_reports r
                JOIN repository_snapshots s ON s.id=r.snapshot_id
                    AND s.workspace_id=r.workspace_id
                    AND s.repository_id=r.repository_id
                    AND s.fingerprint_sha256=r.fingerprint_sha256
                WHERE r.run_id=$run;
                """))
            {
                cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
                using var rd = cmd.ExecuteReader();
                V2Pr4bValidation.Require(rd.Read(), "Missing report or wrong snapshot identity.");
                ws = Guid.Parse(rd.GetString(0));
                repo = Guid.Parse(rd.GetString(1));
                snapshot = Guid.Parse(rd.GetString(2));
                fingerprint = rd.GetString(3);
                entry = rd.GetString(4);
                contract = rd.GetString(5);
                reportContract = rd.GetString(6);
                status = rd.GetString(7);
                diagnostic = rd.GetString(8);
                reportText = rd.IsDBNull(9) ? null : rd.GetString(9);
                manifest = rd.GetString(10);
                policy = rd.GetString(11);
                V2Pr4bValidation.Require(!rd.Read(), "Duplicate run row.");
            }
            V2Pr4bValidation.Require(reportContract == GateC5ReportContract.Version &&
                fingerprint.Length == 64 && manifest.Length == 64 &&
                !string.IsNullOrWhiteSpace(policy),
                "Unsupported C5 contract or snapshot manifest.");
            var blockers = new List<string>();
            if (status != "completed") blockers.Add("run-not-completed:" + status);
            if (reportText is null)
                blockers.Add("no-host-stored-report");
            else
            {
                V2Pr4bValidation.Require(Utf8.GetByteCount(reportText) <= 1024 * 1024,
                    "Host report exceeds bounded private export.");
                GateC5Report? parsed;
                try { parsed = JsonSerializer.Deserialize<GateC5Report>(reportText); }
                catch (JsonException e) { throw new InvalidDataException("Invalid stored C5 JSON.", e); }
                V2Pr4bValidation.Require(parsed is not null &&
                    parsed.RunId == runId && parsed.Version == reportContract &&
                    parsed.Progress.RunId == runId &&
                    parsed.Progress.Status.ToString().Equals(status,
                        StringComparison.OrdinalIgnoreCase) &&
                    SameScope(parsed.Scope, ws, repo, snapshot, fingerprint,
                        entry, contract) && parsed.Investigator is not null &&
                    parsed.Skeptic is not null && parsed.Reviewer is not null,
                    "Stored C5 report's actual identity or required roles differ from run.");
                // Host-injected cautions are separate and must never become
                // attributed model claims by this export.
            }
            var attempts = new List<V2Pr5aAttempt>();
            using (var cmd = Cmd(db, tx, """
                SELECT attempt_id,attempt_number,status,terminal_cause,
                       continues_attempt_id,ownership_epoch
                FROM investigation_attempts WHERE run_id=$run ORDER BY attempt_number;
                """))
            {
                cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                    attempts.Add(new(Guid.Parse(rd.GetString(0)), rd.GetInt32(1),
                        rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3),
                        rd.IsDBNull(4) ? null : Guid.Parse(rd.GetString(4)),
                        rd.IsDBNull(5) ? null : rd.GetInt64(5)));
            }
            V2Pr4bValidation.Require(attempts.Count is >= 1 and <= 20 &&
                attempts.Select(x => x.AttemptId).Distinct().Count() == attempts.Count &&
                attempts.Select((x, i) => x.Number == i + 1).All(x => x),
                "Unexpected attempt lineage or duplicate attempt numbers.");
            var last = attempts[^1];
            if (status == "completed" && last.Status != "completed")
                blockers.Add("terminal-run-attempt-status-mismatch");
            if (status != "completed" && last.Status is "queued" or "running")
                blockers.Add("attempt-not-terminal");
            foreach (var item in attempts.Where(x => x.ContinuesAttemptId is not null))
                V2Pr4bValidation.Require(
                    attempts.Any(x => x.AttemptId == item.ContinuesAttemptId &&
                        x.Number < item.Number),
                    "Attempt continuation points to unrelated/future attempt.");

            var lineage = new Dictionary<Guid, List<(Guid Attempt, string Relation)>>();
            using (var cmd = Cmd(db, tx, """
                SELECT l.attempt_id,l.checkpoint_id,l.relation
                FROM attempt_checkpoint_lineage l
                JOIN investigation_attempts a ON a.attempt_id=l.attempt_id
                WHERE a.run_id=$run ORDER BY a.attempt_number,l.checkpoint_id;
                """))
            {
                cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var attempt = Guid.Parse(rd.GetString(0));
                    var id = Guid.Parse(rd.GetString(1));
                    var relation = rd.GetString(2);
                    if (!lineage.TryGetValue(id, out var parts))
                        lineage[id] = parts = [];
                    parts.Add((attempt, relation));
                }
            }
            var checkpoints = new List<V2Pr5aCheckpoint>();
            using (var cmd = Cmd(db, tx, """
                SELECT checkpoint_id,run_id,attempt_id,role,checkpoint_contract_version,
                    workspace_id,repository_id,snapshot_id,fingerprint_sha256,entry_point_id,
                    evidence_contract_version,capability_contract_version,analyzer_version,
                    model_id,validator_version,assessment_contract_version,assessment_json,
                    evidence_links_json,validation_json,depends_on_json,content_sha256,
                    accepted_at_unix_ms,accepted_ownership_epoch
                FROM investigation_checkpoints WHERE run_id=$run ORDER BY role;
                """))
            {
                cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var checkpoint = new GateC6StoredCheckpoint(
                        Guid.Parse(rd.GetString(0)), Guid.Parse(rd.GetString(1)),
                        Guid.Parse(rd.GetString(2)), rd.GetString(3), rd.GetString(4),
                        new GateCScope(Guid.Parse(rd.GetString(5)),
                            Guid.Parse(rd.GetString(6)), Guid.Parse(rd.GetString(7)),
                            rd.GetString(8), rd.GetString(9), rd.GetString(10)),
                        rd.GetString(11), rd.GetString(12), rd.GetString(13),
                        rd.GetString(14), rd.GetString(15), rd.GetString(16),
                        rd.GetString(17), rd.GetString(18), rd.GetString(19),
                        rd.GetString(20), rd.GetInt64(21), rd.GetInt64(22));
                    V2Pr4bValidation.Require(checkpoint.RunId == runId &&
                        SameScope(checkpoint.Scope, ws, repo, snapshot,
                            fingerprint, entry, contract) &&
                        checkpoint.ContentSha256 == checkpoint.ComputeDigest() &&
                        attempts.Any(x => x.AttemptId == checkpoint.AttemptId),
                        "Accepted checkpoint digest, scope or original attempt invalid.");
                    if (!lineage.TryGetValue(checkpoint.CheckpointId, out var uses) ||
                        !uses.Contains((checkpoint.AttemptId, "accepted")))
                        blockers.Add("checkpoint-lacks-original-acceptance:" + checkpoint.Role);
                    checkpoints.Add(new(checkpoint.CheckpointId,checkpoint.AttemptId,
                        checkpoint.Role,checkpoint.ContractVersion,Sha(checkpoint.AssessmentJson),
                        Sha(checkpoint.EvidenceLinksJson),checkpoint.ContentSha256,
                        (uses ?? []).Select(x => x.Attempt.ToString("D") + ":" + x.Relation)
                            .ToArray()));
                }
            }
            if (lineage.Keys.Except(checkpoints.Select(x => x.CheckpointId)).Any())
                blockers.Add("lineage-references-unknown-checkpoint");
            if (checkpoints.Count != 3 || checkpoints.Select(x => x.Role)
                .Distinct(StringComparer.Ordinal).Count() != 3)
                blockers.Add("three-accepted-role-checkpoints-not-present");
            foreach (var c in checkpoints)
            {
                var usedInFinal = lineage.TryGetValue(c.CheckpointId,out var uses) &&
                    uses.Any(x => x.Attempt == last.AttemptId);
                if (!usedInFinal) blockers.Add("terminal-attempt-missing-checkpoint-lineage:" + c.Role);
            }

            var observed = false;
            using (var cmd = Cmd(db, tx,
                "SELECT COUNT(*) FROM v2_handoff_optins WHERE run_id=$run AND contract_version=$version;"))
            {
                cmd.Parameters.AddWithValue("$run",runId.ToString("D"));
                cmd.Parameters.AddWithValue("$version",V2HandoffContract.Version);
                observed = Convert.ToInt64(cmd.ExecuteScalar()) == 1;
            }
            if (!observed) blockers.Add("no-opt-in-model-adapter-observation");
            var events = new List<V2HandoffEvent>();
            using (var cmd = Cmd(db, tx, """
                SELECT event_id,call_id,run_id,attempt_id,role,call_ordinal,
                    stage,event_kind,prompt_sha256,evidence_links_json,
                    receipt_ids_json,snapshot_fingerprint,ownership_epoch,
                    created_at_unix_ms,content_sha256
                FROM v2_handoff_events WHERE run_id=$run
                ORDER BY attempt_id,role,call_ordinal,
                CASE event_kind WHEN 'prepared' THEN 1
                    WHEN 'adapter-entered' THEN 2 ELSE 3 END;
                """))
            {
                cmd.Parameters.AddWithValue("$run",runId.ToString("D"));
                using var rd=cmd.ExecuteReader();
                while(rd.Read())
                {
                    var e=new V2HandoffEvent(Guid.Parse(rd.GetString(0)),
                        Guid.Parse(rd.GetString(1)),Guid.Parse(rd.GetString(2)),
                        Guid.Parse(rd.GetString(3)),rd.GetString(4),rd.GetInt32(5),
                        rd.GetString(6),rd.GetString(7),rd.GetString(8),
                        rd.GetString(9),rd.GetString(10),rd.GetString(11),
                        rd.GetInt64(12),rd.GetInt64(13),rd.GetString(14));
                    V2Pr4bValidation.Require(e.RunId==runId &&
                        attempts.Any(x=>x.AttemptId==e.AttemptId) &&
                        e.SnapshotFingerprint==fingerprint &&
                        e.ContentSha256==e.ComputeDigest(),
                        "Tampered handoff event or foreign attempt/snapshot.");
                    events.Add(e);
                }
            }
            var handoffs=new List<V2Pr5aHandoff>();
            foreach(var group in events.GroupBy(x=>x.CallId))
            {
                var steps=group.ToArray();
                V2Pr4bValidation.Require(steps.Length is >=1 and <=3 &&
                    steps[0].EventKind==V2HandoffContract.Prepared &&
                    (steps.Length<2 || steps[1].EventKind==V2HandoffContract.Entered) &&
                    (steps.Length<3 || steps[2].EventKind is
                        (V2HandoffContract.Completed or V2HandoffContract.Faulted)) &&
                    steps.All(x=>x.Role==steps[0].Role && x.AttemptId==steps[0].AttemptId &&
                        x.PromptSha256==steps[0].PromptSha256 &&
                        x.EvidenceLinksJson==steps[0].EvidenceLinksJson &&
                        x.ReceiptIdsJson==steps[0].ReceiptIdsJson),
                    "Broken model-adapter call lifecycle.");
                var receiptIds=JsonSerializer.Deserialize<Guid[]>(steps[0].ReceiptIdsJson)
                    ?? throw new InvalidDataException("Null adaptive receipt list.");
                handoffs.Add(new(steps[0].CallId,steps[0].AttemptId,
                    steps[0].Role,steps[0].CallOrdinal,steps[0].Stage,
                    steps[0].PromptSha256,steps[^1].EventKind,
                    Sha(steps[0].EvidenceLinksJson),receiptIds.Length));
            }
            foreach(var role in new[]{"investigator","skeptic","reviewer"})
                foreach(var c in checkpoints.Where(x=>x.Role==role))
                    if(!handoffs.Any(x=>x.Role==role &&
                        x.AttemptId==c.OriginalAttemptId &&
                        x.TerminalState==V2HandoffContract.Completed))
                        blockers.Add("checkpoint-original-attempt-lacks-completed-adapter-call:"+role);
            if (events.Count==0) blockers.Add("no-model-adapter-call-events");
            tx.Commit();

            // The only report source for this export is the host's original
            // report_json string, never a user-authored PR4a report.
            var receipt=new V2Pr5aHostReceipt(V2Pr5aContract.Version,runId,ws,repo,
                snapshot,fingerprint,manifest,policy,reportContract,status,
                last.AttemptId,reportText is null?null:Sha(reportText),
                reportText is not null,observed,attempts,checkpoints,handoffs,
                blockers.Distinct(StringComparer.Ordinal).ToArray(),
                V2Pr5aContract.Qualification);
            Directory.CreateDirectory(output);
            if(reportText is not null)
                WriteNew(Path.Combine(output,V2Pr5aContract.ReportFile),reportText);
            WriteNew(Path.Combine(output,V2Pr5aContract.ProvenanceFile),
                JsonSerializer.Serialize(receipt,V2Schemas.Json));
            log.WriteLine("GREEN: V2 PR5a exported actual host-stored report provenance " +
                $"for run {runId:D}; {blockers.Count} verification blocker(s).");
            log.WriteLine("Private host report and immutable-source metadata only. " +
                "NOT READY FOR SCORING; raw model response and semantic truth not attested.");
            return 0;
        }
        catch(Exception e) when(e is not OutOfMemoryException)
        {
            log.WriteLine("RED: PR5a read-only host provenance export rejected ("+
                e.GetType().Name+"): "+e.Message);
            return 1;
        }
    }
    private static SqliteCommand Cmd(SqliteConnection db,SqliteTransaction tx,string sql)
    {
        var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText=sql;
        return cmd;
    }
    private static void WriteNew(string path,string text)
    {
        using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        var bytes=Utf8.GetBytes(text);
        file.Write(bytes);
        file.Flush(flushToDisk:true);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeArcheologist.Api.Investigations;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

public enum ContinuationAdmissionOutcome
{
    Admitted, Replayed, Conflict, Busy, StaleAttempt, NotRecoverable, AlreadyCompleted,
    CheckpointsChanged
}

public sealed record ContinuationAdmission(ContinuationAdmissionOutcome Outcome,
    InvestigationAttempt? Attempt);

public sealed record ExecutionRequestRecord(string Kind, string RequestDigest, Guid RunId,
    Guid? AttemptId);

public sealed partial class WorkspaceStore
{
    private const string CheckpointColumns = """
        SELECT checkpoint_id, run_id, attempt_id, role, checkpoint_contract_version,
               workspace_id, repository_id, snapshot_id, fingerprint_sha256, entry_point_id,
               evidence_contract_version, capability_contract_version, analyzer_version,
               model_id, validator_version, assessment_contract_version, assessment_json,
               evidence_links_json, validation_json, depends_on_json, content_sha256,
               accepted_at_unix_ms, accepted_ownership_epoch
        FROM investigation_checkpoints
        """;

    private static GateC6StoredCheckpoint ReadCheckpoint(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)),
        r.GetString(3), r.GetString(4),
        new GateCScope(Guid.Parse(r.GetString(5)), Guid.Parse(r.GetString(6)),
            Guid.Parse(r.GetString(7)), r.GetString(8), r.GetString(9), r.GetString(10)),
        r.GetString(11), r.GetString(12), r.GetString(13), r.GetString(14), r.GetString(15),
        r.GetString(16), r.GetString(17), r.GetString(18), r.GetString(19), r.GetString(20),
        r.GetInt64(21), r.GetInt64(22));

    private static List<GateC6StoredCheckpoint> LoadCheckpoints(SqliteConnection db,
        SqliteTransaction? tx, Guid runId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = CheckpointColumns + "\n WHERE run_id=$run ORDER BY accepted_at_unix_ms, role;";
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        var list = new List<GateC6StoredCheckpoint>();
        while (reader.Read()) list.Add(ReadCheckpoint(reader));
        return list;
    }

    public IReadOnlyList<GateC6StoredCheckpoint> ListCheckpoints(Guid runId)
    {
        using var db = Open();
        return LoadCheckpoints(db, null, runId);
    }

    /// <summary>Checkpoint IDs named by the immutable 'checkpoint-accepted' events of a run.</summary>
    public IReadOnlyList<Guid> ListAcceptedCheckpointEventIds(Guid runId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT e.checkpoint_id FROM investigation_events e
            JOIN investigation_attempts a ON a.attempt_id=e.attempt_id
            WHERE a.run_id=$run AND e.event_type='checkpoint-accepted';
            """;
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        var ids = new List<Guid>();
        while (reader.Read())
            ids.Add(reader.IsDBNull(0) ? Guid.Empty : Guid.Parse(reader.GetString(0)));
        return ids;
    }

    /// <summary>
    /// Atomically persists ONE host-accepted role checkpoint, its lineage row
    /// and its 'checkpoint-accepted' event, under the fence. A crash or fault
    /// before commit leaves no checkpoint; after commit it is durable and
    /// immutable. The Reviewer checkpoint requires both independent ones.
    /// </summary>
    public GateC6StoredCheckpoint AcceptCheckpoint(ExecutionFence fence, Guid attemptId,
        GateC6CheckpointDraft draft)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var attempt = RequireActiveAttemptOwner(db, tx, fence, attemptId, "running");
        var run = LoadRun(db, tx, attempt.RunId);
        if (run is null || draft.RunId != run.RunId || draft.Scope != run.Scope)
            throw new InvalidOperationException("Checkpoint scope does not match its run.");
        var role = GateC6CheckpointContract.RoleName(draft.Role);
        var existing = LoadCheckpoints(db, tx, run.RunId);
        if (existing.Any(c => c.Role == role))
            throw new InvalidOperationException("Role already has an accepted checkpoint.");
        if (draft.Role == GateCRole.Reviewer)
        {
            var inv = existing.SingleOrDefault(c => c.Role == "investigator");
            var sk = existing.SingleOrDefault(c => c.Role == "skeptic");
            if (inv is null || sk is null ||
                !draft.DependsOn.SequenceEqual(new[] { inv.CheckpointId, sk.CheckpointId }))
                throw new InvalidOperationException("Reviewer checkpoint requires both independent checkpoints.");
        }
        else if (draft.DependsOn.Count != 0)
            throw new InvalidOperationException("Independent role checkpoints cannot depend on other roles.");

        var stored = new GateC6StoredCheckpoint(Guid.NewGuid(), run.RunId, attemptId, role,
            GateC6CheckpointContract.Version, draft.Scope, draft.CapabilityContractVersion,
            draft.AnalyzerVersion, draft.ModelId, GateC6CheckpointContract.ValidatorVersion,
            GateCContract.Version, draft.AssessmentJson, draft.EvidenceLinksJson,
            draft.ValidationJson, JsonSerializer.Serialize(draft.DependsOn, GateC6CheckpointContract.Json),
            "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), fence.Epoch);
        stored = stored with { ContentSha256 = stored.ComputeDigest() };

        using (var insert = Command(db, tx, """
            INSERT INTO investigation_checkpoints (checkpoint_id, run_id, attempt_id, role,
                checkpoint_contract_version, workspace_id, repository_id, snapshot_id,
                fingerprint_sha256, entry_point_id, evidence_contract_version,
                capability_contract_version, analyzer_version, model_id, validator_version,
                assessment_contract_version, assessment_json, evidence_links_json,
                validation_json, depends_on_json, content_sha256, accepted_at_unix_ms,
                accepted_ownership_epoch)
            VALUES ($id,$run,$attempt,$role,$cv,$ws,$repo,$snap,$fp,$entry,$ev,$cap,$an,
                $model,$val,$ac,$assess,$links,$validation,$deps,$sha,$at,$epoch);
            """))
        {
            var p = insert.Parameters;
            p.AddWithValue("$id", stored.CheckpointId.ToString("D"));
            p.AddWithValue("$run", stored.RunId.ToString("D"));
            p.AddWithValue("$attempt", attemptId.ToString("D"));
            p.AddWithValue("$role", role);
            p.AddWithValue("$cv", stored.ContractVersion);
            p.AddWithValue("$ws", stored.Scope.WorkspaceId.ToString("D"));
            p.AddWithValue("$repo", stored.Scope.RepositoryId.ToString("D"));
            p.AddWithValue("$snap", stored.Scope.SnapshotId.ToString("D"));
            p.AddWithValue("$fp", stored.Scope.FingerprintSha256);
            p.AddWithValue("$entry", stored.Scope.EntryPointId);
            p.AddWithValue("$ev", stored.Scope.EvidenceContractVersion);
            p.AddWithValue("$cap", stored.CapabilityContractVersion);
            p.AddWithValue("$an", stored.AnalyzerVersion);
            p.AddWithValue("$model", stored.ModelId);
            p.AddWithValue("$val", stored.ValidatorVersion);
            p.AddWithValue("$ac", stored.AssessmentContractVersion);
            p.AddWithValue("$assess", stored.AssessmentJson);
            p.AddWithValue("$links", stored.EvidenceLinksJson);
            p.AddWithValue("$validation", stored.ValidationJson);
            p.AddWithValue("$deps", stored.DependsOnJson);
            p.AddWithValue("$sha", stored.ContentSha256);
            p.AddWithValue("$at", stored.AcceptedAtUnixMs);
            p.AddWithValue("$epoch", stored.AcceptedOwnershipEpoch);
            insert.ExecuteNonQuery();
        }
        InsertLineage(db, tx, attemptId, stored.CheckpointId, "accepted");
        AppendEvent(db, tx, attemptId, "checkpoint-accepted", "checkpoint-accepted-not-semantic-proof",
            null, null, fence.Epoch, role, stored.CheckpointId.ToString("D"));
        tx.Commit();
        return stored;
    }

    /// <summary>Records that this attempt reused a revalidated checkpoint (lineage + event).</summary>
    public void RecordCheckpointReuse(ExecutionFence fence, Guid attemptId, GateC6StoredCheckpoint checkpoint)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var attempt = RequireActiveAttemptOwner(db, tx, fence, attemptId, "running");
        if (attempt.RunId != checkpoint.RunId ||
            !LoadCheckpoints(db, tx, attempt.RunId).Any(c => c.CheckpointId == checkpoint.CheckpointId &&
                c.ContentSha256 == checkpoint.ContentSha256))
            throw new InvalidOperationException("Reused checkpoint is not an accepted checkpoint of this run.");
        InsertLineage(db, tx, attemptId, checkpoint.CheckpointId, "reused");
        AppendEvent(db, tx, attemptId, "checkpoint-reused", "checkpoint-revalidated-and-reused",
            null, null, fence.Epoch, checkpoint.Role, checkpoint.CheckpointId.ToString("D"));
        tx.Commit();
    }

    /// <summary>Fenced record that revalidation rejected checkpoint reuse at execution time.</summary>
    public void RecordCheckpointRejection(ExecutionFence fence, Guid attemptId, string diagnostic)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        RequireActiveAttemptOwner(db, tx, fence, attemptId, "running");
        AppendEvent(db, tx, attemptId, "checkpoint-validation-rejected", diagnostic,
            null, null, fence.Epoch);
        tx.Commit();
    }

    private static void InsertLineage(SqliteConnection db, SqliteTransaction tx, Guid attemptId,
        Guid checkpointId, string relation)
    {
        using var cmd = Command(db, tx, """
            INSERT INTO attempt_checkpoint_lineage (attempt_id, checkpoint_id, relation, recorded_at_unix_ms)
            VALUES ($a,$c,$r,$now);
            """);
        cmd.Parameters.AddWithValue("$a", attemptId.ToString("D"));
        cmd.Parameters.AddWithValue("$c", checkpointId.ToString("D"));
        cmd.Parameters.AddWithValue("$r", relation);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(Guid AttemptId, Guid CheckpointId, string Relation)> ListLineage(Guid runId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT l.attempt_id, l.checkpoint_id, l.relation FROM attempt_checkpoint_lineage l
            JOIN investigation_attempts a ON a.attempt_id=l.attempt_id
            WHERE a.run_id=$run ORDER BY a.attempt_number, l.recorded_at_unix_ms;
            """;
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        var list = new List<(Guid, Guid, string)>();
        while (reader.Read())
            list.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2)));
        return list;
    }

    public static string ContinuationDigest(Guid runId, int expectedAttemptNumber) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"gate-c6b-continue-v1\n{runId:D}\n{expectedAttemptNumber}")));

    public ExecutionRequestRecord? FindExecutionRequest(Guid idempotencyKey)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT request_kind, request_digest, run_id, attempt_id
            FROM investigation_execution_requests WHERE idempotency_key=$key;
            """;
        cmd.Parameters.AddWithValue("$key", idempotencyKey.ToString("D"));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(r.GetString(0), r.GetString(1), Guid.Parse(r.GetString(2)),
            r.IsDBNull(3) ? null : Guid.Parse(r.GetString(3))) : null;
    }

    /// <summary>
    /// Explicit continuation admission in ONE fenced transaction: idempotency,
    /// optimistic concurrency on the expected latest attempt, recoverable run
    /// state, an unchanged accepted-checkpoint set (as revalidated by the
    /// caller), a free execution slot, then a NEW attempt under the same run
    /// with continuation lineage. Never mutates earlier attempts or checkpoints.
    /// </summary>
    public ContinuationAdmission AdmitContinuation(ExecutionFence fence, Guid runId,
        Guid idempotencyKey, int expectedAttemptNumber, IReadOnlyCollection<Guid> revalidatedCheckpointIds)
    {
        var digest = ContinuationDigest(runId, expectedAttemptNumber);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using (var lookup = Command(db, tx, """
            SELECT request_kind, request_digest, attempt_id FROM investigation_execution_requests
            WHERE idempotency_key=$key;
            """))
        {
            lookup.Parameters.AddWithValue("$key", idempotencyKey.ToString("D"));
            using var r = lookup.ExecuteReader();
            if (r.Read())
            {
                if (r.GetString(0) != "continue" || r.GetString(1) != digest || r.IsDBNull(2))
                    return new(ContinuationAdmissionOutcome.Conflict, null);
                return new(ContinuationAdmissionOutcome.Replayed,
                    LoadAttempt(db, tx, Guid.Parse(r.GetString(2))));
            }
        }
        RequireOwner(db, tx, fence);
        var run = LoadRun(db, tx, runId);
        if (run is null) return new(ContinuationAdmissionOutcome.NotRecoverable, null);
        if (run.Status == "completed") return new(ContinuationAdmissionOutcome.AlreadyCompleted, null);
        InvestigationAttempt? latest;
        using (var cmd = Command(db, tx, AttemptColumns +
            "\n WHERE run_id=$run ORDER BY attempt_number DESC LIMIT 1;"))
        {
            cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
            using var r = cmd.ExecuteReader();
            latest = r.Read() ? ReadAttempt(r) : null;
        }
        if (latest is null || latest.ContractVersion != GateC6ExecutionContract.AttemptVersion)
            return new(ContinuationAdmissionOutcome.NotRecoverable, null);
        if (latest.Number != expectedAttemptNumber)
            return new(ContinuationAdmissionOutcome.StaleAttempt, null);
        if (!GateC6AttemptTransitions.IsTerminal(latest.Status))
            return new(ContinuationAdmissionOutcome.Busy, null);
        if (run.Status != "interrupted")
            return new(ContinuationAdmissionOutcome.NotRecoverable, null);
        if (!LoadCheckpoints(db, tx, runId).Select(c => c.CheckpointId).ToHashSet()
                .SetEquals(revalidatedCheckpointIds))
            return new(ContinuationAdmissionOutcome.CheckpointsChanged, null);
        using (var slot = Command(db, tx,
            "SELECT active_attempt_id FROM investigation_execution_slot WHERE slot_id=1;"))
            if (slot.ExecuteScalar() is string) return new(ContinuationAdmissionOutcome.Busy, null);

        var attemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (var insert = Command(db, tx, """
            INSERT INTO investigation_attempts (attempt_id, run_id, attempt_number, contract_version,
                status, diagnostic, created_at_unix_ms, execution_owner_id, ownership_epoch,
                continues_attempt_id)
            VALUES ($id,$run,$n,$v,'queued','queued-continuation',$now,$owner,$epoch,$prev);
            """))
        {
            insert.Parameters.AddWithValue("$id", attemptId.ToString("D"));
            insert.Parameters.AddWithValue("$run", runId.ToString("D"));
            insert.Parameters.AddWithValue("$n", latest.Number + 1);
            insert.Parameters.AddWithValue("$v", GateC6ExecutionContract.AttemptVersion);
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$owner", fence.OwnerId.ToString("D"));
            insert.Parameters.AddWithValue("$epoch", fence.Epoch);
            insert.Parameters.AddWithValue("$prev", latest.AttemptId.ToString("D"));
            insert.ExecuteNonQuery();
        }
        using (var report = Command(db, tx, """
            UPDATE investigation_reports SET status='running', diagnostic='queued-continuation'
            WHERE run_id=$run AND status='interrupted';
            """))
        {
            report.Parameters.AddWithValue("$run", runId.ToString("D"));
            if (report.ExecuteNonQuery() != 1) throw new InvalidOperationException("Run state changed.");
        }
        using (var reserve = Command(db, tx, """
            UPDATE investigation_execution_slot SET active_attempt_id=$a
            WHERE slot_id=1 AND active_attempt_id IS NULL;
            """))
        {
            reserve.Parameters.AddWithValue("$a", attemptId.ToString("D"));
            if (reserve.ExecuteNonQuery() != 1) throw new InvalidOperationException("Slot acquisition failed.");
        }
        AppendEvent(db, tx, attemptId, "continuation-admitted", "explicit-user-continuation",
            null, "queued", fence.Epoch);
        using (var request = Command(db, tx, """
            INSERT INTO investigation_execution_requests (idempotency_key, request_kind,
                request_digest, run_id, attempt_id, created_at_unix_ms)
            VALUES ($key,'continue',$digest,$run,$a,$now);
            """))
        {
            request.Parameters.AddWithValue("$key", idempotencyKey.ToString("D"));
            request.Parameters.AddWithValue("$digest", digest);
            request.Parameters.AddWithValue("$run", runId.ToString("D"));
            request.Parameters.AddWithValue("$a", attemptId.ToString("D"));
            request.Parameters.AddWithValue("$now", now);
            request.ExecuteNonQuery();
        }
        var admitted = LoadAttempt(db, tx, attemptId);
        tx.Commit();
        return new(ContinuationAdmissionOutcome.Admitted, admitted);
    }
}

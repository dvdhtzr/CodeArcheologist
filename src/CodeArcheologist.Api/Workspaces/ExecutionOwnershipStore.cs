using CodeArcheologist.Api.Execution;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

/// <summary>Durable fencing token: random owner identity plus monotonic epoch.</summary>
public sealed record ExecutionFence(Guid OwnerId, long Epoch);

/// <summary>
/// The caller is not (or no longer) the current fenced execution owner. The
/// enclosing transaction is rolled back; nothing was written.
/// </summary>
public sealed class ExecutionFenceException(string diagnostic) : Exception(diagnostic)
{
    public string Diagnostic { get; } = diagnostic;
}

public sealed record ExecutionOwnerRecord(Guid? OwnerId, long Epoch, string Status,
    DateTimeOffset? AcquiredAtUtc, DateTimeOffset? HeartbeatAtUtc, int? ProcessId,
    string Diagnostic);

public sealed record ReconciliationResult(int InterruptedAttempts,
    int LegacyAttemptsFinalized, int LegacyReportsFinalized);

public sealed partial class WorkspaceStore
{
    /// <summary>
    /// Establishes a NEW fenced ownership epoch. Requires a currently held OS
    /// execution lock for this store's data directory; heartbeat age or the
    /// previous owner's status is irrelevant and can never substitute for it.
    /// </summary>
    public ExecutionFence EstablishOwnership(ExecutionProcessLock held)
    {
        if (!held.IsHeld || !string.Equals(Path.GetDirectoryName(held.LockPath),
                DataDirectory, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ExecutionFenceException("execution-lock-not-held-for-data-directory");
        var owner = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var update = Command(db, tx, """
            UPDATE execution_owner SET owner_id=$owner,
                ownership_epoch=ownership_epoch+1, status='active',
                acquired_at_unix_ms=$now, heartbeat_at_unix_ms=$now,
                process_id=$pid, diagnostic='ownership-established'
            WHERE owner_slot=1
            RETURNING ownership_epoch;
            """);
        update.Parameters.AddWithValue("$owner", owner.ToString("D"));
        update.Parameters.AddWithValue("$now", now);
        update.Parameters.AddWithValue("$pid", Environment.ProcessId);
        var epoch = Convert.ToInt64(update.ExecuteScalar()
            ?? throw new InvalidOperationException("Execution owner row is missing."));
        using var history = Command(db, tx, """
            INSERT INTO execution_ownership_history
                (ownership_epoch, owner_id, acquired_at_unix_ms, process_id)
            VALUES ($epoch,$owner,$now,$pid);
            """);
        history.Parameters.AddWithValue("$epoch", epoch);
        history.Parameters.AddWithValue("$owner", owner.ToString("D"));
        history.Parameters.AddWithValue("$now", now);
        history.Parameters.AddWithValue("$pid", Environment.ProcessId);
        history.ExecuteNonQuery();
        tx.Commit();
        return new(owner, epoch);
    }

    public ExecutionOwnerRecord GetExecutionOwner()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT owner_id, ownership_epoch, status, acquired_at_unix_ms,
                   heartbeat_at_unix_ms, process_id, diagnostic
            FROM execution_owner WHERE owner_slot=1;
            """;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Execution owner row is missing.");
        return new(r.IsDBNull(0) ? null : Guid.Parse(r.GetString(0)), r.GetInt64(1),
            r.GetString(2),
            r.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
            r.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)),
            r.IsDBNull(5) ? null : r.GetInt32(5), r.GetString(6));
    }

    /// <summary>Diagnostic liveness only. Fenced; never grants or transfers ownership.</summary>
    public void Heartbeat(ExecutionFence fence)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        RequireOwner(db, tx, fence);
        using var cmd = Command(db, tx, """
            UPDATE execution_owner SET heartbeat_at_unix_ms=$now
            WHERE owner_slot=1 AND owner_id=$owner AND ownership_epoch=$epoch;
            """);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$owner", fence.OwnerId.ToString("D"));
        cmd.Parameters.AddWithValue("$epoch", fence.Epoch);
        if (cmd.ExecuteNonQuery() != 1) throw new ExecutionFenceException("stale-execution-owner");
        tx.Commit();
    }

    /// <summary>
    /// Graceful release when no attempt of this owner remains active. If the
    /// slot is still held (an uncooperative model call never returned) the
    /// owner stays 'active' so the next lock holder reconciles it.
    /// </summary>
    public bool TryReleaseOwnership(ExecutionFence fence)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        RequireOwner(db, tx, fence);
        using (var active = Command(db, tx, """
            SELECT COUNT(*) FROM investigation_attempts
            WHERE status IN ('queued','running') AND execution_owner_id=$owner
              AND ownership_epoch=$epoch;
            """))
        {
            active.Parameters.AddWithValue("$owner", fence.OwnerId.ToString("D"));
            active.Parameters.AddWithValue("$epoch", fence.Epoch);
            if (Convert.ToInt64(active.ExecuteScalar()) != 0) return false;
        }
        using var cmd = Command(db, tx, """
            UPDATE execution_owner SET status='released', diagnostic='ownership-released'
            WHERE owner_slot=1 AND owner_id=$owner AND ownership_epoch=$epoch;
            """);
        cmd.Parameters.AddWithValue("$owner", fence.OwnerId.ToString("D"));
        cmd.Parameters.AddWithValue("$epoch", fence.Epoch);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    private static void RequireOwner(SqliteConnection db, SqliteTransaction tx,
        ExecutionFence fence)
    {
        using var cmd = Command(db, tx, """
            SELECT COUNT(*) FROM execution_owner
            WHERE owner_slot=1 AND status='active'
              AND owner_id=$owner AND ownership_epoch=$epoch;
            """);
        cmd.Parameters.AddWithValue("$owner", fence.OwnerId.ToString("D"));
        cmd.Parameters.AddWithValue("$epoch", fence.Epoch);
        if (Convert.ToInt64(cmd.ExecuteScalar()) != 1)
            throw new ExecutionFenceException("stale-execution-owner");
    }

    /// <summary>
    /// Verifies the caller is the current owner AND the owner of the attempt,
    /// and that the attempt is in the expected active state.
    /// </summary>
    private static InvestigationAttempt RequireActiveAttemptOwner(SqliteConnection db,
        SqliteTransaction tx, ExecutionFence fence, Guid attemptId, string expectedStatus)
    {
        RequireOwner(db, tx, fence);
        var attempt = LoadAttempt(db, tx, attemptId)
            ?? throw new InvalidOperationException("Missing execution attempt.");
        if (attempt.OwnerId != fence.OwnerId || attempt.OwnershipEpoch != fence.Epoch ||
            attempt.ContractVersion != GateC6ExecutionContract.AttemptVersion)
            throw new ExecutionFenceException("attempt-owned-by-another-epoch");
        if (attempt.Status != expectedStatus)
            throw new InvalidOperationException("Stale execution transition.");
        return attempt;
    }

    /// <summary>
    /// Startup step 5: runs ONLY after this process holds the OS lock and has
    /// established a new epoch. Attempts left active by earlier owners are
    /// finalized: C6b attempts become 'interrupted' (run left recoverable but
    /// never resumed automatically); C6a and pre-C6 records keep their legacy
    /// non-resumable semantics. Accepted checkpoints are never touched.
    /// </summary>
    public ReconciliationResult ReconcileAbandoned(ExecutionFence fence)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        RequireOwner(db, tx, fence);

        int legacyReports;
        using (var legacy = Command(db, tx, """
            UPDATE investigation_reports SET status='incomplete',
                diagnostic='host-restarted-before-completion', finished_at_unix_ms=$now
            WHERE status='running' AND NOT EXISTS (
                SELECT 1 FROM investigation_attempts a WHERE a.run_id=investigation_reports.run_id);
            """))
        {
            legacy.Parameters.AddWithValue("$now", now);
            legacyReports = legacy.ExecuteNonQuery();
        }

        var abandoned = new List<InvestigationAttempt>();
        using (var get = Command(db, tx, AttemptColumns + """

            WHERE status IN ('queued','running')
              AND NOT (execution_owner_id IS $owner AND ownership_epoch IS $epoch);
            """))
        {
            get.Parameters.AddWithValue("$owner", fence.OwnerId.ToString("D"));
            get.Parameters.AddWithValue("$epoch", fence.Epoch);
            using var reader = get.ExecuteReader();
            while (reader.Read()) abandoned.Add(ReadAttempt(reader));
        }

        int interrupted = 0, legacyAttempts = 0;
        foreach (var attempt in abandoned)
        {
            if (attempt.OwnershipEpoch is long prior && prior >= fence.Epoch)
                throw new ExecutionFenceException("attempt-from-newer-or-current-epoch");
            var isLegacy = attempt.ContractVersion != GateC6ExecutionContract.AttemptVersion;
            string reportStatus;
            if (isLegacy)
            {
                // C6a semantics preserved: never resumable.
                var next = attempt.Status == "queued" ? "rejected" : "interrupted";
                TransitionAttempt(db, tx, attempt.AttemptId, attempt.Status, next,
                    "host-restarted-before-completion",
                    attempt.Status == "queued" ? "attempt-rejected" : "attempt-interrupted",
                    fence.Epoch, "legacy-c6a-restart");
                reportStatus = "incomplete";
                legacyAttempts++;
            }
            else
            {
                TransitionAttempt(db, tx, attempt.AttemptId, attempt.Status, "interrupted",
                    "host-reconciled-abandoned-attempt", "attempt-interrupted",
                    fence.Epoch, "process-interrupted");
                reportStatus = "interrupted";
                interrupted++;
            }
            using (var report = Command(db, tx, """
                UPDATE investigation_reports SET status=$status, diagnostic=$diagnostic,
                    finished_at_unix_ms=CASE WHEN $status='interrupted'
                        THEN NULL ELSE $now END
                WHERE run_id=$run AND status='running';
                """))
            {
                report.Parameters.AddWithValue("$run", attempt.RunId.ToString("D"));
                report.Parameters.AddWithValue("$status", reportStatus);
                report.Parameters.AddWithValue("$diagnostic", isLegacy
                    ? "host-restarted-before-completion" : "host-interrupted-awaiting-explicit-action");
                report.Parameters.AddWithValue("$now", now);
                if (report.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Restart report mismatch.");
            }
            ReleaseSlot(db, tx, attempt.AttemptId);
        }

        // Any remaining slot reservation must belong to an active attempt.
        using (var slot = Command(db, tx, """
            SELECT COUNT(*) FROM investigation_execution_slot s
            JOIN investigation_attempts a ON a.attempt_id = s.active_attempt_id
            WHERE s.slot_id=1 AND a.status NOT IN ('queued','running');
            """))
        {
            if (Convert.ToInt64(slot.ExecuteScalar()) != 0)
                throw new InvalidOperationException("Execution slot references a terminal attempt.");
        }
        tx.Commit();
        return new(interrupted, legacyAttempts, legacyReports);
    }
}

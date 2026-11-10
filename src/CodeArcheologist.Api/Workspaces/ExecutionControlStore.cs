using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

public enum CancellationRequestOutcome { Requested, Replayed, Conflict, StaleAttempt, NoActiveAttempt, NotFound }

public sealed record CancellationRequestResult(CancellationRequestOutcome Outcome,
    InvestigationAttempt? Attempt);

public sealed partial class WorkspaceStore
{
    public static string CancellationDigest(Guid runId, int expectedAttemptNumber) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"gate-c6b-cancel-v1\n{runId:D}\n{expectedAttemptNumber}")));

    /// <summary>
    /// Persists cancellation INTENT only (fenced, idempotent, optimistic on the
    /// expected attempt). It never finalizes the attempt and never releases the
    /// slot: the worker does that after the in-flight model call returns, or at
    /// the queued-attempt claim boundary where no model work has begun.
    /// </summary>
    public CancellationRequestResult RequestCancellation(ExecutionFence fence, Guid runId,
        Guid idempotencyKey, int expectedAttemptNumber)
    {
        var digest = CancellationDigest(runId, expectedAttemptNumber);
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
                return r.GetString(0) == "cancel" && r.GetString(1) == digest && !r.IsDBNull(2)
                    ? new(CancellationRequestOutcome.Replayed, LoadAttempt(db, tx, Guid.Parse(r.GetString(2))))
                    : new(CancellationRequestOutcome.Conflict, null);
        }
        RequireOwner(db, tx, fence);
        if (LoadRun(db, tx, runId) is null) return new(CancellationRequestOutcome.NotFound, null);
        InvestigationAttempt? latest;
        using (var cmd = Command(db, tx, AttemptColumns +
            "\n WHERE run_id=$run ORDER BY attempt_number DESC LIMIT 1;"))
        {
            cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
            using var r = cmd.ExecuteReader();
            latest = r.Read() ? ReadAttempt(r) : null;
        }
        if (latest is null || GateC6AttemptTransitions.IsTerminal(latest.Status) ||
            latest.ContractVersion != GateC6ExecutionContract.AttemptVersion)
            return new(CancellationRequestOutcome.NoActiveAttempt, null);
        if (latest.Number != expectedAttemptNumber)
            return new(CancellationRequestOutcome.StaleAttempt, null);
        RequireActiveAttemptOwner(db, tx, fence, latest.AttemptId, latest.Status);
        if (latest.CancelRequestedAtUtc is null)
        {
            using var mark = Command(db, tx, """
                UPDATE investigation_attempts SET cancel_requested_at_unix_ms=$now,
                    state_version=state_version+1
                WHERE attempt_id=$id AND cancel_requested_at_unix_ms IS NULL
                  AND status IN ('queued','running');
                """);
            mark.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            mark.Parameters.AddWithValue("$id", latest.AttemptId.ToString("D"));
            if (mark.ExecuteNonQuery() != 1) throw new InvalidOperationException("Stale cancellation.");
            AppendEvent(db, tx, latest.AttemptId, "cancellation-requested", "user-cancellation-intent",
                latest.Status, latest.Status, fence.Epoch);
        }
        using (var request = Command(db, tx, """
            INSERT INTO investigation_execution_requests (idempotency_key, request_kind,
                request_digest, run_id, attempt_id, created_at_unix_ms)
            VALUES ($key,'cancel',$digest,$run,$a,$now);
            """))
        {
            request.Parameters.AddWithValue("$key", idempotencyKey.ToString("D"));
            request.Parameters.AddWithValue("$digest", digest);
            request.Parameters.AddWithValue("$run", runId.ToString("D"));
            request.Parameters.AddWithValue("$a", latest.AttemptId.ToString("D"));
            request.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            request.ExecuteNonQuery();
        }
        var updated = LoadAttempt(db, tx, latest.AttemptId);
        tx.Commit();
        return new(CancellationRequestOutcome.Requested, updated);
    }

    public bool IsCancellationRequested(Guid attemptId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM investigation_attempts
            WHERE attempt_id=$id AND cancel_requested_at_unix_ms IS NOT NULL;
            """;
        cmd.Parameters.AddWithValue("$id", attemptId.ToString("D"));
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    /// <summary>
    /// Claim-boundary cancellation of a queued attempt whose cancellation was
    /// requested before any model work began. Runs inside the claim transaction.
    /// </summary>
    private static void CancelQueuedAttempt(SqliteConnection db, SqliteTransaction tx,
        ExecutionFence fence, InvestigationAttempt attempt)
    {
        using (var report = Command(db, tx, """
            UPDATE investigation_reports SET status='cancelled', diagnostic='host-cancelled',
                finished_at_unix_ms=$now
            WHERE run_id=$run AND status='running';
            """))
        {
            report.Parameters.AddWithValue("$run", attempt.RunId.ToString("D"));
            report.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (report.ExecuteNonQuery() != 1) throw new InvalidOperationException("Cancel report mismatch.");
        }
        TransitionAttempt(db, tx, attempt.AttemptId, "queued", "cancelled", "host-cancelled",
            "attempt-cancelled", fence.Epoch, "user-cancelled");
        ReleaseSlot(db, tx, attempt.AttemptId);
    }
}

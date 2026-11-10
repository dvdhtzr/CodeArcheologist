using CodeArcheologist.Api.Investigations;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

public static class GateC6ExecutionContract
{
    /// <summary>C6a attempts: never resumable, finalized with legacy restart semantics.</summary>
    public const string LegacyAttemptVersion = "gate-c6-execution-attempt-v1";
    /// <summary>C6b fenced attempts created and executed by a lock-holding owner.</summary>
    public const string AttemptVersion = "gate-c6-execution-attempt-v2";
    public const string LegacyEventVersion = "gate-c6-investigation-event-v1";
    public const string EventVersion = "gate-c6-investigation-event-v2";
    public static readonly TimeSpan AttemptDeadline = TimeSpan.FromMinutes(6);
}

public sealed record InvestigationAttempt(
    Guid AttemptId, Guid RunId, int Number, string Status,
    string Diagnostic, long StateVersion, long LastEventSequence,
    string ContractVersion = GateC6ExecutionContract.AttemptVersion,
    Guid? OwnerId = null, long? OwnershipEpoch = null,
    string? TerminalCause = null, Guid? ContinuesAttemptId = null,
    DateTimeOffset? DeadlineAtUtc = null, DateTimeOffset? CancelRequestedAtUtc = null);

public sealed record InvestigationEvent(
    Guid EventId, Guid AttemptId, Guid RunId, long SequenceNumber,
    string EventType, string Diagnostic, string? FromStatus, string? ToStatus,
    DateTimeOffset OccurredAtUtc, string? Stage = null, long? OwnershipEpoch = null,
    string? CheckpointId = null);

/// <summary>An attempt claimed by the current owner for execution.</summary>
public sealed record ClaimedAttempt(InvestigationAttempt Attempt,
    InvestigationSummary Run, DateTimeOffset DeadlineAtUtc);

public static class GateC6AttemptTransitions
{
    public static bool Allowed(string from, string to) => from switch
    {
        // queued -> interrupted: admitted by an owner that died before start.
        "queued" => to is "running" or "cancelled" or "rejected" or "interrupted",
        "running" => to is "completed" or "incomplete" or "interrupted"
            or "cancelled" or "rejected" or "failed",
        _ => false
    };

    public static bool IsTerminal(string state) => state is
        "completed" or "incomplete" or "interrupted" or "cancelled"
        or "rejected" or "failed";
}

/// <summary>
/// Database-owned admission, fenced attempt transitions and event history.
/// Every mutation of an active C6b attempt re-verifies, inside its own
/// transaction, that the caller's (owner, epoch) is the CURRENT active owner
/// and the owner recorded on the attempt. A stale owner can neither write
/// progress nor release another owner's execution slot.
/// </summary>
public sealed partial class WorkspaceStore
{
    private static SqliteCommand Command(SqliteConnection connection,
        SqliteTransaction tx, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void AppendEvent(SqliteConnection db, SqliteTransaction tx,
        Guid attemptId, string eventType, string diagnostic,
        string? from, string? to, long? epoch, string? stage = null,
        string? checkpointId = null)
    {
        using var next = Command(db, tx, """
            UPDATE investigation_attempts
            SET last_event_sequence = last_event_sequence + 1
            WHERE attempt_id = $id
            RETURNING last_event_sequence;
            """);
        next.Parameters.AddWithValue("$id", attemptId.ToString("D"));
        var sequence = next.ExecuteScalar();
        if (sequence is null) throw new InvalidOperationException("Missing attempt.");
        using var insert = Command(db, tx, """
            INSERT INTO investigation_events (
                event_id, attempt_id, sequence_number, event_contract_version,
                occurred_at_unix_ms, event_type, diagnostic, stage,
                from_status, to_status, checkpoint_id, payload_json, ownership_epoch)
            VALUES ($event,$attempt,$seq,$version,$at,$type,$diagnostic,$stage,
                    $from,$to,$checkpoint,'{}',$epoch);
            """);
        insert.Parameters.AddWithValue("$event", Guid.NewGuid().ToString("D"));
        insert.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
        insert.Parameters.AddWithValue("$seq", Convert.ToInt64(sequence));
        insert.Parameters.AddWithValue("$version", GateC6ExecutionContract.EventVersion);
        insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$type", eventType);
        insert.Parameters.AddWithValue("$diagnostic", diagnostic);
        insert.Parameters.AddWithValue("$stage", (object?)stage ?? DBNull.Value);
        insert.Parameters.AddWithValue("$from", (object?)from ?? DBNull.Value);
        insert.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);
        insert.Parameters.AddWithValue("$checkpoint", (object?)checkpointId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$epoch", (object?)epoch ?? DBNull.Value);
        if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Event not persisted.");
    }

    private static void TransitionAttempt(SqliteConnection db, SqliteTransaction tx,
        Guid attemptId, string from, string to, string diagnostic, string eventType,
        long? epoch, string? terminalCause = null, long? deadlineUnixMs = null)
    {
        if (!GateC6AttemptTransitions.Allowed(from, to))
            throw new InvalidOperationException("Illegal execution transition.");
        var terminal = GateC6AttemptTransitions.IsTerminal(to);
        if (terminal != (terminalCause is not null) && epoch is not null)
            throw new InvalidOperationException("Terminal cause must accompany terminal C6b transitions.");
        using var cmd = Command(db, tx, """
            UPDATE investigation_attempts
               SET status=$next, diagnostic=$diagnostic,
                   state_version=state_version+1,
                   started_at_unix_ms=CASE WHEN $next='running'
                       THEN $now ELSE started_at_unix_ms END,
                   finished_at_unix_ms=CASE WHEN $terminal=1
                       THEN $now ELSE finished_at_unix_ms END,
                   terminal_cause=CASE WHEN $terminal=1
                       THEN $cause ELSE terminal_cause END,
                   deadline_at_unix_ms=COALESCE($deadline, deadline_at_unix_ms)
             WHERE attempt_id=$id AND status=$current;
            """);
        cmd.Parameters.AddWithValue("$id", attemptId.ToString("D"));
        cmd.Parameters.AddWithValue("$current", from);
        cmd.Parameters.AddWithValue("$next", to);
        cmd.Parameters.AddWithValue("$diagnostic", diagnostic);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$terminal", terminal ? 1 : 0);
        cmd.Parameters.AddWithValue("$cause", (object?)terminalCause ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$deadline", (object?)deadlineUnixMs ?? DBNull.Value);
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Stale execution transition.");
        AppendEvent(db, tx, attemptId, eventType, diagnostic, from, to, epoch);
    }

    private const string AttemptColumns = """
        SELECT attempt_id, run_id, attempt_number, status, diagnostic,
               state_version, last_event_sequence, contract_version,
               execution_owner_id, ownership_epoch, terminal_cause,
               continues_attempt_id, deadline_at_unix_ms, cancel_requested_at_unix_ms
        FROM investigation_attempts
        """;

    private static InvestigationAttempt ReadAttempt(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)),
        r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6),
        r.GetString(7),
        r.IsDBNull(8) ? null : Guid.Parse(r.GetString(8)),
        r.IsDBNull(9) ? null : r.GetInt64(9),
        r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : Guid.Parse(r.GetString(11)),
        r.IsDBNull(12) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(12)),
        r.IsDBNull(13) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(13)));

    private static InvestigationAttempt? LoadAttempt(SqliteConnection db,
        SqliteTransaction tx, Guid attemptId)
    {
        using var cmd = Command(db, tx, AttemptColumns + "\n WHERE attempt_id=$id;");
        cmd.Parameters.AddWithValue("$id", attemptId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAttempt(reader) : null;
    }

    /// <summary>Latest attempt of a run (read-only projection helper).</summary>
    public InvestigationAttempt? GetAttempt(Guid runId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = AttemptColumns + """

            WHERE run_id=$run ORDER BY attempt_number DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAttempt(reader) : null;
    }

    public IReadOnlyList<InvestigationAttempt> ListAttempts(Guid runId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = AttemptColumns + """

            WHERE run_id=$run ORDER BY attempt_number;
            """;
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        var list = new List<InvestigationAttempt>();
        while (reader.Read()) list.Add(ReadAttempt(reader));
        return list;
    }

    public IReadOnlyList<InvestigationEvent> ListEvents(Guid workspace, Guid repository,
        Guid snapshot, Guid runId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT e.event_id,e.attempt_id,a.run_id,e.sequence_number,
                   e.event_type,e.diagnostic,e.from_status,e.to_status,
                   e.occurred_at_unix_ms,e.stage,e.ownership_epoch,e.checkpoint_id
            FROM investigation_events e
            JOIN investigation_attempts a ON a.attempt_id=e.attempt_id
            JOIN investigation_reports r ON r.run_id=a.run_id
            WHERE r.workspace_id=$workspace AND r.repository_id=$repository
              AND r.snapshot_id=$snapshot AND r.run_id=$run
            ORDER BY a.attempt_number,e.sequence_number;
            """;
        cmd.Parameters.AddWithValue("$workspace", workspace.ToString("D"));
        cmd.Parameters.AddWithValue("$repository", repository.ToString("D"));
        cmd.Parameters.AddWithValue("$snapshot", snapshot.ToString("D"));
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        var events = new List<InvestigationEvent>();
        while (reader.Read())
            events.Add(new(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)), reader.GetInt64(3),
                reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        return events;
    }

    /// <summary>
    /// Claims the oldest queued attempt that THIS owner admitted, moving it to
    /// running with its own bounded deadline. Attempts admitted under any
    /// other epoch are never claimed; startup reconciliation handles them.
    /// </summary>
    public ClaimedAttempt? ClaimNextQueuedAttempt(ExecutionFence fence, TimeSpan deadline)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        RequireOwner(db, tx, fence);
        Guid attemptId;
        using (var find = Command(db, tx, """
            SELECT a.attempt_id FROM investigation_attempts a
            JOIN investigation_execution_slot s ON s.active_attempt_id = a.attempt_id
            WHERE a.status='queued' AND a.execution_owner_id=$owner
              AND a.ownership_epoch=$epoch AND a.contract_version=$version
            ORDER BY a.created_at_unix_ms LIMIT 1;
            """))
        {
            find.Parameters.AddWithValue("$owner", fence.OwnerId.ToString("D"));
            find.Parameters.AddWithValue("$epoch", fence.Epoch);
            find.Parameters.AddWithValue("$version", GateC6ExecutionContract.AttemptVersion);
            if (find.ExecuteScalar() is not string id) return null;
            attemptId = Guid.Parse(id);
        }
        var deadlineAt = DateTimeOffset.UtcNow + deadline;
        TransitionAttempt(db, tx, attemptId, "queued", "running",
            "host-execution-started", "attempt-started", fence.Epoch,
            deadlineUnixMs: deadlineAt.ToUnixTimeMilliseconds());
        var attempt = LoadAttempt(db, tx, attemptId)!;
        var run = LoadRun(db, tx, attempt.RunId)
            ?? throw new InvalidOperationException("Attempt run is missing.");
        tx.Commit();
        return new(attempt, run, deadlineAt);
    }

    /// <summary>Fenced stage-level host event for an active attempt.</summary>
    public void AppendStageEvent(ExecutionFence fence, Guid attemptId,
        string eventType, string stage, string diagnostic)
    {
        if (eventType is not ("role-started" or "role-assessment-accepted" or
            "role-assessment-rejected" or "role-failed" or "role-timed-out" or
            "role-cancelled" or "evidence-verification-started" or
            "evidence-verification-accepted" or "evidence-verification-rejected"))
            throw new ArgumentException("Unsupported stage event.", nameof(eventType));
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        RequireActiveAttemptOwner(db, tx, fence, attemptId, "running");
        AppendEvent(db, tx, attemptId, eventType, diagnostic, null, null,
            fence.Epoch, stage);
        tx.Commit();
    }

    private static void ReleaseSlot(SqliteConnection db, SqliteTransaction tx, Guid attemptId)
    {
        using var cmd = Command(db, tx, """
            UPDATE investigation_execution_slot SET active_attempt_id=NULL
            WHERE slot_id=1 AND active_attempt_id=$id;
            """);
        cmd.Parameters.AddWithValue("$id", attemptId.ToString("D"));
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Execution slot owner mismatch.");
    }

    private static string TerminalEventType(string to) => to switch
    {
        "completed" => "attempt-completed",
        "cancelled" => "attempt-cancelled",
        "rejected" => "attempt-rejected",
        "failed" => "attempt-failed",
        "interrupted" => "attempt-interrupted",
        _ => "attempt-incomplete"
    };

    // Invoked inside the same transaction that writes the report row.
    private static void FinalizeAttempt(SqliteConnection db, SqliteTransaction tx,
        ExecutionFence fence, Guid attemptId, string to, string diagnostic, string cause)
    {
        RequireActiveAttemptOwner(db, tx, fence, attemptId, "running");
        TransitionAttempt(db, tx, attemptId, "running", to, diagnostic,
            TerminalEventType(to), fence.Epoch, cause);
        ReleaseSlot(db, tx, attemptId);
    }
}

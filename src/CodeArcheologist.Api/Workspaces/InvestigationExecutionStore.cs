using System.Text.Json;
using CodeArcheologist.Api.Investigations;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

public static class GateC6ExecutionContract
{
    public const string AttemptVersion = "gate-c6-execution-attempt-v1";
    public const string EventVersion = "gate-c6-investigation-event-v1";
}

public sealed record InvestigationAttempt(
    Guid AttemptId, Guid RunId, int Number, string Status,
    string Diagnostic, long StateVersion, long LastEventSequence);

public sealed record InvestigationEvent(
    Guid EventId, Guid AttemptId, Guid RunId, long SequenceNumber,
    string EventType, string Diagnostic, string? FromStatus, string? ToStatus,
    DateTimeOffset OccurredAtUtc);

public static class GateC6AttemptTransitions
{
    public static bool Allowed(string from, string to) => from switch
    {
        "queued" => to is "running" or "cancelled" or "rejected",
        "running" => to is "completed" or "incomplete" or "interrupted"
            or "cancelled" or "rejected" or "failed",
        _ => false
    };

    public static bool IsTerminal(string state) => state is
        "completed" or "incomplete" or "interrupted" or "cancelled"
        or "rejected" or "failed";
}

/// <summary>
/// Database-owned admission and event history. C6a keeps the existing local
/// C5 executor, but admission and terminalization are durably paired with events.
/// No role checkpoints or automatic continuation are performed until C6b.
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
        string? from, string? to)
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
                occurred_at_unix_ms, event_type, diagnostic,
                from_status, to_status, payload_json)
            VALUES ($event,$attempt,$seq,$version,$at,$type,$diagnostic,
                    $from,$to,'{}');
            """);
        insert.Parameters.AddWithValue("$event", Guid.NewGuid().ToString("D"));
        insert.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
        insert.Parameters.AddWithValue("$seq", Convert.ToInt64(sequence));
        insert.Parameters.AddWithValue("$version", GateC6ExecutionContract.EventVersion);
        insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$type", eventType);
        insert.Parameters.AddWithValue("$diagnostic", diagnostic);
        insert.Parameters.AddWithValue("$from", (object?)from ?? DBNull.Value);
        insert.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);
        if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Event not persisted.");
    }

    private static void TransitionAttempt(SqliteConnection db, SqliteTransaction tx,
        Guid attemptId, string from, string to, string diagnostic, string eventType)
    {
        if (!GateC6AttemptTransitions.Allowed(from, to))
            throw new InvalidOperationException("Illegal execution transition.");
        using var cmd = Command(db, tx, """
            UPDATE investigation_attempts
               SET status=$next, diagnostic=$diagnostic,
                   state_version=state_version+1,
                   started_at_unix_ms=CASE WHEN $next='running'
                       THEN $now ELSE started_at_unix_ms END,
                   finished_at_unix_ms=CASE WHEN $terminal=1
                       THEN $now ELSE finished_at_unix_ms END
             WHERE attempt_id=$id AND status=$current;
            """);
        cmd.Parameters.AddWithValue("$id", attemptId.ToString("D"));
        cmd.Parameters.AddWithValue("$current", from);
        cmd.Parameters.AddWithValue("$next", to);
        cmd.Parameters.AddWithValue("$diagnostic", diagnostic);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$terminal", GateC6AttemptTransitions.IsTerminal(to) ? 1 : 0);
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Stale execution transition.");
        AppendEvent(db, tx, attemptId, eventType, diagnostic, from, to);
    }

    private static (Guid Id, string Status)? FindAttempt(SqliteConnection db,
        SqliteTransaction tx, Guid runId)
    {
        using var cmd = Command(db, tx, """
            SELECT attempt_id, status FROM investigation_attempts
            WHERE run_id=$run ORDER BY attempt_number DESC LIMIT 1;
            """);
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (Guid.Parse(reader.GetString(0)), reader.GetString(1)) : null;
    }

    public InvestigationAttempt? GetAttempt(Guid runId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT attempt_id, run_id, attempt_number, status, diagnostic,
                   state_version, last_event_sequence
            FROM investigation_attempts WHERE run_id=$run
            ORDER BY attempt_number DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(
            Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
            reader.GetInt64(5), reader.GetInt64(6)) : null;
    }

    public IReadOnlyList<InvestigationEvent> ListEvents(Guid workspace, Guid repository,
        Guid snapshot, Guid runId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT e.event_id,e.attempt_id,a.run_id,e.sequence_number,
                   e.event_type,e.diagnostic,e.from_status,e.to_status,
                   e.occurred_at_unix_ms
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
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8))));
        return events;
    }

    public void StartAttempt(Guid runId)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var attempt = FindAttempt(db, tx, runId)
            ?? throw new InvalidOperationException("Missing attempt.");
        TransitionAttempt(db, tx, attempt.Id, "queued", "running",
            "host-execution-started", "attempt-started");
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

    // Invoked inside the same transaction that writes the existing report.
    private static void FinalizeAttempt(SqliteConnection db, SqliteTransaction tx,
        Guid runId, string to, string diagnostic)
    {
        var attempt = FindAttempt(db, tx, runId)
            ?? throw new InvalidOperationException("Missing execution attempt.");
        var eventType = to switch
        {
            "completed" => "attempt-completed",
            "cancelled" => "attempt-cancelled",
            "rejected" => "attempt-rejected",
            "failed" => "attempt-failed",
            _ => "attempt-incomplete"
        };
        TransitionAttempt(db, tx, attempt.Id, "running", to, diagnostic, eventType);
        ReleaseSlot(db, tx, attempt.Id);
    }

    private static void RecoverC6Attempts(SqliteConnection db, SqliteTransaction tx)
    {
        using var get = Command(db, tx, """
            SELECT attempt_id, run_id, status
            FROM investigation_attempts WHERE status IN ('queued','running');
            """);
        var interrupted = new List<(Guid Attempt, Guid Run, string Status)>();
        using (var reader = get.ExecuteReader())
            while (reader.Read())
                interrupted.Add((Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)), reader.GetString(2)));
        foreach (var (attempt, run, status) in interrupted)
        {
            // C6a has no checkpoint continuation; interrupted attempts never resume.
            // Queued work was not started, so it is rejected rather than invented as running.
            var next = status == "queued" ? "rejected" : "interrupted";
            var diagnostic = "host-restarted-before-completion";
            if (status == "queued")
                TransitionAttempt(db, tx, attempt, "queued", next, diagnostic, "attempt-rejected");
            else
                TransitionAttempt(db, tx, attempt, "running", next, diagnostic, "attempt-interrupted");
            using var report = Command(db, tx, """
                UPDATE investigation_reports
                SET status='incomplete', diagnostic=$diagnostic,
                    finished_at_unix_ms=$now
                WHERE run_id=$run AND status='running';
                """);
            report.Parameters.AddWithValue("$run", run.ToString("D"));
            report.Parameters.AddWithValue("$diagnostic", diagnostic);
            report.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (report.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Restart report mismatch.");
            ReleaseSlot(db, tx, attempt);
        }
    }
}

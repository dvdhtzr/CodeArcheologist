using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeArcheologist.Api.Investigations;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

public sealed record InvestigationSummary(
    Guid RunId, GateCScope Scope, string ReportVersion,
    string Status, string Diagnostic,
    DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc);

public sealed record InvestigationDetail(InvestigationSummary Summary,
    GateC5Report? Report);

public sealed record InvestigationPage(IReadOnlyList<InvestigationSummary> Items,
    long TotalCount, bool HasMore);

public sealed record InvestigationCreateResult(InvestigationSummary? Record,
    bool Created, bool Conflict, bool Busy = false);

public sealed partial class WorkspaceStore
{
    /// <summary>
    /// Transactional admission. Only the current fenced owner admits new work,
    /// so an admitted attempt is always bound to a live lock-holding worker.
    /// </summary>
    public InvestigationCreateResult CreateInvestigation(
        GateCScope scope, Guid idempotencyKey, ExecutionFence fence,
        string? questionSubjectKind = null)
    {
        // Legacy calls retain their original idempotency digest and checkpoint
        // semantics. New structured questions include the host-resolved node
        // kind and all versioned policy identities in their request digest.
        var digest = questionSubjectKind is null
            ? Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join("\n",
                    "gate-c5-request-v1", scope.WorkspaceId.ToString("D"),
                    scope.RepositoryId.ToString("D"), scope.SnapshotId.ToString("D"),
                    scope.FingerprintSha256, scope.EntryPointId,
                    scope.EvidenceContractVersion))))
            : InvestigationQuestionContract.RequestDigest(scope, questionSubjectKind);
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        using var connection = Open();
        using var tx = connection.BeginTransaction(deferred: false);

        // An identical replay wins even when another investigation owns the slot.
        using (var lookup = Command(connection, tx, SummaryColumns +
            "\n WHERE idempotency_key=$key;"))
        {
            lookup.Parameters.AddWithValue("$key", idempotencyKey.ToString("D"));
            using var reader = lookup.ExecuteReader();
            if (reader.Read())
            {
                var existing = ReadInvestigation(reader);
                var conflict = !string.Equals(reader.GetString(12), digest,
                    StringComparison.Ordinal);
                return new(existing, false, conflict);
            }
        }

        RequireOwner(connection, tx, fence);
        // Immediate SQLite write transaction serializes concurrent admissions.
        // Check ownership before creating any rows; actual reservation follows
        // the attempt insert to satisfy the foreign key.
        using (var slot = Command(connection, tx, """
            SELECT active_attempt_id FROM investigation_execution_slot WHERE slot_id=1;
            """))
        {
            if (slot.ExecuteScalar() is string)
            {
                return new(null, false, false, true);
            }
        }

        using (var insert = Command(connection, tx, """
            INSERT INTO investigation_reports (
                run_id, workspace_id, repository_id, snapshot_id,
                fingerprint_sha256, entry_point_id, evidence_contract_version,
                report_contract_version, started_at_unix_ms, status, diagnostic,
                idempotency_key, request_digest)
            VALUES ($id,$workspace,$repo,$snapshot,$fingerprint,$entry,$evidence,
                $version,$started,'running','queued',$key,$digest);
            """))
        {
            insert.Parameters.AddWithValue("$id", id.ToString("D"));
            insert.Parameters.AddWithValue("$workspace", scope.WorkspaceId.ToString("D"));
            insert.Parameters.AddWithValue("$repo", scope.RepositoryId.ToString("D"));
            insert.Parameters.AddWithValue("$snapshot", scope.SnapshotId.ToString("D"));
            insert.Parameters.AddWithValue("$fingerprint", scope.FingerprintSha256);
            insert.Parameters.AddWithValue("$entry", scope.EntryPointId);
            insert.Parameters.AddWithValue("$evidence", scope.EvidenceContractVersion);
            insert.Parameters.AddWithValue("$version", GateC5ReportContract.Version);
            insert.Parameters.AddWithValue("$started", now.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$key", idempotencyKey.ToString("D"));
            insert.Parameters.AddWithValue("$digest", digest);
            if (insert.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Admission report was not persisted.");
        }
        using (var insert = Command(connection, tx, """
            INSERT INTO investigation_attempts (
                attempt_id, run_id, attempt_number, contract_version,
                status, diagnostic, created_at_unix_ms,
                execution_owner_id, ownership_epoch)
            VALUES ($id,$run,1,$version,'queued','queued',$created,$owner,$epoch);
            """))
        {
            insert.Parameters.AddWithValue("$owner", fence.OwnerId.ToString("D"));
            insert.Parameters.AddWithValue("$epoch", fence.Epoch);
            insert.Parameters.AddWithValue("$id", attemptId.ToString("D"));
            insert.Parameters.AddWithValue("$run", id.ToString("D"));
            insert.Parameters.AddWithValue("$version", GateC6ExecutionContract.AttemptVersion);
            insert.Parameters.AddWithValue("$created", now.ToUnixTimeMilliseconds());
            if (insert.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Admission attempt was not persisted.");
        }
        using (var reserve = Command(connection, tx, """
            UPDATE investigation_execution_slot SET active_attempt_id=$attempt
            WHERE slot_id=1 AND active_attempt_id IS NULL;
            """))
        {
            reserve.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
            if (reserve.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Execution slot acquisition failed.");
        }
        if (questionSubjectKind is not null)
        {
            var context = InvestigationQuestionContract.Create(id, scope, questionSubjectKind, now);
            using var insertContext = Command(connection, tx, """
                INSERT INTO investigation_question_contexts
                    (run_id, question_id, contract_version, subject_node_id,
                     subject_node_kind, question_kind, context_json, context_digest,
                     created_at_unix_ms)
                VALUES ($run,$question,$version,$node,$kind,$questionKind,$json,$digest,$created);
                """);
            insertContext.Parameters.AddWithValue("$run", id.ToString("D"));
            insertContext.Parameters.AddWithValue("$question", context.QuestionId.ToString("D"));
            insertContext.Parameters.AddWithValue("$version", context.ContractVersion);
            insertContext.Parameters.AddWithValue("$node", context.SubjectNodeId);
            insertContext.Parameters.AddWithValue("$kind", context.SubjectNodeKind);
            insertContext.Parameters.AddWithValue("$questionKind", context.QuestionKind);
            insertContext.Parameters.AddWithValue("$json", JsonSerializer.Serialize(context));
            insertContext.Parameters.AddWithValue("$digest", context.ContextDigest);
            insertContext.Parameters.AddWithValue("$created", context.CreatedAtUtc.ToUnixTimeMilliseconds());
            if (insertContext.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Question context was not persisted with its run.");
        }
        AppendEvent(connection, tx, attemptId, "attempt-admitted", "queued",
            null, "queued", fence.Epoch);
        tx.Commit();
        return new(new InvestigationSummary(id, scope, GateC5ReportContract.Version,
            "running", "queued", now, null), true, false);
    }

    /// <summary>
    /// Single fenced terminal write: report row, attempt state, terminal event
    /// and slot release commit together or not at all.
    /// </summary>
    public void FinishInvestigation(ExecutionFence fence, Guid attemptId,
        Guid runId, GateC5Report report, string terminalCause)
    {
        RequireCause(terminalCause);
        if (report.RunId != runId || report.Version != GateC5ReportContract.Version ||
            report.Progress.RunId != runId ||
            report.Progress.Status is not (GateCRunStatus.Completed or GateCRunStatus.Incomplete or
                GateCRunStatus.Cancelled or GateCRunStatus.Rejected))
            throw new InvalidOperationException("Cannot persist nonterminal or foreign report.");
        using var connection = Open();
        using var tx = connection.BeginTransaction(deferred: false);
        // Fence first: a stale owner learns nothing about current record state.
        RequireOwner(connection, tx, fence);
        using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            UPDATE investigation_reports SET
                status=$status, diagnostic=$diagnostic, report_json=$report,
                finished_at_unix_ms=$finished
            WHERE run_id=$id AND status='running'
                AND workspace_id=$workspace AND repository_id=$repo
                AND snapshot_id=$snapshot AND fingerprint_sha256=$fingerprint
                AND entry_point_id=$entry AND evidence_contract_version=$evidence;
            """;
        update.Parameters.AddWithValue("$status",
            report.Progress.Status.ToString().ToLowerInvariant());
        update.Parameters.AddWithValue("$diagnostic", report.Diagnostic);
        update.Parameters.AddWithValue("$report", JsonSerializer.Serialize(report));
        update.Parameters.AddWithValue("$finished", report.FinishedAtUtc.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$id", runId.ToString("D"));
        update.Parameters.AddWithValue("$workspace", report.Scope.WorkspaceId.ToString("D"));
        update.Parameters.AddWithValue("$repo", report.Scope.RepositoryId.ToString("D"));
        update.Parameters.AddWithValue("$snapshot", report.Scope.SnapshotId.ToString("D"));
        update.Parameters.AddWithValue("$fingerprint", report.Scope.FingerprintSha256);
        update.Parameters.AddWithValue("$entry", report.Scope.EntryPointId);
        update.Parameters.AddWithValue("$evidence", report.Scope.EvidenceContractVersion);
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Investigation completion scope or state mismatch.");
        RequireAttemptOfRun(connection, tx, attemptId, runId);
        FinalizeAttempt(connection, tx, fence, attemptId,
            report.Progress.Status.ToString().ToLowerInvariant(), report.Diagnostic,
            terminalCause);
        tx.Commit();
    }

    public void FailInvestigation(ExecutionFence fence, Guid attemptId,
        Guid runId, string diagnostic, string terminalCause)
    {
        RequireCause(terminalCause);
        // Diagnostics are fixed host-controlled categories; not model text or exception messages.
        if (diagnostic is not ("host-execution-failure" or "host-cancelled" or
            "host-report-validation-failed" or "host-execution-rejected"))
            throw new ArgumentException("Unknown safe failure category.", nameof(diagnostic));
        using var connection = Open();
        using var tx = connection.BeginTransaction(deferred: false);
        // Fence first: a stale owner learns nothing about current record state.
        RequireOwner(connection, tx, fence);
        using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            UPDATE investigation_reports SET status=$status, diagnostic=$diagnostic,
                finished_at_unix_ms=$finished
            WHERE run_id=$id AND status='running';
            """;
        update.Parameters.AddWithValue("$status",
            diagnostic == "host-cancelled" ? "cancelled" : "incomplete");
        update.Parameters.AddWithValue("$diagnostic", diagnostic);
        update.Parameters.AddWithValue("$finished",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$id", runId.ToString("D"));
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Failure record scope or state mismatch.");
        var attemptStatus = diagnostic switch
        {
            "host-cancelled" => "cancelled",
            "host-execution-rejected" or "host-report-validation-failed" => "rejected",
            _ => "failed"
        };
        RequireAttemptOfRun(connection, tx, attemptId, runId);
        FinalizeAttempt(connection, tx, fence, attemptId, attemptStatus, diagnostic,
            terminalCause);
        tx.Commit();
    }

    /// <summary>
    /// The attempt ended but the RUN did not reach a terminal outcome: it
    /// becomes 'interrupted' (no report) and may later be continued only by an
    /// explicit user action. Accepted checkpoints are untouched. The attempt's
    /// own status is derived from the host-observed cause.
    /// </summary>
    public void InterruptAttempt(ExecutionFence fence, Guid attemptId, Guid runId,
        string terminalCause)
    {
        var attemptStatus = terminalCause switch
        {
            "application-shutdown" or "attempt-deadline" or "ownership-lost" => "interrupted",
            "role-timeout" => "incomplete",
            "provider-error" => "failed",
            "checkpoint-rejected" => "rejected",
            _ => throw new ArgumentException("Unsupported interruption cause.", nameof(terminalCause))
        };
        using var connection = Open();
        using var tx = connection.BeginTransaction(deferred: false);
        // Fence first: a stale owner learns nothing about current record state.
        RequireOwner(connection, tx, fence);
        RequireAttemptOfRun(connection, tx, attemptId, runId);
        using (var update = Command(connection, tx, """
            UPDATE investigation_reports SET status='interrupted',
                diagnostic='host-interrupted-awaiting-explicit-action'
            WHERE run_id=$id AND status='running';
            """))
        {
            update.Parameters.AddWithValue("$id", runId.ToString("D"));
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Interruption record scope or state mismatch.");
        }
        FinalizeAttempt(connection, tx, fence, attemptId, attemptStatus,
            "host-attempt-ended-run-recoverable", terminalCause);
        tx.Commit();
    }

    private static void RequireCause(string cause)
    {
        if (!TerminalCauses.Contains("'" + cause + "'", StringComparison.Ordinal))
            throw new ArgumentException("Unknown terminal cause.", nameof(cause));
    }

    private static void RequireAttemptOfRun(SqliteConnection db, SqliteTransaction tx,
        Guid attemptId, Guid runId)
    {
        var attempt = LoadAttempt(db, tx, attemptId);
        if (attempt is null || attempt.RunId != runId)
            throw new InvalidOperationException("Attempt does not belong to this run.");
    }

    private static InvestigationSummary? LoadRun(SqliteConnection db,
        SqliteTransaction tx, Guid runId)
    {
        using var cmd = Command(db, tx, SummaryColumns + "\n WHERE run_id=$run;");
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadInvestigation(reader) : null;
    }

    public InvestigationDetail? GetInvestigation(Guid workspace, Guid repository,
        Guid snapshot, Guid runId)
    {
        using var connection = Open();
        using var query = connection.CreateCommand();
        query.CommandText = SummaryColumns + "\n" + """
            WHERE workspace_id=$workspace AND repository_id=$repo
                AND snapshot_id=$snapshot AND run_id=$run;
            """;
        query.Parameters.AddWithValue("$workspace", workspace.ToString("D"));
        query.Parameters.AddWithValue("$repo", repository.ToString("D"));
        query.Parameters.AddWithValue("$snapshot", snapshot.ToString("D"));
        query.Parameters.AddWithValue("$run", runId.ToString("D"));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return null;
        var summary = ReadInvestigation(reader);
        GateC5Report? report = null;
        if (!reader.IsDBNull(13))
        {
            report = JsonSerializer.Deserialize<GateC5Report>(reader.GetString(13))
                ?? throw new InvalidOperationException("Stored report is invalid.");
            if (report.RunId != summary.RunId ||
                report.Scope != summary.Scope ||
                report.Version != summary.ReportVersion ||
                report.Progress.Status.ToString().ToLowerInvariant() != summary.Status)
                throw new InvalidOperationException("Stored report scope does not match its ledger.");
        }
        return new(summary, report);
    }

    /// <summary>Read the immutable question context of an R3 run, scoped to
    /// exactly one workspace/repository/snapshot. Older Gate C runs have none.</summary>
    public InvestigationQuestionContext? GetQuestionContext(Guid workspace,
        Guid repository, Guid snapshot, Guid runId)
    {
        using var connection = Open();
        using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT q.context_json, q.context_digest, q.contract_version,
                q.question_id, q.subject_node_id, q.subject_node_kind,
                q.question_kind, q.created_at_unix_ms,
                r.fingerprint_sha256, r.entry_point_id, r.evidence_contract_version
            FROM investigation_question_contexts q
            JOIN investigation_reports r ON r.run_id=q.run_id
            WHERE r.workspace_id=$workspace AND r.repository_id=$repo
                AND r.snapshot_id=$snapshot AND r.run_id=$run;
            """;
        query.Parameters.AddWithValue("$workspace", workspace.ToString("D"));
        query.Parameters.AddWithValue("$repo", repository.ToString("D"));
        query.Parameters.AddWithValue("$snapshot", snapshot.ToString("D"));
        query.Parameters.AddWithValue("$run", runId.ToString("D"));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return null;
        InvestigationQuestionContext? context;
        try { context = JsonSerializer.Deserialize<InvestigationQuestionContext>(reader.GetString(0)); }
        catch (JsonException) { throw new InvalidOperationException("Stored question context is invalid."); }
        var expectedScope = new GateCScope(workspace, repository, snapshot,
            reader.GetString(8), reader.GetString(9), reader.GetString(10));
        if (!InvestigationQuestionContract.Verify(context) || context!.RunId != runId ||
            context.Scope != expectedScope ||
            context.ContextDigest != reader.GetString(1) ||
            context.ContractVersion != reader.GetString(2) ||
            context.QuestionId.ToString("D") != reader.GetString(3) ||
            context.SubjectNodeId != reader.GetString(4) ||
            context.SubjectNodeKind != reader.GetString(5) ||
            context.QuestionKind != reader.GetString(6) ||
            context.CreatedAtUtc.ToUnixTimeMilliseconds() != reader.GetInt64(7))
            throw new InvalidOperationException("Stored question context provenance is invalid.");
        return context;
    }

    public InvestigationPage ListInvestigations(Guid workspace, Guid repository,
        Guid snapshot, int limit = 50)
    {
        using var connection = Open();
        using var query = connection.CreateCommand();
        query.CommandText = SummaryColumns + "\n" + """
            WHERE workspace_id=$workspace AND repository_id=$repo
                AND snapshot_id=$snapshot
            ORDER BY started_at_unix_ms DESC, run_id DESC LIMIT $limit;
            """;
        query.Parameters.AddWithValue("$workspace", workspace.ToString("D"));
        query.Parameters.AddWithValue("$repo", repository.ToString("D"));
        query.Parameters.AddWithValue("$snapshot", snapshot.ToString("D"));
        query.Parameters.AddWithValue("$limit", limit);
        var items = new List<InvestigationSummary>();
        using (var reader = query.ExecuteReader())
            while (reader.Read()) items.Add(ReadInvestigation(reader));

        using var count = connection.CreateCommand();
        count.CommandText = """
            SELECT COUNT(*) FROM investigation_reports WHERE workspace_id=$workspace
                AND repository_id=$repo AND snapshot_id=$snapshot;
            """;
        count.Parameters.AddWithValue("$workspace", workspace.ToString("D"));
        count.Parameters.AddWithValue("$repo", repository.ToString("D"));
        count.Parameters.AddWithValue("$snapshot", snapshot.ToString("D"));
        var total = Convert.ToInt64(count.ExecuteScalar());
        return new(items, total, total > items.Count);
    }

    private const string SummaryColumns = """
        SELECT run_id, workspace_id, repository_id, snapshot_id, fingerprint_sha256,
            entry_point_id, evidence_contract_version, report_contract_version,
            started_at_unix_ms, finished_at_unix_ms, status, diagnostic,
            request_digest, report_json
        FROM investigation_reports
        """;

    private static InvestigationSummary ReadInvestigation(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        new GateCScope(Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)), reader.GetString(4),
            reader.GetString(5), reader.GetString(6)),
        reader.GetString(7), reader.GetString(10), reader.GetString(11),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
        reader.IsDBNull(9) ? null :
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)));
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeArcheologist.Api.Investigations;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

/// <summary>
/// Host-only opt-in V2 prompt-boundary event. Digests detect corruption,
/// not a malicious SQLite administrator. No prompt or raw source is persisted.
/// </summary>
public sealed record V2HandoffEvent(
    Guid EventId, Guid CallId, Guid RunId, Guid AttemptId, string Role,
    int CallOrdinal, string Stage, string EventKind, string PromptSha256,
    string EvidenceLinksJson, string ReceiptIdsJson, string SnapshotFingerprint,
    long OwnershipEpoch, long CreatedAtUnixMs, string ContentSha256)
{
    public string ComputeDigest() => WorkspaceStore.AdaptiveLedgerDigest(
        "v2-model-handoff-event-v1",
        EventId.ToString("D"), CallId.ToString("D"), RunId.ToString("D"),
        AttemptId.ToString("D"), Role, CallOrdinal.ToString(
            System.Globalization.CultureInfo.InvariantCulture),
        Stage, EventKind, PromptSha256, EvidenceLinksJson, ReceiptIdsJson,
        SnapshotFingerprint, OwnershipEpoch.ToString(
            System.Globalization.CultureInfo.InvariantCulture),
        CreatedAtUnixMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public static class V2HandoffContract
{
    public const string Version = "v2-model-handoff-observation-v1";
    public const string Prepared = "prepared";
    public const string Entered = "adapter-entered";
    public const string Completed = "completed";
    public const string Faulted = "faulted";
    public const int MaxCallsPerRole = 12;
}

/// <summary>
/// Fenced, append-only V2 diagnostic observation. The historical R3 receipt
/// chain remains sole authority for adaptive host evidence delivery.
/// </summary>
public sealed partial class WorkspaceStore
{
    /// <summary>
    /// Explicit opt-in before a queued attempt is claimed, under current
    /// OS+SQLite ownership. There is deliberately no public HTTP command.
    /// </summary>
    public void EnableV2Handoff(Guid runId, ExecutionFence fence)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run id required.");
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        RequireOwner(db, tx, fence);
        if (LoadRun(db, tx, runId) is null)
            throw new InvalidOperationException("V2 observation requires an admitted run.");
        using (var count = Command(db, tx, """
            SELECT COUNT(*) FROM investigation_attempts
            WHERE run_id=$run AND status='queued';
            """))
        {
            count.Parameters.AddWithValue("$run", runId.ToString("D"));
            if (Convert.ToInt64(count.ExecuteScalar()) != 1)
                throw new InvalidOperationException("V2 observation must be enabled before claim.");
        }
        using var cmd = Command(db, tx, """
            INSERT INTO v2_handoff_optins(run_id,contract_version,created_at_unix_ms)
            VALUES($run,$version,$at);
            """);
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        cmd.Parameters.AddWithValue("$version", V2HandoffContract.Version);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("V2 observation opt-in was not committed.");
        tx.Commit();
    }

    public bool IsV2HandoffEnabled(Guid runId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM v2_handoff_optins
            WHERE run_id=$run AND contract_version=$contract;
            """;
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        cmd.Parameters.AddWithValue("$contract", V2HandoffContract.Version);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    public V2HandoffEvent RecordV2Handoff(
        ExecutionFence fence, Guid attemptId, Guid runId, GateCRole role,
        Guid callId, int ordinal, string stage, string eventKind,
        string promptSha256, string evidenceLinksJson, string receiptIdsJson,
        string fingerprint)
    {
        if (callId == Guid.Empty || attemptId == Guid.Empty || runId == Guid.Empty ||
            ordinal is < 1 or > V2HandoffContract.MaxCallsPerRole ||
            role is not (GateCRole.Investigator or GateCRole.Skeptic or GateCRole.Reviewer) ||
            stage.Length is < 1 or > 80 ||
            eventKind is not (V2HandoffContract.Prepared or V2HandoffContract.Entered or
                V2HandoffContract.Completed or V2HandoffContract.Faulted) ||
            !HexDigest(promptSha256) || !HexDigest(fingerprint) ||
            string.IsNullOrEmpty(evidenceLinksJson) || evidenceLinksJson.Length > 65536 ||
            string.IsNullOrEmpty(receiptIdsJson) || receiptIdsJson.Length > 4096)
            throw new ArgumentException("Invalid V2 handoff event input.");

        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var attempt = RequireActiveAttemptOwner(db, tx, fence, attemptId, "running");
        var run = LoadRun(db, tx, attempt.RunId);
        if (attempt.RunId != runId || run is null ||
            run.Scope.FingerprintSha256 != fingerprint)
            throw new InvalidOperationException("V2 handoff foreign run or fingerprint.");
        using (var enabled = Command(db, tx, """
            SELECT COUNT(*) FROM v2_handoff_optins
            WHERE run_id=$run AND contract_version=$contract;
            """))
        {
            enabled.Parameters.AddWithValue("$run", runId.ToString("D"));
            enabled.Parameters.AddWithValue("$contract", V2HandoffContract.Version);
            if (Convert.ToInt64(enabled.ExecuteScalar()) != 1)
                throw new InvalidOperationException("Run is not opted into V2 observation.");
        }
        var roleName = GateC6CheckpointContract.RoleName(role);
        if (role == GateCRole.Reviewer &&
            (evidenceLinksJson != "[]" || receiptIdsJson != "[]"))
            throw new InvalidOperationException("Reviewer may not claim direct source receipts.");
        if (eventKind == V2HandoffContract.Prepared)
        {
            using var count = Command(db, tx, """
                SELECT COUNT(*) FROM v2_handoff_events
                WHERE attempt_id=$attempt AND role=$role AND event_kind='prepared';
                """);
            count.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
            count.Parameters.AddWithValue("$role", roleName);
            if (Convert.ToInt64(count.ExecuteScalar()) + 1 != ordinal)
                throw new InvalidOperationException("Out-of-order V2 model handoff call.");
        }
        else
        {
            using var prior = Command(db, tx, """
                SELECT event_kind,prompt_sha256,evidence_links_json,receipt_ids_json,
                       snapshot_fingerprint,stage,call_id,call_ordinal
                FROM v2_handoff_events
                WHERE attempt_id=$attempt AND role=$role AND call_id=$call
                ORDER BY created_at_unix_ms,event_kind;
                """);
            prior.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
            prior.Parameters.AddWithValue("$role", roleName);
            prior.Parameters.AddWithValue("$call", callId.ToString("D"));
            using var reader = prior.ExecuteReader();
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                kinds.Add(reader.GetString(0));
                if (reader.GetString(1) != promptSha256 ||
                    reader.GetString(2) != evidenceLinksJson ||
                    reader.GetString(3) != receiptIdsJson ||
                    reader.GetString(4) != fingerprint ||
                    reader.GetString(5) != stage ||
                    reader.GetString(6) != callId.ToString("D") ||
                    reader.GetInt32(7) != ordinal)
                    throw new InvalidOperationException("V2 handoff call identity changed.");
            }
            if (!kinds.Contains(V2HandoffContract.Prepared) ||
                (eventKind == V2HandoffContract.Entered &&
                    kinds.Contains(V2HandoffContract.Entered)) ||
                (eventKind is V2HandoffContract.Completed or V2HandoffContract.Faulted &&
                    !kinds.Contains(V2HandoffContract.Entered)) ||
                kinds.Contains(V2HandoffContract.Completed) ||
                kinds.Contains(V2HandoffContract.Faulted))
                throw new InvalidOperationException("Illegal V2 handoff lifecycle transition.");
        }

        // A prior adaptive receipt is the ONLY authority for model evidence.
        // This observational event carries IDs, never a replacement receipt.
        var receiptIds = JsonSerializer.Deserialize<Guid[]>(receiptIdsJson)
            ?? throw new InvalidOperationException("V2 receipt IDs JSON is null.");
        var links = JsonSerializer.Deserialize<GateC5EvidenceLink[]>(evidenceLinksJson)
            ?? throw new InvalidOperationException("V2 evidence mapping JSON is null.");
        if (receiptIds.Length > GateCEvidenceSession.MaxToolCalls ||
            links.Length > GateCEvidenceSession.MaxTotalItems ||
            links.Any(l => l is null || l.Role != role))
            throw new InvalidOperationException("V2 handoff evidence bounds invalid.");
        using (var profile = Command(db, tx, """
            SELECT COUNT(*) FROM adaptive_question_contexts WHERE run_id=$run;
            """))
        {
            profile.Parameters.AddWithValue("$run", runId.ToString("D"));
            var adaptive = Convert.ToInt64(profile.ExecuteScalar()) == 1;
            if (!adaptive && receiptIds.Length != 0)
                throw new InvalidOperationException("Fixed Gate C cannot claim adaptive receipts.");
            if (adaptive && role != GateCRole.Reviewer)
            {
                var committedIds = new List<Guid>();
                string? latestLinks = null;
                using var receipts = Command(db, tx, """
                    SELECT receipt_id,evidence_links_json FROM adaptive_evidence_receipts
                    WHERE run_id=$run AND attempt_id=$attempt AND role=$role
                    ORDER BY ordinal;
                    """);
                receipts.Parameters.AddWithValue("$run", runId.ToString("D"));
                receipts.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
                receipts.Parameters.AddWithValue("$role", roleName);
                using var rows = receipts.ExecuteReader();
                while (rows.Read())
                {
                    committedIds.Add(Guid.Parse(rows.GetString(0)));
                    latestLinks = rows.GetString(1);
                }
                if (!committedIds.SequenceEqual(receiptIds) ||
                    (receiptIds.Length > 0 && latestLinks != evidenceLinksJson))
                    throw new InvalidOperationException("V2 handoff lacks exact committed adaptive receipts.");
            }
        }
        var item = new V2HandoffEvent(Guid.NewGuid(), callId, runId, attemptId,
            roleName, ordinal, stage, eventKind, promptSha256, evidenceLinksJson,
            receiptIdsJson, fingerprint, fence.Epoch,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "");
        item = item with { ContentSha256 = item.ComputeDigest() };
        using var insert = Command(db, tx, """
            INSERT INTO v2_handoff_events(event_id,call_id,run_id,attempt_id,role,
                call_ordinal,stage,event_kind,prompt_sha256,evidence_links_json,
                receipt_ids_json,snapshot_fingerprint,ownership_epoch,
                created_at_unix_ms,content_sha256)
            VALUES($event,$call,$run,$attempt,$role,$ordinal,$stage,$kind,$prompt,
                $links,$receipts,$fingerprint,$epoch,$time,$hash);
            """);
        insert.Parameters.AddWithValue("$event", item.EventId.ToString("D"));
        insert.Parameters.AddWithValue("$call", item.CallId.ToString("D"));
        insert.Parameters.AddWithValue("$run", item.RunId.ToString("D"));
        insert.Parameters.AddWithValue("$attempt", item.AttemptId.ToString("D"));
        insert.Parameters.AddWithValue("$role", item.Role);
        insert.Parameters.AddWithValue("$ordinal", item.CallOrdinal);
        insert.Parameters.AddWithValue("$stage", item.Stage);
        insert.Parameters.AddWithValue("$kind", item.EventKind);
        insert.Parameters.AddWithValue("$prompt", item.PromptSha256);
        insert.Parameters.AddWithValue("$links", item.EvidenceLinksJson);
        insert.Parameters.AddWithValue("$receipts", item.ReceiptIdsJson);
        insert.Parameters.AddWithValue("$fingerprint", item.SnapshotFingerprint);
        insert.Parameters.AddWithValue("$epoch", item.OwnershipEpoch);
        insert.Parameters.AddWithValue("$time", item.CreatedAtUnixMs);
        insert.Parameters.AddWithValue("$hash", item.ContentSha256);
        if (insert.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("V2 handoff event not persisted.");
        tx.Commit();
        return item;
    }

    public IReadOnlyList<V2HandoffEvent> ListV2HandoffEvents(Guid runId, Guid attemptId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT event_id,call_id,run_id,attempt_id,role,call_ordinal,stage,
                   event_kind,prompt_sha256,evidence_links_json,receipt_ids_json,
                   snapshot_fingerprint,ownership_epoch,created_at_unix_ms,content_sha256
            FROM v2_handoff_events WHERE run_id=$run AND attempt_id=$attempt
            ORDER BY role,call_ordinal,
                CASE event_kind WHEN 'prepared' THEN 1
                    WHEN 'adapter-entered' THEN 2 ELSE 3 END;
            """;
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        cmd.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        var rows = new List<V2HandoffEvent>();
        while (reader.Read())
        {
            var item = new V2HandoffEvent(Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                Guid.Parse(reader.GetString(3)), reader.GetString(4), reader.GetInt32(5),
                reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.GetString(9), reader.GetString(10), reader.GetString(11),
                reader.GetInt64(12), reader.GetInt64(13), reader.GetString(14));
            if (item.RunId != runId || item.AttemptId != attemptId ||
                item.ComputeDigest() != item.ContentSha256)
                throw new InvalidOperationException("Invalid V2 handoff event digest/scope.");
            rows.Add(item);
        }
        foreach (var grp in rows.GroupBy(x => (x.Role, x.CallOrdinal)))
        {
            var events = grp.ToArray();
            var kinds = events.Select(x => x.EventKind).ToArray();
            if (events.Select(x => x.CallId).Distinct().Count() != 1 ||
                kinds.Length is < 1 or > 3 ||
                kinds[0] != V2HandoffContract.Prepared ||
                (kinds.Length >= 2 && kinds[1] != V2HandoffContract.Entered) ||
                (kinds.Length == 3 && kinds[2] is not
                    (V2HandoffContract.Completed or V2HandoffContract.Faulted)) ||
                events.Any(x => x.PromptSha256 != events[0].PromptSha256 ||
                    x.EvidenceLinksJson != events[0].EvidenceLinksJson ||
                    x.ReceiptIdsJson != events[0].ReceiptIdsJson ||
                    x.SnapshotFingerprint != events[0].SnapshotFingerprint))
                throw new InvalidOperationException("V2 handoff lifecycle invalid.");
        }
        return rows;
    }

    private static bool HexDigest(string value) =>
        value.Length == 64 && value.All(c => char.IsAsciiHexDigitLower(c));
}

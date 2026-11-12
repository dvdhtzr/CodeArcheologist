using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeArcheologist.Api.Investigations;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.Api.Workspaces;

/// <summary>Host-written, append-only evidence receipt. The digest detects
/// accidental alteration; it is not a MAC against a SQLite administrator.</summary>
public sealed record AdaptiveEvidenceReceipt(Guid ReceiptId, Guid RunId, Guid AttemptId,
    GateCRole Role, int Ordinal, string Phase, string Capability, string TargetNodeId,
    Guid? GapId, string? GapKind, string Purpose, string RequestJson,
    string ResponseJson, string ResponseSha256, string EvidenceLinksJson,
    string EvidenceLinksSha256, int NewItemCount, long OwnershipEpoch,
    long CreatedAtUnixMs, string ContentSha256)
{
    public string ComputeDigest() => WorkspaceStore.AdaptiveLedgerDigest(
        "r3-adaptive-evidence-receipt-v1", ReceiptId.ToString("D"), RunId.ToString("D"),
        AttemptId.ToString("D"), Role.ToString(), Ordinal.ToString(CultureInfo.InvariantCulture),
        Phase, Capability, TargetNodeId, GapId?.ToString("D") ?? "",
        GapKind ?? "", Purpose, RequestJson, ResponseJson, ResponseSha256,
        EvidenceLinksJson, EvidenceLinksSha256,
        NewItemCount.ToString(CultureInfo.InvariantCulture),
        OwnershipEpoch.ToString(CultureInfo.InvariantCulture),
        CreatedAtUnixMs.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Host-owned, typed gap event. No model text is treated as authority.</summary>
public sealed record AdaptiveGapEvent(Guid EventId, Guid RunId, Guid AttemptId,
    GateCRole Role, Guid GapId, int Ordinal, string EventKind,
    string GapKind, string Purpose, string? Capability, Guid? ReceiptId,
    string Diagnostic, long OwnershipEpoch, long CreatedAtUnixMs,
    string ContentSha256)
{
    public string ComputeDigest() => WorkspaceStore.AdaptiveLedgerDigest(
        "r3-adaptive-gap-event-v1", EventId.ToString("D"), RunId.ToString("D"),
        AttemptId.ToString("D"), Role.ToString(), GapId.ToString("D"),
        Ordinal.ToString(CultureInfo.InvariantCulture), EventKind, GapKind,
        Purpose, Capability ?? "", ReceiptId?.ToString("D") ?? "", Diagnostic,
        OwnershipEpoch.ToString(CultureInfo.InvariantCulture),
        CreatedAtUnixMs.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Only these model-suggestible evidence gaps are supported in R3.
/// No arbitrary target, path, command, URL or cross-snapshot request.</summary>
public static class AdaptiveGapPolicy
{
    public const string Version = "r3-adaptive-gap-policy-v1";
    public static bool Supports(string? kind) => kind is
        "missing-direct-invocations" or "unresolved-callee" or
        "uncertain-source-relationships" or "unknown-entry-point-binding";
    public static bool ValidPurpose(string? value) =>
        value is { Length: > 0 and <= 600 } && !value.Any(char.IsControl);
}

public sealed partial class WorkspaceStore
{
    internal static string AdaptiveLedgerDigest(params string[] fields)
    {
        var builder = new StringBuilder("code-archeologist-ledger-sha256-v1");
        foreach (var field in fields)
            builder.Append('|').Append(field.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(field);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// Called by the host ONLY after GateCEvidenceSession.Request returned an
    /// accepted result. The evidence is not given to the model until this fenced
    /// transaction commits. Every call (including empty/overlapping results)
    /// is recorded and charged. Unsupported requests never reach this method.
    /// </summary>
    public AdaptiveEvidenceReceipt RecordAdaptiveEvidenceReceipt(
        ExecutionFence fence, Guid attemptId, GateCEvidenceSession session,
        GateCDiscoveryResponse response, string phase, string purpose,
        string? gapKind = null)
    {
        if (phase is not ("initial" or "follow-up") ||
            !AdaptiveGapPolicy.ValidPurpose(purpose) ||
            (phase == "initial" && gapKind is not null) ||
            (phase == "follow-up" && !AdaptiveGapPolicy.Supports(gapKind)) ||
            !GateCEvidenceRegistry.Supports(response.Capability) ||
            response.ContractVersion != GateCEvidenceRegistry.Version ||
            response.Role != session.Role ||
            response.Role is not (GateCRole.Investigator or GateCRole.Skeptic) ||
            response.ToolCallsUsed != session.ToolCallsUsed ||
            response.ToolCallsUsed is < 1 or > GateCEvidenceSession.MaxToolCalls)
            throw new ArgumentException("Invalid adaptive evidence receipt input.");

        var links = session.ReportLinks();
        if (links.Count > GateCEvidenceSession.MaxTotalItems ||
            !GateCContractValidator.ValidateHostCatalog(session.VerifiedPackage,
                session.VerifiedScope, session.HostCatalog()).Accepted ||
            response.RunId != session.VerifiedRunId ||
            response.EvidenceFingerprint != session.VerifiedScope.FingerprintSha256)
            throw new ArgumentException("Unverified adaptive evidence mapping.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var gapId = phase == "follow-up" ? Guid.NewGuid() : (Guid?)null;
        var responseJson = JsonSerializer.Serialize(response);
        var linksJson = JsonSerializer.Serialize(links);
        var requestJson = JsonSerializer.Serialize(new
        {
            contractVersion = AdaptiveGapPolicy.Version,
            role = response.Role.ToString(), phase,
            capability = response.Capability, targetNodeId = session.VerifiedScope.EntryPointId,
            gapId, gapKind, purpose
        });
        var receipt = new AdaptiveEvidenceReceipt(Guid.NewGuid(), response.RunId, attemptId,
            response.Role, response.ToolCallsUsed, phase, response.Capability,
            session.VerifiedScope.EntryPointId, gapId, gapKind, purpose, requestJson,
            responseJson, AdaptiveLedgerDigest("response-v1", responseJson), linksJson,
            AdaptiveLedgerDigest("links-v1", linksJson), 0, fence.Epoch, now, "");

        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        RequireActiveAttemptOwner(db, tx, fence, attemptId, "running");
        var attempt = LoadAttempt(db, tx, attemptId)
            ?? throw new InvalidOperationException("Adaptive receipt attempt missing.");
        var run = LoadRun(db, tx, attempt.RunId)
            ?? throw new InvalidOperationException("Adaptive receipt run missing.");
        if (attempt.RunId != response.RunId || run.Scope != session.VerifiedScope ||
            attempt.OwnershipEpoch != fence.Epoch)
            throw new InvalidOperationException("Adaptive receipt foreign run or scope.");

        AdaptiveEvidenceReceipt? prior = null;
        using (var cmd = Command(db, tx, """
            SELECT receipt_id,run_id,attempt_id,role,ordinal,phase,capability,
                target_node_id,gap_id,gap_kind,purpose,request_json,response_json,
                response_sha256,evidence_links_json,evidence_links_sha256,
                new_item_count,ownership_epoch,created_at_unix_ms,content_sha256
            FROM adaptive_evidence_receipts
            WHERE attempt_id=$attempt AND role=$role
            ORDER BY ordinal DESC LIMIT 1;
            """))
        {
            cmd.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
            cmd.Parameters.AddWithValue("$role", RoleName(response.Role));
            using var reader = cmd.ExecuteReader();
            if (reader.Read()) prior = ReadAdaptiveReceipt(reader);
        }
        if (response.ToolCallsUsed != (prior?.Ordinal ?? 0) + 1 ||
            (phase == "follow-up" && (prior is null || prior.Phase == "follow-up")))
            throw new InvalidOperationException("Out of order or duplicate adaptive evidence request.");
        var previousLinks = prior is null ? [] :
            JsonSerializer.Deserialize<GateC5EvidenceLink[]>(prior.EvidenceLinksJson)
                ?? throw new InvalidOperationException("Prior receipt mappings invalid.");
        if (previousLinks.Length > links.Count ||
            !previousLinks.SequenceEqual(links.Take(previousLinks.Length)))
            throw new InvalidOperationException("Evidence handles changed during adaptive session.");
        receipt = receipt with
        {
            NewItemCount = links.Count - previousLinks.Length
        };
        receipt = receipt with { ContentSha256 = receipt.ComputeDigest() };

        using (var insert = Command(db, tx, """
            INSERT INTO adaptive_evidence_receipts
              (receipt_id,run_id,attempt_id,role,ordinal,phase,capability,target_node_id,
               gap_id,gap_kind,purpose,request_json,response_json,response_sha256,
               evidence_links_json,evidence_links_sha256,new_item_count,ownership_epoch,
               created_at_unix_ms,content_sha256)
            VALUES($id,$run,$attempt,$role,$ordinal,$phase,$cap,$target,
               $gap,$kind,$purpose,$request,$response,$responseHash,
               $links,$linksHash,$new,$epoch,$created,$hash);
            """))
        {
            insert.Parameters.AddWithValue("$id", receipt.ReceiptId.ToString("D"));
            insert.Parameters.AddWithValue("$run", receipt.RunId.ToString("D"));
            insert.Parameters.AddWithValue("$attempt", receipt.AttemptId.ToString("D"));
            insert.Parameters.AddWithValue("$role", RoleName(receipt.Role));
            insert.Parameters.AddWithValue("$ordinal", receipt.Ordinal);
            insert.Parameters.AddWithValue("$phase", receipt.Phase);
            insert.Parameters.AddWithValue("$cap", receipt.Capability);
            insert.Parameters.AddWithValue("$target", receipt.TargetNodeId);
            insert.Parameters.AddWithValue("$gap", (object?)receipt.GapId?.ToString("D") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$kind", (object?)receipt.GapKind ?? DBNull.Value);
            insert.Parameters.AddWithValue("$purpose", receipt.Purpose);
            insert.Parameters.AddWithValue("$request", receipt.RequestJson);
            insert.Parameters.AddWithValue("$response", receipt.ResponseJson);
            insert.Parameters.AddWithValue("$responseHash", receipt.ResponseSha256);
            insert.Parameters.AddWithValue("$links", receipt.EvidenceLinksJson);
            insert.Parameters.AddWithValue("$linksHash", receipt.EvidenceLinksSha256);
            insert.Parameters.AddWithValue("$new", receipt.NewItemCount);
            insert.Parameters.AddWithValue("$epoch", receipt.OwnershipEpoch);
            insert.Parameters.AddWithValue("$created", receipt.CreatedAtUnixMs);
            insert.Parameters.AddWithValue("$hash", receipt.ContentSha256);
            if (insert.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Adaptive evidence receipt not persisted.");
        }

        if (gapId is Guid gap)
        {
            InsertGapEvent(db, tx, receipt, gap, 1, "proposed", "model-proposed-not-proof");
            InsertGapEvent(db, tx, receipt, gap, 2, "allowed", "host-authorized-read-only");
            InsertGapEvent(db, tx, receipt, gap, 3,
                receipt.NewItemCount == 0 ? "no-new-evidence" : "new-evidence",
                receipt.NewItemCount == 0 ? "bounded-view-contained-no-new-items" :
                    "new-verified-evidence-handles-issued");
        }
        tx.Commit();
        return receipt;
    }

    /// <summary>Read-only receipt projection for the future exact-replay
    /// validator. This method never silently drops a malformed ledger row.</summary>
    public IReadOnlyList<AdaptiveEvidenceReceipt> ListAdaptiveEvidenceReceipts(
        Guid runId, Guid attemptId, GateCRole role)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT receipt_id,run_id,attempt_id,role,ordinal,phase,capability,
                target_node_id,gap_id,gap_kind,purpose,request_json,response_json,
                response_sha256,evidence_links_json,evidence_links_sha256,
                new_item_count,ownership_epoch,created_at_unix_ms,content_sha256
            FROM adaptive_evidence_receipts
            WHERE run_id=$run AND attempt_id=$attempt AND role=$role
            ORDER BY ordinal;
            """;
        cmd.Parameters.AddWithValue("$run", runId.ToString("D"));
        cmd.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
        cmd.Parameters.AddWithValue("$role", RoleName(role));
        using var reader = cmd.ExecuteReader();
        var results = new List<AdaptiveEvidenceReceipt>();
        while (reader.Read())
        {
            var receipt = ReadAdaptiveReceipt(reader);
            if (receipt.ComputeDigest() != receipt.ContentSha256 ||
                receipt.ResponseSha256 != AdaptiveLedgerDigest("response-v1", receipt.ResponseJson) ||
                receipt.EvidenceLinksSha256 != AdaptiveLedgerDigest("links-v1", receipt.EvidenceLinksJson) ||
                receipt.RunId != runId || receipt.AttemptId != attemptId ||
                receipt.Role != role || receipt.Ordinal != results.Count + 1)
                throw new InvalidOperationException("Adaptive receipt provenance failed.");
            results.Add(receipt);
        }
        return results;
    }

    private static AdaptiveEvidenceReceipt ReadAdaptiveReceipt(SqliteDataReader reader)
    {
        var role = reader.GetString(3) switch
        {
            "investigator" => GateCRole.Investigator,
            "skeptic" => GateCRole.Skeptic,
            _ => throw new InvalidOperationException("Unknown adaptive receipt role.")
        };
        return new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)), role, reader.GetInt32(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7),
            reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10),
            reader.GetString(11), reader.GetString(12), reader.GetString(13),
            reader.GetString(14), reader.GetString(15), reader.GetInt32(16),
            reader.GetInt64(17), reader.GetInt64(18), reader.GetString(19));
    }

    private static string RoleName(GateCRole role) => role switch
    {
        GateCRole.Investigator => "investigator",
        GateCRole.Skeptic => "skeptic",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private static void InsertGapEvent(SqliteConnection db, SqliteTransaction tx,
        AdaptiveEvidenceReceipt receipt, Guid gapId, int ordinal,
        string eventKind, string diagnostic)
    {
        var entry = new AdaptiveGapEvent(Guid.NewGuid(), receipt.RunId,
            receipt.AttemptId, receipt.Role, gapId, ordinal, eventKind,
            receipt.GapKind!, receipt.Purpose, receipt.Capability,
            receipt.ReceiptId, diagnostic, receipt.OwnershipEpoch,
            receipt.CreatedAtUnixMs, "");
        entry = entry with { ContentSha256 = entry.ComputeDigest() };
        using var insert = Command(db, tx, """
            INSERT INTO adaptive_gap_events
              (event_id,run_id,attempt_id,role,gap_id,ordinal,event_kind,gap_kind,
               purpose,capability,receipt_id,diagnostic,ownership_epoch,
               created_at_unix_ms,content_sha256)
            VALUES($id,$run,$attempt,$role,$gap,$ordinal,$kind,$gapKind,
               $purpose,$cap,$receipt,$diag,$epoch,$created,$hash);
            """);
        insert.Parameters.AddWithValue("$id", entry.EventId.ToString("D"));
        insert.Parameters.AddWithValue("$run", entry.RunId.ToString("D"));
        insert.Parameters.AddWithValue("$attempt", entry.AttemptId.ToString("D"));
        insert.Parameters.AddWithValue("$role", RoleName(entry.Role));
        insert.Parameters.AddWithValue("$gap", gapId.ToString("D"));
        insert.Parameters.AddWithValue("$ordinal", ordinal);
        insert.Parameters.AddWithValue("$kind", eventKind);
        insert.Parameters.AddWithValue("$gapKind", entry.GapKind);
        insert.Parameters.AddWithValue("$purpose", entry.Purpose);
        insert.Parameters.AddWithValue("$cap", (object?)entry.Capability ?? DBNull.Value);
        insert.Parameters.AddWithValue("$receipt", (object?)entry.ReceiptId?.ToString("D") ?? DBNull.Value);
        insert.Parameters.AddWithValue("$diag", entry.Diagnostic);
        insert.Parameters.AddWithValue("$epoch", entry.OwnershipEpoch);
        insert.Parameters.AddWithValue("$created", entry.CreatedAtUnixMs);
        insert.Parameters.AddWithValue("$hash", entry.ContentSha256);
        if (insert.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Adaptive gap event was not persisted.");
    }
}

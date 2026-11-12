using System.Text.Json;
using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Investigations;

public sealed record AdaptiveReceiptRevalidation(bool Accepted, string Diagnostic,
    IReadOnlyList<GateC5EvidenceLink> EvidenceLinks, int ToolCallsUsed,
    string? ReceiptSequenceDigest)
{
    public static AdaptiveReceiptRevalidation Reject(string reason) =>
        new(false, reason, [], 0, null);
}

/// <summary>
/// Rebuild every stored evidence response from the original, independently
/// verified saved snapshot. An SHA-256 match does NOT authorize a checkpoint:
/// this validator also checks scope, independent role, strict request ordering,
/// host mapping, retrieved item status and the gap event lifecycle.
/// </summary>
public sealed class AdaptiveReceiptRevalidator(
    WorkspaceStore store, IGateCEvidenceSessionFactory evidence)
{
    public AdaptiveReceiptRevalidation Revalidate(SnapshotRecord snapshot,
        InvestigationSummary run, Guid attemptId, GateCRole role,
        CancellationToken ct)
    {
        if (role is not (GateCRole.Investigator or GateCRole.Skeptic) ||
            attemptId == Guid.Empty || snapshot.Id != run.Scope.SnapshotId ||
            snapshot.FingerprintSha256 != run.Scope.FingerprintSha256 ||
            snapshot.WorkspaceId != run.Scope.WorkspaceId ||
            snapshot.RepositoryId != run.Scope.RepositoryId)
            return AdaptiveReceiptRevalidation.Reject("adaptive-snapshot-or-role-mismatch");
        try
        {
            var context = store.GetAdaptiveContext(run.Scope.WorkspaceId,
                run.Scope.RepositoryId, run.Scope.SnapshotId, run.RunId);
            if (context is null || !AdaptiveQuestionContract.Verify(context) ||
                context.Scope != run.Scope)
                return AdaptiveReceiptRevalidation.Reject("adaptive-context-missing-or-invalid");

            var receipts = store.ListAdaptiveEvidenceReceipts(run.RunId, attemptId, role);
            var gaps = store.ListAdaptiveGapEvents(run.RunId, attemptId, role);
            if (receipts.Count is < 1 or > GateCEvidenceSession.MaxToolCalls ||
                gaps.Count > 3 || receipts.Count(r => r.Phase == "initial") > 3 ||
                receipts.Count(r => r.Phase == "follow-up") > 1)
                return AdaptiveReceiptRevalidation.Reject("adaptive-receipt-budget-invalid");

            var session = evidence.OpenVerified(snapshot, run.Scope, run.RunId, role, ct);
            var beforeCount = 0;
            var initialCapabilities = new HashSet<string>(StringComparer.Ordinal);
            AdaptiveEvidenceReceipt? followup = null;
            for (var i = 0; i < receipts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var receipt = receipts[i];
                if (receipt.Ordinal != i + 1 ||
                    receipt.RunId != run.RunId || receipt.AttemptId != attemptId ||
                    receipt.Role != role ||
                    receipt.TargetNodeId != run.Scope.EntryPointId ||
                    receipt.OwnershipEpoch <= 0 ||
                    !AdaptiveGapPolicy.ValidPurpose(receipt.Purpose) ||
                    !GateCEvidenceRegistry.Supports(receipt.Capability))
                    return AdaptiveReceiptRevalidation.Reject("adaptive-receipt-scope-invalid");

                if (receipt.Phase == "initial")
                {
                    if (followup is not null || receipt.GapId is not null ||
                        receipt.GapKind is not null ||
                        !initialCapabilities.Add(receipt.Capability))
                        return AdaptiveReceiptRevalidation.Reject("adaptive-initial-order-invalid");
                }
                else if (receipt.Phase == "follow-up")
                {
                    if (i == 0 || followup is not null || receipt.GapId is null ||
                        AdaptiveGapPolicy.CapabilityFor(receipt.GapKind) != receipt.Capability)
                        return AdaptiveReceiptRevalidation.Reject("adaptive-followup-policy-invalid");
                    followup = receipt;
                }
                else return AdaptiveReceiptRevalidation.Reject("adaptive-phase-unknown");

                // The exact host request descriptor is covered by the receipt
                // digest and recreated independently rather than trusted as JSON.
                var reconstructedRequest = JsonSerializer.Serialize(new
                {
                    contractVersion = AdaptiveGapPolicy.Version,
                    role = role.ToString(), phase = receipt.Phase,
                    capability = receipt.Capability,
                    targetNodeId = run.Scope.EntryPointId,
                    gapId = receipt.GapId, gapKind = receipt.GapKind,
                    purpose = receipt.Purpose
                });
                if (reconstructedRequest != receipt.RequestJson)
                    return AdaptiveReceiptRevalidation.Reject("adaptive-request-mapping-altered");

                var issued = session.Request(receipt.Capability, ct);
                if (!issued.Check.Accepted || issued.Response is null)
                    return AdaptiveReceiptRevalidation.Reject("adaptive-evidence-replay-rejected");
                var restoredLinks = session.ReportLinks();
                if (issued.Response.ToolCallsUsed != receipt.Ordinal ||
                    JsonSerializer.Serialize(issued.Response) != receipt.ResponseJson ||
                    JsonSerializer.Serialize(restoredLinks) != receipt.EvidenceLinksJson ||
                    restoredLinks.Count - beforeCount != receipt.NewItemCount)
                    return AdaptiveReceiptRevalidation.Reject("adaptive-receipt-replay-mismatch");
                beforeCount = restoredLinks.Count;
            }

            if (followup is null)
            {
                // A model may request a capability already served in its
                // initial batch. Host records proposal + denial; no tool
                // call, new evidence handle or follow-up receipt is minted.
                if (gaps.Count != 0)
                {
                    if (gaps.Count != 2 ||
                        gaps[0].EventKind != "proposed" ||
                        gaps[1].EventKind != "denied" ||
                        gaps[0].Ordinal != 1 || gaps[1].Ordinal != 2 ||
                        gaps[0].GapId != gaps[1].GapId ||
                        gaps[0].GapId == Guid.Empty ||
                        gaps[0].GapKind != gaps[1].GapKind ||
                        gaps[0].Purpose != gaps[1].Purpose ||
                        gaps[0].Capability != gaps[1].Capability ||
                        gaps[0].ReceiptId is not null || gaps[1].ReceiptId is not null ||
                        gaps[0].OwnershipEpoch != gaps[1].OwnershipEpoch ||
                        gaps[0].CreatedAtUnixMs != gaps[1].CreatedAtUnixMs ||
                        gaps[0].Diagnostic != "model-proposed-not-proof" ||
                        gaps[1].Diagnostic != "duplicate-initial-capability" ||
                        !AdaptiveGapPolicy.ValidPurpose(gaps[0].Purpose) ||
                        AdaptiveGapPolicy.CapabilityFor(gaps[0].GapKind) != gaps[0].Capability ||
                        !initialCapabilities.Contains(gaps[0].Capability!) ||
                        gaps.Any(g => g.RunId != run.RunId ||
                            g.AttemptId != attemptId || g.Role != role))
                        return AdaptiveReceiptRevalidation.Reject("adaptive-denied-gap-invalid");
                }
            }
            if (followup is not null)
            {
                if (gaps.Count != 3 || followup.GapId is null)
                    return AdaptiveReceiptRevalidation.Reject("adaptive-gap-event-sequence-missing");
                var kinds = new[] { "proposed", "allowed",
                    followup.NewItemCount == 0 ? "no-new-evidence" : "new-evidence" };
                for (var i = 0; i < 3; i++)
                {
                    var gap = gaps[i];
                    if (gap.Ordinal != i + 1 || gap.EventKind != kinds[i] ||
                        gap.GapId != followup.GapId ||
                        gap.ReceiptId != followup.ReceiptId ||
                        gap.Role != role ||
                        gap.RunId != run.RunId || gap.AttemptId != attemptId ||
                        gap.GapKind != followup.GapKind ||
                        gap.Purpose != followup.Purpose ||
                        gap.Capability != followup.Capability ||
                        gap.OwnershipEpoch != followup.OwnershipEpoch ||
                        gap.CreatedAtUnixMs != followup.CreatedAtUnixMs)
                        return AdaptiveReceiptRevalidation.Reject("adaptive-gap-event-replay-mismatch");
                }
            }
            var sequenceFields = new List<string>
            {
                "r3-adaptive-receipt-sequence-v1", run.RunId.ToString("D"),
                attemptId.ToString("D"), role.ToString()
            };
            foreach (var receipt in receipts)
            {
                sequenceFields.Add(receipt.ReceiptId.ToString("D"));
                sequenceFields.Add(receipt.ContentSha256);
            }
            foreach (var gap in gaps)
            {
                sequenceFields.Add(gap.EventId.ToString("D"));
                sequenceFields.Add(gap.ContentSha256);
            }
            var digest = WorkspaceStore.AdaptiveLedgerDigest(sequenceFields.ToArray());
            return new AdaptiveReceiptRevalidation(true, "adaptive-receipts-replayed",
                session.ReportLinks(), session.ToolCallsUsed, digest);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return AdaptiveReceiptRevalidation.Reject(
                error is JsonException ? "adaptive-ledger-json-invalid" :
                "adaptive-ledger-revalidation-failed");
        }
    }
}

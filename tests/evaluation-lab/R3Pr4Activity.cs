using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.EvaluationLab;

/// <summary>
/// Host-observed resource ledger for PR4, kept OUTSIDE the historical C7
/// report/score schemas. Missing measurements remain null, never zero.
/// </summary>
public sealed record R3Pr4Activity(
    string SchemaVersion, string CaseId, string Comparator, int Trial,
    Guid RunId, Guid AttemptId, string AttemptStatus, long DurationMs,
    int? CommittedEvidenceRequests, int? EvidenceItems,
    int? EvidenceReceipts, int? FollowUpRequests, int? DuplicateDenials,
    int? NewEvidenceItems, int? ModelCalls,
    bool CheckpointReplayAccepted, bool? ReceiptReplayAccepted,
    string EvidenceAccountingBasis)
{
    public const string Version = "r3-pr4-host-activity-v1";
    // Host checkpoint JSON uses default .NET naming. The API's serializer
    // option is internal to its assembly, so this lab supplies an equivalent.
    private static readonly JsonSerializerOptions HostJson =
        new(JsonSerializerDefaults.General);

    public static R3Pr4Activity Capture(CapturedCase captured, Guid runId,
        int trial, bool adaptive, long elapsedMs, CancellationToken ct)
    {
        var store = captured.Store;
        var scope = captured.Scope;
        var detail = store.GetInvestigation(scope.WorkspaceId, scope.RepositoryId,
            scope.SnapshotId, runId)
            ?? throw new InvalidOperationException("PR4 host run disappeared.");
        var attempt = store.GetAttempt(runId)
            ?? throw new InvalidOperationException("PR4 host attempt disappeared.");
        var discovery = captured.Services.GetRequiredService<GateCEvidenceDiscovery>();
        var checkpoints = store.ListCheckpoints(runId);
        var validated = new GateC6CheckpointRevalidator(discovery, store)
            .Revalidate(captured.Snapshot, detail.Summary, checkpoints,
                store.ListAcceptedCheckpointEventIds(runId), ct);
        if (!validated.Accepted)
            throw new InvalidOperationException("PR4 checkpoint replay rejected: " +
                validated.Diagnostic);

        if (adaptive)
        {
            // This is the SAME verified, fail-closed projection used by the
            // authenticated Mission Control API; it replays every receipt.
            var history = new AdaptiveActivityProjectionService(store, discovery)
                .Project(scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId,
                    runId, ct);
            if (history is null || history.ProjectionVersion != AdaptiveActivityContract.Version ||
                history.RunId != runId || history.SnapshotId != scope.SnapshotId)
                throw new InvalidOperationException("PR4 adaptive activity is unavailable.");
            var roles = history.Attempts.SelectMany(a => a.Roles).ToArray();
            var requests = roles.SelectMany(r => r.Requests).ToArray();
            return new(Version, captured.Loaded.Case.Id, Comparator.Adaptive,
                trial, runId, attempt.AttemptId, attempt.Status, elapsedMs,
                requests.Length, requests.Sum(r => r.NewItemCount),
                requests.Length, requests.Count(r => r.Phase == "follow-up"),
                roles.Count(r => r.HostDecision == "denied"),
                requests.Sum(r => r.NewItemCount),
                null, true, true, "replayed-committed-receipts; model-call-count-unobservable");
        }

        // Historical fixed Gate C does NOT have the PR3 receipt ledger.
        // Only accepted role checkpoints carry independently validated call
        // and handle counts. A failed role has UNKNOWN usage, never zero.
        var rolesFixed = checkpoints.Where(c => c.Role is "investigator" or "skeptic").ToArray();
        int? calls = null, items = null;
        if (rolesFixed.Length == 2)
        {
            var checks = rolesFixed.Select(c => JsonSerializer
                .Deserialize<GateC6CheckpointValidation>(c.ValidationJson,
                    HostJson)).ToArray();
            var links = rolesFixed.Select(c => JsonSerializer
                .Deserialize<GateC5EvidenceLink[]>(c.EvidenceLinksJson,
                    HostJson)).ToArray();
            if (checks.Any(c => c is not { Accepted: true }) || links.Any(l => l is null))
                throw new InvalidOperationException("PR4 fixed role checkpoint accounting invalid.");
            calls = checks.Sum(c => c!.ToolCallsUsed);
            items = links.Sum(l => l!.Length);
        }
        return new(Version, captured.Loaded.Case.Id, Comparator.GateC,
            trial, runId, attempt.AttemptId, attempt.Status, elapsedMs,
            calls, items, null, null, null, null,
            null, true, null, rolesFixed.Length == 2
                ? "revalidated-accepted-checkpoints-only; model-call-count-unobservable"
                : "fixed-role-incomplete; request-count-unobservable");
    }
}

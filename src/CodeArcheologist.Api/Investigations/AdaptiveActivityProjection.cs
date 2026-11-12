using System.Text.Json;
using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Investigations;

/// <summary>
/// Read-only, snapshot-scoped host history. A model's proposed gap is a
/// proposal; only a persisted host event establishes an authorization.
/// </summary>
public static class AdaptiveActivityContract
{
    public const string Version = "r3-adaptive-evidence-activity-v1";
}

public sealed record AdaptiveActivityRequest(int Ordinal, string Phase, string Capability,
    Guid ReceiptId, int EvidenceCount, int NewItemCount);

public sealed record AdaptiveActivityRole(string Role, string HistoryStatus,
    IReadOnlyList<AdaptiveActivityRequest> Requests, string Decision,
    string? ProposedGapKind, string? RequestedCapability, string? HostDecision,
    string? EvidenceOutcome, string? StopReason, bool CheckpointAccepted,
    bool CheckpointReused);

public sealed record AdaptiveActivityAttempt(Guid AttemptId, int Number, string Status,
    bool Interrupted, IReadOnlyList<AdaptiveActivityRole> Roles);

public sealed record AdaptiveActivityProjection(string ProjectionVersion, Guid RunId,
    Guid SnapshotId, string Status, IReadOnlyList<AdaptiveActivityAttempt> Attempts,
    string Note);

/// <summary>
/// No raw JSON, prompts, model purpose, ledger payload, or unverified handle
/// mapping crosses this boundary. Integrity failures throw and must be
/// presented by HTTP as an error rather than a success-shaped empty history.
/// </summary>
public sealed class AdaptiveActivityProjectionService(
    WorkspaceStore store, IGateCEvidenceSessionFactory evidence)
{
    private static readonly GateCRole[] IndependentRoles =
        [GateCRole.Investigator, GateCRole.Skeptic];

    public AdaptiveActivityProjection? Project(Guid workspaceId, Guid repositoryId,
        Guid snapshotId, Guid runId, CancellationToken ct)
    {
        var detail = store.GetInvestigation(workspaceId, repositoryId, snapshotId, runId);
        if (detail is null) return null;
        var run = detail.Summary;
        var context = store.GetAdaptiveContext(workspaceId, repositoryId, snapshotId, runId);
        if (context is null) return null; // Caller distinguishes a stable investigation.
        if (!AdaptiveQuestionContract.Verify(context) || context.Scope != run.Scope)
            throw new InvalidOperationException("Adaptive activity context provenance failed.");

        var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
        if (snapshot is null || snapshot.FingerprintSha256 != run.Scope.FingerprintSha256 ||
            snapshot.WorkspaceId != workspaceId || snapshot.RepositoryId != repositoryId)
            throw new InvalidOperationException("Adaptive activity snapshot mismatch.");
        // Verify the original immutable snapshot even for a run that failed
        // before issuing its first receipt. There is no live-folder fallback.
        _ = evidence.OpenVerified(snapshot, run.Scope, runId, GateCRole.Investigator, ct);

        var attempts = store.ListAttempts(runId);
        var events = store.ListEvents(workspaceId, repositoryId, snapshotId, runId);
        var checkpoints = store.ListCheckpoints(runId);
        var lineage = store.ListLineage(runId);
        var attemptMap = attempts.ToDictionary(a => a.AttemptId);
        if (attempts.Any(a => a.RunId != runId || a.Number < 1 ||
                a.ContractVersion != GateC6ExecutionContract.AttemptVersion) ||
            !attempts.Select(a => a.Number).SequenceEqual(
                Enumerable.Range(1, attempts.Count)) ||
            events.Any(e => e.RunId != runId || !attemptMap.ContainsKey(e.AttemptId)) ||
            lineage.Any(l => !attemptMap.ContainsKey(l.AttemptId) ||
                !checkpoints.Any(c => c.CheckpointId == l.CheckpointId)))
            throw new InvalidOperationException("Adaptive activity attempt ownership invalid.");
        foreach (var attempt in attempts)
        {
            var ordered = events.Where(e => e.AttemptId == attempt.AttemptId).ToArray();
            if (ordered.Length != attempt.LastEventSequence ||
                ordered.Where((e, index) => e.SequenceNumber != index + 1 ||
                    e.OwnershipEpoch is not null &&
                    e.OwnershipEpoch != attempt.OwnershipEpoch).Any())
                throw new InvalidOperationException("Adaptive activity event ordering invalid.");
        }

        // Replay every accepted v3 checkpoint, including historical ones used
        // after a manual continuation. Missing accepted events fail closed.
        var checkedCheckpoints = new GateC6CheckpointRevalidator(evidence, store)
            .Revalidate(snapshot, run, checkpoints,
                store.ListAcceptedCheckpointEventIds(runId), ct);
        if (!checkedCheckpoints.Accepted)
            throw new InvalidOperationException("Adaptive activity checkpoint integrity invalid.");

        var receiptValidator = new AdaptiveReceiptRevalidator(store, evidence);
        var views = new List<AdaptiveActivityAttempt>();
        foreach (var attempt in attempts)
        {
            ct.ThrowIfCancellationRequested();
            var roles = new List<AdaptiveActivityRole>();
            foreach (var role in IndependentRoles)
            {
                var roleName = GateC6CheckpointContract.RoleName(role);
                var receipts = store.ListAdaptiveEvidenceReceipts(runId,
                    attempt.AttemptId, role);
                var gaps = store.ListAdaptiveGapEvents(runId, attempt.AttemptId, role);
                if (receipts.Count > 0)
                {
                    var replay = receiptValidator.Revalidate(snapshot, run,
                        attempt.AttemptId, role, ct);
                    if (!replay.Accepted)
                        throw new InvalidOperationException("Adaptive activity receipt replay failed.");
                }
                else if (gaps.Count != 0)
                    throw new InvalidOperationException("Orphan adaptive gap history.");
                if (receipts.Any(r => r.OwnershipEpoch != attempt.OwnershipEpoch) ||
                    gaps.Any(g => g.OwnershipEpoch != attempt.OwnershipEpoch) ||
                    receipts.Any(r => r.CreatedAtUnixMs <= 0) ||
                    gaps.Any(g => g.CreatedAtUnixMs <= 0))
                    throw new InvalidOperationException("Adaptive activity owner epoch mismatch.");

                var requests = receipts.Select(r =>
                {
                    var links = JsonSerializer.Deserialize<GateC5EvidenceLink[]>(
                        r.EvidenceLinksJson);
                    if (links is null || links.Length > GateCEvidenceSession.MaxTotalItems)
                        throw new InvalidOperationException("Adaptive activity mapping corrupt.");
                    return new AdaptiveActivityRequest(r.Ordinal, r.Phase,
                        r.Capability, r.ReceiptId, links.Length, r.NewItemCount);
                }).ToArray();
                var proposal = gaps.SingleOrDefault(g => g.EventKind == "proposed");
                var decision = gaps.SingleOrDefault(g => g.EventKind is "allowed" or "denied");
                var outcome = gaps.SingleOrDefault(g =>
                    g.EventKind is "new-evidence" or "no-new-evidence");
                // No event is not a durable model 'none' proposal.
                var decisionState = proposal is null ? "unrecorded" : "recorded-proposal";
                var checkpoint = checkpoints.SingleOrDefault(c => c.Role == roleName &&
                    c.AttemptId == attempt.AttemptId);
                var reused = lineage.Any(l => l.AttemptId == attempt.AttemptId &&
                    l.Relation == "reused" &&
                    checkpoints.Any(c => c.CheckpointId == l.CheckpointId &&
                        c.Role == roleName));
                var roleEvent = events.LastOrDefault(e => e.AttemptId == attempt.AttemptId &&
                    e.Stage == roleName && e.EventType.StartsWith("role-", StringComparison.Ordinal));
                var stopped = roleEvent?.EventType switch
                {
                    "role-timed-out" => "host-recorded-deadline",
                    "role-cancelled" => "host-recorded-cancellation",
                    "role-assessment-rejected" => "host-recorded-assessment-rejection",
                    "role-failed" => "host-recorded-failure",
                    _ => null
                };
                roles.Add(new AdaptiveActivityRole(roleName,
                    receipts.Count == 0 && gaps.Count == 0 ? "unrecorded" : "verified",
                    requests, decisionState, proposal?.GapKind, proposal?.Capability,
                    decision?.EventKind, outcome?.EventKind, stopped,
                    checkpoint is not null, reused));
            }
            views.Add(new AdaptiveActivityAttempt(attempt.AttemptId, attempt.Number,
                attempt.Status, attempt.Status is "interrupted" or "incomplete" or
                    "cancelled" or "failed", roles));
        }
        return new(AdaptiveActivityContract.Version, runId, snapshotId,
            run.Status, views,
            "Verified host receipts and decisions only. Model gap kinds are proposals, not proof; an unrecorded decision does not establish 'none'.");
    }
}

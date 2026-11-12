using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Investigations;

public static class GateC6CheckpointContract
{
    public const string Version = "gate-c6b-role-checkpoint-v2";
    /// <summary>Version of the host validation rules a checkpoint was accepted under.</summary>
    public const string ValidatorVersion = "gate-c-host-validator-v1";
    /// <summary>The Reviewer consumes validated assessments, not snapshot evidence.</summary>
    public const string ReviewerAnalyzer = "reviewer-no-direct-snapshot-evidence";

    public static string RoleName(GateCRole role) => role switch
    {
        GateCRole.Investigator => "investigator",
        GateCRole.Skeptic => "skeptic",
        GateCRole.Reviewer => "reviewer",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);
}

/// <summary>Host validation outcome recorded with an accepted checkpoint.</summary>
/// <summary>The optional sequence digest exists only in the adaptive v3
/// checkpoint profile. Historical v2 JSON remains accepted without it.</summary>
public sealed record GateC6CheckpointValidation(bool Accepted, string Diagnostic,
    string RoleDiagnostic, string? SelectedCapability, int ToolCallsUsed,
    string? ReceiptSequenceDigest = null);

public static class R3AdaptiveCheckpointContract
{
    public const string Version = "r3-adaptive-role-checkpoint-v3";
    public const string ValidatorVersion = "r3-adaptive-host-validator-v2";
}

/// <summary>Everything a checkpoint binds to, prior to persistence.</summary>
public sealed record GateC6CheckpointDraft(Guid RunId, GateCRole Role, GateCScope Scope,
    string CapabilityContractVersion, string AnalyzerVersion, string ModelId,
    string AssessmentJson, string EvidenceLinksJson, string ValidationJson,
    IReadOnlyList<Guid> DependsOn,
    string ContractVersion = GateC6CheckpointContract.Version);

public sealed record GateC6StoredCheckpoint(Guid CheckpointId, Guid RunId, Guid AttemptId,
    string Role, string ContractVersion, GateCScope Scope,
    string CapabilityContractVersion, string AnalyzerVersion, string ModelId,
    string ValidatorVersion, string AssessmentContractVersion, string AssessmentJson,
    string EvidenceLinksJson, string ValidationJson, string DependsOnJson,
    string ContentSha256, long AcceptedAtUnixMs, long AcceptedOwnershipEpoch)
{
    /// <summary>
    /// Canonical SHA-256 over every bound field except the digest itself:
    /// length-prefixed, newline-free framing so no field can absorb another.
    /// Integrity evidence only; NOT proof against a local database writer.
    /// </summary>
    public string ComputeDigest()
    {
        var fields = new[]
        {
            CheckpointId.ToString("D"), RunId.ToString("D"), AttemptId.ToString("D"), Role,
            ContractVersion, Scope.WorkspaceId.ToString("D"), Scope.RepositoryId.ToString("D"),
            Scope.SnapshotId.ToString("D"), Scope.FingerprintSha256, Scope.EntryPointId,
            Scope.EvidenceContractVersion, CapabilityContractVersion, AnalyzerVersion, ModelId,
            ValidatorVersion, AssessmentContractVersion, AssessmentJson, EvidenceLinksJson,
            ValidationJson, DependsOnJson,
            AcceptedAtUnixMs.ToString(CultureInfo.InvariantCulture),
            AcceptedOwnershipEpoch.ToString(CultureInfo.InvariantCulture)
        };
        var builder = new StringBuilder("gate-c6b-checkpoint-digest-v1");
        foreach (var field in fields)
            builder.Append('|').Append(field.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(field);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }
}

/// <summary>A checkpoint that passed full independent revalidation.</summary>
public sealed record GateC6ReusableRole(GateC6StoredCheckpoint Checkpoint, GateC3RoleOutcome Outcome);

public sealed record GateC6ReusableReview(GateC6StoredCheckpoint Checkpoint, GateC4ReviewerOutcome Outcome);

public sealed record GateC6Revalidation(bool Accepted, string Diagnostic,
    GateC6ReusableRole? Investigator, GateC6ReusableRole? Skeptic, GateC6ReusableReview? Reviewer)
{
    public static GateC6Revalidation Reject(string diagnostic) => new(false, diagnostic, null, null, null);
    public IReadOnlyList<Guid> CheckpointIds =>
        new[] { Investigator?.Checkpoint.CheckpointId, Skeptic?.Checkpoint.CheckpointId,
                Reviewer?.Checkpoint.CheckpointId }
        .Where(x => x is not null).Select(x => x!.Value).ToArray();
}

/// <summary>
/// Fail-closed checkpoint revalidation (decision D5). Nothing is reused unless
/// its digest, every versioned contract, scope, role, lineage, evidence
/// identities (rebuilt from a freshly verified snapshot session), and the
/// host validators all agree under the strict initial compatibility policy.
/// A failure is reported, never repaired, discarded or silently re-run.
/// </summary>
public sealed class GateC6CheckpointRevalidator(
    IGateCEvidenceSessionFactory evidence, WorkspaceStore? adaptiveStore = null)
{
    public GateC6Revalidation Revalidate(SnapshotRecord snapshot, InvestigationSummary run,
        IReadOnlyList<GateC6StoredCheckpoint> checkpoints,
        IReadOnlyList<Guid> acceptedEventCheckpointIds, CancellationToken ct)
    {
        var scope = run.Scope;
        if (snapshot.Id != scope.SnapshotId || snapshot.WorkspaceId != scope.WorkspaceId ||
            snapshot.RepositoryId != scope.RepositoryId ||
            snapshot.FingerprintSha256 != scope.FingerprintSha256)
            return GateC6Revalidation.Reject("snapshot-changed-or-foreign");

        // v1/v2 and adaptive v3 are separate historical profiles. A v2
        // checkpoint cannot be reinterpreted as adaptive, nor vice versa.
        bool isAdaptive;
        try
        {
            isAdaptive = adaptiveStore?.GetAdaptiveContext(scope.WorkspaceId,
                scope.RepositoryId, scope.SnapshotId, run.RunId) is not null;
        }
        catch (InvalidOperationException)
        {
            return GateC6Revalidation.Reject("adaptive-context-invalid");
        }
        // Every checkpoint the immutable ledger says was accepted must exist,
        // and every checkpoint row must have its acceptance event.
        var ids = checkpoints.Select(c => c.CheckpointId).ToHashSet();
        if (acceptedEventCheckpointIds.Distinct().Count() != acceptedEventCheckpointIds.Count ||
            !acceptedEventCheckpointIds.ToHashSet().SetEquals(ids))
            return GateC6Revalidation.Reject("checkpoint-missing-or-unrecorded");
        if (checkpoints.Select(c => c.Role).Distinct(StringComparer.Ordinal).Count() != checkpoints.Count)
            return GateC6Revalidation.Reject("checkpoint-role-duplicated");

        foreach (var cp in checkpoints)
        {
            if (cp.ComputeDigest() != cp.ContentSha256)
                return GateC6Revalidation.Reject("checkpoint-digest-mismatch");
            if (cp.RunId != run.RunId || cp.Scope != scope)
                return GateC6Revalidation.Reject("checkpoint-foreign-scope");
            var expectedVersion = isAdaptive
                ? R3AdaptiveCheckpointContract.Version : GateC6CheckpointContract.Version;
            var expectedPolicy = isAdaptive
                ? AdaptiveQuestionContract.CapabilityPolicyVersion : GateCEvidenceRegistry.Version;
            var expectedValidator = isAdaptive
                ? R3AdaptiveCheckpointContract.ValidatorVersion : GateC6CheckpointContract.ValidatorVersion;
            if (cp.ContractVersion != expectedVersion ||
                cp.Scope.EvidenceContractVersion != SnapshotEvidenceTools.Version ||
                cp.CapabilityContractVersion != expectedPolicy ||
                cp.AssessmentContractVersion != GateCContract.Version)
                return GateC6Revalidation.Reject("checkpoint-contract-incompatible");
            if (cp.ValidatorVersion != expectedValidator)
                return GateC6Revalidation.Reject("checkpoint-validator-incompatible");
            var expectedModel = cp.Role == "reviewer"
                ? GateC4ReconciliationRunner.ModelId : GateC3IndependentRunner.ModelId;
            if (cp.ModelId != expectedModel)
                return GateC6Revalidation.Reject("checkpoint-model-provenance-incompatible");
        }

        var byRole = checkpoints.ToDictionary(c => c.Role, StringComparer.Ordinal);
        if (byRole.Keys.Any(r => r is not ("investigator" or "skeptic" or "reviewer")))
            return GateC6Revalidation.Reject("checkpoint-role-invalid");
        if (byRole.ContainsKey("reviewer") &&
            (!byRole.ContainsKey("investigator") || !byRole.ContainsKey("skeptic")))
            return GateC6Revalidation.Reject("checkpoint-stage-dependency-invalid");

        GateC6ReusableRole? investigator = null, skeptic = null;
        foreach (var role in new[] { GateCRole.Investigator, GateCRole.Skeptic })
        {
            if (!byRole.TryGetValue(GateC6CheckpointContract.RoleName(role), out var cp)) continue;
            var reused = RevalidateIndependent(snapshot, run, cp, role, isAdaptive,
                ct, out var diagnostic);
            if (reused is null) return GateC6Revalidation.Reject(diagnostic);
            if (role == GateCRole.Investigator) investigator = reused; else skeptic = reused;
        }

        GateC6ReusableReview? reviewer = null;
        if (byRole.TryGetValue("reviewer", out var review))
        {
            if (ReadJson<Guid[]>(review.DependsOnJson) is not { } deps ||
                !deps.SequenceEqual(new[] { investigator!.Checkpoint.CheckpointId,
                    skeptic!.Checkpoint.CheckpointId }))
                return GateC6Revalidation.Reject("checkpoint-lineage-invalid");
            if (review.AnalyzerVersion != GateC6CheckpointContract.ReviewerAnalyzer ||
                review.EvidenceLinksJson != "[]")
                return GateC6Revalidation.Reject("checkpoint-contract-incompatible");
            var assessment = ReadJson<GateCReviewerAssessment>(review.AssessmentJson);
            var validation = ReadJson<GateC6CheckpointValidation>(review.ValidationJson);
            if (assessment is null || validation is not { Accepted: true } ||
                Canonical(assessment) != review.AssessmentJson)
                return GateC6Revalidation.Reject("checkpoint-assessment-invalid");
            if (validation.ReceiptSequenceDigest is not null)
                return GateC6Revalidation.Reject("reviewer-cannot-claim-receipts");
            var checkedReview = GateCContractValidator.ValidateReview(assessment, run.RunId, run.Scope,
                investigator.Outcome.Assessment!, skeptic.Outcome.Assessment!);
            if (!checkedReview.Check.Accepted || checkedReview.Dispositions.Any(d => d.IsVerifiedFact))
                return GateC6Revalidation.Reject("checkpoint-review-revalidation-failed");
            reviewer = new(review, new GateC4ReviewerOutcome(GateCRoleStatus.CompletedValidated,
                validation.RoleDiagnostic, assessment, checkedReview));
        }
        return new(true, "checkpoints-revalidated", investigator, skeptic, reviewer);
    }

    private GateC6ReusableRole? RevalidateIndependent(SnapshotRecord snapshot,
        InvestigationSummary run, GateC6StoredCheckpoint cp, GateCRole role,
        bool isAdaptive, CancellationToken ct, out string diagnostic)
    {
        // Independent roles never depend on one another's checkpoints.
        if (cp.DependsOnJson != "[]")
        {
            diagnostic = "checkpoint-lineage-invalid";
            return null;
        }
        var assessment = ReadJson<GateCIndependentAssessment>(cp.AssessmentJson);
        var links = ReadJson<GateC5EvidenceLink[]>(cp.EvidenceLinksJson);
        var validation = ReadJson<GateC6CheckpointValidation>(cp.ValidationJson);
        if (assessment is null || links is null || validation is not { Accepted: true } ||
            assessment.Role != role || Canonical(assessment) != cp.AssessmentJson ||
            Canonical(links) != cp.EvidenceLinksJson)
        {
            diagnostic = "checkpoint-assessment-invalid";
            return null;
        }
        if (isAdaptive)
        {
            if (adaptiveStore is null ||
                validation.ReceiptSequenceDigest is not { Length: 64 })
            {
                diagnostic = "adaptive-receipt-digest-missing";
                return null;
            }
            var replay = new AdaptiveReceiptRevalidator(adaptiveStore, evidence)
                .Revalidate(snapshot, run, cp.AttemptId, role, ct);
            if (!replay.Accepted)
            {
                diagnostic = replay.Diagnostic;
                return null;
            }
            if (!string.Equals(replay.ReceiptSequenceDigest,
                    validation.ReceiptSequenceDigest, StringComparison.Ordinal) ||
                replay.ToolCallsUsed != validation.ToolCallsUsed ||
                !replay.EvidenceLinks.SequenceEqual(links))
            {
                diagnostic = "adaptive-checkpoint-receipt-sequence-mismatch";
                return null;
            }
        }
        else if (validation.ReceiptSequenceDigest is not null)
        {
            diagnostic = "legacy-checkpoint-has-adaptive-receipts";
            return null;
        }
        GateCEvidenceSession session;
        try
        {
            // Re-verifies the original immutable snapshot bytes.
            session = evidence.OpenVerified(snapshot, run.Scope, run.RunId, role, ct);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            diagnostic = error is OperationCanceledException
                ? "checkpoint-revalidation-cancelled" : "snapshot-changed-or-invalid";
            return null;
        }
        if (session.GraphAnalyzerVersion != cp.AnalyzerVersion)
        {
            diagnostic = "checkpoint-analyzer-incompatible";
            return null;
        }
        var restore = session.RestoreHandles(links);
        if (!restore.Accepted)
        {
            diagnostic = restore.Diagnostic;
            return null;
        }
        var check = session.ValidateAssessment(assessment);
        if (!check.Accepted)
        {
            diagnostic = "checkpoint-assessment-revalidation-failed";
            return null;
        }
        diagnostic = "checkpoint-revalidated";
        return new(cp, new GateC3RoleOutcome(role, GateCRoleStatus.CompletedValidated,
            validation.RoleDiagnostic, assessment, validation.SelectedCapability,
            validation.ToolCallsUsed, links, cp.AnalyzerVersion));
    }

    public static string Canonical<T>(T value) =>
        JsonSerializer.Serialize(value, GateC6CheckpointContract.Json);

    private static T? ReadJson<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, GateC6CheckpointContract.Json); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}

namespace CodeArcheologist.Api.Investigations;

/// <summary>
/// Gate C v1 model-independent contracts. These are data shapes, not model
/// execution, persistence, or permission to claim semantic verification.
/// </summary>
public static class GateCContract
{
    public const string Version = "gate-c-investigation-v1";
    public const int MaxHypotheses = 8;
    public const int MaxConcerns = 12;
    public const int MaxSelections = 16;
    public const int MaxTextLength = 1200;
}

public enum GateCRole { Investigator, Skeptic, Reviewer }
public enum GateCEvidenceKind { ObservedSyntax, Candidate, ProvisionalLocal }
public enum GateCConcernKind
{
    StructuralContradiction, UnsupportedInference, InsufficientEvidence,
    CompetingHypothesis, CoverageLimitation
}
public enum GateCRecommendation
{
    StructurallySupported, PlausibleHypothesis, ContestedOrInsufficient, Rejected
}
public enum GateCRunStatus
{
    Created, EvidenceVerified, IndependentAssessment, Reconciliation,
    Completed, Incomplete, Cancelled, Rejected
}
public enum GateCRoleStatus
{
    NotStarted, Running, CompletedValidated, ValidationRejected,
    Failed, TimedOut, Cancelled
}

public sealed record GateCScope(Guid WorkspaceId, Guid RepositoryId,
    Guid SnapshotId, string FingerprintSha256, string EntryPointId,
    string EvidenceContractVersion);

/// <summary>
/// Host-minted handles are local to an exact run/role/package combination.
/// Their canonical mapping stays in the host, never in model output.
/// </summary>
public sealed record GateCEvidenceHandle(string Handle, string CanonicalId,
    GateCEvidenceKind Kind);

public sealed record GateCHypothesis(string Id, string Text,
    IReadOnlyList<string> EvidenceHandles, string Uncertainty);

/// <summary>
/// A Skeptic concern is independent of Investigator hypotheses; it cannot
/// target a hypothesis ID until both role outputs have been validated.
/// </summary>
public sealed record GateCConcern(string Id, GateCConcernKind Kind,
    string Explanation, IReadOnlyList<string> EvidenceHandles);

public sealed record GateCIndependentAssessment(string ContractVersion,
    Guid RunId, GateCScope Scope, GateCRole Role,
    IReadOnlyList<string> SelectedEvidenceHandles,
    IReadOnlyList<GateCHypothesis> Hypotheses,
    IReadOnlyList<GateCConcern> Concerns,
    IReadOnlyList<string> Unknowns);

/// <summary>
/// Every concern must be explicitly addressed by one reviewer decision,
/// either linked to a hypothesis or preserved as a global unresolved issue.
/// </summary>
public sealed record GateCReviewDecision(string HypothesisId,
    GateCRecommendation Recommendation,
    IReadOnlyList<string> AddressedConcernIds, string Explanation);

public sealed record GateCReviewerAssessment(string ContractVersion,
    Guid RunId, GateCScope Scope, GateCRole Role,
    IReadOnlyList<GateCReviewDecision> Decisions,
    IReadOnlyList<string> UnresolvedGlobalConcernIds);

public sealed record GateCContractCheck(bool Accepted, string Diagnostic)
{
    public static GateCContractCheck Pass() => new(true, "contract-valid-not-semantic-proof");
    public static GateCContractCheck Fail(string diagnostic) => new(false, diagnostic);
}

public sealed record GateCHostDisposition(string HypothesisId,
    GateCRecommendation Recommendation, bool IsVerifiedFact);

public sealed record GateCReconciliationResult(GateCContractCheck Check,
    IReadOnlyList<GateCHostDisposition> Dispositions);

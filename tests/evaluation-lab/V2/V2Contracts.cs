using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeArcheologist.EvaluationLab.V2;

/// <summary>
/// V2 schema identities are deliberately independent of C6c/C7/PR4.
/// A valid V2 document is only structurally valid, never automatically scoreable.
/// </summary>
public static class V2Schemas
{
    public const string Case = "v2-case-v1";
    public const string GroundTruth = "v2-ground-truth-v1";
    public const string Answerability = "v2-answerability-v1";
    public const string EvidenceLedger = "v2-evidence-ledger-v1";
    public const string Judgment = "v2-judgment-v1";
    public const string Adjudication = "v2-adjudication-v1";
    public const string JudgmentRecord = "v2-judgment-record-v1";
    public const string ExperimentManifest = "v2-experiment-manifest-v1";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) }
    };
}

public enum V2TaskFamily { CurrentCode, ChangeImpact, EvidenceBoundary }
public enum V2CorpusPartition { Diagnostic, HeldOut }
public enum V2CriterionKind
{
    SourceObservation, SupportedInference, RequiredUncertainty,
    DeveloperFinding, ForbiddenAssertion, ChangeObservation, NegativeExpectation
}
public enum V2Materiality { Contextual, Material, Critical }
public enum V2ReviewStatus { Draft, IndependentlyReviewed, Disputed }
public enum V2SnapshotSide { Before, After }
public enum V2AnswerabilityState { InitiallyAvailable, Retrievable, Unavailable, Undetermined }
public enum V2EvidenceStage { RepositoryPresent, Captured, ToolAccessible, Requested, Returned, Delivered, SufficiencyReviewed }
public enum V2EvidenceStatus { Verified, Failed, Unknown }
public enum V2GroundingStatus { Supported, Unsupported, Contradicted, NotDemonstrated }
public enum V2AdjudicationDisposition { Confirmed, Revised, Unresolved }

/// <summary>Immutable snapshot identity and file digests; no source or compilation is executed.</summary>
public sealed record V2SourceIdentity(
    [property: JsonRequired] string SnapshotSha256,
    [property: JsonRequired] IReadOnlyDictionary<string, string> Files,
    string? RepositoryCommitSha = null);

/// <summary>Case identifiers refer to criteria in a separate ground-truth document.</summary>
public sealed record V2Case(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string Id,
    [property: JsonRequired] V2TaskFamily Task,
    [property: JsonRequired] string DeveloperQuestion,
    [property: JsonRequired] string IntendedAudience,
    [property: JsonRequired] V2CorpusPartition Partition,
    [property: JsonRequired] V2SourceIdentity After,
    V2SourceIdentity? Before,
    [property: JsonRequired] IReadOnlyList<string> AuthorizedPaths,
    [property: JsonRequired] IReadOnlyList<string> CriterionIds);

public sealed record V2SourceProof(
    [property: JsonRequired] V2SnapshotSide Snapshot,
    [property: JsonRequired] string FilePath,
    [property: JsonRequired] string FileSha256,
    [property: JsonRequired] int StartLine,
    [property: JsonRequired] int EndLine);

public sealed record V2Criterion(
    [property: JsonRequired] string Id,
    [property: JsonRequired] V2CriterionKind Kind,
    [property: JsonRequired] string Proposition,
    [property: JsonRequired] V2Materiality Materiality,
    [property: JsonRequired] IReadOnlyList<V2SourceProof> SourceProofs,
    [property: JsonRequired] IReadOnlyList<string> RequiredEvidenceIds,
    [property: JsonRequired] V2ReviewStatus ReviewStatus);

public sealed record V2GroundTruth(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string CaseId,
    [property: JsonRequired] string AuthoredBy,
    [property: JsonRequired] V2ReviewStatus ReviewStatus,
    [property: JsonRequired] IReadOnlyList<string> ReviewerIds,
    [property: JsonRequired] IReadOnlyList<V2Criterion> Criteria);

public sealed record V2CriterionAnswerability(
    [property: JsonRequired] string CriterionId,
    [property: JsonRequired] V2AnswerabilityState State,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] IReadOnlyList<string> RequiredEvidenceIds);

public sealed record V2Answerability(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string CaseId,
    [property: JsonRequired] string Comparator,
    [property: JsonRequired] string PreflightDigest,
    [property: JsonRequired] IReadOnlyList<V2CriterionAnswerability> Criteria);

/// <summary>Stages are separate; 'returned' must never imply model delivery.</summary>
public sealed record V2EvidenceEvent(
    [property: JsonRequired] string EvidenceId,
    [property: JsonRequired] V2EvidenceStage Stage,
    [property: JsonRequired] V2EvidenceStatus Status,
    [property: JsonRequired] string HostEventId,
    [property: JsonRequired] string Detail);

public sealed record V2EvidenceLedger(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string CaseId,
    [property: JsonRequired] string Comparator,
    [property: JsonRequired] string RunId,
    [property: JsonRequired] IReadOnlyList<V2EvidenceEvent> Events);

public sealed record V2CriterionGrade(
    [property: JsonRequired] string CriterionId,
    [property: JsonRequired] int? Grade,
    [property: JsonRequired] V2GroundingStatus Grounding,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] IReadOnlyList<string> ReportClaimRefs,
    [property: JsonRequired] IReadOnlyList<string> SourceProofRefs);

public sealed record V2Judgment(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string JudgmentId,
    [property: JsonRequired] string PacketId,
    [property: JsonRequired] string ReportSha256,
    [property: JsonRequired] string CaseId,
    [property: JsonRequired] string JudgeId,
    [property: JsonRequired] string RubricSha256,
    [property: JsonRequired] IReadOnlyList<V2CriterionGrade> Grades);

public sealed record V2Adjudication(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string AdjudicationId,
    [property: JsonRequired] string CaseId,
    [property: JsonRequired] IReadOnlyList<string> JudgmentIds,
    [property: JsonRequired] string ReviewerId,
    [property: JsonRequired] V2AdjudicationDisposition Disposition,
    [property: JsonRequired] string Explanation);

public sealed record V2JudgmentRecord(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string ExportSha256,
    [property: JsonRequired] string RubricSha256,
    [property: JsonRequired] IReadOnlyList<string> JudgmentIds,
    [property: JsonRequired] IReadOnlyList<string> AdjudicationIds);

public sealed record V2ExperimentManifest(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string ExperimentId,
    [property: JsonRequired] string ProtocolSha256,
    [property: JsonRequired] string GroundTruthSha256,
    [property: JsonRequired] string RubricSha256,
    [property: JsonRequired] IReadOnlyList<string> CaseIds,
    [property: JsonRequired] IReadOnlyList<string> Comparators,
    [property: JsonRequired] string ExecutionPolicy);

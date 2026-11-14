using System.Text.Json.Serialization;

namespace CodeArcheologist.EvaluationLab.V2;

/// <summary>
/// PR4c is a diagnostic companion to the frozen PR1 and PR4b contracts.
/// Its dispositions never rewrite either original external judgment.
/// </summary>
public static class V2Pr4cSchemas
{
    public const string Census = "v2-pr4c-dispute-census-v1";
    public const string Submission = "v2-pr4c-adjudicator-submission-v1";
    public const string Record = "v2-pr4c-adjudication-record-v1";
    public const string KeyFile = "DO-NOT-UPLOAD-pr4c-private-census.json";
    public const string RecordFile = "first-adjudication-record.json";
    public const string UploadFolder = "upload-these-to-adjudicator";
}

public enum V2Pr4cItemKind { Criterion, Claim }
public enum V2Pr4cResolutionSource { First, Second, Independent, None }

public sealed record V2Pr4cOpinion(
    int? Grade, V2GroundingStatus Grounding, V2Materiality? Materiality,
    IReadOnlyList<string> SourceProofRefs, IReadOnlyList<string> ReportClaimRefs,
    string Reason);

public sealed record V2Pr4cDispute(
    string DisputeId, string PacketId, V2Pr4cItemKind Kind, string ItemId,
    IReadOnlyList<string> Dimensions, V2Pr4cOpinion First, V2Pr4cOpinion Second,
    IReadOnlyList<string> AllowedProofRefs, IReadOnlyList<string> AllowedClaimRefs,
    bool FirstIsJudgeA);

public sealed record V2Pr4cCriticalConsensus(
    string PacketId, string ClaimId, V2GroundingStatus AgreedGrounding,
    string ReviewStatus);

public sealed record V2Pr4cReasonVariation(
    string PacketId, V2Pr4cItemKind Kind, string ItemId, string Status);

public sealed record V2Pr4cPacketExport(
    string PacketId, string PacketFile, string PacketSha256,
    string Origin, string OriginalPacketSha256);

public sealed record V2Pr4cCensus(
    string SchemaVersion, string BatchKeySha256, string JudgeARecordSha256,
    string JudgeBRecordSha256, string RubricSha256, string InstructionsSha256,
    string Qualification, IReadOnlyList<V2Pr4cDispute> Disputes,
    IReadOnlyList<V2Pr4cPacketExport> PublicPackets,
    IReadOnlyList<V2Pr4cCriticalConsensus> CriticalConsensusFlags,
    IReadOnlyList<V2Pr4cReasonVariation> ReasonVariations,
    IReadOnlyList<V2Pr4bCalibrationDifference> CalibrationDifferencesA,
    IReadOnlyList<V2Pr4bCalibrationDifference> CalibrationDifferencesB);

/// <summary>
/// Complete owner-operated third-party answer, including unresolved decisions.
/// Null grade is legitimate only when the rubric permits uncertainty.
/// </summary>
public sealed record V2Pr4cDecision(
    [property: JsonRequired] string DisputeId,
    [property: JsonRequired] V2AdjudicationDisposition Disposition,
    [property: JsonRequired] V2Pr4cResolutionSource ResolutionSource,
    [property: JsonRequired] int? Grade,
    [property: JsonRequired] V2GroundingStatus? Grounding,
    [property: JsonRequired] V2Materiality? Materiality,
    [property: JsonRequired] IReadOnlyList<string> SourceProofRefs,
    [property: JsonRequired] IReadOnlyList<string> ReportClaimRefs,
    [property: JsonRequired] string Explanation);

public sealed record V2Pr4cSubmission(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string CensusSha256,
    [property: JsonRequired] string ReviewerId,
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] IReadOnlyList<V2Pr4cDecision> Decisions);

public sealed record V2Pr4cRecord(
    string SchemaVersion, string CensusSha256, string SubmissionSha256,
    string BatchKeySha256, string JudgeARecordSha256, string JudgeBRecordSha256,
    string ReviewerId, string SessionId, long ImportedAtUnixMs,
    string Qualification, V2Pr4cSubmission Submission, string ContentSha256);

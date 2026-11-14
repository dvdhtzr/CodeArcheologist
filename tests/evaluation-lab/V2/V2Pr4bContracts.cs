using System.Text.Json.Serialization;

namespace CodeArcheologist.EvaluationLab.V2;

/// <summary>Strict PR4b-only contracts. Does not alter the frozen V2 PR1 types.</summary>
public static class V2Pr4bSchemas
{
    public const string Calibration = "v2-pr4b-calibration-key-v1";
    public const string Batch = "v2-pr4b-blinded-batch-v1";
    public const string Submission = "v2-pr4b-blind-submission-v1";
    public const string Record = "v2-pr4b-judge-record-v1";
    public const string KeyFile = "DO-NOT-UPLOAD-pr4b-private-key.json";
    public const string UploadFolder = "upload-these-to-judge";
    public const string RecordA = "judge-A-record.json";
    public const string RecordB = "judge-B-record.json";
}

public sealed record V2Pr4bClaimGrade(
    [property: JsonRequired] string ClaimId,
    [property: JsonRequired] V2GroundingStatus Grounding,
    [property: JsonRequired] V2Materiality Materiality,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] IReadOnlyList<string> SourceProofRefs);

public sealed record V2Pr4bAssessment(
    [property: JsonRequired] string PacketId,
    [property: JsonRequired] IReadOnlyList<V2CriterionGrade> Grades,
    [property: JsonRequired] IReadOnlyList<V2Pr4bClaimGrade> Claims);

/// <summary>
/// Owner-supplied, pre-frozen expected answers for disjoint calibration cases.
/// The authored/reviewed identities are declarations, NOT identity proof.
/// </summary>
public sealed record V2Pr4bCalibrationKey(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string RubricSha256,
    [property: JsonRequired] string AuthoredBy,
    [property: JsonRequired] string ReviewedBy,
    [property: JsonRequired] IReadOnlyList<V2Pr4bAssessment> Expected);

public sealed record V2Pr4bCriterionScope(
    string CriterionId, V2CriterionKind Kind, IReadOnlyList<string> ProofRefs);

public sealed record V2Pr4bPacket(
    string PacketId, string PacketFile, string PacketSha256,
    string Origin, string OriginalPacketId, string OriginalKeySha256,
    string CaseId, string CaseSha256, string GroundTruthSha256,
    string SourcePacketSha256, string ReportSha256,
    V2Pr4aOutcome Outcome, IReadOnlyList<V2Pr4bCriterionScope> Criteria,
    IReadOnlyList<string> ClaimIds);

public sealed record V2Pr4bBatchKey(
    string SchemaVersion, string RubricSha256, string ReviewManifestSha256,
    string ReviewImportSha256, string OfficialExportKeySha256,
    string CalibrationExportKeySha256, string CalibrationKeySha256,
    string InstructionsSha256, string Qualification,
    IReadOnlyList<V2Pr4bPacket> Packets,
    IReadOnlyList<V2Pr4bAssessment> ExpectedCalibration);

public sealed record V2Pr4bSubmission(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string BatchKeySha256,
    [property: JsonRequired] string JudgeId,
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] IReadOnlyList<V2Pr4bAssessment> Assessments);

public sealed record V2Pr4bCalibrationDifference(
    string PacketId, string ItemId, string Issue);

public sealed record V2Pr4bJudgeRecord(
    string SchemaVersion, string BatchKeySha256, string SubmissionSha256,
    string JudgeSlot, string JudgeId, string SessionId,
    long ImportedAtUnixMs, int CalibrationDecisions, int CalibrationAgreements,
    IReadOnlyList<V2Pr4bCalibrationDifference> CalibrationDifferences,
    string Qualification, V2Pr4bSubmission Submission);

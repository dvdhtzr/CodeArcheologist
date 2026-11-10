using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeArcheologist.EvaluationLab;

public static class LabSchema
{
    public const string Case = "c6c-case-v1";
    public const string Labels = "c6c-labels-v1";
    public const string Config = "c6c-lab-config-v1";
    public const string Report = "c6c-lab-report-v1";
    public const string Score = "c6c-lab-score-v1";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public enum CaseCategory { Fixture, ChangeExperiment, Curated, HeldOut }
public enum LabelStatus { Draft, OwnerApproved }

/// <summary>Where the case's source comes from. C6c-1 supports in-repo fixtures only.</summary>
public sealed record CaseSource(string Kind, string Directory, IReadOnlyDictionary<string, string> FileSha256);

/// <summary>
/// Entry point selected by written-syntax location, resolved at run time to
/// the analyzer's graph node; never a model-chosen or free-form identifier.
/// </summary>
public sealed record EntryPointSelector(string Kind, string FilePath, int Line);

public sealed record LabCase(string SchemaVersion, string Id, CaseCategory Category, string Title,
    string Purpose, CaseSource Source, EntryPointSelector EntryPoint, bool DiagnosticRepeat);

/// <summary>Labeler-chosen source location; checked to be citable, never derived from output.</summary>
public sealed record LabelAnchor(string FilePath, int Line);

public sealed record RequiredObservation(string Id, string Description, LabelAnchor Anchor);
public sealed record RequiredUncertainty(string Id, string Description);
public sealed record ForbiddenClaim(string Id, string Description, string Category);
public sealed record MaterialIssue(string Id, string Description);

public sealed record LabLabels(string SchemaVersion, string CaseId, LabelStatus Status, string AuthoredBy,
    IReadOnlyList<RequiredObservation> RequiredObservations,
    IReadOnlyList<RequiredUncertainty> RequiredUncertainties,
    IReadOnlyList<ForbiddenClaim> ForbiddenClaims,
    IReadOnlyList<MaterialIssue> MaterialIssues);

/// <summary>
/// Declared budgets and thresholds. Budgets must EQUAL the host constants;
/// the file declares and records them before scoring, it never reconfigures the host.
/// </summary>
public sealed record LabBudgets(string Model, int AttemptDeadlineSeconds, int RoleTimeoutSeconds,
    int AssessmentAttemptsPerRole, int MaxToolCallsPerRole, int MaxEvidenceItemsPerRole);

public sealed record LabThresholds(double EligibleCompleteWorkflows, double MaterialIssueDetection,
    double MaterialObjectionCoverage, double ActionableRecommendations,
    double UsefulnessGainOverGateBPoints, int AllowedIntegrityBreaches);

public sealed record LabConfig(string SchemaVersion, LabBudgets Budgets, int PrimaryTrials,
    int DiagnosticExtraTrials, IReadOnlyList<string> Comparators, LabThresholds ProvisionalThresholds,
    string ThresholdStatus);

public sealed record LoadedCase(LabCase Case, LabLabels Labels, string Directory);

public static class Comparator
{
    public const string Baseline = "deterministic-baseline";
    public const string GateB = "gate-b";
    public const string GateC = "gate-c";
    public static readonly string[] All = [Baseline, GateB, GateC];
}

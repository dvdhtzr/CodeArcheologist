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
    public const string Curated = "c6c-curated-v1";

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

/// <summary>
/// Where the case's source comes from: an in-repo fixture (bytes pinned here)
/// or a curated checkout (pinned by the curated manifest: commit, licence
/// hash, subtree and snapshot fingerprint).
/// </summary>
public sealed record CaseSource(string Kind, string Directory, IReadOnlyDictionary<string, string> FileSha256,
    string? Repository = null, IReadOnlyDictionary<string, string>? BeforeFileSha256 = null);

/// <summary>
/// C6c-2b deliberate change: an in-repo before/after fixture pair with one
/// known edit. Impact is judged on the entry's normalized evidence (relation
/// kinds and targets, ignoring hashes/lines/IDs); ImpactTarget optionally names
/// the edited method in the BEFORE version for the ADR 0015 one-hop caller check.
/// </summary>
public sealed record ChangeSpec(string Edit, string Impact, EntryPointSelector BeforeEntryPoint,
    LabelAnchor? ImpactTarget = null);

/// <summary>
/// Entry point selected by written-syntax location, resolved at run time to
/// the analyzer's graph node; never a model-chosen or free-form identifier.
/// </summary>
public sealed record EntryPointSelector(string Kind, string FilePath, int Line);

public static class CaseExpectation
{
    public const string Investigate = "investigate";
    /// <summary>The analyzer must report zero supported HTTP candidates; nothing is invented.</summary>
    public const string NoSupportedCandidate = "no-supported-candidate";
    /// <summary>Capture must fail closed on a snapshot/analysis limit and leave no trusted record.</summary>
    public const string CaptureLimitFailClosed = "capture-limit-fail-closed";
    public static readonly string[] All = [Investigate, NoSupportedCandidate, CaptureLimitFailClosed];
}

public sealed record LabCase(string SchemaVersion, string Id, CaseCategory Category, string Title,
    string Purpose, CaseSource Source, EntryPointSelector? EntryPoint, bool DiagnosticRepeat,
    string Expectation = CaseExpectation.Investigate, ChangeSpec? Change = null);

/// <summary>One owner-acquired real repository, pinned exactly.</summary>
public sealed record CuratedRepository(string Id, string Repository, string Url, string CommitSha,
    string License, string LicensePath, string LicenseSha256, string Subtree, string SnapshotFingerprint,
    int SnapshotFileCount, string Group, string Purpose);

public sealed record CuratedManifest(string SchemaVersion, IReadOnlyList<CuratedRepository> Repositories);

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

public sealed record LoadedCase(LabCase Case, LabLabels Labels, string Directory,
    CuratedRepository? Curated = null);

public static class Comparator
{
    public const string Baseline = "deterministic-baseline";
    public const string GateB = "gate-b";
    public const string GateC = "gate-c";
    public static readonly string[] All = [Baseline, GateB, GateC];
}

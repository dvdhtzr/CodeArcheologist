using System.Text.Json;
using System.Text.RegularExpressions;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.EvaluationLab;

public sealed record CorpusReport(IReadOnlyList<LoadedCase> Cases, LabConfig Config,
    IReadOnlyList<string> Errors, int DraftLabels, int ApprovedLabels)
{
    public bool Valid => Errors.Count == 0;
}

/// <summary>
/// Static, model-free corpus validation: schemas, identities, pinned fixture
/// bytes, label shape, and the budget declaration against host constants.
/// Satisfiability of anchors (citable in the evidence package) is checked
/// separately by the harness because it requires a snapshot capture.
/// </summary>
public static partial class CorpusValidator
{
    [GeneratedRegex("^[A-Z][0-9]{2}-[a-z0-9-]{3,60}$")]
    private static partial Regex CaseId();

    [GeneratedRegex("^[A-Z][0-9]{1,2}$")]
    private static partial Regex ItemId();

    private static readonly string[] ForbiddenCategories =
        ["runtime-verified", "compiled-binding", "registered-route", "di-resolution",
         "authorization-inferred", "instruction-following", "completeness"];

    public static CorpusReport Validate(string evalRoot)
    {
        var errors = new List<string>();
        var cases = new List<LoadedCase>();
        var config = Read<LabConfig>(Path.Combine(evalRoot, "lab-config.json"), errors);
        if (config is not null) ValidateConfig(config, errors);

        var casesRoot = Path.Combine(evalRoot, "cases");
        if (!Directory.Exists(casesRoot)) errors.Add("cases directory missing");
        else
            foreach (var dir in Directory.GetDirectories(casesRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                var labCase = Read<LabCase>(Path.Combine(dir, "case.json"), errors);
                var labels = Read<LabLabels>(Path.Combine(dir, "labels.json"), errors);
                if (labCase is null || labels is null) continue;
                var before = errors.Count;
                ValidateCase(name, dir, labCase, labels, errors);
                if (errors.Count == before) cases.Add(new(labCase, labels, dir));
            }

        var ids = cases.Select(c => c.Case.Id).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) errors.Add("duplicate case IDs");
        return new(cases, config!, errors,
            cases.Count(c => c.Labels.Status == LabelStatus.Draft),
            cases.Count(c => c.Labels.Status == LabelStatus.OwnerApproved));
    }

    private static void ValidateConfig(LabConfig config, List<string> errors)
    {
        if (config.SchemaVersion != LabSchema.Config) errors.Add("lab-config: unsupported schema");
        var b = config.Budgets;
        // Declared budgets must equal what the host actually enforces.
        void Same<T>(string name, T declared, T host)
        {
            if (!EqualityComparer<T>.Default.Equals(declared, host))
                errors.Add($"lab-config: budget {name} declares {declared} but the host enforces {host}");
        }
        Same("model", b.Model, GateC3IndependentRunner.ModelId);
        Same("reviewer model", b.Model, GateC4ReconciliationRunner.ModelId);
        Same("attemptDeadlineSeconds", b.AttemptDeadlineSeconds,
            (int)GateC6ExecutionContract.AttemptDeadline.TotalSeconds);
        Same("roleTimeoutSeconds", b.RoleTimeoutSeconds, GateC3IndependentRunner.RoleTimeoutSeconds);
        Same("reviewer roleTimeoutSeconds", b.RoleTimeoutSeconds, GateC4ReconciliationRunner.RoleTimeoutSeconds);
        Same("assessmentAttemptsPerRole", b.AssessmentAttemptsPerRole, GateC3IndependentRunner.AssessmentAttempts);
        Same("maxToolCallsPerRole", b.MaxToolCallsPerRole, GateCEvidenceSession.MaxToolCalls);
        Same("maxEvidenceItemsPerRole", b.MaxEvidenceItemsPerRole, GateCEvidenceSession.MaxTotalItems);
        if (config.PrimaryTrials != 1 || config.DiagnosticExtraTrials != 2)
            errors.Add("lab-config: trials must be 1 primary + 2 diagnostic extra (Gate C v1)");
        if (!config.Comparators.SequenceEqual(Comparator.All))
            errors.Add("lab-config: comparators must be baseline, gate-b, gate-c");
        if (config.ProvisionalThresholds.AllowedIntegrityBreaches != 0)
            errors.Add("lab-config: integrity breaches must be zero-tolerance");
        if (config.ThresholdStatus is not ("provisional" or "owner-approved-for-scoring"))
            errors.Add("lab-config: invalid threshold status");
    }

    private static void ValidateCase(string dirName, string dir, LabCase c, LabLabels l, List<string> errors)
    {
        var p = dirName + ": ";
        if (c.SchemaVersion != LabSchema.Case) errors.Add(p + "unsupported case schema");
        if (c.Id != dirName || !CaseId().IsMatch(c.Id)) errors.Add(p + "case id must equal directory name");
        if (c.Category != CaseCategory.Fixture)
            errors.Add(p + "only fixture cases are supported in C6c-1");
        if (string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Purpose))
            errors.Add(p + "title and purpose required");
        if (c.Source.Kind != "in-repo-fixture" || c.Source.Directory != "source")
            errors.Add(p + "source must be the in-repo 'source' directory");
        if (c.EntryPoint.Kind is not ("http-endpoint-attribute" or "http-minimal-mapping") || c.EntryPoint.Line < 1)
            errors.Add(p + "unsupported entry point selector");

        // Pinned fixture bytes: every file listed, nothing unlisted, hashes exact.
        var sourceDir = Path.Combine(dir, "source");
        var actual = Directory.Exists(sourceDir)
            ? Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(sourceDir, f).Replace('\\', '/'),
                    f => LabSchema.Sha256(File.ReadAllBytes(f)), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        if (actual.Count == 0) errors.Add(p + "fixture source missing");
        if (!actual.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(c.Source.FileSha256.Keys))
            errors.Add(p + "fixture file list differs from the pinned manifest");
        foreach (var (file, hash) in c.Source.FileSha256)
            if (actual.TryGetValue(file, out var real) && real != hash)
                errors.Add(p + $"fixture file {file} does not match its pinned SHA-256");
        if (!c.Source.FileSha256.ContainsKey(c.EntryPoint.FilePath))
            errors.Add(p + "entry point file is not a pinned fixture file");

        if (l.SchemaVersion != LabSchema.Labels) errors.Add(p + "unsupported labels schema");
        if (l.CaseId != c.Id) errors.Add(p + "labels belong to another case");
        if (l.Status == LabelStatus.OwnerApproved && l.AuthoredBy is not ("owner" or "assistant-draft-owner-reviewed"))
            errors.Add(p + "approved labels must record owner review");
        if (l.Status == LabelStatus.Draft && l.AuthoredBy != "assistant-draft")
            errors.Add(p + "draft labels must be marked assistant-draft");
        if (l.RequiredObservations.Count == 0 && l.RequiredUncertainties.Count == 0)
            errors.Add(p + "labels must require at least one observation or uncertainty");
        var itemIds = l.RequiredObservations.Select(x => x.Id).Concat(l.RequiredUncertainties.Select(x => x.Id))
            .Concat(l.ForbiddenClaims.Select(x => x.Id)).Concat(l.MaterialIssues.Select(x => x.Id)).ToArray();
        if (itemIds.Any(id => !ItemId().IsMatch(id)) || itemIds.Distinct(StringComparer.Ordinal).Count() != itemIds.Length)
            errors.Add(p + "label item IDs must be unique short IDs");
        foreach (var o in l.RequiredObservations)
            if (!c.Source.FileSha256.ContainsKey(o.Anchor.FilePath) || o.Anchor.Line < 1)
                errors.Add(p + $"observation {o.Id} anchor is not in a pinned fixture file");
        if (l.ForbiddenClaims.Any(f => !ForbiddenCategories.Contains(f.Category)))
            errors.Add(p + "unknown forbidden-claim category");
        if (itemIds.Length == 0 || new[] { l.RequiredObservations.Select(x => x.Description),
                l.RequiredUncertainties.Select(x => x.Description), l.ForbiddenClaims.Select(x => x.Description),
                l.MaterialIssues.Select(x => x.Description) }.SelectMany(x => x).Any(string.IsNullOrWhiteSpace))
            errors.Add(p + "label descriptions required");
    }

    private static T? Read<T>(string path, List<string> errors) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), LabSchema.Json)
                ?? throw new JsonException("empty");
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            errors.Add(Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path) +
                ": unreadable or invalid (" + error.GetType().Name + ")");
            return null;
        }
    }
}

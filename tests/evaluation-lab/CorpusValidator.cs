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

        var manifestPath = Path.Combine(evalRoot, "curated", "repositories.json");
        var manifest = File.Exists(manifestPath) ? Read<CuratedManifest>(manifestPath, errors) : null;
        var curated = new Dictionary<string, CuratedRepository>(StringComparer.Ordinal);
        if (manifest is not null)
        {
            ValidateManifest(manifest, curated, errors);
            ValidatePerFileManifests(Path.Combine(evalRoot, "curated", "manifests"), curated.Values, errors);
        }

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
                ValidateCase(name, dir, labCase, labels, curated, errors);
                if (errors.Count == before)
                    cases.Add(new(labCase, labels, dir, labCase.Source.Repository is { } r ? curated[r] : null));
            }

        foreach (var unused in curated.Keys.Where(k => !cases.Any(c => c.Case.Source.Repository == k)))
            errors.Add($"curated repository {unused} has no case");
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

    [GeneratedRegex("^R[0-9]{2}$")] private static partial Regex CuratedId();
    [GeneratedRegex("^[0-9a-f]{40}$")] private static partial Regex CommitSha();
    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex Sha256Hex();
    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")] private static partial Regex RepoName();

    /// <summary>A relative path with no rooting, traversal or backslashes.</summary>
    private static bool SafeRelative(string path, bool allowEmpty) =>
        (allowEmpty && path.Length == 0) ||
        (path.Length is > 0 and <= 512 && !Path.IsPathRooted(path) && !path.Contains('\\') &&
         !path.Contains(':') && path.Split('/').All(seg => seg.Length > 0 && seg != "." && seg != ".."));

    private static void ValidateManifest(CuratedManifest m, Dictionary<string, CuratedRepository> byId,
        List<string> errors)
    {
        if (m.SchemaVersion != LabSchema.Curated) errors.Add("curated manifest: unsupported schema");
        foreach (var r in m.Repositories)
        {
            var p = "curated " + r.Id + ": ";
            if (!CuratedId().IsMatch(r.Id) || !byId.TryAdd(r.Id, r)) { errors.Add(p + "invalid or duplicate id"); continue; }
            if (!RepoName().IsMatch(r.Repository) || r.Url != "https://github.com/" + r.Repository)
                errors.Add(p + "url must be https://github.com/<owner>/<repo>");
            if (!CommitSha().IsMatch(r.CommitSha)) errors.Add(p + "commit must be a full 40-hex SHA");
            if (!Sha256Hex().IsMatch(r.LicenseSha256) || !SafeRelative(r.LicensePath, false))
                errors.Add(p + "licence path/hash invalid");
            if (r.License is not ("MIT" or "Apache-2.0" or "BSD-3-Clause"))
                errors.Add(p + "licence must be an approved permissive licence");
            if (!SafeRelative(r.Subtree, true)) errors.Add(p + "subtree must be a safe relative path");
            if (r.Group != "capture-limit" && (!Sha256Hex().IsMatch(r.SnapshotFingerprint) || r.SnapshotFileCount < 1))
                errors.Add(p + "snapshot fingerprint must be pinned");
            if (r.Group == "capture-limit" && r.SnapshotFingerprint.Length != 0)
                errors.Add(p + "a capture-limit repository has no trusted fingerprint");
            if (r.Group is not ("attribute-controllers" or "minimal-api" or "unsupported-convention" or "capture-limit"))
                errors.Add(p + "unknown group");
            if (string.IsNullOrWhiteSpace(r.Purpose)) errors.Add(p + "purpose required");
        }
    }

    /// <summary>
    /// Offline consistency: each pinned per-file manifest must reproduce the
    /// pinned snapshot fingerprint under the current snapshot policy.
    /// </summary>
    private static void ValidatePerFileManifests(string dir, IEnumerable<CuratedRepository> repos, List<string> errors)
    {
        foreach (var r in repos.Where(r => r.Group != "capture-limit"))
        {
            var path = Path.Combine(dir, r.Id + ".tsv");
            if (!File.Exists(path)) { errors.Add($"curated {r.Id}: per-file manifest missing"); continue; }
            var files = new List<SnapshotFile>();
            var exclusions = new List<SnapshotExclusion>();
            foreach (var line in File.ReadAllLines(path).Where(l => l.Length > 0))
            {
                var f = line.Split('\t');
                if (f is ["file", var p, var size, var sha] && long.TryParse(size, out var n)) files.Add(new(p, n, sha));
                else if (f is ["excluded", var ep, var reason]) exclusions.Add(new(ep, reason));
                else { errors.Add($"curated {r.Id}: malformed per-file manifest line"); break; }
            }
            if (SnapshotPolicy.Fingerprint(files, exclusions) != r.SnapshotFingerprint || files.Count != r.SnapshotFileCount)
                errors.Add($"curated {r.Id}: per-file manifest does not reproduce the pinned fingerprint");
            // Portability: a pinned subtree must capture identically on case-insensitive
            // filesystems (Windows, default macOS). Any file or directory path that
            // differs only by case would be merged there and yield a different snapshot.
            var prefixes = files.Select(f => f.Path).Concat(exclusions.Select(e => e.Path))
                .SelectMany(path => path.Split('/').Select((_, i) => string.Join('/', path.Split('/').Take(i + 1))))
                .Distinct(StringComparer.Ordinal).ToArray();
            var collision = prefixes.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (collision is not null)
                errors.Add($"curated {r.Id}: case-insensitive path collision ({string.Join(" vs ", collision)}); choose a subtree without it");
        }
    }

    private static void ValidateCase(string dirName, string dir, LabCase c, LabLabels l,
        IReadOnlyDictionary<string, CuratedRepository> curated, List<string> errors)
    {
        var p = dirName + ": ";
        if (c.SchemaVersion != LabSchema.Case) errors.Add(p + "unsupported case schema");
        if (c.Id != dirName || !CaseId().IsMatch(c.Id)) errors.Add(p + "case id must equal directory name");
        if (c.Category is not (CaseCategory.Fixture or CaseCategory.Curated))
            errors.Add(p + "only fixture and curated cases are supported");
        if (string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Purpose))
            errors.Add(p + "title and purpose required");
        if (!CaseExpectation.All.Contains(c.Expectation)) errors.Add(p + "unknown expectation");
        if ((c.Expectation == CaseExpectation.Investigate) != (c.EntryPoint is not null))
            errors.Add(p + "an entry point is required exactly when the expectation is investigate");
        if (c.EntryPoint is { } e && (e.Kind is not ("http-endpoint-attribute" or "http-minimal-mapping") ||
                e.Line < 1 || !SafeRelative(e.FilePath, false)))
            errors.Add(p + "unsupported entry point selector");
        if (c.Category == CaseCategory.Curated)
        {
            ValidateCuratedCase(p, c, l, curated, errors);
            ValidateLabels(p, c, l, file => SafeRelative(file, false), errors);
            return;
        }
        if (c.Expectation != CaseExpectation.Investigate) errors.Add(p + "fixtures must be investigate cases");
        if (c.Source.Kind != "in-repo-fixture" || c.Source.Directory != "source" || c.Source.Repository is not null)
            errors.Add(p + "source must be the in-repo 'source' directory");

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
            {
                // Diagnose (never accept) the common Windows checkout conversion.
                var bytes = File.ReadAllBytes(Path.Combine(sourceDir, file));
                var lf = System.Text.Encoding.UTF8.GetBytes(
                    System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n"));
                errors.Add(p + $"fixture file {file} does not match its pinned SHA-256" +
                    (LabSchema.Sha256(lf) == hash
                        ? " (line endings were converted to CRLF on checkout; see .gitattributes and eval/README.md)"
                        : ""));
            }
        if (c.EntryPoint is { } entry && !c.Source.FileSha256.ContainsKey(entry.FilePath))
            errors.Add(p + "entry point file is not a pinned fixture file");
        ValidateLabels(p, c, l, file => c.Source.FileSha256.ContainsKey(file), errors);
    }

    private static void ValidateCuratedCase(string p, LabCase c, LabLabels l,
        IReadOnlyDictionary<string, CuratedRepository> curated, List<string> errors)
    {
        if (c.Source.Kind != "curated-checkout" || c.Source.Directory.Length != 0 || c.Source.FileSha256.Count != 0 ||
            c.Source.Repository is not { } id || !curated.TryGetValue(id, out var repo))
        {
            errors.Add(p + "curated source must name a pinned manifest repository");
            return;
        }
        if (!c.Id.StartsWith(id + "-", StringComparison.Ordinal)) errors.Add(p + "curated case id must start with its repository id");
        var expected = repo.Group switch
        {
            "capture-limit" => CaseExpectation.CaptureLimitFailClosed,
            "unsupported-convention" => CaseExpectation.NoSupportedCandidate,
            _ => CaseExpectation.Investigate
        };
        if (c.Expectation != expected) errors.Add(p + $"expectation must be {expected} for group {repo.Group}");
        if (c.Expectation != CaseExpectation.Investigate && l.RequiredObservations.Count != 0)
            errors.Add(p + "non-investigate cases cannot require cited observations");
    }

    private static void ValidateLabels(string p, LabCase c, LabLabels l, Func<string, bool> anchorFileAllowed,
        List<string> errors)
    {

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
            if (!anchorFileAllowed(o.Anchor.FilePath) || o.Anchor.Line < 1)
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

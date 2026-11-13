using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeArcheologist.EvaluationLab.V2;

public sealed record V2ValidationResult(int CaseCount, IReadOnlyList<string> Errors)
{
    public bool Valid => Errors.Count == 0;

    // PR1 establishes only structural validity. Qualification is a later gate.
    public bool ReadyForScoring => false;
}

/// <summary>
/// Fail-closed structural checks only. Source truth, answerability and
/// scientific qualification are explicitly out of scope for PR1.
/// Never reads or mutates a production workspace or imported repository.
/// </summary>
public static class V2CorpusValidator
{
    private const long MaxDocumentBytes = 1024 * 1024;

    public static V2ValidationResult Validate(string root)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new(0, ["V2 root directory does not exist."]);

        if (IsLink(root))
            return new(0, ["V2 root cannot be a symbolic link or reparse point."]);

        var casesRoot = Path.Combine(root, "cases");
        if (!Directory.Exists(casesRoot))
            return new(0, errors); // Empty PR1 layout is valid but NOT scoreable.
        if (IsLink(casesRoot))
            return new(0, ["V2 cases directory cannot be a link."]);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        try
        {
            foreach (var dir in Directory.GetDirectories(casesRoot).Order(StringComparer.Ordinal))
            {
                var folder = Path.GetFileName(dir);
                if (IsLink(dir) || !ValidId(folder))
                {
                    errors.Add($"Invalid or linked V2 case directory: {folder}");
                    continue;
                }
                var file = Path.Combine(dir, "case.json");
                var labelsFile = Path.Combine(dir, "ground-truth.json");
                var item = ReadStrict<V2Case>(file, errors);
                var truth = ReadStrict<V2GroundTruth>(labelsFile, errors);
                if (item is null || truth is null) continue;
                count++;
                if (!ids.Add(item.Id ?? ""))
                    errors.Add($"Duplicate V2 case ID: {item.Id}");
                if (!string.Equals(folder, item.Id, StringComparison.Ordinal))
                    errors.Add($"{folder}: directory name must equal case ID.");
                ValidateCase(item, truth, errors, folder);
            }
            foreach (var entry in Directory.GetFiles(casesRoot))
                errors.Add($"Unexpected file in V2 cases root: {Path.GetFileName(entry)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Cannot inspect V2 cases: {e.Message}");
        }
        return new(count, errors);
    }

    public static T? ReadStrict<T>(string path, List<string> errors) where T : class
    {
        if (!File.Exists(path))
        {
            errors.Add($"Required V2 document missing: {path}");
            return null;
        }
        try
        {
            if (IsLink(path))
                throw new InvalidDataException("linked document is not allowed");
            if (new FileInfo(path).Length > MaxDocumentBytes)
                throw new InvalidDataException("document exceeds size limit");
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), V2Schemas.Json);
            if (value is null) throw new InvalidDataException("document is null");
            return value;
        }
        catch (Exception e) when (e is JsonException or IOException or
                                   UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            errors.Add($"Invalid V2 document {path}: {e.Message}");
            return null;
        }
    }

    private static void ValidateCase(V2Case item, V2GroundTruth truth, List<string> errors, string folder)
    {
        void Fail(string detail) => errors.Add($"{folder}: {detail}");
        if (item.SchemaVersion != V2Schemas.Case) Fail("unsupported case schema");
        if (!ValidId(item.Id)) Fail("invalid case ID");
        if (!Enum.IsDefined(item.Task)) Fail("unknown task family");
        if (!Enum.IsDefined(item.Partition)) Fail("unknown corpus partition");
        if (string.IsNullOrWhiteSpace(item.DeveloperQuestion)) Fail("developer question required");
        if (string.IsNullOrWhiteSpace(item.IntendedAudience)) Fail("intended audience required");
        if (item.Task == V2TaskFamily.ChangeImpact && item.Before is null)
            Fail("change-impact tasks require a before snapshot");
        if (item.Task != V2TaskFamily.ChangeImpact && item.Before is not null)
            Fail("before snapshot only allowed for change-impact tasks");
        ValidateSource(item.After, "after", Fail);
        if (item.Before is not null)
        {
            ValidateSource(item.Before, "before", Fail);
            if (item.After is not null && item.Before.SnapshotSha256 == item.After.SnapshotSha256)
                Fail("change-impact snapshots must be distinct");
        }
        if (item.AuthorizedPaths is null || item.AuthorizedPaths.Count == 0 ||
            item.AuthorizedPaths.Any(p => !SafeRelativePath(p)) ||
            item.AuthorizedPaths.Distinct(StringComparer.Ordinal).Count() != item.AuthorizedPaths.Count)
            Fail("authorized paths must be nonempty, distinct safe relative paths");
        if (item.CriterionIds is null || item.CriterionIds.Count == 0 ||
            item.CriterionIds.Any(id => !ValidId(id)) ||
            item.CriterionIds.Distinct(StringComparer.Ordinal).Count() != item.CriterionIds.Count)
            Fail("criterion IDs must be nonempty, valid and unique");

        if (truth.SchemaVersion != V2Schemas.GroundTruth) Fail("unsupported ground-truth schema");
        if (truth.CaseId != item.Id) Fail("ground truth refers to a different case");
        if (string.IsNullOrWhiteSpace(truth.AuthoredBy)) Fail("ground-truth author required");
        if (!Enum.IsDefined(truth.ReviewStatus)) Fail("unknown review status");
        if (truth.ReviewerIds is null ||
            truth.ReviewerIds.Any(string.IsNullOrWhiteSpace) ||
            truth.ReviewerIds.Distinct(StringComparer.Ordinal).Count() != truth.ReviewerIds.Count)
            Fail("reviewer identities must be distinct and nonblank");
        if (truth.ReviewStatus == V2ReviewStatus.IndependentlyReviewed && truth.ReviewerIds?.Count == 0)
            Fail("independently reviewed ground truth requires a reviewer identity");
        if (truth.Criteria is null || truth.Criteria.Count == 0)
        {
            Fail("ground truth must contain criteria");
            return;
        }
        if (truth.Criteria.Any(c => c is null || !ValidId(c.Id)) ||
            truth.Criteria.Select(c => c?.Id).Distinct(StringComparer.Ordinal).Count() != truth.Criteria.Count)
            Fail("ground-truth criterion IDs must be valid and unique");
        // A null criterion is invalid input, not permission to crash the validator.
        if (truth.Criteria.Any(c => c is null))
        {
            Fail("ground truth contains a null criterion");
            return;
        }
        if (item.CriterionIds is not null &&
            !truth.Criteria.Select(c => c.Id).Order(StringComparer.Ordinal)
                .SequenceEqual(item.CriterionIds.Order(StringComparer.Ordinal)))
            Fail("case and ground-truth criterion IDs differ");
        foreach (var c in truth.Criteria)
        {
            if (item.Task != V2TaskFamily.ChangeImpact && c.Kind == V2CriterionKind.ChangeObservation)
                Fail($"criterion {c.Id}: historical change observations require a change-impact task");
            if (!Enum.IsDefined(c.Kind) || !Enum.IsDefined(c.Materiality) ||
                !Enum.IsDefined(c.ReviewStatus))
                Fail($"criterion {c.Id}: invalid enum value");
            if (string.IsNullOrWhiteSpace(c.Proposition))
                Fail($"criterion {c.Id}: proposition required");
            if (c.RequiredEvidenceIds is null ||
                c.RequiredEvidenceIds.Any(id => !ValidId(id)) ||
                c.RequiredEvidenceIds.Distinct(StringComparer.Ordinal).Count() != c.RequiredEvidenceIds.Count)
                Fail($"criterion {c.Id}: invalid evidence IDs");
            if (c.SourceProofs is null)
            {
                Fail($"criterion {c.Id}: source proofs required");
                continue;
            }
            foreach (var proof in c.SourceProofs)
            {
                if (proof is null)
                {
                    Fail($"criterion {c.Id}: null source proof");
                    continue;
                }
                if (!SafeRelativePath(proof.FilePath) || !IsSha256(proof.FileSha256) ||
                    proof.StartLine < 1 || proof.EndLine < proof.StartLine)
                    Fail($"criterion {c.Id}: invalid source proof identity/range");
                if (!Enum.IsDefined(proof.Snapshot))
                    Fail($"criterion {c.Id}: invalid source proof snapshot");
                var source = proof.Snapshot == V2SnapshotSide.Before ? item.Before : item.After;
                if (source?.Files is null || !source.Files.TryGetValue(proof.FilePath ?? "", out var sha) ||
                    !string.Equals(sha, proof.FileSha256, StringComparison.Ordinal))
                    Fail($"criterion {c.Id}: source proof does not match the declared snapshot file digest");
            }
        }
    }

    private static void ValidateSource(V2SourceIdentity? source, string side, Action<string> fail)
    {
        if (source is null)
        {
            fail($"{side} source identity required");
            return;
        }
        if (!IsSha256(source.SnapshotSha256))
            fail($"{side} snapshot fingerprint must be a SHA-256 digest");
        if (source.RepositoryCommitSha is not null &&
            !Regex.IsMatch(source.RepositoryCommitSha, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
            fail($"{side} repository commit must be a lowercase 40-character SHA");
        if (source.Files is null || source.Files.Count == 0)
        {
            fail($"{side} source must include file digests");
            return;
        }
        foreach (var (path, digest) in source.Files)
            if (!SafeRelativePath(path) || !IsSha256(digest))
                fail($"{side} source contains an unsafe path or invalid SHA-256 digest: {path}");
    }

    private static bool ValidId(string? id) =>
        !string.IsNullOrEmpty(id) && Regex.IsMatch(id, "^[A-Za-z][A-Za-z0-9-]{2,80}$", RegexOptions.CultureInvariant);

    private static bool IsSha256(string? value) =>
        value is not null && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private static bool SafeRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.StartsWith('/') &&
        !path.Contains('\\') && !path.Contains(':') && !path.Contains('\0') &&
        path.Split('/').All(s => s.Length > 0 && s is not ("." or ".."));

    private static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}

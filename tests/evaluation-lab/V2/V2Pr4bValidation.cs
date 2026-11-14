using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeArcheologist.EvaluationLab.V2;

/// <summary>Model-free PR4b identity, scope, and strict judgment validation.</summary>
internal static class V2Pr4bValidation
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex ShaPattern = new("^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex IdPattern = new("^[A-Za-z][A-Za-z0-9-]{2,80}$",
        RegexOptions.CultureInvariant);
    internal static string Sha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string ShaFile(string path) => Sha(File.ReadAllBytes(path));
    internal static bool IsHash(string? h) => h is not null && ShaPattern.IsMatch(h);
    internal static bool IsId(string? id) => id is not null && IdPattern.IsMatch(id);
    internal static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidDataException(detail);
    }
    internal static T Read<T>(string path, int maxBytes = 1024 * 1024) where T : class
    {
        Require(File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0,
            "Missing or linked input: " + Path.GetFileName(path));
        var length = new FileInfo(path).Length;
        Require(length > 0 && length <= maxBytes, "Invalid input size: " + Path.GetFileName(path));
        var text = Utf8.GetString(File.ReadAllBytes(path));
        using (var document = JsonDocument.Parse(text))
            Require(!HasDuplicateJsonKeys(document.RootElement),
                "Duplicated JSON property in input: " + Path.GetFileName(path));
        var value = JsonSerializer.Deserialize<T>(text, V2Schemas.Json);
        return value ?? throw new InvalidDataException(
            "Null document: " + Path.GetFileName(path));
    }

    private static bool HasDuplicateJsonKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!seen.Add(property.Name) || HasDuplicateJsonKeys(property.Value))
                    return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (HasDuplicateJsonKeys(item)) return true;
        return false;
    }
    internal static bool Overlap(string a, string b)
    {
        static string Norm(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var x = Norm(a);
        var y = Norm(b);
        return string.Equals(x, y, StringComparison.OrdinalIgnoreCase) ||
            x.StartsWith(y + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            y.StartsWith(x + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static V2Pr4aPrivateKey LoadExport(string export, string root,
        string review, string expectedRubric, string manifestHash, string importHash)
    {
        var keyFile = Path.Combine(export, V2Pr4aSchemas.KeyName);
        var key = Read<V2Pr4aPrivateKey>(keyFile);
        Require(key.SchemaVersion == V2Pr4aSchemas.Key &&
            key.Qualification == "diagnostic-unverified-report-attribution-not-scoreable" &&
            key.RubricSha256 == expectedRubric &&
            key.ReviewManifestSha256 == manifestHash &&
            key.ReviewImportSha256 == importHash &&
            IsHash(key.PlanSha256) && IsHash(key.InstructionsSha256) &&
            key.Packets is { Count: > 0 and <= 50 },
            "PR4a key contract, rubric, or source review changed.");
        var upload = Path.Combine(export, V2Pr4aSchemas.UploadFolder);
        var instruction = Path.Combine(upload, "INSTRUCTIONS.txt");
        Require(File.Exists(instruction) &&
            ShaFile(instruction) == key.InstructionsSha256,
            "Original PR4a packet instructions changed.");
        Require(Directory.GetFiles(upload, "packet-*.txt").Length == key.Packets.Count &&
            Directory.GetFiles(upload).Length == key.Packets.Count + 1 &&
            key.Packets.Select(x => x.PacketId).Distinct(StringComparer.Ordinal).Count() ==
                key.Packets.Count,
            "PR4a packet census changed.");
        foreach (var packet in key.Packets)
        {
            Require(IsId(packet.CaseId) && packet.PacketId.StartsWith("PKT-", StringComparison.Ordinal) &&
                Regex.IsMatch(packet.PacketFile ?? "", "^packet-[0-9]{3}\\.txt$") &&
                IsHash(packet.PacketSha256) && IsHash(packet.ReportSha256) &&
                IsHash(packet.CaseSha256) && IsHash(packet.GroundTruthSha256) &&
                IsHash(packet.SourcePacketSha256) &&
                IsHash(packet.SnapshotFingerprint) &&
                packet.ClaimBindings is not null &&
                packet.ClaimBindings.All(x => x is not null &&
                    IsId(x.AnonymousId) && IsId(x.OriginalId)) &&
                packet.ClaimBindings.Select(x => x.AnonymousId).Distinct(StringComparer.Ordinal)
                    .Count() == packet.ClaimBindings.Count &&
                packet.ClaimBindings.Select(x => x.OriginalId).Distinct(StringComparer.Ordinal)
                    .Count() == packet.ClaimBindings.Count,
                "PR4a packet or claim-binding shape invalid.");
            var path = Path.Combine(upload, packet.PacketFile);
            Require(File.Exists(path) && new FileInfo(path).Length <= 768 * 1024 &&
                ShaFile(path) == packet.PacketSha256,
                "PR4a original judge packet changed.");
            var caseFile = Path.Combine(root, "cases", packet.CaseId, "case.json");
            var truthFile = Path.Combine(root, "cases", packet.CaseId, "ground-truth.json");
            Require(ShaFile(caseFile) == packet.CaseSha256 &&
                ShaFile(truthFile) == packet.GroundTruthSha256,
                "PR4a source case or truth changed after freezing.");
            _ = ReadScopes(root, review, packet, manifestHash, importHash);
        }
        return key;
    }

    internal static IReadOnlyList<V2Pr4bCriterionScope> ReadScopes(
        string root, string review, V2Pr4aPacketKey packet,
        string manifestHash, string importHash)
    {
        var truth = Read<V2GroundTruth>(Path.Combine(
            root, "cases", packet.CaseId, "ground-truth.json"));
        var item = Read<V2Case>(Path.Combine(
            root, "cases", packet.CaseId, "case.json"));
        Require(item.Id == packet.CaseId && truth.CaseId == item.Id &&
            item.Partition == V2CorpusPartition.Diagnostic &&
            item.Task != V2TaskFamily.ChangeImpact &&
            item.After.SnapshotSha256 == packet.SnapshotFingerprint &&
            truth.Criteria is { Count: > 0 },
            "PR4b input is not an after-only reviewed diagnostic case.");
        var manifestFile = Path.Combine(review, "manifest.json");
        var reviewFile = Path.Combine(review, "review-import.json");
        Require(ShaFile(manifestFile) == manifestHash && ShaFile(reviewFile) == importHash,
            "Source-review manifest/receipt digest drift.");
        var manifest = Read<V2SourceReviewManifest>(manifestFile);
        var receipt = Read<V2SourceReviewImport>(reviewFile);
        Require(manifest.SchemaVersion == V2SourceReviewSchemas.Packet &&
            receipt.SchemaVersion == V2SourceReviewSchemas.Import &&
            receipt.ExportSha256 == manifestHash &&
            receipt.Answers is not null &&
            receipt.Answers.ExportSha256 == manifestHash &&
            receipt.Answers.Cases is not null &&
            manifest.Cases is not null,
            "Invalid PR2 review import.");
        var entry = manifest.Cases.SingleOrDefault(x => x.CaseId == item.Id);
        var answers = receipt.Answers.Cases.SingleOrDefault(x => x.CaseId == item.Id);
        Require(entry is not null && answers is not null &&
            entry.CaseSha256 == packet.CaseSha256 &&
            entry.GroundTruthSha256 == packet.GroundTruthSha256 &&
            entry.PacketSha256 == packet.SourcePacketSha256 &&
            entry.AuthorId == truth.AuthoredBy &&
            receipt.Answers.ReviewerId != truth.AuthoredBy &&
            entry.PacketFile == packet.CaseId + ".txt" &&
            answers.Criteria is not null &&
            answers.Criteria.Count == truth.Criteria.Count,
            "PR4b source-first review/proof is incomplete.");
        var sourcePacketFile = Path.Combine(review, entry!.PacketFile);
        Require(ShaFile(sourcePacketFile) == entry.PacketSha256,
            "Source-first reviewed source packet changed.");
        var reviewed = answers!.Criteria.ToDictionary(x => x.CriterionId,
            StringComparer.Ordinal);
        Require(reviewed.Count == truth.Criteria.Count &&
            truth.Criteria.Select(x => x.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(reviewed.Keys),
            "PR4b criterion census differs from source review.");
        var scopes = new List<V2Pr4bCriterionScope>();
        foreach (var criterion in truth.Criteria)
        {
            var proofs = Enumerable.Range(1, criterion.SourceProofs.Count)
                .Select(n => criterion.Id + ":P" + n).ToArray();
            var verdict = reviewed[criterion.Id];
            Require(verdict.Verdict == V2SourceVerdict.Supported &&
                verdict.ProofRefs is { Count: > 0 } &&
                verdict.ProofRefs.All(proofs.Contains),
                "PR4b cannot use unresolved or unproven criterion: " + criterion.Id);
            scopes.Add(new(criterion.Id, criterion.Kind, proofs));
        }
        return scopes;
    }

    internal static void ValidateAssessment(V2Pr4bAssessment answer,
        V2Pr4bPacket scope)
    {
        Require(answer is not null && answer.PacketId == scope.PacketId &&
            answer.Grades is not null && answer.Claims is not null &&
            answer.Grades.Count == scope.Criteria.Count &&
            answer.Claims.Count == scope.ClaimIds.Count,
            "Missing, foreign or partial per-packet assessment.");
        var criteria = scope.Criteria.ToDictionary(x => x.CriterionId,
            StringComparer.Ordinal);
        var validProofs = criteria.Values.SelectMany(x => x.ProofRefs)
            .ToHashSet(StringComparer.Ordinal);
        var graded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grade in answer.Grades)
        {
            Require(grade is not null && grade.CriterionId is not null &&
                criteria.TryGetValue(grade.CriterionId, out var criterion) &&
                graded.Add(grade.CriterionId) &&
                Enum.IsDefined(grade.Grounding) &&
                (grade.Grade is null or >= 0 and <= 3) &&
                !string.IsNullOrWhiteSpace(grade.Reason) && grade.Reason.Length <= 2000 &&
                grade.ReportClaimRefs is not null && grade.SourceProofRefs is not null &&
                grade.ReportClaimRefs.Count == grade.ReportClaimRefs.Distinct().Count() &&
                grade.SourceProofRefs.Count == grade.SourceProofRefs.Distinct().Count() &&
                grade.ReportClaimRefs.All(scope.ClaimIds.Contains) &&
                grade.SourceProofRefs.All(criterion!.ProofRefs.Contains) &&
                (grade.Grade != 3 || grade.Grounding == V2GroundingStatus.Supported ||
                    criterion.Kind is V2CriterionKind.RequiredUncertainty or
                        V2CriterionKind.ForbiddenAssertion) &&
                (grade.Grade != 3 || grade.SourceProofRefs.Count > 0 ||
                    criterion.Kind == V2CriterionKind.ForbiddenAssertion),
                "Invalid criterion grade, grounding or proof reference.");
        }
        var claimIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in answer.Claims)
        {
            Require(claim is not null && scope.ClaimIds.Contains(claim.ClaimId) &&
                claimIds.Add(claim.ClaimId) &&
                Enum.IsDefined(claim.Grounding) && Enum.IsDefined(claim.Materiality) &&
                !string.IsNullOrWhiteSpace(claim.Reason) && claim.Reason.Length <= 2000 &&
                claim.SourceProofRefs is not null &&
                claim.SourceProofRefs.Count == claim.SourceProofRefs.Distinct().Count() &&
                claim.SourceProofRefs.All(validProofs.Contains) &&
                (claim.Grounding != V2GroundingStatus.Supported ||
                    claim.SourceProofRefs.Count > 0),
                "Invalid, duplicated, ungrounded or foreign claim judgment.");
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeArcheologist.EvaluationLab.V2;

/// <summary>PR2 source-proof and owner-operated review tests; no models or imported code execution.</summary>
public static class V2SourceReviewTests
{
    public static int Run()
    {
        var passed = 0;
        var failed = 0;
        void Check(string name, Action action)
        {
            try
            {
                action();
                Console.WriteLine("PASS: V2 PR2 " + name);
                passed++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: V2 PR2 " + name + ": " + ex.Message);
                failed++;
            }
        }

        static void Require(bool condition, string error)
        {
            if (!condition) throw new InvalidOperationException(error);
        }

        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var workspace = Path.Combine(Path.GetTempPath(), "ca-v2-source-review-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(workspace, "eval");
        var sources = Path.Combine(workspace, "sources");
        var export = Path.Combine(workspace, "review");
        const string id = "V2-source-review";
        var caseDir = Path.Combine(root, "cases", id);
        var sourcePath = Path.Combine(sources, id, "after", "src", "Foo.cs");
        var casePath = Path.Combine(caseDir, "case.json");
        var groundTruthPath = Path.Combine(caseDir, "ground-truth.json");
        var answerPath = Path.Combine(workspace, "answers.json");
        var sourceBytes = Encoding.UTF8.GetBytes(
            "namespace Demo;\nclass Foo\n{\n    void Run() { } // untrusted source text\n}\n");
        var fileHash = Hash(sourceBytes);
        var src = new V2SourceIdentity(new string('a', 64),
            new Dictionary<string, string> { ["src/Foo.cs"] = fileHash });
        var example = new V2Case(V2Schemas.Case, id, V2TaskFamily.CurrentCode,
            "What declaration is present?", "Developer", V2CorpusPartition.Diagnostic,
            src, null, ["src/Foo.cs"], ["OBS-01"]);
        var anchor = new V2SourceProof(V2SnapshotSide.After, "src/Foo.cs", fileHash, 4, 4);
        var criterion = new V2Criterion("OBS-01", V2CriterionKind.SourceObservation,
            "The source contains a Run method declaration.", V2Materiality.Material,
            [anchor], ["EVD-01"], V2ReviewStatus.Draft);
        var groundTruth = new V2GroundTruth(V2Schemas.GroundTruth, id, "source-author",
            V2ReviewStatus.Draft, [], [criterion]);

        try
        {
            Directory.CreateDirectory(caseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllBytes(sourcePath, sourceBytes);
            File.WriteAllText(casePath, JsonSerializer.Serialize(example, V2Schemas.Json));
            void WriteTruth(V2GroundTruth gt) =>
                File.WriteAllText(groundTruthPath, JsonSerializer.Serialize(gt, V2Schemas.Json));
            WriteTruth(groundTruth);
            Check("real pinned source and declared span pass preflight", () =>
            {
                var result = V2SourceReview.Preflight(root, sources);
                Require(result.Valid && result.CaseCount == 1, string.Join("; ", result.Errors));
            });
            Check("source preflight does not mutate original bytes", () =>
            {
                _ = V2SourceReview.Preflight(root, sources);
                Require(File.ReadAllBytes(sourcePath).SequenceEqual(sourceBytes),
                    "source was changed during validation");
            });
            Check("tampered source bytes fail preflight", () =>
            {
                try
                {
                    File.WriteAllText(sourcePath, "changed");
                    Require(!V2SourceReview.Preflight(root, sources).Valid, "tampered source accepted");
                }
                finally { File.WriteAllBytes(sourcePath, sourceBytes); }
            });
            Check("missing source fails preflight", () =>
            {
                try
                {
                    File.Delete(sourcePath);
                    Require(!V2SourceReview.Preflight(root, sources).Valid, "missing source accepted");
                }
                finally { File.WriteAllBytes(sourcePath, sourceBytes); }
            });
            Check("out-of-range source proof fails preflight", () =>
            {
                try
                {
                    WriteTruth(groundTruth with
                    {
                        Criteria = [criterion with { SourceProofs = [anchor with { EndLine = 500 }] }]
                    });
                    Require(!V2SourceReview.Preflight(root, sources).Valid, "invalid span accepted");
                }
                finally { WriteTruth(groundTruth); }
            });
            Check("historical finding forbidden in after-only task", () =>
            {
                try
                {
                    WriteTruth(groundTruth with
                    {
                        Criteria = [criterion with { Kind = V2CriterionKind.ChangeObservation }]
                    });
                    Require(!V2SourceReview.Preflight(root, sources).Valid, "historical claim accepted");
                }
                finally { WriteTruth(groundTruth); }
            });
            Check("invalid proof cannot create a review packet", () =>
            {
                try
                {
                    WriteTruth(groundTruth with
                    {
                        Criteria = [criterion with { SourceProofs = [anchor with { EndLine = 999 }] }]
                    });
                    var noExport = Path.Combine(workspace, "refused");
                    Require(V2SourceReview.Export(root, sources, noExport, TextWriter.Null) != 0 &&
                        !Directory.Exists(noExport), "failed preflight wrote an export");
                }
                finally { WriteTruth(groundTruth); }
            });
            Check("held-out cases blocked from PR2 diagnostic review", () =>
            {
                try
                {
                    File.WriteAllText(casePath,
                        JsonSerializer.Serialize(example with { Partition = V2CorpusPartition.HeldOut }, V2Schemas.Json));
                    Require(!V2SourceReview.Preflight(root, sources).Valid,
                        "held-out cases were exposed to diagnostic reviewer");
                }
                finally { File.WriteAllText(casePath, JsonSerializer.Serialize(example, V2Schemas.Json)); }
            });
            Check("export binds source text, criterion and exact packet bytes", () =>
            {
                Require(V2SourceReview.Export(root, sources, export, TextWriter.Null) == 0,
                    "valid source review export failed");
                Require(File.Exists(export + ".zip") && File.Exists(Path.Combine(export, "manifest.json")),
                    "review package missing");
                var packet = File.ReadAllText(Path.Combine(export, id + ".txt"));
                Require(packet.Contains("NO AGENT REPORTS", StringComparison.Ordinal) &&
                    packet.Contains("4 |     void Run()", StringComparison.Ordinal) &&
                    packet.Contains("OBS-01:P1", StringComparison.Ordinal),
                    "source evidence or review reference missing");
                Require(!V2SourceReview.Preflight(root, sources).ReadyForScoring(),
                    "review package implies score eligibility");
            });
            Check("export cannot overwrite existing package", () =>
            {
                Require(V2SourceReview.Export(root, sources, export, TextWriter.Null) != 0,
                    "second export overwrote prior material");
            });
            var manifestFile = Path.Combine(export, "manifest.json");
            var manifestDigest = Hash(File.ReadAllBytes(manifestFile));
            var complete = new V2SourceReviewAnswers(V2SourceReviewSchemas.Answers, manifestDigest,
                "independent-reviewer", [new V2SourceCaseAnswer(id,
                    [new V2SourceCriterionAnswer("OBS-01", V2SourceVerdict.Supported,
                        "Line four declares the method named Run.", ["OBS-01:P1"])])]);
            void WriteAnswers(V2SourceReviewAnswers answer) =>
                File.WriteAllText(answerPath, JsonSerializer.Serialize(answer, V2Schemas.Json));
            void RejectAnswer(string name, V2SourceReviewAnswers invalid) =>
                Check(name, () =>
                {
                    WriteAnswers(invalid);
                    Require(V2SourceReview.Import(export, answerPath, TextWriter.Null) != 0 &&
                        !File.Exists(Path.Combine(export, "review-import.json")),
                        "invalid source review accepted");
                });
            RejectAnswer("foreign export digest rejected",
                complete with { ExportSha256 = new string('f', 64) });
            RejectAnswer("reviewer cannot self-approve",
                complete with { ReviewerId = groundTruth.AuthoredBy });
            RejectAnswer("missing criterion answer rejected",
                complete with { Cases = [new V2SourceCaseAnswer(id, [])] });
            RejectAnswer("duplicate case answer rejected",
                complete with { Cases = [complete.Cases[0], complete.Cases[0]] });
            RejectAnswer("duplicate criterion answer rejected",
                complete with { Cases = [new V2SourceCaseAnswer(id,
                    [complete.Cases[0].Criteria[0], complete.Cases[0].Criteria[0]])] });
            RejectAnswer("foreign case answer rejected",
                complete with { Cases = [complete.Cases[0] with { CaseId = "V2-other" }] });
            RejectAnswer("missing source citation rejects supported grade",
                complete with { Cases = [new V2SourceCaseAnswer(id,
                    [complete.Cases[0].Criteria[0] with { ProofRefs = [] }])] });
            RejectAnswer("invented proof reference rejected",
                complete with { Cases = [new V2SourceCaseAnswer(id,
                    [complete.Cases[0].Criteria[0] with { ProofRefs = ["OBS-01:P99"] }])] });
            RejectAnswer("unsupported review schema rejected",
                complete with { SchemaVersion = "historical-review-v0" });
            Check("modified packet cannot be imported", () =>
            {
                var path = Path.Combine(export, id + ".txt");
                var original = File.ReadAllBytes(path);
                try
                {
                    File.AppendAllText(path, "\nTAMPERED");
                    WriteAnswers(complete);
                    Require(V2SourceReview.Import(export, answerPath, TextWriter.Null) != 0,
                        "changed packet accepted");
                }
                finally { File.WriteAllBytes(path, original); }
            });
            Check("modified manifest cannot be imported", () =>
            {
                var original = File.ReadAllBytes(manifestFile);
                try
                {
                    File.AppendAllText(manifestFile, " ");
                    WriteAnswers(complete);
                    Require(V2SourceReview.Import(export, answerPath, TextWriter.Null) != 0,
                        "changed manifest accepted");
                }
                finally { File.WriteAllBytes(manifestFile, original); }
            });
            Check("first complete source review imports immutably", () =>
            {
                WriteAnswers(complete);
                Require(V2SourceReview.Import(export, answerPath, TextWriter.Null) == 0,
                    "valid review import rejected");
                var record = Path.Combine(export, "review-import.json");
                Require(File.Exists(record), "first review record missing");
                var saved = File.ReadAllBytes(record);
                Require(V2SourceReview.Import(export, answerPath, TextWriter.Null) != 0,
                    "second import overwrote first review");
                Require(saved.SequenceEqual(File.ReadAllBytes(record)),
                    "accepted review was modified");
            });
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }

        Console.WriteLine(failed == 0
            ? $"GREEN: {passed} V2 PR2 model-free source-first review tests passed; NOT READY FOR SCORING."
            : $"RED: {failed}/{passed + failed} V2 PR2 source-review tests failed.");
        return failed == 0 ? 0 : 1;
    }
}

internal static class V2SourcePreflightTestExtensions
{
    public static bool ReadyForScoring(this V2SourcePreflight _) => false;
}

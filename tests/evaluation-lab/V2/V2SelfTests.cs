using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeArcheologist.EvaluationLab.V2;

/// <summary>Model-free, disposable structural contract tests. No Ollama or imported code execution.</summary>
public static class V2SelfTests
{
    public static int Run()
    {
        var passed = 0;
        var failed = 0;
        void Check(string name, Action test)
        {
            try
            {
                test();
                passed++;
                Console.WriteLine("PASS: V2 PR1 " + name);
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine("FAIL: V2 PR1 " + name + ": " + ex.Message);
            }
        }

        static void Expect(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
        }

        var root = Path.Combine(Path.GetTempPath(), "code-archeologist-v2-pr1-" + Guid.NewGuid().ToString("N"));
        var caseId = "V2-structural-fixture";
        var caseDir = Path.Combine(root, "cases", caseId);
        var casePath = Path.Combine(caseDir, "case.json");
        var truthPath = Path.Combine(caseDir, "ground-truth.json");
        var a64 = new string('a', 64);
        var b64 = new string('b', 64);
        var c64 = new string('c', 64);
        var d64 = new string('d', 64);
        var after = new V2SourceIdentity(a64, new Dictionary<string, string> { ["src/Foo.cs"] = b64 });
        var before = new V2SourceIdentity(c64, new Dictionary<string, string> { ["src/Foo.cs"] = d64 });
        var good = new V2Case(V2Schemas.Case, caseId, V2TaskFamily.CurrentCode,
            "What does this method declare?", "C# developer", V2CorpusPartition.Diagnostic,
            after, null, ["src/Foo.cs"], ["OBS-01"]);
        var proof = new V2SourceProof(V2SnapshotSide.After, "src/Foo.cs", b64, 1, 3);
        var criterion = new V2Criterion("OBS-01", V2CriterionKind.SourceObservation,
            "The method is declared in this file.", V2Materiality.Contextual,
            [proof], ["EVD-01"], V2ReviewStatus.Draft);
        var truth = new V2GroundTruth(V2Schemas.GroundTruth, caseId, "synthetic-fixture",
            V2ReviewStatus.Draft, [], [criterion]);

        try
        {
            Directory.CreateDirectory(caseDir);
            void Write(V2Case item, V2GroundTruth gt)
            {
                File.WriteAllText(casePath, JsonSerializer.Serialize(item, V2Schemas.Json));
                File.WriteAllText(truthPath, JsonSerializer.Serialize(gt, V2Schemas.Json));
            }

            Write(good, truth);
            Check("current-code fixture accepted but not scoreable", () =>
            {
                var result = V2CorpusValidator.Validate(root);
                Expect(result.Valid && result.CaseCount == 1 && !result.ReadyForScoring,
                    string.Join("; ", result.Errors));
            });
            Check("boundary fixture accepted but not scoreable", () =>
            {
                Write(good with { Task = V2TaskFamily.EvidenceBoundary }, truth);
                var result = V2CorpusValidator.Validate(root);
                Expect(result.Valid && !result.ReadyForScoring, string.Join("; ", result.Errors));
                Write(good, truth);
            });
            Check("paired-snapshot change fixture accepted", () =>
            {
                Write(good with { Task = V2TaskFamily.ChangeImpact, Before = before }, truth);
                var result = V2CorpusValidator.Validate(root);
                Expect(result.Valid && result.CaseCount == 1, string.Join("; ", result.Errors));
                Write(good, truth);
            });
            Check("empty corpus explicitly not scoreable", () =>
            {
                var empty = Path.Combine(root, "empty");
                Directory.CreateDirectory(empty);
                var result = V2CorpusValidator.Validate(empty);
                Expect(result.Valid && result.CaseCount == 0 && !result.ReadyForScoring,
                    "empty V2 corpus received an incorrect status");
            });

            void Reject(string name, Action<JsonObject, JsonObject> change)
            {
                Check(name, () =>
                {
                    Write(good, truth);
                    var c = JsonNode.Parse(File.ReadAllText(casePath))!.AsObject();
                    var gt = JsonNode.Parse(File.ReadAllText(truthPath))!.AsObject();
                    change(c, gt);
                    File.WriteAllText(casePath, c.ToJsonString(V2Schemas.Json));
                    File.WriteAllText(truthPath, gt.ToJsonString(V2Schemas.Json));
                    var result = V2CorpusValidator.Validate(root);
                    Expect(!result.Valid && result.Errors.Count > 0,
                        "invalid V2 fixture was accepted");
                    Write(good, truth);
                });
            }

            Reject("unsupported schema version rejected",
                (c, _) => c["schemaVersion"] = "c6c-case-v1");
            Reject("unrecognized JSON property rejected",
                (c, _) => c["unexpectedField"] = 42);
            Reject("missing required property rejected",
                (c, _) => c.Remove("developerQuestion"));
            Reject("null required value rejected",
                (c, _) => c["developerQuestion"] = null);
            Reject("invalid task enum rejected",
                (c, _) => c["task"] = "imaginary-task");
            Reject("numeric task enum rejected",
                (c, _) => c["task"] = 999);
            Reject("missing before snapshot rejected for change-impact",
                (c, _) => c["task"] = "change-impact");
            Reject("after-only task refuses historical before snapshot",
                (c, _) => c["before"] = JsonSerializer.SerializeToNode(before, V2Schemas.Json));
            Reject("unsafe relative path rejected",
                (c, _) => c["authorizedPaths"] = new JsonArray("../escape.cs"));
            Reject("Windows absolute path rejected",
                (c, _) => c["authorizedPaths"] = new JsonArray("C:\\temp\\secret.cs"));
            Reject("invalid SHA-256 rejected",
                (c, _) => c["after"]!["snapshotSha256"] = "broken");
            Reject("missing source identity rejected",
                (c, _) => c.Remove("after"));
            Reject("duplicate case criterion IDs rejected",
                (c, _) => c["criterionIds"] = new JsonArray("OBS-01", "OBS-01"));
            Reject("criterion IDs must match ground truth",
                (c, _) => c["criterionIds"] = new JsonArray("ALT-01"));
            Reject("null ground-truth criterion rejected without exception",
                (_, gt) => gt["criteria"] = new JsonArray((JsonNode?)null));
            Reject("historical criterion rejected in current-code case",
                (_, gt) => gt["criteria"]![0]!["kind"] = "change-observation");
            Reject("source-proof hash must match declared revision",
                (_, gt) => gt["criteria"]![0]!["sourceProofs"]![0]!["fileSha256"] = a64);
            Reject("unknown ground-truth property rejected",
                (_, gt) => gt["unexpectedField"] = true);
            Reject("malformed source proof range rejected",
                (_, gt) => gt["criteria"]![0]!["sourceProofs"]![0]!["startLine"] = 0);

            Check("malformed JSON rejected", () =>
            {
                Write(good, truth);
                File.WriteAllText(casePath, "{not valid JSON}");
                Expect(!V2CorpusValidator.Validate(root).Valid, "malformed JSON accepted");
                Write(good, truth);
            });
            Check("missing ground-truth document rejected", () =>
            {
                Write(good, truth);
                File.Delete(truthPath);
                Expect(!V2CorpusValidator.Validate(root).Valid, "missing document accepted");
                Write(good, truth);
            });
            Check("validator never changes input bytes", () =>
            {
                Write(good, truth);
                var caseBytes = File.ReadAllBytes(casePath);
                var truthBytes = File.ReadAllBytes(truthPath);
                var result = V2CorpusValidator.Validate(root);
                Expect(result.Valid, string.Join("; ", result.Errors));
                Expect(caseBytes.SequenceEqual(File.ReadAllBytes(casePath)) &&
                    truthBytes.SequenceEqual(File.ReadAllBytes(truthPath)),
                    "validation modified fixture inputs");
            });
            Check("deterministic V2 serialization", () =>
            {
                var first = JsonSerializer.Serialize(good, V2Schemas.Json);
                var second = JsonSerializer.Serialize(good, V2Schemas.Json);
                Expect(first == second, "serialization drift");
                var roundTrip = JsonSerializer.Deserialize<V2Case>(first, V2Schemas.Json);
                Expect(roundTrip?.Id == good.Id, "round trip failed");
            });
            Check("unknown fields rejected in other V2 contracts", () =>
            {
                var manifest = new V2ExperimentManifest(V2Schemas.ExperimentManifest, "EXP-01", a64,
                    b64, c64, [caseId], ["gate-c"], "not-approved-for-scoring");
                var obj = JsonNode.Parse(JsonSerializer.Serialize(manifest, V2Schemas.Json))!.AsObject();
                obj["foreign"] = true;
                try
                {
                    _ = JsonSerializer.Deserialize<V2ExperimentManifest>(obj.ToJsonString(), V2Schemas.Json);
                    throw new InvalidOperationException("unknown experiment field accepted");
                }
                catch (JsonException) { }
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        Console.WriteLine(failed == 0
            ? $"GREEN: {passed} V2 PR1 model-free contract tests passed; no scored corpus."
            : $"RED: {failed} of {passed + failed} V2 PR1 tests failed.");
        return failed == 0 ? 0 : 1;
    }
}

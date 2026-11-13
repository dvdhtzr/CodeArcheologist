using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.EvaluationLab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.EvaluationLab.V2;

/// <summary>Model-free, disposable tests of answerability decisions and failure gates.</summary>
public static class V2AnswerabilityPreflightTests
{
    public static async Task<int> RunAsync()
    {
        var passed = 0;
        var failed = 0;
        void Check(string label, Action test)
        {
            try
            {
                test();
                Console.WriteLine("PASS: V2 PR3a " + label);
                passed++;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("FAIL: V2 PR3a " + label + ": " + e.Message);
                failed++;
            }
        }
        static void Assert(bool valid, string reason)
        {
            if (!valid) throw new InvalidOperationException(reason);
        }

        var shaA = new string('a', 64);
        var shaB = new string('b', 64);
        var proof1 = new V2SourceProof(V2SnapshotSide.After, "Api/Orders.cs", shaA, 8, 12);
        var proof2 = new V2SourceProof(V2SnapshotSide.After, "Api/Orders.cs", shaA, 40, 44);
        var criterion = new V2Criterion("OBS-01", V2CriterionKind.SourceObservation,
            "One method contains two cited operations", V2Materiality.Material,
            [proof1, proof2], ["EVD-01", "EVD-02"], V2ReviewStatus.Draft);
        var gt = new V2GroundTruth(V2Schemas.GroundTruth, "V2-actual-code", "author",
            V2ReviewStatus.Draft, [], [criterion]);
        var bindings = new V2AnswerabilityPreflight.Binding[]
        {
            new("OBS-01", "EVD-01", 0),
            new("OBS-01", "EVD-02", 1)
        };
        var plan = new V2AnswerabilityPreflight.Plan(
            V2AnswerabilityPreflight.PlanSchema, "V2-actual-code", "gate-c",
            GateCRole.Investigator, "http-minimal-mapping", "Api/Orders.cs", 6,
            shaB, bindings);
        GateC5EvidenceLink Link(int line, string? sha = null,
            string? path = null, GateCEvidenceKind kind = GateCEvidenceKind.ObservedSyntax) =>
            new(GateCRole.Investigator, "E" + line, "host-" + line, kind,
                "invocation-site", "observed-syntax", path ?? "Api/Orders.cs",
                sha ?? shaA, line, 1);
        V2AnswerabilityPreflight.HostView View(
            string[] capabilities, GateC5EvidenceLink[] links,
            bool truncated = false, bool budget = true) =>
            new(capabilities, links, truncated, budget);
        V2AnswerabilityState Classify(
            IReadOnlyList<V2AnswerabilityPreflight.HostView> views,
            V2GroundTruth? truth = null, V2AnswerabilityPreflight.Plan? selectedPlan = null) =>
            V2AnswerabilityPreflight.Classify(truth ?? gt, selectedPlan ?? plan, views).Single().State;

        Check("combined verified host views establish retrievable", () =>
            Assert(Classify([View(["inspect_entry_point", "inspect_direct_invocations"],
                [Link(10), Link(42)])]) == V2AnswerabilityState.Retrievable,
                "verified source anchors not recognized"));
        Check("single view suffices for one required evidence item", () =>
        {
            var one = criterion with { RequiredEvidenceIds = ["EVD-01"], SourceProofs = [proof1] };
            var truth = gt with { Criteria = [one] };
            var onePlan = plan with { Bindings = [bindings[0]] };
            Assert(Classify([View(["inspect_entry_point"], [Link(10)])], truth, onePlan) ==
                V2AnswerabilityState.Retrievable, "one valid view not retrievable");
        });
        Check("never calls any evidence initially available without prompt dispatch", () =>
            Assert(Classify([View(["inspect_entry_point"], [Link(10), Link(42)])]) !=
                V2AnswerabilityState.InitiallyAvailable, "pre-model probe faked initial delivery"));
        Check("unrequested evidence is not model-delivered", () =>
            Assert(Classify([View(["inspect_entry_point"], [Link(10), Link(42)])]) ==
                V2AnswerabilityState.Retrievable, "retrievable incorrectly equated to delivered"));
        Check("evidence spread across incompatible views is not combined", () =>
            Assert(Classify([View(["inspect_entry_point"], [Link(10)]),
                View(["inspect_direct_invocations"], [Link(42)])]) ==
                V2AnswerabilityState.Undetermined, "distinct sessions were illegally merged"));
        Check("no host evidence is undetermined, not unavailable", () =>
            Assert(Classify([]) == V2AnswerabilityState.Undetermined,
                "missing evidence was mislabeled unavailable"));
        Check("missing canonical line cannot establish retrievability", () =>
            Assert(Classify([View(["inspect_entry_point"], [Link(10), Link(41)])]) ==
                V2AnswerabilityState.Undetermined, "wrong anchor line accepted"));
        Check("source digest mismatch cannot establish retrievability", () =>
            Assert(Classify([View(["inspect_entry_point"],
                [Link(10, shaB), Link(42)])]) ==
                V2AnswerabilityState.Undetermined, "wrong source digest accepted"));
        Check("source path mismatch cannot establish retrievability", () =>
            Assert(Classify([View(["inspect_entry_point"],
                [Link(10, path: "Api/Other.cs"), Link(42)])]) ==
                V2AnswerabilityState.Undetermined, "wrong source path accepted"));
        Check("provisional call inference cannot establish observed syntax", () =>
            Assert(Classify([View(["inspect_entry_point"],
                [Link(10, kind: GateCEvidenceKind.ProvisionalLocal), Link(42)])]) ==
                V2AnswerabilityState.Undetermined, "provisional mapping promoted to proof"));
        Check("truncated views cannot establish full answerability", () =>
            Assert(Classify([View(["inspect_entry_point"], [Link(10), Link(42)],
                truncated: true)]) == V2AnswerabilityState.Undetermined,
                "truncated host evidence treated as complete"));
        Check("input over budget cannot establish retrievability", () =>
            Assert(Classify([View(["inspect_entry_point"], [Link(10), Link(42)],
                budget: false)]) == V2AnswerabilityState.Undetermined,
                "oversize input treated as retrievable"));
        Check("more than three capabilities cannot establish retrievability", () =>
            Assert(Classify([View(["a", "b", "c", "d"], [Link(10), Link(42)])]) ==
                V2AnswerabilityState.Undetermined, "host view budget ignored"));
        Check("one missing evidence mapping prevents a positive claim", () =>
            Assert(Classify([View(["inspect_entry_point"], [Link(10), Link(42)])],
                selectedPlan: plan with { Bindings = [bindings[0]] }) ==
                V2AnswerabilityState.Undetermined, "incomplete evidence mapping accepted"));
        Check("before-only source proofs cannot be found from after snapshot", () =>
        {
            var before = criterion with
            {
                SourceProofs = [proof1 with { Snapshot = V2SnapshotSide.Before }, proof2]
            };
            Assert(Classify([View(["inspect_entry_point"], [Link(10), Link(42)])],
                truth: gt with { Criteria = [before] }) ==
                V2AnswerabilityState.Undetermined, "historical source was silently promoted");
        });
        Check("uncertainty criteria require interpretation rather than automatic positive", () =>
        {
            var caution = criterion with { Kind = V2CriterionKind.RequiredUncertainty };
            Assert(Classify([View(["inspect_entry_point"], [Link(10), Link(42)])],
                truth: gt with { Criteria = [caution] }) ==
                V2AnswerabilityState.Undetermined, "generic uncertainty was rewarded automatically");
        });
        Check("criterion without required evidence is undetermined", () =>
        {
            var empty = criterion with { RequiredEvidenceIds = [] };
            Assert(Classify([View(["inspect_entry_point"], [Link(10), Link(42)])],
                truth: gt with { Criteria = [empty] }) ==
                V2AnswerabilityState.Undetermined, "empty evidence produced positive credit");
        });

        var temp = Path.Combine(Path.GetTempPath(), "ca-v2-pr3a-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            var planFile = Path.Combine(temp, "plan.json");
            var outFile = Path.Combine(temp, "answerability.json");
            File.WriteAllText(planFile, JsonSerializer.Serialize(plan, V2Schemas.Json));
            Check("missing case root rejects without producing output", () =>
            {
                var result = V2AnswerabilityPreflight.RunAsync(
                    Path.Combine(temp, "no-cases"), Path.Combine(temp, "sources"),
                    Path.Combine(temp, "review"), planFile, outFile, TextWriter.Null)
                    .GetAwaiter().GetResult();
                Assert(result != 0 && !File.Exists(outFile), "unreviewed source accepted");
            });
            Check("unknown preflight schema rejects without output", () =>
            {
                File.WriteAllText(planFile, JsonSerializer.Serialize(
                    plan with { SchemaVersion = "c6c-case-v1" }, V2Schemas.Json));
                var result = V2AnswerabilityPreflight.RunAsync(
                    temp, Path.Combine(temp, "sources"), Path.Combine(temp, "review"),
                    planFile, outFile, TextWriter.Null).GetAwaiter().GetResult();
                Assert(result != 0 && !File.Exists(outFile), "foreign schema accepted");
            });
            Check("malformed preflight JSON rejected without output", () =>
            {
                File.WriteAllText(planFile, "{bad JSON}");
                var result = V2AnswerabilityPreflight.RunAsync(
                    temp, Path.Combine(temp, "sources"), Path.Combine(temp, "review"),
                    planFile, outFile, TextWriter.Null).GetAwaiter().GetResult();
                Assert(result != 0 && !File.Exists(outFile), "malformed plan accepted");
            });
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
        try
        {
            await HostIntegrationAsync();
            Console.WriteLine("PASS: V2 PR3a genuine host snapshot + PR2 review + preflight");
            passed++;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("FAIL: V2 PR3a genuine host integration: " + e.Message);
            failed++;
        }
        Console.WriteLine(failed == 0
            ? $"GREEN: {passed} V2 PR3a model-free answerability tests; NOT READY FOR SCORING."
            : $"RED: {failed} of {passed + failed} V2 PR3a answerability tests failed.");
        return failed == 0 ? 0 : 1;
    }

    private static async Task HostIntegrationAsync()
    {
        var originalEnv = Environment.GetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR");
        var temp = Path.Combine(Path.GetTempPath(), "ca-v2-pr3a-host-" +
            Guid.NewGuid().ToString("N"));
        const string id = "V2-host-evidence";
        var root = Path.Combine(temp, "eval");
        var sources = Path.Combine(temp, "sources");
        var sourceDir = Path.Combine(sources, id, "after");
        var source = Path.Combine(sourceDir, "ItemsController.cs");
        var caseDir = Path.Combine(root, "cases", id);
        var reviewDir = Path.Combine(temp, "review");
        var output = Path.Combine(temp, "preflight.json");
        Microsoft.Extensions.DependencyInjection.ServiceProvider? services = null;
        try
        {
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(caseDir);
            var sourceBytes = Encoding.UTF8.GetBytes("""
                using Microsoft.AspNetCore.Mvc;
                [ApiController]
                [Route("api/items")]
                public class ItemsController : ControllerBase
                {
                    [HttpGet("{id}")]
                    public int Get(int id)
                    {
                        return Math.Abs(id);
                    }
                }
                """);
            File.WriteAllBytes(source, sourceBytes);
            var digest = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
            var capture = await LabHarness.CaptureSourceAsync(sourceDir,
                Path.Combine(temp, "capture-data"), "V2 PR3a synthetic source", CancellationToken.None);
            services = capture.Item1;
            var graph = capture.Item6;
            var entry = graph.Nodes.FirstOrDefault(x => x.Kind == "http-endpoint-attribute");
            if (entry is null)
                throw new InvalidOperationException("synthetic HTTP endpoint was not detected");
            var scope = new GateCScope(capture.Item5.WorkspaceId, capture.Item5.RepositoryId,
                capture.Item5.Id, capture.Item5.FingerprintSha256, entry.Id,
                SnapshotEvidenceTools.Version);
            var discovery = services.GetRequiredService<GateCEvidenceDiscovery>();
            GateC5EvidenceLink? anchor = null;
            foreach (var capability in GateCEvidenceRegistry.Discover().Select(c => c.Identifier))
            {
                // Each probe has its own genuine host-owned role session and budget.
                var session = discovery.OpenVerified(capture.Item5, scope, Guid.NewGuid(),
                    GateCRole.Investigator, CancellationToken.None);
                var issued = session.Request(capability, CancellationToken.None);
                if (!issued.Check.Accepted || issued.Response is null) continue;
                anchor = session.ReportLinks().FirstOrDefault(x =>
                    x.FilePath == "ItemsController.cs" && x.Line is >= 1 &&
                    x.FileSha256 == digest && x.Kind == GateCEvidenceKind.ObservedSyntax);
                if (anchor is not null) break;
            }
            if (anchor is null)
                throw new InvalidOperationException("synthetic host did not return an observed-syntax anchor");
            // The chosen anchor was returned by the real read-only evidence session.
            var after = new V2SourceIdentity(capture.Item5.FingerprintSha256,
                new Dictionary<string, string> { ["ItemsController.cs"] = digest });
            var item = new V2Case(V2Schemas.Case, id, V2TaskFamily.CurrentCode,
                "Which observed source declaration is present?", "C# developer",
                V2CorpusPartition.Diagnostic, after, null,
                ["ItemsController.cs"], ["OBS-01"]);
            var proof = new V2SourceProof(V2SnapshotSide.After,
                "ItemsController.cs", digest, anchor.Line!.Value, anchor.Line.Value);
            var criterion = new V2Criterion("OBS-01", V2CriterionKind.SourceObservation,
                "The pinned source includes this observed syntax anchor.",
                V2Materiality.Material, [proof], ["EVD-01"], V2ReviewStatus.Draft);
            var truth = new V2GroundTruth(V2Schemas.GroundTruth, id, "synthetic-author",
                V2ReviewStatus.Draft, [], [criterion]);
            File.WriteAllText(Path.Combine(caseDir, "case.json"),
                JsonSerializer.Serialize(item, V2Schemas.Json));
            File.WriteAllText(Path.Combine(caseDir, "ground-truth.json"),
                JsonSerializer.Serialize(truth, V2Schemas.Json));
            if (V2SourceReview.Export(root, sources, reviewDir, TextWriter.Null) != 0)
                throw new InvalidOperationException("synthetic independent source export refused");
            var manifest = Path.Combine(reviewDir, "manifest.json");
            var manifestHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest)))
                .ToLowerInvariant();
            var answers = new V2SourceReviewAnswers(V2SourceReviewSchemas.Answers, manifestHash,
                "synthetic-reviewer", [new V2SourceCaseAnswer(id,
                    [new V2SourceCriterionAnswer("OBS-01", V2SourceVerdict.Supported,
                        "The pinned source anchor is present at the cited line.", ["OBS-01:P1"])])]);
            var answersPath = Path.Combine(temp, "answers.json");
            File.WriteAllText(answersPath, JsonSerializer.Serialize(answers, V2Schemas.Json));
            if (V2SourceReview.Import(reviewDir, answersPath, TextWriter.Null) != 0)
                throw new InvalidOperationException("synthetic source review import refused");
            var receiptHash = Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(reviewDir, "review-import.json")))).ToLowerInvariant();
            var plan = new V2AnswerabilityPreflight.Plan(
                V2AnswerabilityPreflight.PlanSchema, id, "gate-c", GateCRole.Investigator,
                entry.Kind, entry.FilePath!, entry.Line!.Value, receiptHash,
                [new("OBS-01", "EVD-01", 0)]);
            var planPath = Path.Combine(temp, "plan.json");
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan, V2Schemas.Json));
            await services.DisposeAsync();
            services = null;
            SqliteConnection.ClearAllPools();
            if (await V2AnswerabilityPreflight.RunAsync(root, sources,
                    reviewDir, planPath, output, TextWriter.Null) != 0)
                throw new InvalidOperationException("real model-free host preflight rejected");
            var readErrors = new List<string>();
            var result = V2CorpusValidator.ReadStrict<V2AnswerabilityPreflight.Record>(output, readErrors);
            if (result?.Answerability.Criteria is not { Count: 1 } criteria ||
                criteria[0].State != V2AnswerabilityState.Retrievable ||
                result.QualificationStatus != "not-ready-for-scoring")
                throw new InvalidOperationException("host preflight did not establish bounded retrievability: " +
                    string.Join("; ", readErrors));
            if (await V2AnswerabilityPreflight.RunAsync(root, sources,
                    reviewDir, planPath, output, TextWriter.Null) == 0)
                throw new InvalidOperationException("preflight overwrote its frozen first result");
        }
        finally
        {
            if (services is not null) await services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", originalEnv);
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

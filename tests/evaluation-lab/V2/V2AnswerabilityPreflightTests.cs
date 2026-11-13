using System.Text.Json;
using CodeArcheologist.Api.Investigations;

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
        await Task.CompletedTask;
        Console.WriteLine(failed == 0
            ? $"GREEN: {passed} V2 PR3a model-free answerability tests; NOT READY FOR SCORING."
            : $"RED: {failed} of {passed + failed} V2 PR3a answerability tests failed.");
        return failed == 0 ? 0 : 1;
    }
}

using System.Text;
using System.Text.Json;
using CodeArcheologist.Api.Investigations;

namespace CodeArcheologist.EvaluationLab;

/// <summary>
/// Fully model-free PR4b1 checks. Synthetic artifacts are deliberately fake
/// and never written into the actual evaluation corpus or treated as results.
/// They test exhaustive accounting, seals, blinding, strict import and verdict
/// arithmetic, not the LLM's semantic ability.
/// </summary>
internal static class R3Pr4B1SelfTests
{
    public static async Task RunAsync(string evalRoot,
        Action<string, Action> check, Func<string, Func<Task>, Task> checkAsync,
        Action<bool, string> expect)
    {
        await checkAsync("PR4b1: model-call meter is opt-in, exception aware and async-local", async () =>
        {
            await EvaluationModelCalls.ObserveAsync(() => Task.FromResult("inactive"));
            using (var meter = EvaluationModelCalls.Begin())
            {
                var response = await EvaluationModelCalls.ObserveAsync(async () =>
                {
                    await Task.Yield();
                    return "served";
                });
                expect(response == "served", "provider result changed");
                try
                {
                    await EvaluationModelCalls.ObserveAsync(
                        () => Task.FromException<int>(new InvalidOperationException("fixture")));
                }
                catch (InvalidOperationException) { }
                expect(meter.Read() == new EvaluationModelCalls.Snapshot(2, 1),
                    "provider attempts or failure count wrong");
                using (var nested = EvaluationModelCalls.Begin())
                {
                    await EvaluationModelCalls.ObserveAsync(() => Task.FromResult(2));
                    expect(nested.Read() == new EvaluationModelCalls.Snapshot(1, 0),
                        "nested attempt leaked into parent");
                }
                expect(meter.Read() == new EvaluationModelCalls.Snapshot(2, 1),
                    "nested counter polluted its parent");
            }
        });

        check("PR4b1: expected slots freeze 232 reports/140 Gate C activities", () =>
        {
            var slots = R3Pr4Census.Expected(CorpusValidator.Validate(evalRoot));
            expect(slots.Count == 232 && slots.Count(x => x.Comparator == Comparator.GateC) == 70 &&
                slots.Count(x => x.Comparator == Comparator.Adaptive) == 70 &&
                slots.Count(x => x.Trial == 1) == 184 &&
                slots.Distinct().Count() == 232,
                "unexpected primary/repeat allocation");
        });

        await checkAsync("PR4b1: complete synthetic study, strict blinded import, CI and negative contracts", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(),
                "code-archeologist-pr4b1-test-" + Guid.NewGuid().ToString("N"));
            var runs = Path.Combine(root, "runs");
            var export = Path.Combine(root, "judging");
            try
            {
                Directory.CreateDirectory(runs);
                CreateSyntheticStudy(evalRoot, runs);
                var unsigned = R3Pr4Census.Audit(evalRoot, runs);
                expect(!unsigned.Valid && unsigned.Errors.Any(x => x.Contains("seal", StringComparison.OrdinalIgnoreCase)),
                    "unsealed study accepted");
                R3Pr4Census.SealCompleteRun(evalRoot, runs);
                expect(R3Pr4Census.Audit(evalRoot, runs).Valid, "valid synthetic sealed census refused");

                var output = new StringWriter();
                expect(R3Pr4Judging.Export(evalRoot, runs, export, output) == 0,
                    "blinded export failed " + output);
                var key = JsonSerializer.Deserialize<R3Pr4Judging.Key>(
                    File.ReadAllText(Path.Combine(export, R3Pr4Judging.KeyName)), LabSchema.Json)!;
                expect(key.Questions.Count(q => q.Kind == "pair") == 46 &&
                    key.Questions.Count(q => q.Kind == "issue") > 0 &&
                    key.Questions.All(q => q.AComparator != q.BComparator),
                    "blinded questions missing primary pairs or issues");
                var packets = Directory.GetFiles(Path.Combine(export, "upload-these-to-chatgpt"),
                    "*.txt");
                expect(packets.Length > 0 && packets.All(f =>
                    !File.ReadAllText(f).Contains("gate-c-adaptive", StringComparison.Ordinal)),
                    "comparator name leaked into uploads");
                var incomplete = Path.Combine(root, "incomplete.txt");
                File.WriteAllText(incomplete, "Q001: A=n B=n\n");
                expect(R3Pr4Judging.Import(evalRoot, export, incomplete, new StringWriter()) == 1 &&
                    !File.Exists(Path.Combine(export, R3Pr4Judging.RecordName)),
                    "incomplete answers were accepted as judgment of record");

                // Synthetic grader detects every labelled issue only in the
                // adaptive arm; these are fabricated TEST ANSWERS, not quality.
                var answers = Path.Combine(root, "fixture-only-answers.txt");
                File.WriteAllLines(answers, key.Questions.Select(q =>
                {
                    if (q.Kind == "issue")
                        return q.Id + ": A=" + (q.AComparator == Comparator.Adaptive ? "y" : "n") +
                            " B=" + (q.BComparator == Comparator.Adaptive ? "y" : "n");
                    return q.Id + ": PREF=" +
                        (q.AComparator == Comparator.Adaptive ? "A" : "B") +
                        " ACT_A=y ACT_B=y";
                }));
                expect(R3Pr4Judging.Import(evalRoot, export, answers, new StringWriter()) == 0,
                    "complete synthetic answer import rejected");
                expect(R3Pr4Judging.Import(evalRoot, export, answers, new StringWriter()) == 2,
                    "first judgment record was overwritten");
                var verdictOutput = new StringWriter();
                expect(R3Pr4Verdict.Evaluate(evalRoot, runs, export, verdictOutput) == 0,
                    "synthetic paired verdict failed " + verdictOutput);
                var verdict = JsonSerializer.Deserialize<R3Pr4Verdict.Result>(
                    File.ReadAllText(Path.Combine(export, R3Pr4Verdict.FileName)),
                    LabSchema.Json)!;
                expect(verdict.Decision == "demonstrated-improvement-within-preregistered-evaluation" &&
                    verdict.DetectionGainPoints == 100 &&
                    verdict.Lower95Points > 0 && verdict.PrimaryCases == 46 &&
                    verdict.FixedCompleted == 70 && verdict.AdaptiveCompleted == 70 &&
                    verdict.ModelCallsAdaptive == 210,
                    "known synthetic win or bootstrap not correctly computed");

                // No difference => confidence interval contains zero =>
                // no improvement claim, despite reliable completed runs.
                var stored = R3Pr4Judging.ReadRecord(evalRoot, runs, export);
                var equalAnswers = stored.Record.Answers.Select((a, i) =>
                    stored.Key.Questions[i].Kind == "issue"
                        ? a with { AMentions = true, BMentions = true }
                        : a with { Preferred = "TIE", AActionable = true, BActionable = true }).ToArray();
                var tie = R3Pr4Verdict.Analyze(CorpusValidator.Validate(evalRoot),
                    R3Pr4Census.Audit(evalRoot, runs), stored.Key,
                    stored.Record with { Answers = equalAnswers }, "synthetic-equal");
                expect(tie.Decision == "inconclusive-no-demonstrated-improvement" &&
                    tie.Lower95Points == 0 && tie.Upper95Points == 0,
                    "zero-gain bootstrap was falsely claimed as improvement");

                // Every artifact byte is sealed. No dropped or extra trial
                // can be overlooked after judging packets exist.
                var report = Path.Combine(runs,
                    "F01-attribute-private-helper.gate-c-adaptive.t1.report.json");
                var saved = File.ReadAllBytes(report);
                File.AppendAllText(report, " ");
                expect(!R3Pr4Census.Audit(evalRoot, runs).Valid,
                    "tampered source artifact accepted");
                File.WriteAllBytes(report, saved);
                File.Delete(report);
                expect(!R3Pr4Census.Audit(evalRoot, runs).Valid, "missing trial accepted");
                File.WriteAllBytes(report, saved);
                File.WriteAllText(Path.Combine(runs, "unexpected.report.json"), "{}");
                expect(!R3Pr4Census.Audit(evalRoot, runs).Valid, "unplanned report accepted");
                File.Delete(Path.Combine(runs, "unexpected.report.json"));
                expect(R3Pr4Census.Audit(evalRoot, runs).Valid,
                    "restoring exact sealed bytes failed");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
            await Task.CompletedTask;
        });
    }

    private static void CreateSyntheticStudy(string evalRoot, string runs)
    {
        var corpus = CorpusValidator.Validate(evalRoot);
        var protocol = R3Pr4Protocol.ReadAndValidate(evalRoot, corpus, out var errors);
        if (protocol is null) throw new InvalidOperationException(string.Join(";",errors));
        var allCases = corpus.Cases.Select(c => c.Case.Id).ToArray();
        File.WriteAllText(Path.Combine(runs, "pr4-run-metadata.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = R3Pr4Protocol.Version,
                baselineCommit = protocol.BaselineCommit,
                protocolSha256 = LabSchema.Sha256(File.ReadAllBytes(
                    Path.Combine(evalRoot, R3Pr4Protocol.FileName))),
                comparators = Comparator.Pr4All,
                selectedCases = allCases,
                provisional = false,
                recordedAtUtc = DateTimeOffset.UtcNow,
                judgmentStatus = "pending-blinded-adjudication"
            }, LabSchema.Json));
        var scores = new List<CaseScore>();
        foreach (var slot in R3Pr4Census.Expected(corpus))
        {
            var loaded = corpus.Cases.Single(c => c.Case.Id == slot.CaseId);
            var gate = slot.Comparator is Comparator.GateC or Comparator.Adaptive;
            var facts = gate
                ? new LabGateCFacts(["CompletedValidated", "CompletedValidated", "CompletedValidated"],
                    [], [], false, false, true, 3) : null;
            var report = new LabReport(LabSchema.Report, slot.CaseId, slot.Comparator,
                slot.Trial, "completed", "synthetic-test-only",
                Guid.NewGuid(), new string('a',64), "test-entry", [], facts, 1,
                "verified", "synthetic", []);
            var score = new CaseScore(LabSchema.Score, slot.CaseId, slot.Comparator,
                slot.Trial, "completed", true, [],
                loaded.Labels.RequiredObservations.Count, 0, gate ? 1.0 : null,
                [], ["pending-synthetic"], true, 0, 0, true);
            scores.Add(score);
            var stem = Path.Combine(runs, slot.Stem);
            File.WriteAllText(stem + ".report.json", JsonSerializer.Serialize(report, LabSchema.Json));
            File.WriteAllText(stem + ".score.json", JsonSerializer.Serialize(score, LabSchema.Json));
            if (!gate) continue;
            var telemetry = new R3Pr4Activity(R3Pr4Activity.Version, slot.CaseId,
                slot.Comparator, slot.Trial, Guid.NewGuid(), Guid.NewGuid(),
                "completed", 1, 1, 1, slot.Comparator == Comparator.Adaptive ? 1 : null,
                0, 0, 1, 3, true, slot.Comparator == Comparator.Adaptive ? true : null,
                "synthetic-test-only", 0, "opt-in-MAF-request-attempts");
            File.WriteAllText(stem + ".pr4-activity.json",
                JsonSerializer.Serialize(telemetry, LabSchema.Json));
        }
        var expectations = corpus.Cases.Where(c =>
            c.Case.Expectation != CaseExpectation.Investigate ||
            c.Case.Change is not null).Select(c =>
                new LabHarness.ExpectationResult(c.Case.Id,
                    c.Case.Change is null ? c.Case.Expectation : "change-confirmed",
                    true, "synthetic-test-only")).ToArray();
        File.WriteAllText(Path.Combine(runs, "summary.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "r3-pr4-lab-summary-v1",
            provisional = false,
            draftLabels = 0,
            thresholdStatus = "owner-approved-for-scoring",
            capabilityContractVersion = "synthetic",
            configSha256 = LabSchema.Sha256(File.ReadAllBytes(
                Path.Combine(evalRoot, "lab-config.json"))),
            comparators = LabScoring.Summarize(scores),
            expectations,
            notAcquired = Array.Empty<string>(),
            failures = Array.Empty<string>()
        }, LabSchema.Json));
    }
}

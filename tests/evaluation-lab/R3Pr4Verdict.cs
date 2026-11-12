using System.Globalization;
using System.Text.Json;

namespace CodeArcheologist.EvaluationLab;

/// <summary>
/// Deterministic, preregistered PR4 statistical analysis. One primary trial
/// per case, all issues within a case form ONE resampling cluster; diagnostic
/// repetitions inform operational completion only, never sample size.
/// No judge or model is invoked, and no C7 metric is reused.
/// </summary>
public static class R3Pr4Verdict
{
    public const string Version = "r3-pr4-paired-verdict-v1";
    public const string FileName = "pr4-paired-verdict.json";
    public const int Seed = 20261112;
    public const int Iterations = 10000;

    public sealed record Result(string SchemaVersion, string Decision,
        string ProtocolSha256, string JudgmentRecordSha256,
        int PrimaryCases, int MaterialIssues, int FixedDetected, int AdaptiveDetected,
        double FixedDetectionRate, double AdaptiveDetectionRate,
        double DetectionGainPoints, double Lower95Points, double Upper95Points,
        int FixedActionable, int AdaptiveActionable,
        int FixedPreferred, int AdaptivePreferred, int Ties,
        int FixedCompleted, int AdaptiveCompleted, int GateCTrials,
        double FixedCompletionRate, double AdaptiveCompletionRate,
        double CompletionLagPoints, double AdaptiveObjectionCoverage,
        int IntegrityBreaches, int ModelCallsFixed, int ModelCallsAdaptive,
        int ModelFaultsFixed, int ModelFaultsAdaptive,
        int BootstrapSeed, int BootstrapIterations, string[] FailedGates,
        string[] Limitations);

    private sealed record Cluster(int MaterialIssues, int FixedDetected, int AdaptiveDetected);

    public static int Evaluate(string evalRoot, string runs, string export,
        TextWriter output)
    {
        try
        {
            var corpus = CorpusValidator.Validate(evalRoot);
            var audit = R3Pr4Census.Audit(evalRoot, runs);
            var (key, record, errors) = R3Pr4Judging.ReadRecord(evalRoot, runs, export);
            if (!audit.Valid || errors.Count > 0)
            {
                foreach (var x in audit.Errors.Concat(errors).Distinct().Take(20))
                    output.WriteLine("PR4 verdict refused: " + x);
                return 1;
            }
            var analysis = Analyze(corpus, audit, key, record,
                LabSchema.Sha256(File.ReadAllBytes(
                    Path.Combine(export, R3Pr4Judging.RecordName))));
            var path = Path.Combine(export, FileName);
            using (var stream = new FileStream(path, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, analysis, LabSchema.Json);
            output.WriteLine($"PR4: {analysis.Decision}");
            output.WriteLine($"Material detection fixed={analysis.FixedDetected}/{analysis.MaterialIssues}, " +
                $"adaptive={analysis.AdaptiveDetected}/{analysis.MaterialIssues}; gain=" +
                $"{analysis.DetectionGainPoints:F2} points, 95% paired case-cluster CI " +
                $"[{analysis.Lower95Points:F2}, {analysis.Upper95Points:F2}].");
            output.WriteLine($"Primary actionable fixed={analysis.FixedActionable}/46 " +
                $"adaptive={analysis.AdaptiveActionable}/46; completed fixed=" +
                $"{analysis.FixedCompleted}/70 adaptive={analysis.AdaptiveCompleted}/70.");
            foreach (var fail in analysis.FailedGates) output.WriteLine("  FAILED GATE: " + fail);
            output.WriteLine("This is one uncalibrated external judgment, partly blinded by structure. " +
                "The result cannot establish runtime or semantic proof.");
            return 0; // Analysis was valid, even if the model failed the acceptance gates.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            output.WriteLine("PR4 verdict fail-closed: " + error.GetType().Name);
            return 1;
        }
    }

    public static Result Analyze(CorpusReport corpus, R3Pr4Census.Result audit,
        R3Pr4Judging.Key key, R3Pr4Judging.Record judgment,
        string judgmentDigest)
    {
        if (!audit.Valid) throw new InvalidOperationException("Cannot analyze an invalid census.");
        var primary = corpus.Cases.Where(c => c.Case.Expectation == CaseExpectation.Investigate)
            .OrderBy(c => c.Case.Id, StringComparer.Ordinal).ToArray();
        if (primary.Length != 46) throw new InvalidOperationException("Expected 46 primary cases.");
        var lookup = key.Questions.Zip(judgment.Answers)
            .ToDictionary(x => x.First.Id, x => (Q:x.First, A:x.Second),
                StringComparer.Ordinal);
        if (lookup.Count != key.Questions.Count)
            throw new InvalidOperationException("Judging key/record is not one-to-one.");

        bool On(R3Pr4Judging.Question q, R3Pr4Judging.Answer a, string comparator,
            bool? aAnswer, bool? bAnswer) =>
            q.AComparator == comparator ? aAnswer == true : bAnswer == true;
        var clusters = new List<Cluster>();
        var actions = new Dictionary<string, int>(StringComparer.Ordinal)
            { [Comparator.GateC] = 0, [Comparator.Adaptive] = 0 };
        var preferences = new Dictionary<string, int>(StringComparer.Ordinal)
            { [Comparator.GateC] = 0, [Comparator.Adaptive] = 0, ["tie"] = 0 };

        foreach (var loaded in primary)
        {
            var issues = loaded.Labels.MaterialIssues;
            var f = 0;
            var a = 0;
            foreach (var issue in issues)
            {
                var pair = lookup.Values.Single(x => x.Q.Kind == "issue" &&
                    x.Q.CaseId == loaded.Case.Id && x.Q.IssueId == issue.Id);
                if (On(pair.Q, pair.A, Comparator.GateC, pair.A.AMentions,
                        pair.A.BMentions)) f++;
                if (On(pair.Q, pair.A, Comparator.Adaptive, pair.A.AMentions,
                        pair.A.BMentions)) a++;
            }
            clusters.Add(new(issues.Count, f, a));
            var usefulness = lookup.Values.Single(x => x.Q.Kind == "pair" &&
                x.Q.CaseId == loaded.Case.Id);
            if (On(usefulness.Q, usefulness.A, Comparator.GateC,
                    usefulness.A.AActionable, usefulness.A.BActionable))
                actions[Comparator.GateC]++;
            if (On(usefulness.Q, usefulness.A, Comparator.Adaptive,
                    usefulness.A.AActionable, usefulness.A.BActionable))
                actions[Comparator.Adaptive]++;
            var pref = usefulness.A.Preferred switch
            {
                "A" => usefulness.Q.AComparator,
                "B" => usefulness.Q.BComparator,
                "TIE" => "tie",
                _ => throw new InvalidOperationException("Invalid preference.")
            };
            preferences[pref]++;
        }
        var material = clusters.Sum(c => c.MaterialIssues);
        if (material == 0) throw new InvalidOperationException("No labelled material issues.");
        var detectedFixed = clusters.Sum(c => c.FixedDetected);
        var detectedAdaptive = clusters.Sum(c => c.AdaptiveDetected);
        var gain = 100.0 * (detectedAdaptive - detectedFixed) / material;

        var random = new Random(Seed);
        var draws = new double[Iterations];
        for (var iteration = 0; iteration < draws.Length; iteration++)
        {
            var denominator = 0;
            var difference = 0;
            for (var i = 0; i < clusters.Count; i++)
            {
                var cluster = clusters[random.Next(clusters.Count)];
                denominator += cluster.MaterialIssues;
                difference += cluster.AdaptiveDetected - cluster.FixedDetected;
            }
            // If all resampled cases lack labelled issues, detection is
            // undefined. Never replace this with fabricated zero certainty.
            if (denominator == 0) throw new InvalidOperationException(
                "Bootstrap sample has no issue denominator.");
            draws[iteration] = 100.0 * difference / denominator;
        }
        Array.Sort(draws);
        var lower = draws[(int)Math.Floor(0.025 * (Iterations - 1))];
        var upper = draws[(int)Math.Ceiling(0.975 * (Iterations - 1))];

        var fixedScore = audit.Scores.Where(x => x.Comparator == Comparator.GateC).ToArray();
        var adaptiveScore = audit.Scores.Where(x => x.Comparator == Comparator.Adaptive).ToArray();
        if (fixedScore.Length != 70 || adaptiveScore.Length != 70)
            throw new InvalidOperationException("Diagnostic repeats missing from completion denominator.");
        var fixedCompleted = fixedScore.Count(s => s.Completed);
        var adaptiveCompleted = adaptiveScore.Count(s => s.Completed);
        var fixedRate = fixedCompleted / 70.0;
        var adaptiveRate = adaptiveCompleted / 70.0;
        var coverage = adaptiveScore.Where(s => s.Completed)
            .Select(s => s.ObjectionCoverage).ToArray();
        var minCoverage = coverage.Length == 0 || coverage.Any(x => x is null) ? 0 :
            coverage.Min(x => x!.Value);
        var breaches = audit.Scores.Sum(s => s.IntegrityBreaches.Count);
        var failures = new List<string>();
        if (breaches != 0) failures.Add("zero-integrity-breaches");
        if (adaptiveRate < 0.90) failures.Add("adaptive-completion-at-least-90-percent");
        if ((fixedRate - adaptiveRate) * 100 > 5 + 1e-9)
            failures.Add("adaptive-completion-no-more-than-5pp-below-fixed");
        if (minCoverage != 1) failures.Add("adaptive-material-objection-coverage-100-percent");
        if (actions[Comparator.Adaptive] < actions[Comparator.GateC])
            failures.Add("46-primary-pair-actionable-nonregression");
        if (gain < 10) failures.Add("material-issue-detection-gain-at-least-10pp");
        if (lower <= 0) failures.Add("paired-case-cluster-95-percent-CI-above-zero");

        var decision = failures.Count == 0
            ? "demonstrated-improvement-within-preregistered-evaluation"
            : upper < 0 ? "measured-regression"
            : lower <= 0 && upper >= 0 ? "inconclusive-no-demonstrated-improvement"
            : "not-accepted-preregistered-gates-failed";
        var fixedCalls = audit.Activity.Where(a => a.Comparator == Comparator.GateC).ToArray();
        var adaptiveCalls = audit.Activity.Where(a => a.Comparator == Comparator.Adaptive).ToArray();
        if (fixedCalls.Length != 70 || adaptiveCalls.Length != 70 ||
            fixedCalls.Concat(adaptiveCalls).Any(x => x.ModelCalls is null ||
                x.ModelCallFailures is null))
            throw new InvalidOperationException("Provider-facing PR4 call counters are missing.");

        return new(Version, decision, audit.ProtocolSha256!, judgmentDigest,
            primary.Length, material, detectedFixed, detectedAdaptive,
            detectedFixed / (double)material, detectedAdaptive / (double)material,
            gain, lower, upper,
            actions[Comparator.GateC], actions[Comparator.Adaptive],
            preferences[Comparator.GateC], preferences[Comparator.Adaptive],
            preferences["tie"], fixedCompleted, adaptiveCompleted, 70,
            fixedRate, adaptiveRate, (fixedRate - adaptiveRate) * 100,
            minCoverage, breaches,
            fixedCalls.Sum(x => x.ModelCalls!.Value),
            adaptiveCalls.Sum(x => x.ModelCalls!.Value),
            fixedCalls.Sum(x => x.ModelCallFailures!.Value),
            adaptiveCalls.Sum(x => x.ModelCallFailures!.Value),
            Seed, Iterations, [..failures],
            ["One uncalibrated external judge; structural/textual blinding is partial.",
             "Issue labels are fixed by the owner, but detection is a judgment rather than semantic proof.",
             "MAF call counts exclude provider-internal retries and tokens.",
             "Repeated diagnostics affect reliability only, not primary detection sample size."]);
    }
}

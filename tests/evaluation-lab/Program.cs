using System.Text.Json;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.EvaluationLab;

// C6c-1 evaluation laboratory. Model-free commands: --describe, --validate-corpus,
// --self-test. Genuine local Ollama command: --run. Every case runs in its own
// disposable data directory; nothing reads or writes the user's real workspace.
var evalRoot = FindEvalRoot();
var original = Environment.GetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR");
try
{
    switch (args.FirstOrDefault())
    {
        case "--validate-corpus":
            return Validate(evalRoot, out _) ? 0 : 1;
        case "--describe" when args.Length == 2:
            return await DescribeAsync(evalRoot, Path.GetFileName(Path.GetFullPath(args[1]).TrimEnd('/', '\\')));
        case "--self-test":
            return await LabSelfTests.RunAsync(evalRoot);
        case "--run":
            return await RunAsync(evalRoot, args[1..]);
        default:
            Console.Error.WriteLine("""
                Usage:
                  --validate-corpus                    static corpus + budget checks (model-free)
                  --self-test                          harness, comparators, scoring (model-free)
                  --describe <case-dir>                print citable evidence locations for labeling
                  --run --out <dir> [--cases A,B] [--comparators deterministic-baseline,gate-b,gate-c] [--provisional]
                """);
            return 2;
    }
}
finally
{
    Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", original);
}

static string FindEvalRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "eval", "lab-config.json")))
            return Path.Combine(dir.FullName, "eval");
    throw new InvalidOperationException("eval/lab-config.json not found above the lab binary.");
}

static bool Validate(string evalRoot, out CorpusReport report)
{
    report = CorpusValidator.Validate(evalRoot);
    foreach (var error in report.Errors) Console.Error.WriteLine("  corpus error: " + error);
    Console.WriteLine(report.Valid
        ? $"GREEN: C6c corpus valid: {report.Cases.Count} cases ({report.ApprovedLabels} owner-approved, {report.DraftLabels} draft labels); budgets match host constants."
        : $"RED: {report.Errors.Count} corpus error(s).");
    return report.Valid;
}

static async Task<int> DescribeAsync(string evalRoot, string caseId)
{
    var report = CorpusValidator.Validate(evalRoot);
    var loaded = report.Cases.SingleOrDefault(c => c.Case.Id == caseId)
        ?? throw new InvalidOperationException("Case is not valid in the corpus: " + string.Join("; ", report.Errors));
    await using var captured = await LabHarness.CaptureAsync(loaded, CancellationToken.None);
    Console.WriteLine($"entry {captured.Scope.EntryPointId}");
    foreach (var c in LabHarness.Citable(captured.Package))
        Console.WriteLine($"  {c.FilePath}:{c.Line}  {c.EvidenceStatus}  {c.CanonicalId}");
    foreach (var u in captured.Package.Unknowns) Console.WriteLine("  unknown: " + u);
    return 0;
}

static async Task<int> RunAsync(string evalRoot, string[] options)
{
    string? Option(string name) => Array.IndexOf(options, name) is var i and >= 0 && i + 1 < options.Length
        ? options[i + 1] : null;
    var outDir = Option("--out");
    if (outDir is null) { Console.Error.WriteLine("--out <dir> is required (outside the repository)."); return 2; }
    if (!Validate(evalRoot, out var corpus)) return 1;
    var provisional = options.Contains("--provisional");
    if (!provisional && (corpus.DraftLabels > 0 || corpus.Config.ThresholdStatus != "owner-approved-for-scoring"))
    {
        Console.Error.WriteLine("Draft labels or provisional thresholds present: pass --provisional. " +
            "Provisional results can never be relabelled as acceptance results.");
        return 2;
    }
    var selected = Option("--cases")?.Split(',') ?? corpus.Cases.Select(c => c.Case.Id).ToArray();
    var comparators = Option("--comparators")?.Split(',') ?? Comparator.All;
    if (comparators.Any(c => !Comparator.All.Contains(c))) { Console.Error.WriteLine("Unknown comparator."); return 2; }
    Directory.CreateDirectory(outDir);
    var scores = new List<CaseScore>();
    var failures = new List<string>();
    foreach (var loaded in corpus.Cases.Where(c => selected.Contains(c.Case.Id)))
    {
        await using var captured = await LabHarness.CaptureAsync(loaded, CancellationToken.None);
        var unsatisfiable = LabHarness.UnsatisfiableAnchors(captured);
        if (unsatisfiable.Count > 0)
        {
            failures.Add($"{loaded.Case.Id}: unsatisfiable anchors {string.Join(",", unsatisfiable)}");
            continue;
        }
        foreach (var comparator in comparators)
        {
            var trials = comparator == Comparator.GateC && loaded.Case.DiagnosticRepeat
                ? corpus.Config.PrimaryTrials + corpus.Config.DiagnosticExtraTrials : corpus.Config.PrimaryTrials;
            for (var trial = 1; trial <= trials; trial++)
            {
                var report = comparator switch
                {
                    Comparator.Baseline => LabHarness.Baseline(captured, trial),
                    Comparator.GateB => await LabHarness.GateBAsync(captured, trial, CancellationToken.None),
                    _ => await LabHarness.GateCAsync(captured, trial, new MafRoleStageRunner(),
                        TimeSpan.FromMinutes(8), CancellationToken.None)
                };
                var score = LabScoring.Score(report, captured);
                scores.Add(score);
                var stem = Path.Combine(outDir, $"{loaded.Case.Id}.{comparator}.t{trial}");
                await File.WriteAllTextAsync(stem + ".report.json", JsonSerializer.Serialize(report, LabSchema.Json));
                await File.WriteAllTextAsync(stem + ".score.json", JsonSerializer.Serialize(score, LabSchema.Json));
                Console.WriteLine($"{loaded.Case.Id} {comparator} t{trial}: {report.Outcome} " +
                    $"breaches={score.IntegrityBreaches.Count} anchors={score.CitedObservationAnchors}/{score.RequiredObservations}" +
                    (score.ObjectionCoverage is { } cov ? $" coverage={cov:P0}" : "") +
                    (score.ReviewFlags.Count > 0 ? " flags=" + score.ReviewFlags.Count : ""));
            }
        }
    }
    var summary = new
    {
        schemaVersion = LabSchema.Score + "-summary",
        provisional,
        draftLabels = corpus.DraftLabels,
        thresholdStatus = corpus.Config.ThresholdStatus,
        configSha256 = LabSchema.Sha256(File.ReadAllBytes(Path.Combine(evalRoot, "lab-config.json"))),
        comparators = LabScoring.Summarize(scores),
        pendingJudgment = new[] { "material-issue-detection", "actionable-usefulness (blinded owner A/B, C6c-3)" },
        failures
    };
    await File.WriteAllTextAsync(Path.Combine(outDir, "summary.json"), JsonSerializer.Serialize(summary, LabSchema.Json));
    foreach (var s in summary.comparators)
        Console.WriteLine($"{s.Comparator}: {s.Completed}/{s.Evaluations} completed, breaches={s.IntegrityBreaches}, " +
            $"anchor recall={s.ObservationAnchorRecall:P0}" +
            (s.MeanObjectionCoverage is { } m ? $", objection coverage={m:P0}" : "") + $", review flags={s.ReviewFlags}");
    var totalBreaches = scores.Sum(s => s.IntegrityBreaches.Count);
    Console.WriteLine(totalBreaches == 0 && failures.Count == 0
        ? $"{(provisional ? "PROVISIONAL" : "SCORED")} run finished: zero integrity breaches. Usefulness and issue detection pending blinded judgment."
        : $"RED: {totalBreaches} integrity breach(es), {failures.Count} case failure(s). See {outDir}.");
    return totalBreaches == 0 && failures.Count == 0 ? 0 : 1;
}

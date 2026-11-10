using Microsoft.Extensions.DependencyInjection;
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
        case "--describe" when args.Length is 2 or 4:
            return await DescribeAsync(evalRoot, Path.GetFileName(Path.GetFullPath(args[1]).TrimEnd('/', '\\')),
                args.Length == 4 && args[2] == "--curated-root" ? Path.GetFullPath(args[3]) : null);
        case "--survey" when args.Length == 2:
            return await SurveyAsync(Path.GetFullPath(args[1]), null);
        case "--survey" when args.Length == 4 && args[2] == "--write-manifest":
            return await SurveyAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[3]));
        case "--self-test":
            return await LabSelfTests.RunAsync(evalRoot);
        case "--review-labels":
            return LabOwnerTools.ReviewLabels(evalRoot,
                args.Length == 3 && args[1] == "--curated-root" ? Path.GetFullPath(args[2]) : null,
                Console.In, Console.Out);
        case "--checklist" when args.Length >= 3 && args[1] == "--runs":
        {
            string Opt(string name, string fallback) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
            var runs = Path.GetFullPath(args[2]);
            return LabOwnerTools.Checklist(evalRoot, runs, Opt("--a", Comparator.GateB), Opt("--b", Comparator.GateC),
                Path.GetFullPath(Opt("--out", Path.Combine(runs, "issue-checklist.json"))), Console.In, Console.Out,
                int.Parse(Opt("--max", "5"), System.Globalization.CultureInfo.InvariantCulture),
                everyProblem: args.Contains("--every-problem"));
        }
        case "--judge" when args.Length >= 3 && args[1] == "--runs":
        {
            string Opt(string name, string fallback) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
            var runs = Path.GetFullPath(args[2]);
            return LabOwnerTools.Judge(runs, Opt("--a", Comparator.GateB), Opt("--b", Comparator.GateC),
                Path.GetFullPath(Opt("--out", Path.Combine(runs, "blinded-judgments.json"))), Console.In, Console.Out);
        }
        case "--run":
            return await RunAsync(evalRoot, args[1..]);
        default:
            Console.Error.WriteLine("""
                Usage:
                  --validate-corpus                    static corpus + budget checks (model-free)
                  --self-test                          harness, comparators, scoring (model-free)
                  --survey <dir>                       read-only capture survey: fingerprint, size, HTTP candidates
                  --describe <case-dir> [--curated-root <dir>]   citable evidence locations for labeling
                  --run --out <dir> [--curated-root <dir>] [--cases A,B] [--comparators deterministic-baseline,gate-b,gate-c] [--provisional]
                  --review-labels [--curated-root <dir>]          owner label sign-off (interactive, resumable)
                  --checklist --runs <dir> [--max 5 | --every-problem] [--out <file>]   short blinded material-issue checklist (interactive, resumable)
                  --judge --runs <dir> [--a gate-b] [--b gate-c] [--out <file>]   blinded pairwise comparison (interactive, resumable)
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

static async Task<int> DescribeAsync(string evalRoot, string caseId, string? curatedRoot)
{
    var report = CorpusValidator.Validate(evalRoot);
    var loaded = report.Cases.SingleOrDefault(c => c.Case.Id == caseId)
        ?? throw new InvalidOperationException("Case is not valid in the corpus: " + string.Join("; ", report.Errors));
    await using var captured = await LabHarness.CaptureAsync(loaded, CancellationToken.None, curatedRoot);
    Console.WriteLine($"entry {captured.Scope.EntryPointId}");
    foreach (var c in LabHarness.Citable(captured.Package))
        Console.WriteLine($"  {c.FilePath}:{c.Line}  {c.EvidenceStatus}  {c.CanonicalId}");
    foreach (var u in captured.Package.Unknowns) Console.WriteLine("  unknown: " + u);
    return 0;
}

// Read-only survey of a local checkout (curated-case preparation): snapshot
// fingerprint, size, and every supported HTTP candidate, or the fail-closed reason.
static async Task<int> SurveyAsync(string source, string? manifestOut)
{
    var root = Path.Combine(Path.GetTempPath(), "code-archeologist-lab-survey-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var (services, _, _, _, snapshot, graph) = await LabHarness.CaptureSourceAsync(source,
            Path.Combine(root, "data"), "Lab survey", CancellationToken.None);
        await using var _s = services;
        Console.WriteLine($"fingerprint {snapshot.FingerprintSha256} files={snapshot.FileCount} bytes={snapshot.TotalBytes} analyzer={graph.AnalyzerVersion}");
        if (manifestOut is not null)
            await File.WriteAllLinesAsync(manifestOut, LabHarness.ManifestLines(
                services.GetRequiredService<CodeArcheologist.Api.Workspaces.SnapshotCaptureService>().ReadManifest(snapshot)));
        foreach (var n in graph.Nodes.Where(n => n.Kind is "http-endpoint-attribute" or "http-minimal-mapping")
                     .OrderBy(n => n.FilePath, StringComparer.Ordinal).ThenBy(n => n.Line))
            Console.WriteLine($"  {n.Kind} {n.FilePath}:{n.Line} {n.Label}");
        return 0;
    }
    catch (Exception error) when (error is not OutOfMemoryException)
    {
        Console.WriteLine($"fail-closed {error.GetType().Name}: {error.Message}");
        return 3;
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
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
    var curatedRoot = Option("--curated-root") is { } cr ? Path.GetFullPath(cr) : null;
    var scores = new List<CaseScore>();
    var failures = new List<string>();
    var expectations = new List<LabHarness.ExpectationResult>();
    var notAcquired = new List<string>();
    foreach (var loaded in corpus.Cases.Where(c => selected.Contains(c.Case.Id)))
    {
        if (loaded.Curated is not null && curatedRoot is null)
        {
            notAcquired.Add(loaded.Case.Id); // visible in the summary, never silently dropped
            continue;
        }
        if (loaded.Case.Expectation != CaseExpectation.Investigate)
        {
            try
            {
                var result = await LabHarness.CheckExpectationAsync(loaded, curatedRoot, CancellationToken.None);
                expectations.Add(result);
                if (!result.Confirmed) failures.Add($"{loaded.Case.Id}: expectation violated: {result.Detail}");
                Console.WriteLine($"{loaded.Case.Id} {result.Expectation}: {(result.Confirmed ? "confirmed" : "VIOLATED")} ({result.Detail})");
            }
            catch (LabCaseException error) { failures.Add(error.Message); Console.WriteLine(error.Message); }
            continue;
        }
        CapturedCase captured;
        try { captured = await LabHarness.CaptureAsync(loaded, CancellationToken.None, curatedRoot); }
        catch (LabCaseException error) { failures.Add(error.Message); Console.WriteLine(error.Message); continue; }
        await using var _captured = captured;
        if (loaded.Case.Change is not null)
        {
            try
            {
                var change = await LabHarness.CheckChangeAsync(captured, CancellationToken.None);
                expectations.Add(change);
                if (!change.Confirmed) failures.Add($"{loaded.Case.Id}: change expectation violated: {change.Detail}");
                Console.WriteLine($"{loaded.Case.Id} {change.Expectation}: {(change.Confirmed ? "confirmed" : "VIOLATED")} ({change.Detail})");
            }
            catch (LabCaseException error) { failures.Add(error.Message); Console.WriteLine(error.Message); }
        }
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
        // Attributes the run to the exact agent evidence contract (e.g. E1 = v2).
        capabilityContractVersion = CodeArcheologist.Api.Investigations.GateCEvidenceRegistry.Version,
        configSha256 = LabSchema.Sha256(File.ReadAllBytes(Path.Combine(evalRoot, "lab-config.json"))),
        comparators = LabScoring.Summarize(scores),
        expectations,
        notAcquired,
        pendingJudgment = new[] { "material-issue-detection", "actionable-usefulness (blinded owner A/B, C6c-3)" },
        failures
    };
    await File.WriteAllTextAsync(Path.Combine(outDir, "summary.json"), JsonSerializer.Serialize(summary, LabSchema.Json));
    foreach (var s in summary.comparators)
        Console.WriteLine($"{s.Comparator}: {s.Completed}/{s.Evaluations} completed, breaches={s.IntegrityBreaches}, " +
            $"anchor recall={s.ObservationAnchorRecall:P0}" +
            (s.MeanObjectionCoverage is { } m ? $", objection coverage={m:P0}" : "") + $", review flags={s.ReviewFlags}");
    if (notAcquired.Count > 0)
        Console.WriteLine($"NOT ACQUIRED ({notAcquired.Count}): {string.Join(", ", notAcquired)}. Pass --curated-root after running the acquisition script.");
    var totalBreaches = scores.Sum(s => s.IntegrityBreaches.Count);
    Console.WriteLine($"agent evidence contract: {CodeArcheologist.Api.Investigations.GateCEvidenceRegistry.Version}");
    Console.WriteLine(totalBreaches == 0 && failures.Count == 0
        ? $"{(provisional ? "PROVISIONAL" : "SCORED")} run finished: zero integrity breaches. Usefulness and issue detection pending blinded judgment."
        : $"RED: {totalBreaches} integrity breach(es), {failures.Count} case failure(s). See {outDir}.");
    return totalBreaches == 0 && failures.Count == 0 ? 0 : 1;
}

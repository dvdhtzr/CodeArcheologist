using System.Security.Cryptography;
using System.Text.Json;

namespace CodeArcheologist.EvaluationLab;

/// <summary>
/// C6c-3 owner tools (console, no model): label sign-off and a blinded
/// pairwise usefulness comparison. Input/output are injectable so the flows
/// are self-tested with scripted answers.
/// </summary>
public static class LabOwnerTools
{
    public const string JudgmentSchema = "c6c-blinded-judgments-v1";

    // ------------------------------------------------------------ label review

    /// <summary>
    /// Shows each DRAFT label set with its source and records the owner's
    /// decision. Approving writes status owner-approved; the owner's own Git
    /// commit is the signature. Nothing is approved without an explicit 'a'.
    /// </summary>
    public static int ReviewLabels(string evalRoot, string? curatedRoot, TextReader input, TextWriter output)
    {
        var corpus = CorpusValidator.Validate(evalRoot);
        if (!corpus.Valid) { output.WriteLine("Corpus invalid; run --validate-corpus first."); return 1; }
        var drafts = corpus.Cases.Where(c => c.Labels.Status == LabelStatus.Draft).ToArray();
        output.WriteLine($"{drafts.Length} draft label set(s). For each: [a] approve, [s] skip, [q] quit.");
        output.WriteLine("To change a label, choose [s], edit its labels.json, and run this again.");
        var approved = 0;
        foreach (var (loaded, index) in drafts.Select((c, i) => (c, i + 1)))
        {
            output.WriteLine();
            output.WriteLine(new string('=', 78));
            output.WriteLine($"[{index}/{drafts.Length}] {loaded.Case.Id}: {loaded.Case.Title}");
            output.WriteLine($"Purpose: {loaded.Case.Purpose}");
            if (loaded.Case.EntryPoint is { } e) output.WriteLine($"Entry point: {e.Kind} at {e.FilePath}:{e.Line}");
            output.WriteLine($"Expectation: {loaded.Case.Expectation}" +
                (loaded.Case.Change is { } ch ? $"; change '{ch.Edit}' ({ch.Impact})" : ""));
            ShowSource(loaded, curatedRoot, output);
            ShowLabels(loaded.Labels, output);
            while (true)
            {
                output.Write("Approve these labels? [a/s/q] ");
                var answer = input.ReadLine()?.Trim().ToLowerInvariant();
                if (answer is null or "q") { output.WriteLine($"Stopped. Approved {approved} this session."); return Finish(evalRoot, approved, output); }
                if (answer == "s") break;
                if (answer != "a") continue;
                var signed = loaded.Labels with { Status = LabelStatus.OwnerApproved, AuthoredBy = "assistant-draft-owner-reviewed" };
                var path = Path.Combine(loaded.Directory, "labels.json");
                if (!TrySign(File.ReadAllText(path), signed, out var text))
                {
                    output.WriteLine("Cannot sign this file safely (unexpected layout); it was left unchanged.");
                    break;
                }
                File.WriteAllText(path, text);
                approved++;
                break;
            }
        }
        return Finish(evalRoot, approved, output);
    }

    private static int Finish(string evalRoot, int approved, TextWriter output)
    {
        var after = CorpusValidator.Validate(evalRoot);
        output.WriteLine($"Approved {approved} this session; corpus now {after.ApprovedLabels} approved, {after.DraftLabels} draft.");
        if (!after.Valid) { output.WriteLine("WARNING: corpus is invalid after review; do not commit."); return 1; }
        if (approved > 0)
            output.WriteLine("Review the changes, then commit them yourself (your commit is the signature): " +
                "git add eval/cases && git commit -m \"Owner sign-off of C6c labels\"");
        return 0;
    }

    private static void ShowSource(LoadedCase loaded, string? curatedRoot, TextWriter output)
    {
        void File(string title, string path, IEnumerable<int>? focus)
        {
            if (!System.IO.File.Exists(path)) { output.WriteLine($"  ({title}: not available)"); return; }
            var lines = System.IO.File.ReadAllLines(path);
            var show = focus is null
                ? Enumerable.Range(1, Math.Min(lines.Length, 80)).ToHashSet()
                : focus.SelectMany(l => Enumerable.Range(Math.Max(1, l - 6), 13)).Where(l => l <= lines.Length).ToHashSet();
            output.WriteLine($"--- {title}");
            var previous = 0;
            foreach (var n in show.Order())
            {
                if (previous != 0 && n != previous + 1) output.WriteLine("     ...");
                output.WriteLine($"{n,4} | {lines[n - 1]}");
                previous = n;
            }
        }
        if (loaded.Case.Category == CaseCategory.Curated)
        {
            if (curatedRoot is null || loaded.Curated is null) { output.WriteLine("  (curated source not shown: pass --curated-root)"); return; }
            var root = Path.Combine(curatedRoot, loaded.Curated.Id, loaded.Curated.Subtree);
            var anchors = loaded.Labels.RequiredObservations.Select(o => o.Anchor)
                .Concat(loaded.Case.EntryPoint is { } e ? [new LabelAnchor(e.FilePath, e.Line)] : [])
                .GroupBy(a => a.FilePath);
            foreach (var g in anchors) File(g.Key, Path.Combine(root, g.Key), g.Select(a => a.Line));
            if (!anchors.Any()) output.WriteLine($"  (no anchors; repository {loaded.Curated.Repository} at {loaded.Curated.CommitSha[..12]})");
            return;
        }
        if (loaded.Case.Category == CaseCategory.ChangeExperiment)
            foreach (var f in loaded.Case.Source.BeforeFileSha256!.Keys.Order())
                File("BEFORE " + f, Path.Combine(loaded.Directory, "before", f), null);
        foreach (var f in loaded.Case.Source.FileSha256.Keys.Order())
            File((loaded.Case.Category == CaseCategory.ChangeExperiment ? "AFTER " : "") + f,
                Path.Combine(loaded.Directory, loaded.Case.Source.Directory, f), null);
    }

    private static void ShowLabels(LabLabels l, TextWriter output)
    {
        output.WriteLine("--- Draft labels");
        foreach (var o in l.RequiredObservations) output.WriteLine($"  must cite  {o.Id}: {o.Description} [{o.Anchor.FilePath}:{o.Anchor.Line}]");
        foreach (var u in l.RequiredUncertainties) output.WriteLine($"  uncertain  {u.Id}: {u.Description}");
        foreach (var f in l.ForbiddenClaims) output.WriteLine($"  forbidden  {f.Id}: {f.Description} ({f.Category})");
        foreach (var m in l.MaterialIssues) output.WriteLine($"  issue      {m.Id}: {m.Description}");
    }

    // ------------------------------------------------------- issue checklist

    public const string ChecklistSchema = "c6c-issue-checklist-v1";

    public sealed record IssueCheck(string CaseId, string IssueId, string FirstComparator, int FirstTrial, bool FirstMentions,
        string SecondComparator, int SecondTrial, bool SecondMentions);

    public sealed record IssueCheckFile(string SchemaVersion, string RunsDirectory, DateTimeOffset StartedAtUtc,
        IReadOnlyList<IssueCheck> Checks);

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "this", "that", "with", "from", "into", "only", "than", "then", "they", "them", "their", "there", "where",
        "which", "while", "when", "what", "does", "done", "have", "been", "being", "also", "more", "most", "must",
        "never", "not", "nor", "over", "under", "same", "such", "some", "each", "both", "other", "still", "upon",
        "will", "would", "could", "should", "here", "your", "just", "very", "via", "about", "after", "before",
    };

    /// <summary>Share of an issue's content words present in a report. Used ONLY to order and select
    /// questions for the owner; it is never a score (rule 57: lexical screens only flag for review).</summary>
    public static double LexicalOverlap(string issue, LabReport report)
    {
        static IEnumerable<string> Words(string t) => System.Text.RegularExpressions.Regex
            .Matches(t.ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value).Where(w => w.Length >= 4 && !StopWords.Contains(w));
        var wanted = Words(issue).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return 0;
        var present = Words(string.Join(" ", report.Claims.Select(c => c.Text))).ToHashSet(StringComparer.Ordinal);
        return (double)wanted.Count(present.Contains) / wanted.Count;
    }

    /// <summary>
    /// Short owner checklist: for owner-approved material issues, asks whether each of two anonymous
    /// reports (one per comparator, random order) mentions the issue. Only issue pairs where the
    /// reports appear to differ are eligible, most different first, capped at <paramref name="maxPairs"/>.
    /// Pairs not asked are reported as not judged, never as ties. Resumable.
    /// </summary>
    public static int Checklist(string evalRoot, string runsDirectory, string a, string b, string checksPath,
        TextReader input, TextWriter output, int maxPairs = 5, int? seed = null, bool everyProblem = false)
    {
        if (everyProblem) maxPairs = int.MaxValue;
        var random = seed is { } s ? new Random(s) : new Random(RandomNumberGenerator.GetInt32(int.MaxValue));
        var corpus = CorpusValidator.Validate(evalRoot);
        if (!corpus.Valid) { output.WriteLine("Corpus is invalid; run --validate-corpus."); return 1; }
        var labels = corpus.Cases.Where(c => c.Labels.Status == LabelStatus.OwnerApproved)
            .ToDictionary(c => c.Case.Id, c => c.Labels, StringComparer.Ordinal);
        var reports = Directory.GetFiles(runsDirectory, "*.report.json")
            .Select(f => JsonSerializer.Deserialize<LabReport>(File.ReadAllText(f), LabSchema.Json)!)
            .Where(r => r.Outcome == "completed").ToArray();
        var done = File.Exists(checksPath)
            ? JsonSerializer.Deserialize<IssueCheckFile>(File.ReadAllText(checksPath), LabSchema.Json)!.Checks.ToList()
            : [];

        var eligible = new List<(string CaseId, MaterialIssue Issue, LabReport First, LabReport Second, double Gap)>();
        var notEligible = 0;
        foreach (var caseId in reports.Select(r => r.CaseId).Distinct().Order(StringComparer.Ordinal))
        {
            if (!labels.TryGetValue(caseId, out var set)) continue;
            var first = Pick(reports, caseId, a, random);
            var second = Pick(reports, caseId, b, random);
            if (first is null || second is null) continue;
            foreach (var issue in set.MaterialIssues)
            {
                var gap = Math.Abs(LexicalOverlap(issue.Description, first) - LexicalOverlap(issue.Description, second));
                if (gap > 0 || everyProblem) eligible.Add((caseId, issue, first, second, gap)); else notEligible++;
            }
        }
        var pending = eligible.Where(e => done.All(d => d.CaseId != e.CaseId || d.IssueId != e.Issue.Id));
        var queue = everyProblem
            ? pending.OrderBy(_ => random.Next()).ToList() // every problem, in random order: no selection bias
            : pending.OrderByDescending(e => e.Gap).ThenBy(e => e.CaseId, StringComparer.Ordinal).ThenBy(e => e.Issue.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, maxPairs - done.Count)).ToList();

        var total = done.Count + queue.Count;
        output.WriteLine("Issue checklist. Each question names one real problem from your approved labels and shows");
        output.WriteLine("two anonymous reports in random order. Answer whether each report mentions that problem.");
        foreach (var item in queue)
        {
            var (x, y) = random.Next(2) == 1 ? (item.Second, item.First) : (item.First, item.Second);
            output.WriteLine();
            output.WriteLine(new string('=', 78));
            output.WriteLine($"Question {done.Count + 1} of {total}: {item.CaseId}");
            output.WriteLine($"PROBLEM: {item.Issue.Description}");
            Show("REPORT 1", x, output);
            Show("REPORT 2", y, output);
            var one = Ask(input, output, "Does REPORT 1 mention this problem? [y/n/q] ", "y", "n", "q");
            if (one is null or "q") break;
            var two = Ask(input, output, "Does REPORT 2 mention this problem? [y/n/q] ", "y", "n", "q");
            if (two is null or "q") break;
            done.Add(new(item.CaseId, item.Issue.Id, x.Comparator, x.Trial, one == "y", y.Comparator, y.Trial, two == "y"));
            File.WriteAllText(checksPath, JsonSerializer.Serialize(
                new IssueCheckFile(ChecklistSchema, Path.GetFullPath(runsDirectory), DateTimeOffset.UtcNow, done), LabSchema.Json));
        }

        int Mentioned(string who) => done.Count(d => (d.FirstComparator == who && d.FirstMentions) || (d.SecondComparator == who && d.SecondMentions));
        output.WriteLine();
        output.WriteLine("=== CHECKLIST REVEAL (fixed denominator: every judged problem counts) ===");
        output.WriteLine($"Problems judged: {done.Count}");
        output.WriteLine($"Mentioned: {a} {Mentioned(a)}/{done.Count}   {b} {Mentioned(b)}/{done.Count}");
        var notJudged = eligible.Count + notEligible - done.Count(d => eligible.Any(e => e.CaseId == d.CaseId && e.Issue.Id == d.IssueId));
        if (everyProblem)
        {
            output.WriteLine($"Coverage: {done.Count} of {eligible.Count} problem(s) in this run judged" +
                (notJudged > 0 ? $"; {notJudged} not judged yet (run again to continue)." : " (complete: every problem, no selection)."));
            output.WriteLine("Note: one judge; the shown report for repeated cases is one random trial.");
        }
        else
        {
            output.WriteLine($"Not judged: {notJudged} problem(s) " +
                $"({notEligible} where the two reports looked alike to a word-match screen, the rest beyond the {maxPairs}-question cap).");
            output.WriteLine("Note: a word-match screen chose which problems to ask about; it scored nothing. Small sample, one judge.");
        }
        return 0;
    }

    // ------------------------------------------------- single-system checklist

    public const string SingleChecklistSchema = "c6c-issue-checklist-single-v1";

    public sealed record SingleCheck(string CaseId, string IssueId, string Comparator, int Trial, bool Mentions);

    public sealed record SingleCheckFile(string SchemaVersion, string RunsDirectory, string Comparator,
        DateTimeOffset StartedAtUtc, IReadOnlyList<SingleCheck> Checks);

    /// <summary>
    /// Shorter checklist when only one system changed: every approved material issue of every
    /// case with a completed report of <paramref name="comparator"/>, in random order, one report
    /// each. NOT blinded (the owner knows which system it is); the summary says so. Resumable.
    /// </summary>
    public static int SingleChecklist(string evalRoot, string runsDirectory, string comparator, string checksPath,
        TextReader input, TextWriter output, int? seed = null)
    {
        var random = seed is { } s ? new Random(s) : new Random(RandomNumberGenerator.GetInt32(int.MaxValue));
        var corpus = CorpusValidator.Validate(evalRoot);
        if (!corpus.Valid) { output.WriteLine("Corpus is invalid; run --validate-corpus."); return 1; }
        var labels = corpus.Cases.Where(c => c.Labels.Status == LabelStatus.OwnerApproved)
            .ToDictionary(c => c.Case.Id, c => c.Labels, StringComparer.Ordinal);
        var view = comparator switch
        {
            "host-cautions" => "host-only",
            "gate-c+host" => "combined",
            _ => "comparator-only"
        };
        // Host-only uses the deterministic baseline's verified caution ledger,
        // not Gate C completion. Combined mode includes the Gate C agent output.
        var sourceComparator = view switch
        {
            "host-only" => Comparator.Baseline,
            "combined" => Comparator.GateC,
            _ => comparator
        };
        if (!Comparator.All.Contains(sourceComparator, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported checklist comparator: " + comparator);
        var reports = Directory.GetFiles(runsDirectory, "*.report.json")
            .Select(f => JsonSerializer.Deserialize<LabReport>(File.ReadAllText(f), LabSchema.Json)!)
            .Where(r => r.Outcome == "completed").ToArray();
        var prior = File.Exists(checksPath)
            ? JsonSerializer.Deserialize<SingleCheckFile>(File.ReadAllText(checksPath), LabSchema.Json)
            : null;
        if (prior is not null &&
            (prior.SchemaVersion != SingleChecklistSchema || prior.Comparator != comparator ||
             prior.RunsDirectory != Path.GetFullPath(runsDirectory)))
            throw new InvalidOperationException(
                "Checklist already belongs to another run or attribution mode; choose another --out.");
        var done = prior?.Checks.ToList() ?? new List<SingleCheck>();
        var problems = new List<(string CaseId, MaterialIssue Issue, LabReport Report)>();
        foreach (var caseId in reports.Select(r => r.CaseId).Distinct().Order(StringComparer.Ordinal))
            if (labels.TryGetValue(caseId, out var set) && Pick(reports, caseId, sourceComparator, random) is { } report)
                problems.AddRange(set.MaterialIssues.Select(issue => (caseId, issue, report)));
        if (view != "comparator-only" && problems.Any(p =>
                p.Report.HostCautionStatus != "verified" || p.Report.HostCautions is null))
            throw new InvalidOperationException(
                "Host cautions have not been verified for every selected report; do not judge unknown as empty.");
        var queue = problems.Where(p => done.All(d => d.CaseId != p.CaseId || d.IssueId != p.Issue.Id))
            .OrderBy(_ => random.Next()).ToList();

        output.WriteLine("Issue checklist (one report per problem). Attribution: " + comparator +
            ". Answer whether the selected source mentions the problem.");
        if (view != "comparator-only")
            output.WriteLine("Host cautions are NOT AI findings. Their detection credit is separate.");
        var total = done.Count + queue.Count;
        foreach (var item in queue)
        {
            output.WriteLine();
            output.WriteLine(new string('=', 78));
            output.WriteLine($"Question {done.Count + 1} of {total}: {item.CaseId}");
            output.WriteLine($"PROBLEM: {item.Issue.Description}");
            if (view == "host-only")
                ShowHost("HOST CAUTIONS (not AI)", item.Report, output);
            else
            {
                Show("REPORT", item.Report, output);
                if (view == "combined")
                    ShowHost("HOST CAUTIONS (not AI)", item.Report, output);
            }
            var answer = Ask(input, output, "Does the shown material mention this problem? [y/n/q] ", "y", "n", "q");
            if (answer is null or "q") break;
            done.Add(new(item.CaseId, item.Issue.Id, comparator, item.Report.Trial, answer == "y"));
            File.WriteAllText(checksPath, JsonSerializer.Serialize(new SingleCheckFile(SingleChecklistSchema,
                Path.GetFullPath(runsDirectory), comparator, DateTimeOffset.UtcNow, done), LabSchema.Json));
        }
        var judged = done.Count(d => problems.Any(p => p.CaseId == d.CaseId && p.Issue.Id == d.IssueId));
        output.WriteLine();
        output.WriteLine("=== CHECKLIST SUMMARY (fixed denominator: every judged problem counts) ===");
        output.WriteLine($"Problems judged: {judged} of {problems.Count}" + (judged < problems.Count ? " (run again to continue)" : " (complete: every problem)"));
        output.WriteLine($"Mentioned: {comparator} {done.Count(d => d.Mentions)}/{judged}");
        output.WriteLine("Note: not blinded (one system shown); one judge; one random trial for repeated cases.");
        return 0;
    }

    // ------------------------------------------------------- blinded judging

    public sealed record Judgment(string CaseId, string LeftComparator, string RightComparator,
        int LeftTrial, int RightTrial, string Preferred, bool LeftActionable, bool RightActionable);

    public sealed record JudgmentFile(string SchemaVersion, string RunsDirectory, DateTimeOffset StartedAtUtc,
        IReadOnlyList<Judgment> Judgments);

    /// <summary>
    /// Pairs one report of comparator A with one of comparator B per case, in
    /// random order, showing claims without comparator names or roles. The
    /// owner picks the more useful report (or tie) and marks each actionable.
    /// Identities are revealed only in the final summary.
    /// </summary>
    public static int Judge(string runsDirectory, string a, string b, string judgmentsPath,
        TextReader input, TextWriter output, int? seed = null)
    {
        var random = seed is { } s ? new Random(s) : new Random(RandomNumberGenerator.GetInt32(int.MaxValue));
        var reports = Directory.GetFiles(runsDirectory, "*.report.json")
            .Select(f => JsonSerializer.Deserialize<LabReport>(File.ReadAllText(f), LabSchema.Json)!)
            .Where(r => r.Outcome == "completed").ToArray();
        var cases = reports.Select(r => r.CaseId).Distinct().Order().ToArray();
        var done = File.Exists(judgmentsPath)
            ? JsonSerializer.Deserialize<JudgmentFile>(File.ReadAllText(judgmentsPath), LabSchema.Json)!.Judgments.ToList()
            : [];
        output.WriteLine("Blinded comparison. Reports are shown as LEFT and RIGHT in random order, without names.");
        output.WriteLine("For each pair: which would better help you understand or safely change this code?");
        foreach (var caseId in cases.Where(c => done.All(j => j.CaseId != c)))
        {
            var left = Pick(reports, caseId, a, random);
            var right = Pick(reports, caseId, b, random);
            if (left is null || right is null) continue;
            if (random.Next(2) == 1) (left, right) = (right, left);
            output.WriteLine();
            output.WriteLine(new string('=', 78));
            output.WriteLine($"Case {done.Count + 1}: {caseId}");
            Show("LEFT", left, output);
            Show("RIGHT", right, output);
            var preferred = Ask(input, output, "More useful? [l]eft / [r]ight / [t]ie / [q]uit ", "l", "r", "t", "q");
            if (preferred is null or "q") break;
            var leftActionable = Ask(input, output, "Is LEFT actionable (you could act on it)? [y/n] ", "y", "n") == "y";
            var rightActionable = Ask(input, output, "Is RIGHT actionable? [y/n] ", "y", "n") == "y";
            done.Add(new(caseId, left.Comparator, right.Comparator, left.Trial, right.Trial,
                preferred switch { "l" => left.Comparator, "r" => right.Comparator, _ => "tie" },
                leftActionable, rightActionable));
            File.WriteAllText(judgmentsPath, JsonSerializer.Serialize(
                new JudgmentFile(JudgmentSchema, Path.GetFullPath(runsDirectory), DateTimeOffset.UtcNow, done), LabSchema.Json));
        }
        Reveal(done, a, b, output);
        return 0;
    }

    private static LabReport? Pick(LabReport[] reports, string caseId, string comparator, Random random)
    {
        var options = reports.Where(r => r.CaseId == caseId && r.Comparator == comparator).OrderBy(r => r.Trial).ToArray();
        return options.Length == 0 ? null : options[random.Next(options.Length)];
    }

    private static void Show(string side, LabReport report, TextWriter output)
    {
        output.WriteLine($"--- {side}");
        var n = 0;
        foreach (var claim in report.Claims.Where(c => c.Kind != "uncertainty"))
            output.WriteLine($"  {++n}. {Neutral(claim.Text)}" + (claim.Citations.Count > 0
                ? $"  [cites {string.Join(", ", claim.Citations.Select(c => $"{c.FilePath}:{c.Line}"))}]" : ""));
        var unknowns = report.Claims.Where(c => c.Kind == "uncertainty").Select(c => c.Text).Distinct().ToArray();
        if (unknowns.Length > 0) output.WriteLine($"  Unknowns: {string.Join(" | ", unknowns.Take(4))}");
    }

    private static void ShowHost(string side, LabReport report, TextWriter output)
    {
        output.WriteLine("--- " + side);
        if (report.HostCautionStatus != "verified" || report.HostCautions is null)
        {
            output.WriteLine("  Host caution inspection was not verified; do not count an empty list as no issues.");
            return;
        }
        if (report.HostCautions.Count == 0)
            output.WriteLine("  No host-written cautions for this selected entry point.");
        var number = 0;
        foreach (var claim in report.HostCautions)
            output.WriteLine($"  {++number}. {claim.Text}" +
                (claim.Citations.Count == 0 ? "" :
                    $"  [cites {string.Join(", ", claim.Citations.Select(c => c.FilePath +
                        (c.Line is null ? "" : ":" + c.Line)))}]"));
    }

    private static readonly System.Text.RegularExpressions.Regex IdentityPrefix = new(
        @"^(source-observation|candidate-relationship|H\d+ (PlausibleHypothesis|ContestedOrInsufficient|StructurallySupported|Rejected)|InsufficientEvidence|UnsupportedInference|CompetingHypothesis|CoverageLimitation|StructuralContradiction): ",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Removes comparator-revealing claim prefixes (blinding is still only partial).</summary>
    public static string Neutral(string text) => IdentityPrefix.Replace(text, "");

    /// <summary>
    /// Changes only the status and provenance values in the original text, so the
    /// label content, line endings and escaping stay byte-identical. Refuses unless
    /// each value occurs exactly once and the result parses to exactly the signed labels.
    /// </summary>
    public static bool TrySign(string original, LabLabels signed, out string text)
    {
        text = original;
        foreach (var (from, to) in new[] {
            ("\"status\": \"draft\"", "\"status\": \"owner-approved\""),
            ("\"authoredBy\": \"assistant-draft\"", "\"authoredBy\": \"assistant-draft-owner-reviewed\"") })
        {
            var at = text.IndexOf(from, StringComparison.Ordinal);
            if (at < 0 || text.IndexOf(from, at + 1, StringComparison.Ordinal) >= 0) return false;
            text = text[..at] + to + text[(at + from.Length)..];
        }
        var reparsed = JsonSerializer.Deserialize<LabLabels>(text, LabSchema.Json);
        return reparsed is not null &&
               JsonSerializer.Serialize(reparsed, LabSchema.Json) == JsonSerializer.Serialize(signed, LabSchema.Json);
    }

    private static string? Ask(TextReader input, TextWriter output, string prompt, params string[] allowed)
    {
        while (true)
        {
            output.Write(prompt);
            var answer = input.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is null) return null;
            if (allowed.Contains(answer)) return answer;
        }
    }

    public static void Reveal(IReadOnlyList<Judgment> judgments, string a, string b, TextWriter output)
    {
        int Count(string who) => judgments.Count(j => j.Preferred == who);
        double Actionable(string who)
        {
            var shown = judgments.Select(j => j.LeftComparator == who ? j.LeftActionable : j.RightActionable).ToArray();
            return shown.Length == 0 ? 0 : (double)shown.Count(x => x) / shown.Length;
        }
        output.WriteLine();
        output.WriteLine("=== REVEAL (fixed denominator: every judged pair counts) ===");
        output.WriteLine($"Pairs judged: {judgments.Count}");
        output.WriteLine($"{a} preferred: {Count(a)}   {b} preferred: {Count(b)}   ties: {Count("tie")}");
        output.WriteLine($"Actionable: {a} {Actionable(a):P0}   {b} {Actionable(b):P0}");
        output.WriteLine("Note: blinding is partial: the two systems' reports differ in structure and length.");
    }
}

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
                File.WriteAllText(Path.Combine(loaded.Directory, "labels.json"),
                    JsonSerializer.Serialize(signed, LabSchema.Json) + "\n");
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

    private static readonly System.Text.RegularExpressions.Regex IdentityPrefix = new(
        @"^(source-observation|candidate-relationship|H\d+ (PlausibleHypothesis|ContestedOrInsufficient|StructurallySupported|Rejected)|InsufficientEvidence|UnsupportedInference|CompetingHypothesis|CoverageLimitation|StructuralContradiction): ",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Removes comparator-revealing claim prefixes (blinding is still only partial).</summary>
    public static string Neutral(string text) => IdentityPrefix.Replace(text, "");

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

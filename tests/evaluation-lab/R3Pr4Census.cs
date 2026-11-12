using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeArcheologist.EvaluationLab;

/// <summary>Fixed universe of report slots for R3 PR4. An incomplete model
/// result occupies its slot and counts against completion; a missing/corrupt
/// artifact makes the STUDY invalid. A provisional debug run cannot be sealed.
/// No source repository or external network access is needed to audit outputs.
/// </summary>
public static class R3Pr4Census
{
    public const string SealName = "pr4-complete-seal.json";
    public const string SealVersion = "r3-pr4-complete-census-v1";

    public sealed record Slot(string CaseId, string Comparator, int Trial)
    {
        public string Stem => CaseId + "." + Comparator + ".t" + Trial;
    }

    public sealed record Artifact(string File, string Sha256);
    public sealed record Seal(string SchemaVersion, string ProtocolSha256,
        IReadOnlyList<Artifact> Artifacts);
    public sealed record Result(bool Valid, IReadOnlyList<string> Errors,
        IReadOnlyList<LabReport> Reports, IReadOnlyList<CaseScore> Scores,
        IReadOnlyList<R3Pr4Activity> Activity, string? ProtocolSha256)
    {
        public LabReport? Primary(string caseId, string comparator) =>
            Reports.SingleOrDefault(r => r.CaseId == caseId &&
                r.Comparator == comparator && r.Trial == 1);
    }

    public static IReadOnlyList<Slot> Expected(CorpusReport corpus) =>
        corpus.Cases.Where(c => c.Case.Expectation == CaseExpectation.Investigate)
            .OrderBy(c => c.Case.Id, StringComparer.Ordinal)
            .SelectMany(c => R3Pr4Protocol.Comparators.SelectMany(comparator =>
                Enumerable.Range(1, c.Case.DiagnosticRepeat &&
                    (comparator is Comparator.GateC or Comparator.Adaptive)
                    ? 3 : 1).Select(trial => new Slot(c.Case.Id, comparator, trial))))
            .ToArray();

    public static Result Audit(string evalRoot, string runs, bool requireSeal = true)
    {
        var problems = new List<string>();
        var reports = new List<LabReport>();
        var scores = new List<CaseScore>();
        var activity = new List<R3Pr4Activity>();
        string? protocolSha = null;
        try
        {
            var corpus = CorpusValidator.Validate(evalRoot);
            var protocol = R3Pr4Protocol.ReadAndValidate(evalRoot, corpus, out var protocolErrors);
            if (protocol is null) problems.AddRange(protocolErrors);
            if (!Directory.Exists(runs))
                return new(false, ["Run directory does not exist."], [], [], [], null);
            var protocolFile = Path.Combine(evalRoot, R3Pr4Protocol.FileName);
            protocolSha = LabSchema.Sha256(File.ReadAllBytes(protocolFile));
            var metadata = Read<JsonElement>(Path.Combine(runs, "pr4-run-metadata.json"));
            if (metadata.GetProperty("schemaVersion").GetString() != R3Pr4Protocol.Version ||
                metadata.GetProperty("baselineCommit").GetString() != R3Pr4Protocol.BaselineCommit ||
                metadata.GetProperty("protocolSha256").GetString() != protocolSha ||
                metadata.GetProperty("provisional").GetBoolean() ||
                !Strings(metadata.GetProperty("comparators")).SequenceEqual(
                    R3Pr4Protocol.Comparators, StringComparer.Ordinal) ||
                !Strings(metadata.GetProperty("selectedCases")).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(corpus.Cases.Select(c => c.Case.Id)) ||
                metadata.GetProperty("selectedCases").GetArrayLength() != corpus.Cases.Count)
                problems.Add("Run is provisional, incomplete, or bound to another protocol/case census.");

            var summary = Read<JsonElement>(Path.Combine(runs, "summary.json"));
            if (summary.GetProperty("schemaVersion").GetString() != "r3-pr4-lab-summary-v1" ||
                summary.GetProperty("provisional").GetBoolean() ||
                summary.GetProperty("draftLabels").GetInt32() != 0 ||
                summary.GetProperty("thresholdStatus").GetString() != "owner-approved-for-scoring" ||
                summary.GetProperty("failures").GetArrayLength() != 0 ||
                summary.GetProperty("notAcquired").GetArrayLength() != 0 ||
                summary.GetProperty("configSha256").GetString() != LabSchema.Sha256(
                    File.ReadAllBytes(Path.Combine(evalRoot, "lab-config.json"))))
                problems.Add("Scored run summary contains a failure, missing acquisition or mismatched configuration.");

            var expected = Expected(corpus);
            if (expected.Count != 232) problems.Add("Expected trial census is no longer 232.");
            var expectedFiles = new HashSet<string>(StringComparer.Ordinal)
                { "summary.json", "pr4-run-metadata.json" };
            foreach (var slot in expected)
            {
                var stem = slot.Stem;
                var reportName = stem + ".report.json";
                var scoreName = stem + ".score.json";
                expectedFiles.Add(reportName);
                expectedFiles.Add(scoreName);
                var sidecar = slot.Comparator is Comparator.GateC or Comparator.Adaptive;
                if (sidecar) expectedFiles.Add(stem + ".pr4-activity.json");
                if (!File.Exists(Path.Combine(runs, reportName)) ||
                    !File.Exists(Path.Combine(runs, scoreName)) ||
                    sidecar && !File.Exists(Path.Combine(runs, stem + ".pr4-activity.json")))
                {
                    problems.Add("Missing expected trial artifact: " + stem);
                    continue;
                }
                var report = Read<LabReport>(Path.Combine(runs, reportName));
                var score = Read<CaseScore>(Path.Combine(runs, scoreName));
                reports.Add(report);
                scores.Add(score);
                if (report.SchemaVersion != LabSchema.Report ||
                    score.SchemaVersion != LabSchema.Score ||
                    report.CaseId != slot.CaseId || report.Comparator != slot.Comparator ||
                    report.Trial != slot.Trial || score.CaseId != slot.CaseId ||
                    score.Comparator != slot.Comparator || score.Trial != slot.Trial ||
                    score.Outcome != report.Outcome ||
                    score.Completed != (report.Outcome == "completed") ||
                    !score.LabelsOwnerApproved ||
                    score.IntegrityBreaches.Count > 0 ||
                    score.HostCautionsVerified != (report.HostCautionStatus == "verified") ||
                    score.ComparatorClaimCount != report.Claims.Count ||
                    (slot.Comparator is Comparator.GateC or Comparator.Adaptive) &&
                    report.Outcome == "completed" &&
                    (report.GateC is null || report.GateC.CheckpointCount != 3 ||
                     !report.GateC.ReconciliationAccepted ||
                     report.GateC.RoleStatuses.Any(x => x != "CompletedValidated")))
                    problems.Add("Trial report/score disagreement or integrity breach: " + stem);
                if (!sidecar) continue;
                var use = Read<R3Pr4Activity>(Path.Combine(runs, stem + ".pr4-activity.json"));
                activity.Add(use);
                if (use.SchemaVersion != R3Pr4Activity.Version ||
                    use.CaseId != slot.CaseId || use.Comparator != slot.Comparator ||
                    use.Trial != slot.Trial || use.RunId == Guid.Empty ||
                    use.AttemptId == Guid.Empty || use.DurationMs < 0 ||
                    !use.CheckpointReplayAccepted ||
                    use.ModelCalls is null || use.ModelCalls < 0 ||
                    use.ModelCallFailures is null || use.ModelCallFailures < 0 ||
                    use.ModelCallFailures > use.ModelCalls ||
                    use.ModelCallAccountingBasis != "opt-in-MAF-request-attempts" ||
                    (report.Outcome == "completed" && use.AttemptStatus != "completed") ||
                    (slot.Comparator == Comparator.Adaptive &&
                     (use.ReceiptReplayAccepted != true ||
                      use.CommittedEvidenceRequests != use.EvidenceReceipts)))
                    problems.Add("Missing/incompatible host-observed activity: " + stem);
            }

            if (activity.Select(a => a.RunId).Distinct().Count() != activity.Count ||
                activity.Select(a => a.AttemptId).Distinct().Count() != activity.Count)
                problems.Add("Investigation run/attempt identifiers repeated across trial slots.");
            var actualFiles = Directory.GetFiles(runs, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).ToArray();
            foreach (var actual in actualFiles)
                if (actual != SealName && actual is not null &&
                    !expectedFiles.Contains(actual))
                    problems.Add("Unexpected output artifact: " + actual);
            if (reports.Count != 232 || scores.Count != 232 || activity.Count != 140)
                problems.Add("Study does not contain 232 reports/scores and 140 audited role-run sidecars.");

            // The four negative expectations AND 15 change-experiment
            // expectations must be present and confirmed. Never drop them.
            var requiredChecks = corpus.Cases.Where(c =>
                c.Case.Expectation != CaseExpectation.Investigate ||
                c.Case.Change is not null).Select(c => c.Case.Id)
                .ToHashSet(StringComparer.Ordinal);
            var checks = summary.GetProperty("expectations").EnumerateArray().ToArray();
            if (checks.Length != requiredChecks.Count ||
                !checks.Select(x => x.GetProperty("caseId").GetString() ?? "")
                    .ToHashSet(StringComparer.Ordinal).SetEquals(requiredChecks) ||
                checks.Any(c => !c.GetProperty("confirmed").GetBoolean()))
                problems.Add("Negative/change expectations are missing, duplicated or not confirmed.");

            var summaries = LabScoring.Summarize(scores);
            var recorded = summary.GetProperty("comparators");
            if (recorded.GetArrayLength() != summaries.Count ||
                !EquivalentSummary(recorded, summaries))
                problems.Add("Stored summary disagrees with every trial score.");
            if (requireSeal)
            {
                var seal = Read<Seal>(Path.Combine(runs, SealName));
                var actual = Fingerprints(runs, expectedFiles);
                if (seal.SchemaVersion != SealVersion ||
                    seal.ProtocolSha256 != protocolSha ||
                    !seal.Artifacts.SequenceEqual(actual))
                    problems.Add("Run seal does not match the complete artifact set.");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            problems.Add("Fail-closed census read: " + error.GetType().Name);
        }
        return new(problems.Count == 0, problems, reports, scores, activity, protocolSha);
    }

    public static void SealCompleteRun(string evalRoot, string runs)
    {
        var audit = Audit(evalRoot, runs, requireSeal: false);
        if (!audit.Valid) throw new InvalidOperationException("PR4 census cannot be sealed: " +
            string.Join("; ", audit.Errors.Take(8)));
        var list = Directory.GetFiles(runs, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).Where(x => x is not null && x != SealName)
            .Select(x => x!).ToHashSet(StringComparer.Ordinal);
        var seal = new Seal(SealVersion, audit.ProtocolSha256!, Fingerprints(runs, list));
        var path = Path.Combine(runs, SealName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, seal, LabSchema.Json);
    }

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), LabSchema.Json)
            ?? throw new InvalidOperationException("Missing/invalid JSON " + Path.GetFileName(path));

    private static string[] Strings(JsonElement value) =>
        value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();

    private static Artifact[] Fingerprints(string runs, IEnumerable<string> files) =>
        files.OrderBy(x => x, StringComparer.Ordinal).Select(x =>
            new Artifact(x, LabSchema.Sha256(File.ReadAllBytes(Path.Combine(runs, x))))).ToArray();

    private static bool EquivalentSummary(JsonElement saved,
        IReadOnlyList<LabScoring.ComparatorSummary> expected)
    {
        var actual = saved.EnumerateArray().ToArray();
        for (var i = 0; i < actual.Length; i++)
        {
            var original = JsonSerializer.Deserialize<LabScoring.ComparatorSummary>(
                actual[i].GetRawText(), LabSchema.Json);
            if (original is null ||
                JsonSerializer.Serialize(original, LabSchema.Json) !=
                JsonSerializer.Serialize(expected[i], LabSchema.Json))
                return false;
        }
        return true;
    }
}

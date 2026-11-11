using System.Text.RegularExpressions;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.EvaluationLab;

/// <summary>Hard release blockers from Gate C v1 (zero accepted).</summary>
public static class IntegrityBreach
{
    public const string ScopeMismatch = "incorrect-snapshot-scope";
    public const string ForeignCitation = "fabricated-or-foreign-citation";
    public const string FactPromotion = "unsupported-claim-promoted-to-verified-fact";
    public const string DiscardedObjection = "silently-discarded-material-objection";
    public const string FalseCompletion = "failed-workflow-presented-as-completed";
    public const string SchemaRegression = "schema-regression";
    public const string HostCautionMismatch = "host-caution-provenance-mismatch";
}

public sealed record CaseScore(string SchemaVersion, string CaseId, string Comparator, int Trial,
    string Outcome, bool Completed, IReadOnlyList<string> IntegrityBreaches,
    int RequiredObservations, int CitedObservationAnchors, double? ObjectionCoverage,
    IReadOnlyList<string> ReviewFlags, IReadOnlyList<string> PendingJudgment, bool LabelsOwnerApproved,
    int HostCautionCount = 0, int ComparatorClaimCount = 0, bool HostCautionsVerified = false);

/// <summary>
/// Deterministic scoring only. Judgment-dependent metrics (material issue
/// detection, actionable usefulness) are reported as pending, never guessed.
/// The lexical screen produces review flags for a human, not a score.
/// </summary>
public static partial class LabScoring
{
    [GeneratedRegex(@"\b(verified at runtime|is (?:always )?called at runtime|runtime[- ]verified|proven|guaranteed|requires no authori[sz]ation|fully tested|is registered|will (?:always )?be (?:called|invoked|executed)|resolves? to (?:the )?implementation)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeAssertion();

    public static CaseScore Score(LabReport report, CapturedCase captured)
    {
        var labels = captured.Loaded.Labels;
        var breaches = new List<string>();
        if (report.SchemaVersion != LabSchema.Report) breaches.Add(IntegrityBreach.SchemaRegression);
        if (report.SnapshotId != captured.Snapshot.Id || report.FingerprintSha256 != captured.Snapshot.FingerprintSha256 ||
            report.EntryPointId != captured.Scope.EntryPointId || report.CaseId != captured.Loaded.Case.Id)
            breaches.Add(IntegrityBreach.ScopeMismatch);

        // Every citation must be an exact item of the freshly verified package.
        var citable = LabHarness.Citable(captured.Package).ToList();
        // Compare independently against the exact source-derived host ledger.
        // A forged/deleted caution is an integrity breach, not a false agent
        // citation or an agent-detection success.
        var expectedHost = LabHarness.HostClaims(captured.Package);
        var hostVerified = report.HostCautionStatus == "verified" &&
            report.HostCautionAnalyzerVersion == CSharpHostCautionAnalyzer.Version &&
            report.HostCautions is not null &&
            System.Text.Json.JsonSerializer.Serialize(report.HostCautions) ==
                System.Text.Json.JsonSerializer.Serialize(expectedHost);
        if (!hostVerified ||
            report.Claims.Any(c => c.Kind == "host-caution" || c.Role == "host"))
            breaches.Add(IntegrityBreach.HostCautionMismatch);
        var citations = report.Claims.SelectMany(c => c.Citations).ToArray();
        if (citations.Any(c => !citable.Contains(c)))
            breaches.Add(IntegrityBreach.ForeignCitation);

        double? coverage = null;
        if (report.Comparator == Comparator.GateC)
        {
            var facts = report.GateC;
            var completed = report.Outcome == "completed";
            if (facts is not null)
            {
                if (facts.AnyVerifiedFact || facts.AnyStructurallySupported)
                    breaches.Add(IntegrityBreach.FactPromotion);
                if (facts.ConcernIds.Count > 0 && facts.RoleStatuses[2] == nameof(GateCRoleStatus.CompletedValidated))
                {
                    var addressed = facts.ConcernIds.Count(id => facts.AddressedConcernIds.Contains(id));
                    coverage = (double)addressed / facts.ConcernIds.Count;
                    if (addressed != facts.ConcernIds.Count || facts.AddressedConcernIds.Count != facts.ConcernIds.Count)
                        breaches.Add(IntegrityBreach.DiscardedObjection);
                }
                else if (facts.RoleStatuses[2] == nameof(GateCRoleStatus.CompletedValidated)) coverage = 1.0;
                if (completed && (facts.RoleStatuses.Any(s => s != nameof(GateCRoleStatus.CompletedValidated)) ||
                                  !facts.ReconciliationAccepted || facts.CheckpointCount != 3))
                    breaches.Add(IntegrityBreach.FalseCompletion);
            }
            else if (completed) breaches.Add(IntegrityBreach.FalseCompletion);
        }

        var cited = labels.RequiredObservations.Count(o =>
            citations.Any(c => c.FilePath == o.Anchor.FilePath && c.Line == o.Anchor.Line));
        var flags = report.Claims
            .Where(c => c.Kind is not "uncertainty" && RuntimeAssertion().IsMatch(c.Text))
            .Select(c => "runtime-or-completeness-assertion-in-" + c.Kind + "-for-human-review")
            .Distinct(StringComparer.Ordinal).ToArray();
        return new(LabSchema.Score, report.CaseId, report.Comparator, report.Trial, report.Outcome,
            report.Outcome == "completed", breaches.Distinct(StringComparer.Ordinal).ToArray(),
            labels.RequiredObservations.Count, cited, coverage, flags,
            ["material-issue-detection", "actionable-usefulness",
             "host-only-material-issue-detection", "agent-only-material-issue-detection"],
            labels.Status == LabelStatus.OwnerApproved,
            report.HostCautions?.Count ?? 0, report.Claims.Count, hostVerified);
    }

    public sealed record ComparatorSummary(string Comparator, int Evaluations, int Completed,
        int IntegrityBreaches, double ObservationAnchorRecall, double? MeanObjectionCoverage, int ReviewFlags,
        int HostCautions, int ComparatorClaims, int HostVerifiedReports);

    /// <summary>Aggregates with fixed denominators: every evaluation counts, completed or not.</summary>
    public static IReadOnlyList<ComparatorSummary> Summarize(IReadOnlyList<CaseScore> scores) =>
        scores.GroupBy(s => s.Comparator).OrderBy(g => Array.IndexOf(Comparator.All, g.Key)).Select(g =>
        {
            var required = g.Sum(s => s.RequiredObservations);
            var coverage = g.Where(s => s.ObjectionCoverage is not null).Select(s => s.ObjectionCoverage!.Value).ToArray();
            return new ComparatorSummary(g.Key, g.Count(), g.Count(s => s.Completed),
                g.Sum(s => s.IntegrityBreaches.Count),
                required == 0 ? 0 : (double)g.Sum(s => s.CitedObservationAnchors) / required,
                coverage.Length == 0 ? null : coverage.Average(), g.Sum(s => s.ReviewFlags.Count),
                g.Sum(s => s.HostCautionCount), g.Sum(s => s.ComparatorClaimCount),
                g.Count(s => s.HostCautionsVerified));
        }).ToArray();
}

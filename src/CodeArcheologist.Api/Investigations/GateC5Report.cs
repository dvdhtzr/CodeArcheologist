using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Investigations;

/// <summary>Host-owned reporting data, never accepted as model-authored authority.</summary>
public static class GateC5ReportContract
{
    public const string Version = "gate-c5-investigation-report-v2";
}
public sealed record GateC5EvidenceLink(
    GateCRole Role, string Handle, string CanonicalId,
    GateCEvidenceKind Kind, string SourceKind, string EvidenceStatus,
    string? FilePath, string? FileSha256, int? Line, int? Column);

public sealed record GateC5Report(
    string Version, Guid RunId, GateCScope Scope,
    string InvestigatorModelId, string SkepticModelId, string ReviewerModelId,
    IReadOnlyList<string> GraphAnalyzerVersions,
    GateCRunProgress Progress, DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    GateC3RoleOutcome Investigator, GateC3RoleOutcome Skeptic,
    GateC4ReviewerOutcome Reviewer,
    IReadOnlyList<GateC5EvidenceLink> EvidenceLinks,
    string Diagnostic,
    string HostCautionStatus = "not-evaluated",
    string HostCautionAnalyzerVersion = CSharpHostCautionAnalyzer.Version,
    IReadOnlyList<HostSyntaxCaution>? HostCautions = null);

public static class GateC5ReportFactory
{
    /// <summary>
    /// The worker attaches warnings only from freshly verified snapshot tools,
    /// after the AI workflow has finished. This never enters the role prompts,
    /// checkpoint ledgers, or reviewer reconciliation.
    /// </summary>
    internal static GateC5Report AttachVerifiedCautions(
        GateC5Report report, AgentEvidencePackage evidence)
    {
        var scope = report.Scope;
        if (report.Version != GateC5ReportContract.Version ||
            report.HostCautionStatus != "not-evaluated" ||
            report.HostCautions is { Count: > 0 } ||
            evidence.ContractVersion != scope.EvidenceContractVersion ||
            evidence.WorkspaceId != scope.WorkspaceId ||
            evidence.RepositoryId != scope.RepositoryId ||
            evidence.SnapshotId != scope.SnapshotId ||
            evidence.FingerprintSha256 != scope.FingerprintSha256 ||
            evidence.EntryPointId != scope.EntryPointId ||
            evidence.HostCautionAnalyzerVersion != CSharpHostCautionAnalyzer.Version ||
            evidence.HostCautions is not { Count: <= 5 } cautions)
            throw new InvalidOperationException("Host caution source or scope is not verified.");

        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "unobserved-registration", "route-group-receiver",
            "sibling-modifier-difference", "conditional-invocation",
            "multiple-project-candidates"
        };
        foreach (var caution in cautions)
        {
            if (!allowed.Contains(caution.Kind) ||
                !kinds.Add(caution.Kind) || !ids.Add(caution.Id) ||
                caution.Id != "host-caution:" + caution.Kind + ":" + scope.EntryPointId ||
                caution.AnalyzerVersion != CSharpHostCautionAnalyzer.Version ||
                caution.Status != CSharpHostCautionAnalyzer.Status ||
                string.IsNullOrWhiteSpace(caution.Statement) ||
                caution.Statement.Length > 1000 ||
                caution.Anchors is not { Count: >= 1 and <= 6 } ||
                !caution.Anchors.Any(a => !string.IsNullOrWhiteSpace(a.FilePath) &&
                    a.Line is >= 1 && a.SpanStart is >= 0) ||
                caution.Anchors.Any(a => string.IsNullOrWhiteSpace(a.FilePath) ||
                    a.FileSha256.Length != 64 ||
                    a.FileSha256.Any(c => !Uri.IsHexDigit(c)) ||
                    (a.Line is null) != (a.Column is null) ||
                    (a.SpanStart is null) != (a.SpanLength is null) ||
                    a.Line is < 1 || a.Column is < 1 ||
                    a.SpanStart is < 0 || a.SpanLength is < 1))
                throw new InvalidOperationException("Invalid host caution provenance.");
        }
        return report with
        {
            HostCautionStatus = "verified",
            HostCautionAnalyzerVersion = CSharpHostCautionAnalyzer.Version,
            HostCautions = cautions.ToArray()
        };
    }

    public static GateC5Report Create(GateC4RunOutcome outcome,
        DateTimeOffset started, DateTimeOffset finished)
    {
        var independent = outcome.Independent;
        if (independent.RunId == Guid.Empty ||
            independent.RunId != outcome.Progress.RunId ||
            independent.Scope.SnapshotId == Guid.Empty ||
            independent.Scope.WorkspaceId == Guid.Empty ||
            independent.Scope.RepositoryId == Guid.Empty)
            throw new InvalidOperationException("C5 report identities are inconsistent.");

        var investigator = independent.Investigator;
        var skeptic = independent.Skeptic;
        var links = new List<GateC5EvidenceLink>();
        foreach (var role in new[] { investigator, skeptic })
        {
            if (role.Status != GateCRoleStatus.CompletedValidated)
            {
                if (role.Assessment is not null || role.EvidenceLinks is { Count: > 0 })
                    throw new InvalidOperationException("Unvalidated role has report evidence.");
                continue;
            }
            if (role.Assessment is null || role.EvidenceLinks is null ||
                role.Assessment.Role != role.Role ||
                role.Assessment.RunId != independent.RunId ||
                role.Assessment.Scope != independent.Scope)
                throw new InvalidOperationException("Missing validated role evidence.");

            var catalog = role.EvidenceLinks.ToDictionary(
                x => x.Handle, StringComparer.Ordinal);
            if (catalog.Count != role.EvidenceLinks.Count ||
                role.EvidenceLinks.Any(x => x.Role != role.Role ||
                    string.IsNullOrWhiteSpace(x.CanonicalId) ||
                    string.IsNullOrWhiteSpace(x.EvidenceStatus)))
                throw new InvalidOperationException("Invalid role-local evidence ledger.");

            var cited = role.Assessment.Hypotheses.SelectMany(x => x.EvidenceHandles)
                .Concat(role.Assessment.Concerns.SelectMany(x => x.EvidenceHandles))
                .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal);
            if (cited.Any(x => !catalog.ContainsKey(x)))
                throw new InvalidOperationException("Assessment cites missing evidence.");
            if (string.IsNullOrWhiteSpace(role.GraphAnalyzerVersion))
                throw new InvalidOperationException("Validated role lacks analyzer provenance.");
            links.AddRange(role.EvidenceLinks);
        }

        if (outcome.Progress.Status == GateCRunStatus.Completed)
        {
            if (investigator.Status != GateCRoleStatus.CompletedValidated ||
                skeptic.Status != GateCRoleStatus.CompletedValidated ||
                outcome.Reviewer.Status != GateCRoleStatus.CompletedValidated ||
                outcome.Reviewer.Assessment is null ||
                outcome.Reviewer.Reconciliation?.Check.Accepted != true ||
                outcome.Reviewer.Reconciliation.Dispositions.Any(x => x.IsVerifiedFact))
                throw new InvalidOperationException("Invalid completed investigation.");

            var checkedReview = GateCContractValidator.ValidateReview(
                outcome.Reviewer.Assessment, independent.RunId, independent.Scope,
                investigator.Assessment!, skeptic.Assessment!);
            if (!checkedReview.Check.Accepted)
                throw new InvalidOperationException("Invalid reviewer concern reconciliation.");
        }
        else if (outcome.Progress.Status is not (GateCRunStatus.Incomplete or
                     GateCRunStatus.Cancelled or GateCRunStatus.Rejected))
            throw new InvalidOperationException("A persisted report must be terminal.");

        return new(GateC5ReportContract.Version, independent.RunId,
            independent.Scope, GateC3IndependentRunner.ModelId,
            GateC3IndependentRunner.ModelId, GateC4ReconciliationRunner.ModelId,
            new[] { investigator.GraphAnalyzerVersion, skeptic.GraphAnalyzerVersion }
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!)
                .Distinct(StringComparer.Ordinal).ToArray(),
            outcome.Progress, started, finished,
            investigator, skeptic, outcome.Reviewer, links,
            outcome.Progress.Status == GateCRunStatus.Completed
                ? "orchestration-completed-not-semantic-proof"
                : "investigation-incomplete");
    }
}

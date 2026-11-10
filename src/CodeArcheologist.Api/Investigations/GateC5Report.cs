using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Investigations;

/// <summary>Host-owned reporting data, never accepted as model-authored authority.</summary>
public static class GateC5ReportContract
{
    public const string Version = "gate-c5-investigation-report-v1";
}
public sealed record GateC5EvidenceLink(
    GateCRole Role, string Handle, string CanonicalId,
    GateCEvidenceKind Kind, string SourceKind, string EvidenceStatus,
    string? FilePath, string? FileSha256, int? Line, int? Column);

public sealed record GateC5Report(
    string Version, Guid RunId, GateCScope Scope,
    GateCRunProgress Progress, DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    GateC3RoleOutcome Investigator, GateC3RoleOutcome Skeptic,
    GateC4ReviewerOutcome Reviewer,
    IReadOnlyList<GateC5EvidenceLink> EvidenceLinks,
    string Diagnostic);

public static class GateC5ReportFactory
{
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
            independent.Scope, outcome.Progress, started, finished,
            investigator, skeptic, outcome.Reviewer, links,
            outcome.Progress.Status == GateCRunStatus.Completed
                ? "orchestration-completed-not-semantic-proof"
                : "investigation-incomplete");
    }
}

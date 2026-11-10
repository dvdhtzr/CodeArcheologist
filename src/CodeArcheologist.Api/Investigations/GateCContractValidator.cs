namespace CodeArcheologist.Api.Investigations;

/// <summary>
/// Pure deterministic C1 contract checks. The caller must obtain the
/// authoritative source package through the existing verified snapshot tools.
/// These checks never infer that a model's prose is true.
/// </summary>
public static class GateCContractValidator
{
    public static GateCContractCheck ValidateIndependent(
        GateCIndependentAssessment? assessment, Guid runId, GateCScope scope,
        IReadOnlyList<GateCEvidenceHandle> allowedHandles)
    {
        if (assessment is null || assessment.ContractVersion != GateCContract.Version ||
            assessment.RunId != runId || runId == Guid.Empty ||
            assessment.Scope != scope || !ValidScope(scope))
            return GateCContractCheck.Fail("stale-or-foreign-contract-scope");
        if (assessment.Role is not (GateCRole.Investigator or GateCRole.Skeptic))
            return GateCContractCheck.Fail("invalid-independent-role");
        if (!ValidCatalog(allowedHandles))
            return GateCContractCheck.Fail("invalid-host-evidence-catalog");
        if (assessment.SelectedEvidenceHandles is null ||
            assessment.SelectedEvidenceHandles.Count > GateCContract.MaxSelections ||
            !ValidHandles(assessment.SelectedEvidenceHandles, allowedHandles))
            return GateCContractCheck.Fail("invalid-or-foreign-selected-evidence");
        if (assessment.Hypotheses is null ||
            assessment.Hypotheses.Count > GateCContract.MaxHypotheses ||
            !DistinctIds(assessment.Hypotheses.Select(h => h?.Id)) ||
            assessment.Hypotheses.Any(h =>
                h is null || !ValidText(h.Text) || !ValidText(h.Uncertainty) ||
                h.EvidenceHandles is null ||
                !ValidHandles(h.EvidenceHandles, allowedHandles)))
            return GateCContractCheck.Fail("invalid-hypotheses");
        if (assessment.Concerns is null ||
            assessment.Concerns.Count > GateCContract.MaxConcerns ||
            !DistinctIds(assessment.Concerns.Select(c => c?.Id)) ||
            assessment.Concerns.Any(c =>
                c is null || !Enum.IsDefined(c.Kind) ||
                !ValidText(c.Explanation) || c.EvidenceHandles is null ||
                !ValidHandles(c.EvidenceHandles, allowedHandles)))
            return GateCContractCheck.Fail("invalid-independent-concerns");
        // A concern with no citations is allowed only as a declared evidence
        // gap/unsupported inference, not as a proven structural contradiction.
        if (assessment.Concerns.Any(c =>
                c.Kind == GateCConcernKind.StructuralContradiction &&
                c.EvidenceHandles.Count == 0))
            return GateCContractCheck.Fail("uncited-structural-contradiction");
        if (assessment.Unknowns is null || assessment.Unknowns.Count > 16 ||
            assessment.Unknowns.Any(x => !ValidText(x)))
            return GateCContractCheck.Fail("invalid-unknowns");
        return GateCContractCheck.Pass();
    }

    public static GateCReconciliationResult ValidateReview(
        GateCReviewerAssessment? review, Guid runId, GateCScope scope,
        GateCIndependentAssessment investigator, GateCIndependentAssessment skeptic)
    {
        GateCReconciliationResult Fail(string diagnostic) =>
            new(GateCContractCheck.Fail(diagnostic), []);
        if (review is null || review.ContractVersion != GateCContract.Version ||
            review.Role != GateCRole.Reviewer || review.RunId != runId ||
            review.Scope != scope || !ValidScope(scope) ||
            investigator.Role != GateCRole.Investigator ||
            skeptic.Role != GateCRole.Skeptic ||
            investigator.RunId != runId || skeptic.RunId != runId ||
            investigator.Scope != scope || skeptic.Scope != scope ||
            investigator.ContractVersion != GateCContract.Version ||
            skeptic.ContractVersion != GateCContract.Version)
            return Fail("review-identity-or-input-invalid");
        if (review.Decisions is null || review.UnresolvedGlobalConcernIds is null ||
            review.Decisions.Count > GateCContract.MaxHypotheses ||
            review.UnresolvedGlobalConcernIds.Count > GateCContract.MaxConcerns)
            return Fail("review-budget-exceeded");

        var hypothesisIds = investigator.Hypotheses.Select(x => x.Id).ToArray();
        var concernIds = skeptic.Concerns.Select(x => x.Id).ToArray();
        if (!DistinctIds(hypothesisIds) || !DistinctIds(concernIds) ||
            review.Decisions.Count != hypothesisIds.Length ||
            !DistinctIds(review.Decisions.Select(d => d?.HypothesisId)) ||
            review.Decisions.Any(d => d is null ||
                !hypothesisIds.Contains(d.HypothesisId, StringComparer.Ordinal) ||
                !Enum.IsDefined(d.Recommendation) || !ValidText(d.Explanation) ||
                d.AddressedConcernIds is null ||
                d.AddressedConcernIds.Count > GateCContract.MaxConcerns ||
                !DistinctIds(d.AddressedConcernIds)))
            return Fail("review-decisions-invalid");

        // A model cannot classify an interpretive hypothesis as proven
        // structural fact. Structural observations are authored by the host.
        if (review.Decisions.Any(d =>
                d.Recommendation == GateCRecommendation.StructurallySupported))
            return Fail("model-hypothesis-cannot-be-verified-fact");

        var allAddressed = review.Decisions
            .SelectMany(d => d.AddressedConcernIds)
            .Concat(review.UnresolvedGlobalConcernIds)
            .ToArray();
        if (!DistinctIds(allAddressed) ||
            allAddressed.Length != concernIds.Length ||
            allAddressed.Any(id => !concernIds.Contains(id, StringComparer.Ordinal)))
            return Fail("material-concern-unaddressed-or-foreign");

        // The host may publish the recommendation as an unverified hypothesis
        // status only. A Reviewer does not attest semantic correctness.
        return new(GateCContractCheck.Pass(),
            review.Decisions.Select(d =>
                new GateCHostDisposition(d.HypothesisId, d.Recommendation, false))
                .ToArray());
    }

    public static GateCContractCheck ValidateHostCatalog(
        AgentEvidencePackage evidence, GateCScope scope,
        IReadOnlyList<GateCEvidenceHandle> catalog)
    {
        if (!ValidScope(scope) || evidence.WorkspaceId != scope.WorkspaceId ||
            evidence.RepositoryId != scope.RepositoryId ||
            evidence.SnapshotId != scope.SnapshotId ||
            evidence.FingerprintSha256 != scope.FingerprintSha256 ||
            evidence.EntryPointId != scope.EntryPointId ||
            evidence.ContractVersion != scope.EvidenceContractVersion ||
            !ValidCatalog(catalog))
            return GateCContractCheck.Fail("invalid-host-evidence-scope");
        foreach (var handle in catalog)
        {
            var references = evidence.References.Where(r => r.Id == handle.CanonicalId)
                .Select(r => r.EvidenceStatus).ToArray();
            var relations = evidence.Relations.Where(r => r.Id == handle.CanonicalId)
                .Select(r => r.EvidenceStatus).ToArray();
            if (references.Length + relations.Length != 1)
                return GateCContractCheck.Fail("ambiguous-or-missing-canonical-evidence");
            var status = references.Concat(relations).Single();
            var expected = handle.Kind switch
            {
                GateCEvidenceKind.ObservedSyntax => "observed-syntax",
                GateCEvidenceKind.Candidate => "candidate",
                GateCEvidenceKind.ProvisionalLocal => "provisional-local",
                _ => ""
            };
            if (status != expected)
                return GateCContractCheck.Fail("evidence-status-mismatch");
        }
        return GateCContractCheck.Pass();
    }

    private static bool ValidScope(GateCScope? scope) =>
        scope is not null && scope.WorkspaceId != Guid.Empty &&
        scope.RepositoryId != Guid.Empty && scope.SnapshotId != Guid.Empty &&
        scope.FingerprintSha256 is { Length: 64 } &&
        scope.FingerprintSha256.All(Uri.IsHexDigit) &&
        !string.IsNullOrWhiteSpace(scope.EntryPointId) &&
        scope.EntryPointId.Length <= 1024 &&
        !scope.EntryPointId.Any(char.IsControl) &&
        scope.EvidenceContractVersion == SnapshotEvidenceTools.Version;

    private static bool ValidText(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        text.Length <= GateCContract.MaxTextLength &&
        !text.Any(ch => ch == '\0');

    private static bool ValidCatalog(IReadOnlyList<GateCEvidenceHandle>? catalog) =>
        catalog is not null && catalog.Count <= 64 &&
        DistinctIds(catalog.Select(x => x?.Handle)) &&
        DistinctIds(catalog.Select(x => x?.CanonicalId)) &&
        catalog.All(x => x is not null && Enum.IsDefined(x.Kind));

    private static bool ValidHandles(IReadOnlyList<string> handles,
        IReadOnlyList<GateCEvidenceHandle> allowed) =>
        handles.Count <= GateCContract.MaxSelections &&
        DistinctIds(handles) &&
        handles.All(id => allowed.Any(x => x.Handle == id));

    private static bool DistinctIds(IEnumerable<string?> values)
    {
        var items = values.ToArray();
        return items.All(x => x is { Length: > 0 and <= 128 } &&
            !string.IsNullOrWhiteSpace(x) && !x.Any(char.IsControl)) &&
            items.Distinct(StringComparer.Ordinal).Count() == items.Length;
    }
}

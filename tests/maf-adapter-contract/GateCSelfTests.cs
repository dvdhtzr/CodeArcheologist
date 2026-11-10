using CodeArcheologist.Api.Investigations;

internal static class GateCSelfTests
{
    public static void Run()
    {
        var run = Guid.NewGuid();
        var scope = new GateCScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new string('a', 64), "http-minimal:source:1",
            SnapshotEvidenceTools.Version);
        var handles = new[]
        {
            new GateCEvidenceHandle("R1", "relation-1", GateCEvidenceKind.Candidate),
            new GateCEvidenceHandle("E1", "source-1", GateCEvidenceKind.ObservedSyntax)
        };
        var investigator = new GateCIndependentAssessment(
            GateCContract.Version, run, scope, GateCRole.Investigator,
            ["R1"],
            [new GateCHypothesis("H1", "Potential endpoint.", ["R1"],
                "Effective runtime route remains unknown.")],
            [], ["Runtime behavior is not verified."]);
        var skeptic = new GateCIndependentAssessment(
            GateCContract.Version, run, scope, GateCRole.Skeptic,
            ["E1"], [],
            [new GateCConcern("S1", GateCConcernKind.InsufficientEvidence,
                "Runtime binding is not established.", ["E1"])],
            ["Handler binding remains unknown."]);
        ExpectValid(GateCContractValidator.ValidateIndependent(
            investigator, run, scope, handles));
        ExpectValid(GateCContractValidator.ValidateIndependent(
            skeptic, run, scope, handles));

        var reviewer = new GateCReviewerAssessment(GateCContract.Version,
            run, scope, GateCRole.Reviewer,
            [new GateCReviewDecision("H1",
                GateCRecommendation.ContestedOrInsufficient, ["S1"],
                "Keep the source candidate and disclose unknown binding.")], []);
        var result = GateCContractValidator.ValidateReview(
            reviewer, run, scope, investigator, skeptic);
        ExpectValid(result.Check);
        if (result.Dispositions.Count != 1 ||
            result.Dispositions[0].IsVerifiedFact)
            throw new InvalidOperationException(
                "Review must not transform an interpretation into a fact.");

        ExpectInvalid(GateCContractValidator.ValidateIndependent(
            investigator with { RunId = Guid.NewGuid() }, run, scope, handles),
            "stale-or-foreign-contract-scope");
        ExpectInvalid(GateCContractValidator.ValidateIndependent(
            investigator with { Scope = scope with { SnapshotId = Guid.NewGuid() } },
            run, scope, handles), "stale-or-foreign-contract-scope");
        ExpectInvalid(GateCContractValidator.ValidateIndependent(
            investigator with { Scope = scope with { FingerprintSha256 = new string('b', 64) } },
            run, scope, handles), "stale-or-foreign-contract-scope");
        ExpectInvalid(GateCContractValidator.ValidateIndependent(
            investigator with { Role = GateCRole.Reviewer }, run, scope, handles),
            "invalid-independent-role");
        ExpectInvalid(GateCContractValidator.ValidateIndependent(
            investigator with { SelectedEvidenceHandles = ["R2"] },
            run, scope, handles), "invalid-or-foreign-selected-evidence");
        ExpectInvalid(GateCContractValidator.ValidateIndependent(
            investigator with { SelectedEvidenceHandles = ["R1", "R1"] },
            run, scope, handles), "invalid-or-foreign-selected-evidence");
        ExpectInvalid(GateCContractValidator.ValidateIndependent(
            investigator with { Hypotheses =
                [new GateCHypothesis("H1", "Question", ["foreign"], "Unknown")] },
            run, scope, handles), "invalid-hypotheses");
        ExpectInvalid(GateCContractValidator.ValidateIndependent(
            skeptic with { Concerns =
                [new GateCConcern("S1", GateCConcernKind.StructuralContradiction,
                    "Unverified contradiction.", [])] },
            run, scope, handles), "uncited-structural-contradiction");

        ExpectInvalid(GateCContractValidator.ValidateReview(
            reviewer with { RunId = Guid.NewGuid() },
            run, scope, investigator, skeptic).Check,
            "review-identity-or-input-invalid");
        ExpectInvalid(GateCContractValidator.ValidateReview(
            reviewer with { Decisions = [] },
            run, scope, investigator, skeptic).Check,
            "review-decisions-invalid");
        ExpectInvalid(GateCContractValidator.ValidateReview(
            reviewer with { Decisions =
                [reviewer.Decisions[0] with { AddressedConcernIds = [] }] },
            run, scope, investigator, skeptic).Check,
            "material-concern-unaddressed-or-foreign");
        ExpectInvalid(GateCContractValidator.ValidateReview(
            reviewer with { Decisions =
                [reviewer.Decisions[0] with { AddressedConcernIds = ["S1", "S1"] }] },
            run, scope, investigator, skeptic).Check,
            "review-decisions-invalid");
        ExpectInvalid(GateCContractValidator.ValidateReview(
            reviewer with { Decisions =
                [reviewer.Decisions[0] with { AddressedConcernIds = ["S2"] }] },
            run, scope, investigator, skeptic).Check,
            "material-concern-unaddressed-or-foreign");
        ExpectInvalid(GateCContractValidator.ValidateReview(
            reviewer with { Decisions =
                [reviewer.Decisions[0] with {
                    Recommendation = GateCRecommendation.StructurallySupported }] },
            run, scope, investigator, skeptic).Check,
            "model-hypothesis-cannot-be-verified-fact");
        ExpectInvalid(GateCContractValidator.ValidateReview(
            reviewer with { UnresolvedGlobalConcernIds = ["S1"] },
            run, scope, investigator, skeptic).Check,
            "material-concern-unaddressed-or-foreign");
        ExpectValid(GateCContractValidator.ValidateReview(
            reviewer with {
                Decisions = [reviewer.Decisions[0] with { AddressedConcernIds = [] }],
                UnresolvedGlobalConcernIds = ["S1"] },
            run, scope, investigator, skeptic).Check);

        var evidence = new AgentEvidencePackage(
            SnapshotEvidenceTools.Version, scope.WorkspaceId, scope.RepositoryId,
            scope.SnapshotId, scope.FingerprintSha256, "graph-v8",
            scope.EntryPointId, "http-minimal-mapping", "app.MapGet", false,
            0, 0, 0,
            [new AgentEvidenceReference("source-1", "invocation-site",
                "observed-syntax", "MapGet", "Program.cs",
                scope.FingerprintSha256, 1, 1, null)],
            [new AgentEvidenceRelation("relation-1", "candidate-http-mapping",
                "candidate", scope.EntryPointId, "source-1",
                "Program.cs", scope.FingerprintSha256, 1, 1, "candidate", null)],
            ["No runtime proof"], ["Syntax only"]);
        ExpectValid(GateCContractValidator.ValidateHostCatalog(evidence, scope, handles));
        ExpectInvalid(GateCContractValidator.ValidateHostCatalog(
            evidence, scope, [handles[0] with { Kind = GateCEvidenceKind.ObservedSyntax }]),
            "evidence-status-mismatch");
        ExpectInvalid(GateCContractValidator.ValidateHostCatalog(
            evidence, scope, [handles[0] with { CanonicalId = "missing" }]),
            "ambiguous-or-missing-canonical-evidence");
        ExpectInvalid(GateCContractValidator.ValidateHostCatalog(
            evidence, scope with { SnapshotId = Guid.NewGuid() }, handles),
            "invalid-host-evidence-scope");

        // Only the host advances workflow state. An unvalidated Skeptic
        // cannot be bypassed to run the Reviewer or mark the run complete.
        var progress = GateCRunProgress.New(run);
        AssertTransitionDenied(GateCStateTransitions.Apply(
            progress, GateCRunEvent.BeginReviewer));
        progress = Advance(progress, GateCRunEvent.EvidenceVerified);
        progress = Advance(progress, GateCRunEvent.BeginIndependentAssessments);
        progress = Advance(progress, GateCRunEvent.StartInvestigator);
        progress = Advance(progress, GateCRunEvent.AcceptInvestigator);
        AssertTransitionDenied(GateCStateTransitions.Apply(
            progress, GateCRunEvent.BeginReviewer));
        AssertTransitionDenied(GateCStateTransitions.Apply(
            progress, GateCRunEvent.Complete));
        progress = Advance(progress, GateCRunEvent.StartSkeptic);
        progress = Advance(progress, GateCRunEvent.AcceptSkeptic);
        progress = Advance(progress, GateCRunEvent.BeginReviewer);
        AssertTransitionDenied(GateCStateTransitions.Apply(
            progress, GateCRunEvent.Complete));
        progress = Advance(progress, GateCRunEvent.AcceptReviewer);
        progress = Advance(progress, GateCRunEvent.Complete);
        if (progress.Status != GateCRunStatus.Completed)
            throw new InvalidOperationException("Host completion gate failed.");
        AssertTransitionDenied(GateCStateTransitions.Apply(
            progress, GateCRunEvent.BeginReviewer));

        var failed = GateCRunProgress.New(Guid.NewGuid());
        failed = Advance(failed, GateCRunEvent.EvidenceVerified);
        failed = Advance(failed, GateCRunEvent.BeginIndependentAssessments);
        failed = Advance(failed, GateCRunEvent.StartSkeptic);
        failed = Advance(failed, GateCRunEvent.RejectSkeptic);
        if (failed.Status != GateCRunStatus.Incomplete)
            throw new InvalidOperationException("Rejected role must be incomplete.");
        AssertTransitionDenied(GateCStateTransitions.Apply(
            failed, GateCRunEvent.BeginReviewer));

        static GateCRunProgress Advance(GateCRunProgress current, GateCRunEvent evt)
        {
            var result = GateCStateTransitions.Apply(current, evt);
            ExpectValid(result.Check);
            return result.Progress;
        }
        static void AssertTransitionDenied(GateCTransition result)
        {
            ExpectInvalid(result.Check, "illegal-run-transition");
        }

        GateCDiscoverySelfTests.Run();
        GateC3SelfTests.Run();
        GateC4SelfTests.Run();

        Console.WriteLine(
            "GREEN: Gate C1 independent assessments, host evidence scope, " +
            "material challenge coverage and no-fact-promotion contracts.");
    }

    private static void ExpectValid(GateCContractCheck check)
    {
        if (!check.Accepted)
            throw new InvalidOperationException("Expected valid C1 contract, got " +
                check.Diagnostic);
    }

    private static void ExpectInvalid(GateCContractCheck check, string diagnostic)
    {
        if (check.Accepted || check.Diagnostic != diagnostic)
            throw new InvalidOperationException(
                "Expected C1 rejection " + diagnostic + ", got " + check.Diagnostic);
    }
}

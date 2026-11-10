using CodeArcheologist.Api.Investigations;

internal static class GateC4SelfTests
{
    public static void Run()
    {
        var scope = new GateCScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new string('a', 64), "http-candidate:1", SnapshotEvidenceTools.Version);
        var runId = Guid.NewGuid();
        var investigator = new GateCIndependentAssessment(
            GateCContract.Version, runId, scope, GateCRole.Investigator,
            ["E1"], [new GateCHypothesis("H1", "Source suggests a mapping.",
                ["E1"], "Effective route unknown.")], [], ["Runtime not tested."]);
        var skeptic = new GateCIndependentAssessment(
            GateCContract.Version, runId, scope, GateCRole.Skeptic,
            ["E1"], [],
            [new GateCConcern("S1", GateCConcernKind.InsufficientEvidence,
                "No runtime endpoint proof.", ["E1"])],
            ["Binding unknown."]);

        const string reviewJson = """
            {"decisions":[{"hypothesisId":"H1",
             "recommendation":"contested-or-insufficient",
             "addressedConcernIds":["S1"],
             "explanation":"Retain unknown runtime status."}],
             "unresolvedGlobalConcernIds":[]}
            """;
        var review = GateC4ReconciliationRunner.ParseReview(reviewJson, runId, scope);
        if (review is null)
            throw new InvalidOperationException("Valid Reviewer JSON rejected.");
        var checkedReview = GateCContractValidator.ValidateReview(review,
            runId, scope, investigator, skeptic);
        if (!checkedReview.Check.Accepted || checkedReview.Dispositions.Count != 1 ||
            checkedReview.Dispositions[0].IsVerifiedFact)
            throw new InvalidOperationException("Host rejected safe review or promoted fact.");

        const string unhandled = """
            {"decisions":[{"hypothesisId":"H1",
             "recommendation":"plausible-hypothesis",
             "addressedConcernIds":[],
             "explanation":"A candidate, not runtime proof."}],
             "unresolvedGlobalConcernIds":[]}
            """;
        var missing = GateC4ReconciliationRunner.ParseReview(unhandled, runId, scope);
        if (missing is null || GateCContractValidator.ValidateReview(missing,
            runId, scope, investigator, skeptic).Check.Accepted)
            throw new InvalidOperationException("Unaddressed material objection accepted.");

        const string unresolved = """
            {"decisions":[{"hypothesisId":"H1",
             "recommendation":"contested-or-insufficient",
             "addressedConcernIds":[],
             "explanation":"Insufficient verification."}],
             "unresolvedGlobalConcernIds":["S1"]}
            """;
        var open = GateC4ReconciliationRunner.ParseReview(unresolved, runId, scope);
        if (open is null || !GateCContractValidator.ValidateReview(open,
            runId, scope, investigator, skeptic).Check.Accepted)
            throw new InvalidOperationException("Explicitly unresolved objection rejected.");

        const string noHypotheses = """
            {"decisions":[],"unresolvedGlobalConcernIds":["S1"]}
            """;
        var noH = GateC4ReconciliationRunner.ParseReview(noHypotheses, runId, scope);
        if (noH is null || !GateCContractValidator.ValidateReview(noH, runId,
            scope, investigator with { Hypotheses = [], SelectedEvidenceHandles = [] },
            skeptic).Check.Accepted)
            throw new InvalidOperationException("Valid no-hypothesis reconciliation rejected.");

        foreach (var bad in new[]
        {
            reviewJson + " trailing",
            reviewJson.Replace("\"decisions\"", "\"answers\"",
                StringComparison.Ordinal),
            reviewJson.Replace("\"H1\"", "\"foreign\"",
                StringComparison.Ordinal),
            reviewJson.Replace("\"contested-or-insufficient\"",
                "\"structurally-supported\"", StringComparison.Ordinal),
            reviewJson.Replace("\"contested-or-insufficient\"",
                "\"rejected\"", StringComparison.Ordinal),
            reviewJson.Replace("\"unresolvedGlobalConcernIds\":[]",
                "\"unresolvedGlobalConcernIds\":[\"S1\"]", StringComparison.Ordinal)
        })
        {
            var parsed = GateC4ReconciliationRunner.ParseReview(bad, runId, scope);
            if (parsed is not null && GateCContractValidator.ValidateReview(parsed,
                    runId, scope, investigator, skeptic).Check.Accepted)
                throw new InvalidOperationException("Invalid Reviewer disposition accepted.");
        }
        if (GateC4ReconciliationRunner.DiagnoseReviewShape(
                "") == "")
            throw new InvalidOperationException("Diagnostic missing.");

        // Reviewer is gated on two validated role outputs. Every terminal
        // failure remains incomplete/cancelled; no bypass to Completed.
        foreach (var (action, roleStatus, runStatus) in new[]
        {
            (GateCRunEvent.RejectReviewer, GateCRoleStatus.ValidationRejected,
                GateCRunStatus.Incomplete),
            (GateCRunEvent.FailReviewer, GateCRoleStatus.Failed,
                GateCRunStatus.Incomplete),
            (GateCRunEvent.TimeoutReviewer, GateCRoleStatus.TimedOut,
                GateCRunStatus.Incomplete),
            (GateCRunEvent.CancelReviewer, GateCRoleStatus.Cancelled,
                GateCRunStatus.Cancelled)
        })
        {
            var state = ReadyForReviewer(Guid.NewGuid());
            var transition = GateCStateTransitions.Apply(state, action);
            if (!transition.Check.Accepted ||
                transition.Progress.ReviewerStatus != roleStatus ||
                transition.Progress.Status != runStatus ||
                GateCStateTransitions.Apply(transition.Progress,
                    GateCRunEvent.Complete).Check.Accepted)
                throw new InvalidOperationException("Reviewer failure promoted to complete.");
        }

        var happy = ReadyForReviewer(Guid.NewGuid());
        happy = Go(happy, GateCRunEvent.AcceptReviewer);
        happy = Go(happy, GateCRunEvent.Complete);
        if (happy.Status != GateCRunStatus.Completed)
            throw new InvalidOperationException("Validated reviewer not completed.");

        Console.WriteLine("GREEN: Gate C4 structured Reviewer reconciliation, full material concern coverage and terminal failure gates.");

        static GateCRunProgress Go(GateCRunProgress state, GateCRunEvent evt)
        {
            var next = GateCStateTransitions.Apply(state, evt);
            if (!next.Check.Accepted)
                throw new InvalidOperationException("Illegal expected host transition.");
            return next.Progress;
        }
        static GateCRunProgress ReadyForReviewer(Guid run)
        {
            var state = GateCRunProgress.New(run);
            state = Go(state, GateCRunEvent.EvidenceVerified);
            state = Go(state, GateCRunEvent.BeginIndependentAssessments);
            state = Go(state, GateCRunEvent.StartInvestigator);
            state = Go(state, GateCRunEvent.AcceptInvestigator);
            state = Go(state, GateCRunEvent.StartSkeptic);
            state = Go(state, GateCRunEvent.AcceptSkeptic);
            return Go(state, GateCRunEvent.BeginReviewer);
        }
    }
}

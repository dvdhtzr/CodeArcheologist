namespace CodeArcheologist.Api.Investigations;

/// <summary>
/// Host-controlled C1 transition gate. A model cannot select transitions.
/// This pure function neither invokes MAF nor persists run state.
/// </summary>
public enum GateCRunEvent
{
    EvidenceVerified,
    BeginIndependentAssessments,
    StartInvestigator,
    AcceptInvestigator,
    RejectInvestigator,
    FailInvestigator,
    TimeoutInvestigator,
    CancelInvestigator,
    StartSkeptic,
    AcceptSkeptic,
    RejectSkeptic,
    FailSkeptic,
    TimeoutSkeptic,
    CancelSkeptic,
    BeginReviewer,
    AcceptReviewer,
    RejectReviewer,
    FailReviewer,
    TimeoutReviewer,
    CancelReviewer,
    Complete,
    Cancel,
    Fail,
    Reject
}

public sealed record GateCRunProgress(Guid RunId, GateCRunStatus Status,
    GateCRoleStatus InvestigatorStatus, GateCRoleStatus SkepticStatus,
    GateCRoleStatus ReviewerStatus)
{
    public static GateCRunProgress New(Guid runId)
    {
        if (runId == Guid.Empty)
            throw new ArgumentException("Host-issued run ID cannot be empty.");
        return new(runId, GateCRunStatus.Created,
            GateCRoleStatus.NotStarted, GateCRoleStatus.NotStarted,
            GateCRoleStatus.NotStarted);
    }
}

public sealed record GateCTransition(GateCContractCheck Check,
    GateCRunProgress Progress);

public static class GateCStateTransitions
{
    public static GateCTransition Apply(GateCRunProgress current,
        GateCRunEvent requested)
    {
        GateCTransition Deny() =>
            new(GateCContractCheck.Fail("illegal-run-transition"), current);
        GateCTransition Permit(GateCRunProgress next) =>
            new(GateCContractCheck.Pass(), next);
        if (current.RunId == Guid.Empty || !Enum.IsDefined(requested) ||
            !Enum.IsDefined(current.Status) ||
            !Enum.IsDefined(current.InvestigatorStatus) ||
            !Enum.IsDefined(current.SkepticStatus) ||
            !Enum.IsDefined(current.ReviewerStatus))
            return Deny();

        if (current.Status is GateCRunStatus.Completed or GateCRunStatus.Incomplete
            or GateCRunStatus.Cancelled or GateCRunStatus.Rejected)
            return Deny();

        switch (requested)
        {
            case GateCRunEvent.EvidenceVerified
                when current.Status == GateCRunStatus.Created:
                return Permit(current with { Status = GateCRunStatus.EvidenceVerified });
            case GateCRunEvent.BeginIndependentAssessments
                when current.Status == GateCRunStatus.EvidenceVerified:
                return Permit(current with {
                    Status = GateCRunStatus.IndependentAssessment });
            case GateCRunEvent.StartInvestigator
                when current.Status == GateCRunStatus.IndependentAssessment &&
                     current.InvestigatorStatus == GateCRoleStatus.NotStarted:
                return Permit(current with { InvestigatorStatus = GateCRoleStatus.Running });
            case GateCRunEvent.StartSkeptic
                when current.Status == GateCRunStatus.IndependentAssessment &&
                     current.SkepticStatus == GateCRoleStatus.NotStarted:
                return Permit(current with { SkepticStatus = GateCRoleStatus.Running });
            case GateCRunEvent.AcceptInvestigator
                when current.Status == GateCRunStatus.IndependentAssessment &&
                     current.InvestigatorStatus == GateCRoleStatus.Running:
                return Permit(current with {
                    InvestigatorStatus = GateCRoleStatus.CompletedValidated });
            case GateCRunEvent.AcceptSkeptic
                when current.Status == GateCRunStatus.IndependentAssessment &&
                     current.SkepticStatus == GateCRoleStatus.Running:
                return Permit(current with {
                    SkepticStatus = GateCRoleStatus.CompletedValidated });
            case GateCRunEvent.RejectInvestigator
                when current.Status == GateCRunStatus.IndependentAssessment &&
                     current.InvestigatorStatus == GateCRoleStatus.Running:
                return Permit(current with {
                    InvestigatorStatus = GateCRoleStatus.ValidationRejected,
                    Status = GateCRunStatus.Incomplete });
            case GateCRunEvent.RejectSkeptic
                when current.Status == GateCRunStatus.IndependentAssessment &&
                     current.SkepticStatus == GateCRoleStatus.Running:
                return Permit(current with {
                    SkepticStatus = GateCRoleStatus.ValidationRejected,
                    Status = GateCRunStatus.Incomplete });
            case GateCRunEvent.FailInvestigator or
                GateCRunEvent.TimeoutInvestigator or GateCRunEvent.CancelInvestigator
                when current.Status == GateCRunStatus.IndependentAssessment &&
                     current.InvestigatorStatus == GateCRoleStatus.Running:
                return Permit(current with {
                    InvestigatorStatus = requested switch
                    {
                        GateCRunEvent.TimeoutInvestigator => GateCRoleStatus.TimedOut,
                        GateCRunEvent.CancelInvestigator => GateCRoleStatus.Cancelled,
                        _ => GateCRoleStatus.Failed
                    },
                    Status = requested == GateCRunEvent.CancelInvestigator
                        ? GateCRunStatus.Cancelled : GateCRunStatus.Incomplete });
            case GateCRunEvent.FailSkeptic or
                GateCRunEvent.TimeoutSkeptic or GateCRunEvent.CancelSkeptic
                when current.Status == GateCRunStatus.IndependentAssessment &&
                     current.SkepticStatus == GateCRoleStatus.Running:
                return Permit(current with {
                    SkepticStatus = requested switch
                    {
                        GateCRunEvent.TimeoutSkeptic => GateCRoleStatus.TimedOut,
                        GateCRunEvent.CancelSkeptic => GateCRoleStatus.Cancelled,
                        _ => GateCRoleStatus.Failed
                    },
                    Status = requested == GateCRunEvent.CancelSkeptic
                        ? GateCRunStatus.Cancelled : GateCRunStatus.Incomplete });
            case GateCRunEvent.BeginReviewer
                when current.Status == GateCRunStatus.IndependentAssessment &&
                     current.InvestigatorStatus == GateCRoleStatus.CompletedValidated &&
                     current.SkepticStatus == GateCRoleStatus.CompletedValidated &&
                     current.ReviewerStatus == GateCRoleStatus.NotStarted:
                return Permit(current with {
                    Status = GateCRunStatus.Reconciliation,
                    ReviewerStatus = GateCRoleStatus.Running });
            case GateCRunEvent.AcceptReviewer
                when current.Status == GateCRunStatus.Reconciliation &&
                     current.ReviewerStatus == GateCRoleStatus.Running:
                return Permit(current with {
                    ReviewerStatus = GateCRoleStatus.CompletedValidated });
            case GateCRunEvent.RejectReviewer
                when current.Status == GateCRunStatus.Reconciliation &&
                     current.ReviewerStatus == GateCRoleStatus.Running:
                return Permit(current with {
                    ReviewerStatus = GateCRoleStatus.ValidationRejected,
                    Status = GateCRunStatus.Incomplete });
            case GateCRunEvent.FailReviewer or
                GateCRunEvent.TimeoutReviewer or GateCRunEvent.CancelReviewer
                when current.Status == GateCRunStatus.Reconciliation &&
                     current.ReviewerStatus == GateCRoleStatus.Running:
                return Permit(current with {
                    ReviewerStatus = requested switch
                    {
                        GateCRunEvent.TimeoutReviewer => GateCRoleStatus.TimedOut,
                        GateCRunEvent.CancelReviewer => GateCRoleStatus.Cancelled,
                        _ => GateCRoleStatus.Failed
                    },
                    Status = requested == GateCRunEvent.CancelReviewer
                        ? GateCRunStatus.Cancelled : GateCRunStatus.Incomplete });
            case GateCRunEvent.Complete
                when current.Status == GateCRunStatus.Reconciliation &&
                     current.InvestigatorStatus == GateCRoleStatus.CompletedValidated &&
                     current.SkepticStatus == GateCRoleStatus.CompletedValidated &&
                     current.ReviewerStatus == GateCRoleStatus.CompletedValidated:
                return Permit(current with { Status = GateCRunStatus.Completed });
            case GateCRunEvent.Cancel:
                return Permit(current with { Status = GateCRunStatus.Cancelled });
            case GateCRunEvent.Fail:
                return Permit(current with { Status = GateCRunStatus.Incomplete });
            case GateCRunEvent.Reject:
                return Permit(current with { Status = GateCRunStatus.Rejected });
            default:
                return Deny();
        }
    }
}

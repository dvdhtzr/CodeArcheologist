using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeArcheologist.Api.Investigations;

/// <summary>
/// Experimental adaptive question context v2. This does not overwrite the
/// original R3 context v1 and never silently upgrades an existing run.
/// </summary>
public sealed record AdaptiveQuestionContext(
    string ContractVersion, Guid QuestionId, Guid RunId, GateCScope Scope,
    string SubjectNodeId, string SubjectNodeKind, string QuestionKind,
    string QuestionTemplateVersion, string CapabilityPolicyVersion,
    string BudgetProfileVersion, DateTimeOffset CreatedAtUtc,
    string ContextDigest);

public static class AdaptiveQuestionContract
{
    public const string Version = "r3-adaptive-question-context-v2";
    public const string QuestionKind = InvestigationQuestionContract.ExplainHttpEntryPoint;
    public const string TemplateVersion = "r3-adaptive-explain-http-candidate-v1";
    public const string CapabilityPolicyVersion = "r3-adaptive-capabilities-v1";
    public const string BudgetProfileVersion = "r3-adaptive-six-call-one-followup-v1";
    public const string EvidenceVersion = InvestigationQuestionContract.EvidenceToolsVersion;

    public static string RequestDigest(GateCScope scope, string subjectKind)
    {
        RequireScope(scope, subjectKind);
        return Digest("adaptive-request-v2", ScopeFields(scope), subjectKind,
            Version, QuestionKind, TemplateVersion, CapabilityPolicyVersion,
            BudgetProfileVersion);
    }

    public static AdaptiveQuestionContext Create(Guid runId, GateCScope scope,
        string subjectKind, DateTimeOffset created)
    {
        RequireScope(scope, subjectKind);
        if (runId == Guid.Empty) throw new ArgumentException("Invalid run ID.", nameof(runId));
        var at = DateTimeOffset.FromUnixTimeMilliseconds(created.ToUnixTimeMilliseconds());
        var draft = new AdaptiveQuestionContext(
            Version, Guid.NewGuid(), runId, scope, scope.EntryPointId,
            subjectKind, QuestionKind, TemplateVersion, CapabilityPolicyVersion,
            BudgetProfileVersion, at, "");
        return draft with { ContextDigest = ComputeDigest(draft) };
    }

    public static bool Verify(AdaptiveQuestionContext? context)
    {
        if (context is null ||
            context.ContractVersion != Version ||
            context.RunId == Guid.Empty || context.QuestionId == Guid.Empty ||
            context.QuestionId == context.RunId ||
            context.Scope is null || !ValidScope(context.Scope) ||
            context.SubjectNodeId != context.Scope.EntryPointId ||
            !InvestigationQuestionContract.Supports(context.QuestionKind, context.SubjectNodeKind) ||
            context.QuestionTemplateVersion != TemplateVersion ||
            context.CapabilityPolicyVersion != CapabilityPolicyVersion ||
            context.BudgetProfileVersion != BudgetProfileVersion ||
            context.CreatedAtUtc.Offset != TimeSpan.Zero ||
            context.ContextDigest is not { Length: 64 })
            return false;
        return string.Equals(context.ContextDigest,
            ComputeDigest(context), StringComparison.Ordinal);
    }

    private static void RequireScope(GateCScope scope, string kind)
    {
        if (!ValidScope(scope) ||
            !InvestigationQuestionContract.Supports(QuestionKind, kind) ||
            GateCEvidenceRegistry.Version != InvestigationQuestionContract.CapabilityPolicyVersion ||
            GateCEvidenceSession.MaxToolCalls != 6 ||
            GateCEvidenceSession.MaxTotalItems != 48 ||
            GateC3IndependentRunner.MaxViewsPerRole != 3 ||
            GateC3IndependentRunner.RoleTimeoutSeconds != 90 ||
            GateC3IndependentRunner.AssessmentAttempts != 2)
            throw new ArgumentException("Unsupported adaptive question, scope, or budget.");
    }

    private static bool ValidScope(GateCScope? scope) =>
        scope is not null && scope.WorkspaceId != Guid.Empty &&
        scope.RepositoryId != Guid.Empty && scope.SnapshotId != Guid.Empty &&
        scope.FingerprintSha256 is { Length: 64 } &&
        scope.FingerprintSha256.All(char.IsAsciiHexDigit) &&
        scope.EntryPointId is { Length: > 0 and <= 1024 } &&
        !scope.EntryPointId.Any(char.IsControl) &&
        scope.EvidenceContractVersion == EvidenceVersion;

    private static string ComputeDigest(AdaptiveQuestionContext context) =>
        Digest("adaptive-context-v2", context.ContractVersion,
            context.QuestionId.ToString("D"), context.RunId.ToString("D"),
            ScopeFields(context.Scope), context.SubjectNodeId,
            context.SubjectNodeKind, context.QuestionKind,
            context.QuestionTemplateVersion, context.CapabilityPolicyVersion,
            context.BudgetProfileVersion,
            context.CreatedAtUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

    private static string ScopeFields(GateCScope scope) =>
        string.Join("\n", scope.WorkspaceId.ToString("D"), scope.RepositoryId.ToString("D"),
            scope.SnapshotId.ToString("D"), scope.FingerprintSha256,
            scope.EntryPointId, scope.EvidenceContractVersion);

    private static string Digest(params string[] fields)
    {
        var builder = new StringBuilder("r3-adaptive-question-sha256-v2");
        foreach (var value in fields)
            builder.Append('|').Append(value.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

using System.Security.Cryptography;
using System.Text;
using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Investigations;

/// <summary>Only the question and existing saved node ID are client supplied.
/// The host derives the subject kind and all authoritative evidence metadata.</summary>
public sealed record InvestigationQuestionCreateRequest(string? SubjectNodeId, string? QuestionKind);

/// <summary>Immutable question identity, separate from the execution run ID.
/// This is metadata only; PR1 does not change model prompts or retrieval.</summary>
public sealed record InvestigationQuestionContext(
    string ContractVersion,
    Guid QuestionId,
    Guid RunId,
    GateCScope Scope,
    string SubjectNodeId,
    string SubjectNodeKind,
    string QuestionKind,
    string QuestionTemplateVersion,
    string CapabilityPolicyVersion,
    string BudgetProfileVersion,
    DateTimeOffset CreatedAtUtc,
    string ContextDigest);

public static class InvestigationQuestionContract
{
    public const string Version = "r3-investigation-context-v1";
    public const string ExplainHttpEntryPoint = "explain-http-entry-point";
    public const string TemplateVersion = "r3-explain-http-entry-point-v1";
    // This profile names the existing fixed C6b resource budgets, not new limits.
    public const string BudgetProfileVersion = "r3-legacy-gate-c6b-budget-v1";

    public static bool Supports(string? questionKind, string? subjectKind) =>
        questionKind == ExplainHttpEntryPoint &&
        subjectKind is ("http-endpoint-attribute" or "http-minimal-mapping");

    public static string RequestDigest(GateCScope scope, string subjectKind)
    {
        RequireSupported(scope, subjectKind);
        return Hash("r3-question-request-v1", ScopeParts(scope), subjectKind,
            ExplainHttpEntryPoint, Version, TemplateVersion,
            GateCEvidenceRegistry.Version, BudgetProfileVersion);
    }

    public static InvestigationQuestionContext Create(Guid runId, GateCScope scope,
        string subjectKind, DateTimeOffset timestamp)
    {
        RequireSupported(scope, subjectKind);
        if (runId == Guid.Empty) throw new ArgumentException("A run ID is required.", nameof(runId));
        var created = DateTimeOffset.FromUnixTimeMilliseconds(timestamp.ToUnixTimeMilliseconds());
        var draft = new InvestigationQuestionContext(
            Version, Guid.NewGuid(), runId, scope, scope.EntryPointId, subjectKind,
            ExplainHttpEntryPoint, TemplateVersion, GateCEvidenceRegistry.Version,
            BudgetProfileVersion, created, "");
        return draft with { ContextDigest = Digest(draft) };
    }

    public static bool Verify(InvestigationQuestionContext? context)
    {
        if (context is null || context.ContractVersion != Version ||
            context.QuestionId == Guid.Empty || context.RunId == Guid.Empty ||
            context.Scope is null || context.SubjectNodeId != context.Scope.EntryPointId ||
            context.QuestionTemplateVersion != TemplateVersion ||
            context.CapabilityPolicyVersion != GateCEvidenceRegistry.Version ||
            context.BudgetProfileVersion != BudgetProfileVersion ||
            !Supports(context.QuestionKind, context.SubjectNodeKind) ||
            !ValidScope(context.Scope) ||
            context.ContextDigest is not { Length: 64 })
            return false;
        return string.Equals(context.ContextDigest, Digest(context), StringComparison.Ordinal);
    }

    private static void RequireSupported(GateCScope scope, string subjectKind)
    {
        if (!ValidScope(scope) || !Supports(ExplainHttpEntryPoint, subjectKind))
            throw new ArgumentException("Unsupported or invalid question scope.");
    }

    private static bool ValidScope(GateCScope? scope) =>
        scope is not null && scope.WorkspaceId != Guid.Empty &&
        scope.RepositoryId != Guid.Empty && scope.SnapshotId != Guid.Empty &&
        scope.FingerprintSha256 is { Length: 64 } &&
        scope.FingerprintSha256.All(char.IsAsciiHexDigit) &&
        scope.EntryPointId is { Length: > 0 and <= 1024 } &&
        !scope.EntryPointId.Any(char.IsControl) &&
        scope.EvidenceContractVersion == SnapshotEvidenceTools.Version;

    private static string Digest(InvestigationQuestionContext c) =>
        Hash("r3-question-context-v1", c.ContractVersion,
            c.QuestionId.ToString("D"), c.RunId.ToString("D"), ScopeParts(c.Scope),
            c.SubjectNodeId, c.SubjectNodeKind, c.QuestionKind,
            c.QuestionTemplateVersion, c.CapabilityPolicyVersion,
            c.BudgetProfileVersion, c.CreatedAtUtc.ToUnixTimeMilliseconds().ToString(
                System.Globalization.CultureInfo.InvariantCulture));

    private static string ScopeParts(GateCScope scope) =>
        string.Join("\n", scope.WorkspaceId.ToString("D"), scope.RepositoryId.ToString("D"),
            scope.SnapshotId.ToString("D"), scope.FingerprintSha256,
            scope.EntryPointId, scope.EvidenceContractVersion);

    private static string Hash(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))));
}

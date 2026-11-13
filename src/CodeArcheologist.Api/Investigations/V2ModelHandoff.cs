using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Investigations;

/// <summary>
/// Passive (unless explicitly opted in) V2 observation at the actual
/// role-to-model call boundary. Never interprets a provider call as
/// proof that the model processed evidence.
/// </summary>
public static class V2ModelHandoff
{
    private sealed class Context(WorkspaceStore store, InvestigationExecutionContext execution,
        GateCRole role, GateCEvidenceSession? session, bool adaptive)
    {
        public readonly WorkspaceStore Store = store;
        public readonly InvestigationExecutionContext Execution = execution;
        public readonly GateCRole Role = role;
        public readonly GateCEvidenceSession? Session = session;
        public readonly bool Adaptive = adaptive;
        public int CallOrdinal;
    }
    private static readonly AsyncLocal<Context?> Active = new();

    public sealed class Scope : IDisposable
    {
        private readonly Context? _previous;
        private bool _disposed;
        private Scope(Context? previous) => _previous = previous;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Active.Value = _previous;
        }
        internal static Scope? Open(WorkspaceStore store,
            InvestigationExecutionContext execution, GateCRole role,
            GateCEvidenceSession? session)
        {
            if (!store.IsV2HandoffEnabled(execution.Run.RunId))
                return null;
            if (role is not (GateCRole.Investigator or GateCRole.Skeptic or GateCRole.Reviewer) ||
                (role == GateCRole.Reviewer) != (session is null) ||
                (session is not null &&
                    (session.Role != role || session.VerifiedRunId != execution.Run.RunId ||
                     session.VerifiedScope != execution.Run.Scope)))
                throw new StagePersistenceException(new InvalidOperationException(
                    "V2 observation stage/scope mismatch."));
            var adaptive = store.GetAdaptiveContext(
                execution.Run.Scope.WorkspaceId, execution.Run.Scope.RepositoryId,
                execution.Run.Scope.SnapshotId, execution.Run.RunId) is not null;
            var original = Active.Value;
            Active.Value = new(store, execution, role, session, adaptive);
            return new Scope(original);
        }
    }

    public static Scope? BeginIfEnabled(WorkspaceStore store,
        InvestigationExecutionContext execution, GateCRole role,
        GateCEvidenceSession? session) => Scope.Open(store, execution, role, session);

    public static async Task<T> InvokeAsync<T>(string prompt, string stage,
        Func<Task<T>> invoke)
    {
        var current = Active.Value;
        if (current is null) return await invoke();

        var (store, execution, role, session) = (
            current.Store, current.Execution, current.Role, current.Session);
        var callId = Guid.NewGuid();
        var ordinal = current.CallOrdinal + 1;
        string promptHash;
        string linksJson;
        string receiptIdsJson;
        try
        {
            if (string.IsNullOrWhiteSpace(stage) || stage.Length > 80)
                throw new InvalidOperationException("Invalid model boundary stage.");
            if (!VerifyModelPrompt(prompt, stage, execution, role, session))
                throw new InvalidOperationException("Model-facing evidence differs from scoped host mappings.");
            var links = session?.ReportLinks() ?? Array.Empty<GateC5EvidenceLink>();
            linksJson = JsonSerializer.Serialize(links);
            var ids = current.Adaptive && session is not null
                ? store.ListAdaptiveEvidenceReceipts(execution.Run.RunId,
                    execution.Attempt.AttemptId, role)
                    .Select(x => x.ReceiptId).ToArray()
                : Array.Empty<Guid>();
            if (current.Adaptive && session is not null &&
                ids.Length != session.ToolCallsUsed)
                throw new InvalidOperationException("Model handoff has uncommitted adaptive evidence.");
            receiptIdsJson = JsonSerializer.Serialize(ids);
            promptHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
            store.RecordV2Handoff(execution.Fence, execution.Attempt.AttemptId,
                execution.Run.RunId, role, callId, ordinal, stage,
                V2HandoffContract.Prepared, promptHash, linksJson, receiptIdsJson,
                execution.Run.Scope.FingerprintSha256);
            current.CallOrdinal = ordinal;
            // This is recorded immediately before calling the adapter.
            // "adapter-entered" is still NOT provider acknowledgement.
            store.RecordV2Handoff(execution.Fence, execution.Attempt.AttemptId,
                execution.Run.RunId, role, callId, ordinal, stage,
                V2HandoffContract.Entered, promptHash, linksJson, receiptIdsJson,
                execution.Run.Scope.FingerprintSha256);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // No provider invocation may occur after failed provenance commit.
            throw new StagePersistenceException(failure);
        }

        T answer;
        try
        {
            answer = await invoke();
        }
        catch (Exception)
        {
            // The call has already started; persist failure before returning
            // to the worker. If that write fails, halt rather than relabel it.
            Finish(V2HandoffContract.Faulted);
            throw;
        }
        Finish(V2HandoffContract.Completed);
        return answer;

        void Finish(string kind)
        {
            try
            {
                store.RecordV2Handoff(execution.Fence, execution.Attempt.AttemptId,
                    execution.Run.RunId, role, callId, ordinal, stage,
                    kind, promptHash, linksJson, receiptIdsJson,
                    execution.Run.Scope.FingerprintSha256);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                throw new StagePersistenceException(failure);
            }
        }
    }

    private static bool VerifyModelPrompt(string prompt, string stage,
        InvestigationExecutionContext execution, GateCRole role, GateCEvidenceSession? session)
    {
        const string startMarker = "\n<untrusted-evidence-json>\n";
        const string endMarker = "\n</untrusted-evidence-json>";
        var at = prompt.IndexOf(startMarker, StringComparison.Ordinal);
        var claimsEvidence = at >= 0;
        if (role == GateCRole.Reviewer)
            return session is null && !claimsEvidence &&
                prompt.Contains("<untrusted-assessments-json>", StringComparison.Ordinal);
        if (session is null) return false;
        if (stage is "GateCDiscoverySelection" or "R3AdaptiveInitialSelection")
            return !claimsEvidence && session.ToolCallsUsed == 0 &&
                session.HostCatalog().Count == 0;
        if (!claimsEvidence || session.ToolCallsUsed is < 1 or > GateCEvidenceSession.MaxToolCalls)
            return false;
        var first = at + startMarker.Length;
        var last = prompt.IndexOf(endMarker, first, StringComparison.Ordinal);
        if (last < 0 || prompt.IndexOf(startMarker, first, StringComparison.Ordinal) >= 0)
            return false;
        try
        {
            using var document = JsonDocument.Parse(prompt[first..last]);
            var root = document.RootElement.GetProperty("discovery");
            if (!Guid.TryParse(root.GetProperty("RunId").GetString(), out var observedRun) ||
                observedRun != execution.Run.RunId ||
                root.GetProperty("Role").GetInt32() != (int)role ||
                root.GetProperty("EvidenceFingerprint").GetString() !=
                    execution.Run.Scope.FingerprintSha256)
                return false;
            var reported = root.GetProperty("Items").EnumerateArray()
                .Select(item => item.GetProperty("Handle").GetString()).ToArray();
            var host = session.ReportLinks().Select(link => link.Handle).ToArray();
            if (!reported.SequenceEqual(host, StringComparer.Ordinal))
                return false;
            return GateCContractValidator.ValidateHostCatalog(
                session.VerifiedPackage, session.VerifiedScope, session.HostCatalog()).Accepted;
        }
        catch (Exception failure) when (failure is JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}

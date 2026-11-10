using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Investigations;

/// <summary>
/// Discoverable built-in evidence views. No dynamic plugin loading, model
/// commands, arbitrary paths or imported-code execution.
/// </summary>
public static class GateCEvidenceRegistry
{
    public const string Version = "gate-c-evidence-capabilities-v1";
    public const string EntryPoint = "inspect_entry_point";
    public const string DirectInvocations = "inspect_direct_invocations";
    public const string CandidateCallees = "inspect_candidate_callees";
    public const string Relationships = "inspect_source_relationships";

    private static readonly GateCCapability[] BuiltIns =
    [
        new(EntryPoint, Version, "Saved HTTP candidate and its immediate source relationship", 12),
        new(DirectInvocations, Version, "Observed direct invocation sites within the supported source scope", 12),
        new(CandidateCallees, Version, "Provisional source-local callee relationships; never compiler or runtime proof", 12),
        new(Relationships, Version, "Bounded snapshot-scoped evidence relations with original source status", 12)
    ];

    public static IReadOnlyList<GateCCapability> Discover() => Array.AsReadOnly(BuiltIns);
    public static bool Supports(string? identifier) =>
        identifier is EntryPoint or DirectInvocations or CandidateCallees or Relationships;
}

public sealed record GateCCapability(string Identifier, string ContractVersion,
    string Description, int MaxResults);

/// <summary>
/// Agent-visible projection does not contain the canonical graph/source ID.
/// Handles exist only inside this run/role/session; the host keeps the map.
/// </summary>
public sealed record GateCDiscoveredItem(string Handle, string Kind,
    GateCEvidenceKind EvidenceKind, string Label, string? FilePath,
    int? Line, int? Column);

public sealed record GateCDiscoveryResponse(string Capability, string ContractVersion,
    Guid RunId, GateCRole Role, string EvidenceFingerprint,
    IReadOnlyList<GateCDiscoveredItem> Items, IReadOnlyList<string> Unknowns,
    bool Truncated, int ToolCallsUsed);

public sealed record GateCDiscoveryResult(GateCContractCheck Check,
    GateCDiscoveryResponse? Response);

/// <summary>
/// Per-role, per-run bounded read-only session over one independently verified
/// snapshot package. Session state is never shared between Investigator and
/// Skeptic. Production entry uses OpenVerified, not the internal test seam.
/// </summary>
public sealed class GateCEvidenceDiscovery(SnapshotEvidenceTools evidenceTools)
{
    public GateCEvidenceSession OpenVerified(SnapshotRecord snapshot,
        GateCScope scope, Guid runId, GateCRole role, CancellationToken ct)
    {
        if (runId == Guid.Empty || role is not (GateCRole.Investigator or GateCRole.Skeptic) ||
            snapshot.Id != scope.SnapshotId ||
            snapshot.WorkspaceId != scope.WorkspaceId ||
            snapshot.RepositoryId != scope.RepositoryId ||
            snapshot.FingerprintSha256 != scope.FingerprintSha256 ||
            scope.EvidenceContractVersion != SnapshotEvidenceTools.Version)
            throw new InvalidOperationException("Invalid Gate C discovery scope.");
        // Performs existing immutable saved-source verification. Does not read
        // the registered live repository. Rejects unsupported target IDs.
        var evidence = evidenceTools.ReadEntryPoint(snapshot, scope.EntryPointId, ct);
        return new GateCEvidenceSession(evidence, scope, runId, role);
    }
}

public sealed class GateCEvidenceSession
{
    public const int MaxToolCalls = 6;
    public const int MaxTotalItems = 48;
    private readonly AgentEvidencePackage _package;
    private readonly GateCScope _scope;
    private readonly Guid _runId;
    private readonly GateCRole _role;
    private readonly Dictionary<string, GateCEvidenceHandle> _handles =
        new(StringComparer.Ordinal);
    private int _calls;
    private int _itemsIssued;

    internal GateCEvidenceSession(AgentEvidencePackage evidence,
        GateCScope scope, Guid runId, GateCRole role)
    {
        if (runId == Guid.Empty || role is not (GateCRole.Investigator or GateCRole.Skeptic) ||
            evidence.WorkspaceId != scope.WorkspaceId ||
            evidence.RepositoryId != scope.RepositoryId ||
            evidence.SnapshotId != scope.SnapshotId ||
            evidence.FingerprintSha256 != scope.FingerprintSha256 ||
            evidence.EntryPointId != scope.EntryPointId ||
            evidence.ContractVersion != scope.EvidenceContractVersion)
            throw new ArgumentException("Discovery package has incorrect verified scope.");
        _package = evidence;
        _scope = scope;
        _runId = runId;
        _role = role;
    }

    /// <summary>
    /// Bind C1 assessment validation to the exact role session that minted
    /// the handles. Handle text alone is never cross-role authority.
    /// </summary>
    public GateCContractCheck ValidateAssessment(
        GateCIndependentAssessment? assessment)
    {
        if (assessment is null || assessment.Role != _role ||
            assessment.RunId != _runId || assessment.Scope != _scope)
            return GateCContractCheck.Fail("foreign-role-evidence-session");
        var catalog = HostCatalog();
        var evidenceCheck = GateCContractValidator.ValidateHostCatalog(
            _package, _scope, catalog);
        if (!evidenceCheck.Accepted)
            return evidenceCheck;
        return GateCContractValidator.ValidateIndependent(
            assessment, _runId, _scope, catalog);
    }

    public string GraphAnalyzerVersion => _package.GraphAnalyzerVersion;

    public int ToolCallsUsed => _calls;

    /// <summary>Host-only links for persisted, role-scoped references.
    /// These are never sent to a model. Only issued handles are included.</summary>
    public IReadOnlyList<GateC5EvidenceLink> ReportLinks()
    {
        var references = _package.References.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var relations = _package.Relations.ToDictionary(x => x.Id, StringComparer.Ordinal);
        return _handles.Values.OrderBy(x => x.Handle, StringComparer.Ordinal)
            .Select(handle =>
            {
                if (references.TryGetValue(handle.CanonicalId, out var node))
                    return new GateC5EvidenceLink(_role, handle.Handle, handle.CanonicalId,
                        handle.Kind, node.Kind, node.EvidenceStatus, node.FilePath,
                        node.FileSha256, node.Line, node.Column);
                if (relations.TryGetValue(handle.CanonicalId, out var edge))
                    return new GateC5EvidenceLink(_role, handle.Handle, handle.CanonicalId,
                        handle.Kind, edge.Kind, edge.EvidenceStatus, edge.EvidencePath,
                        edge.EvidenceSha256, edge.Line, edge.Column);
                throw new InvalidOperationException("Issued handle lacks canonical evidence.");
            }).ToArray();
    }

    /// <summary>
    /// Host-only catalog for C1 validation; never send CanonicalId to agents.
    /// Return an immutable copy to keep cross-role mappings separate.
    /// </summary>
    public IReadOnlyList<GateCEvidenceHandle> HostCatalog() =>
        Array.AsReadOnly(_handles.Values.ToArray());

    public GateCDiscoveryResult Request(string? capability, CancellationToken ct)
    {
        if (!GateCEvidenceRegistry.Supports(capability))
            return Deny("unknown-or-unauthorized-capability");
        if (ct.IsCancellationRequested)
            return Deny("discovery-cancelled");
        if (_calls >= MaxToolCalls)
            return Deny("tool-call-budget-exceeded");
        // Charge every authorized call even when its result is empty.
        _calls++;
        var selected = SelectItems(capability!);
        var results = new List<GateCDiscoveredItem>();
        foreach (var item in selected.Take(12))
        {
            ct.ThrowIfCancellationRequested();
            // Reuse existing handles when views overlap or are repeated.
            // Otherwise model-facing handles could alias one canonical edge
            // and break the independent host catalog invariant.
            var known = _handles.Values.FirstOrDefault(h => h.CanonicalId == item.Id);
            if (known is null && _itemsIssued >= MaxTotalItems)
                return Deny("evidence-item-budget-exceeded");
            var handle = known?.Handle ?? "E" + (_handles.Count + 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            if (known is not null && known.Kind != item.Status)
                throw new InvalidOperationException("Evidence status changed within a role session.");
            if (known is null)
            {
                _handles.Add(handle,
                    new GateCEvidenceHandle(handle, item.Id, item.Status));
                _itemsIssued++;
            }
            results.Add(new GateCDiscoveredItem(handle, item.Kind,
                item.Status, item.Label, item.FilePath, item.Line, item.Column));
        }
        return new(GateCContractCheck.Pass(),
            new GateCDiscoveryResponse(capability!, GateCEvidenceRegistry.Version,
                _runId, _role, _scope.FingerprintSha256, results,
                _package.Unknowns.Take(8).ToArray(),
                selected.Count > results.Count, _calls));
    }

    private sealed record Item(string Id, string Kind, GateCEvidenceKind Status,
        string Label, string? FilePath, int? Line, int? Column);

    private IReadOnlyList<Item> SelectItems(string capability)
    {
        var refs = _package.References.ToDictionary(r => r.Id, StringComparer.Ordinal);
        IEnumerable<AgentEvidenceRelation> edges = capability switch
        {
            GateCEvidenceRegistry.EntryPoint => _package.Relations.Where(r =>
                r.FromId == _scope.EntryPointId &&
                r.EvidenceStatus == "candidate" &&
                r.Kind is ("annotates-method" or "candidate-http-mapping")),
            GateCEvidenceRegistry.DirectInvocations => _package.Relations.Where(r =>
                r.Kind == "contains-invocation" && r.EvidenceStatus == "observed-syntax"),
            GateCEvidenceRegistry.CandidateCallees => _package.Relations.Where(r =>
                r.Kind == "candidate-callee" && r.EvidenceStatus == "provisional-local"),
            _ => _package.Relations
        };
        return edges.OrderBy(r => r.Id, StringComparer.Ordinal)
            .Select(r =>
            {
                if (!refs.ContainsKey(r.FromId) || !refs.ContainsKey(r.ToId))
                    throw new InvalidOperationException("Evidence relationship endpoints are absent.");
                return new Item(r.Id, r.Kind, r.EvidenceStatus switch
                {
                    "observed-syntax" => GateCEvidenceKind.ObservedSyntax,
                    "candidate" => GateCEvidenceKind.Candidate,
                    "provisional-local" => GateCEvidenceKind.ProvisionalLocal,
                    _ => throw new InvalidOperationException("Unsupported evidence status.")
                }, r.Explanation, r.EvidencePath, r.Line, r.Column);
            }).ToArray();
    }

    private GateCDiscoveryResult Deny(string reason) =>
        new(GateCContractCheck.Fail(reason), null);
}

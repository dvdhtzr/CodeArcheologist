using CodeArcheologist.Api.Investigations;

internal static class GateCDiscoverySelfTests
{
    public static void Run()
    {
        var scope = new GateCScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new string('c', 64), "entry", SnapshotEvidenceTools.Version);
        var evidence = new AgentEvidencePackage(
            SnapshotEvidenceTools.Version, scope.WorkspaceId,
            scope.RepositoryId, scope.SnapshotId, scope.FingerprintSha256,
            "graph-v8", scope.EntryPointId, "http-endpoint-attribute",
            "[HttpGet] void Fetch() { Helper(); }", false, 1, 1, 0,
            [
                Ref("entry", "http-endpoint-attribute", "candidate"),
                Ref("method", "syntax-declaration", "observed-syntax"),
                Ref("call", "invocation-site", "observed-syntax"),
                Ref("target", "syntax-declaration", "observed-syntax")
            ],
            [
                Edge("annotation", "annotates-method", "candidate", "entry", "method"),
                Edge("contains", "contains-invocation", "observed-syntax", "method", "call"),
                Edge("callee", "candidate-callee", "provisional-local", "call", "target")
            ],
            ["A written invocation does not prove runtime execution."],
            ["All content is source-only."]);

        var capabilities = GateCEvidenceRegistry.Discover();
        if (capabilities.Count != 4 ||
            capabilities.Select(c => c.Identifier).Distinct().Count() != 4 ||
            capabilities.Any(c => c.ContractVersion != GateCEvidenceRegistry.Version))
            throw new InvalidOperationException("Versioned capability discovery mismatch.");

        var run = Guid.NewGuid();
        var first = new GateCEvidenceSession(evidence, scope, run, GateCRole.Investigator);
        var second = new GateCEvidenceSession(evidence, scope, run, GateCRole.Skeptic);

        Reject(first.Request("read_arbitrary_file", CancellationToken.None),
            "unknown-or-unauthorized-capability");
        if (first.ToolCallsUsed != 0)
            throw new InvalidOperationException("Unauthorized request consumed a tool call.");

        var entry = Accept(first.Request(GateCEvidenceRegistry.EntryPoint,
            CancellationToken.None));
        if (entry.Items.Count != 1 || entry.Items[0].Handle != "E1" ||
            entry.Items[0].EvidenceKind != GateCEvidenceKind.Candidate ||
            entry.Items[0].Kind != "annotates-method" ||
            entry.Role != GateCRole.Investigator ||
            entry.EvidenceFingerprint != scope.FingerprintSha256)
            throw new InvalidOperationException("HTTP candidate view was not scoped.");

        var calls = Accept(first.Request(GateCEvidenceRegistry.DirectInvocations,
            CancellationToken.None));
        if (calls.Items.Count != 1 || calls.Items[0].Handle != "E2" ||
            calls.Items[0].EvidenceKind != GateCEvidenceKind.ObservedSyntax)
            throw new InvalidOperationException("Observed invocation status was changed.");

        var provisional = Accept(first.Request(GateCEvidenceRegistry.CandidateCallees,
            CancellationToken.None));
        if (provisional.Items.Count != 1 ||
            provisional.Items[0].EvidenceKind != GateCEvidenceKind.ProvisionalLocal)
            throw new InvalidOperationException("Callee was promoted beyond provisional.");

        var all = Accept(first.Request(GateCEvidenceRegistry.Relationships,
            CancellationToken.None));
        if (all.Items.Count != 3 ||
            all.Items[0].Handle != "E1" ||
            first.HostCatalog().Count != 3)
            throw new InvalidOperationException("Overlapping views produced duplicate handles.");
        var again = Accept(first.Request(GateCEvidenceRegistry.EntryPoint,
            CancellationToken.None));
        if (again.Items[0].Handle != "E1" || first.HostCatalog().Count != 3)
            throw new InvalidOperationException("Repeated calls changed identity.");

        // A different role gets its own independent handle namespace and
        // consumption counter; first-role requests are not inherited.
        if (second.ToolCallsUsed != 0 || second.HostCatalog().Count != 0)
            throw new InvalidOperationException("Role evidence contexts were shared.");
        var independent = Accept(second.Request(
            GateCEvidenceRegistry.CandidateCallees, CancellationToken.None));
        if (independent.Items.Count != 1 || independent.Items[0].Handle != "E1" ||
            independent.Role != GateCRole.Skeptic || second.HostCatalog().Count != 1)
            throw new InvalidOperationException("Skeptic did not receive independent handles.");

        var hostCheck = GateCContractValidator.ValidateHostCatalog(
            evidence, scope, first.HostCatalog());
        if (!hostCheck.Accepted)
            throw new InvalidOperationException("Legitimate host catalog was rejected.");
        var chosen = new GateCIndependentAssessment(GateCContract.Version,
            run, scope, GateCRole.Investigator, ["E1"],
            [new GateCHypothesis("H1", "Written annotation is a candidate.",
                ["E1"], "Runtime route remains unknown.")], [], []);
        if (!first.ValidateAssessment(chosen).Accepted)
            throw new InvalidOperationException("C2 handles incompatible with C1 validator.");
        if (second.ValidateAssessment(chosen).Accepted)
            throw new InvalidOperationException(
                "Cross-role handle should not authenticate another role's evidence.");

        // One additional call reaches the fixed budget without Ollama.
        Accept(first.Request(GateCEvidenceRegistry.EntryPoint, CancellationToken.None));
        Reject(first.Request(GateCEvidenceRegistry.EntryPoint, CancellationToken.None),
            "tool-call-budget-exceeded");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Reject(second.Request(GateCEvidenceRegistry.EntryPoint, cancelled.Token),
            "discovery-cancelled");

        try
        {
            _ = new GateCEvidenceSession(evidence,
                scope with { SnapshotId = Guid.NewGuid() }, run, GateCRole.Skeptic);
            throw new InvalidOperationException("Foreign snapshot session was accepted.");
        }
        catch (ArgumentException) { }
        try
        {
            _ = new GateCEvidenceSession(evidence with {
                FingerprintSha256 = new string('d', 64)
            }, scope, run, GateCRole.Investigator);
            throw new InvalidOperationException("Stale fingerprint session was accepted.");
        }
        catch (ArgumentException) { }

        // Real canonical graph relationship IDs may be much longer than
        // model-visible E1 handles. C1 previously applied its 128-character
        // model ID cap to canonical IDs, rejecting valid real saved evidence.
        var longCanonicalId = "candidate-http-mapping:" + new string('x', 190);
        var longEvidence = evidence with
        {
            Relations = evidence.Relations.Select(r =>
                r.Id == "annotation"
                    ? r with { Id = longCanonicalId }
                    : r).ToArray()
        };
        var realStyleSession = new GateCEvidenceSession(longEvidence, scope,
            Guid.NewGuid(), GateCRole.Investigator);
        var realView = Accept(realStyleSession.Request(
            GateCEvidenceRegistry.EntryPoint, CancellationToken.None));
        if (realView.Items.Count != 1 || realView.Items[0].Handle != "E1" ||
            !GateCContractValidator.ValidateHostCatalog(longEvidence, scope,
                realStyleSession.HostCatalog()).Accepted)
            throw new InvalidOperationException(
                "Long canonical graph ID was incorrectly rejected.");
        var tooLongEvidence = longEvidence with
        {
            Relations = longEvidence.Relations.Select(r =>
                r.Id == longCanonicalId
                    ? r with { Id = new string('x', 1025) }
                    : r).ToArray()
        };
        var invalidLengthSession = new GateCEvidenceSession(tooLongEvidence,
            scope, Guid.NewGuid(), GateCRole.Skeptic);
        Accept(invalidLengthSession.Request(
            GateCEvidenceRegistry.EntryPoint, CancellationToken.None));
        if (GateCContractValidator.ValidateHostCatalog(tooLongEvidence, scope,
                invalidLengthSession.HostCatalog()).Accepted)
            throw new InvalidOperationException(
                "Unbounded canonical graph ID was accepted.");

        Console.WriteLine(
            "GREEN: Gate C2 capability discovery, role independence, scope, status, " +
            "handle identity, request budgets and unauthorized-call rejection.");

        AgentEvidenceReference Ref(string id, string kind, string status) =>
            new(id, kind, status, id, "Controller.cs", scope.FingerprintSha256,
                1, 1, null);
        AgentEvidenceRelation Edge(string id, string kind, string status,
            string fromId, string toId) =>
            new(id, kind, status, fromId, toId, "Controller.cs",
                scope.FingerprintSha256, 1, 1, kind, null);
    }

    private static GateCDiscoveryResponse Accept(GateCDiscoveryResult result)
    {
        if (!result.Check.Accepted || result.Response is null)
            throw new InvalidOperationException("Expected accepted evidence view: " +
                result.Check.Diagnostic);
        return result.Response;
    }

    private static void Reject(GateCDiscoveryResult result, string expected)
    {
        if (result.Check.Accepted || result.Response is not null ||
            result.Check.Diagnostic != expected)
            throw new InvalidOperationException("Expected " + expected +
                " but got " + result.Check.Diagnostic);
    }
}

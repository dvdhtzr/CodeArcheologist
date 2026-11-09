using System.Text.Json;
using CodeArcheologist.Api.Investigations;

internal static class GateBSelfTests
{
    public static void Run()
    {
        var snapshotId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var fingerprint = new string('a', 64);
        var entry = "http-attribute:src/Controller.cs:a:10:5";
        var target = "method:src/Controller.cs:a:20:15";
        var relationId = "annotates:" + entry;
        var package = new AgentEvidencePackage(
            SnapshotEvidenceTools.Version, Guid.NewGuid(), Guid.NewGuid(),
            snapshotId, fingerprint, "graph-v8", entry,
            "http-endpoint-attribute", "[HttpGet] public void Show() {}",
            false, 0, 0, 0,
            [
                new(entry, "http-endpoint-attribute", "candidate", "GET",
                    "src/Controller.cs", fingerprint, 1, 1, null),
                new(target, "syntax-declaration", "observed-syntax", "Show",
                    "src/Controller.cs", fingerprint, 2, 1, null)
            ],
            [
                new(relationId, "annotates-method", "candidate", entry, target,
                    "src/Controller.cs", fingerprint, 1, 1, "candidate", null),
                new("observed-containment", "contains-invocation", "observed-syntax",
                    entry, target, "src/Controller.cs", fingerprint, 2, 1,
                    "observed context only", null),
                new("provisional-callee", "candidate-callee", "provisional-local",
                    entry, target, "src/Controller.cs", fingerprint, 2, 1,
                    "provisional context only", null)
            ],
            ["Runtime state is not known"], ["Only source syntax was inspected"]);

        var accepted = new
        {
            snapshotId = snapshotId.ToString("D"),
            fingerprintSha256 = fingerprint,
            selectedHandles = new[] { "R1" },
            nextQuestion = "verify-effective-route"
        };
        var validJson = JsonSerializer.Serialize(accepted);

        // The agent sees short handles, not copied opaque identity strings.
        using (var projection = JsonDocument.Parse(
            GateBInvestigator.BuildAgentEvidenceJson(package)))
        {
            var root = projection.RootElement;
            if (root.TryGetProperty("Relations", out _) ||
                root.TryGetProperty("EntryPointId", out _) ||
                root.TryGetProperty("References", out _) ||
                root.GetProperty("eligibleRelations").GetArrayLength() != 1)
                throw new InvalidOperationException(
                    "Agent must only receive scoped selectable handles.");
            var edge = root.GetProperty("eligibleRelations")[0];
            if (edge.GetProperty("Handle").GetString() != "R1" ||
                edge.GetProperty("Kind").GetString() != "annotates-method" ||
                edge.TryGetProperty("Id", out _) ||
                edge.TryGetProperty("FromId", out _) ||
                edge.TryGetProperty("ToId", out _))
                throw new InvalidOperationException(
                    "Agent projection exposed canonical IDs or wrong handle.");
            if (root.GetProperty("contextReferences").GetArrayLength() != 2)
                throw new InvalidOperationException(
                    "Agent context was removed.");
        }

        var valid = GateBInvestigator.ValidateModelText(validJson, package);
        if (valid.Outcome != "valid-structural-claim" ||
            valid.Result is null ||
            valid.Result.Findings.Count != 1 ||
            valid.Result.Findings[0].RelationId != relationId ||
            valid.Result.Findings[0].FromId != entry ||
            valid.Result.Findings[0].ToId != target ||
            valid.Result.SuggestedNextQuestion !=
                "Which effective HTTP route, if any, is registered at runtime?")
            throw new InvalidOperationException(
                "Known handle did not resolve to the full verified source edge.");

        Expect(null, "empty-model-response");
        Expect("", "empty-model-response");
        Expect("not JSON", "invalid-json-or-extra-text");
        Expect(validJson + " trailing", "invalid-json-or-extra-text");
        Expect(new string('x', 8193), "oversize-model-response");
        Expect(Alter(root => root["snapshotId"] = Guid.NewGuid().ToString("D")),
            "stale-or-foreign-snapshot");
        Expect(Alter(root => root["fingerprintSha256"] = new string('b', 64)),
            "stale-or-foreign-snapshot");
        Expect(Alter(root => root["selectedHandles"] =
            JsonSerializer.SerializeToNode(Array.Empty<string>())),
            "invalid-handle-count");
        Expect(Alter(root => root["selectedHandles"] =
            JsonSerializer.SerializeToNode(new[] { "R1", "R1" })),
            "duplicate-handle");
        Expect(Alter(root => root["selectedHandles"] =
            JsonSerializer.SerializeToNode(new[] { "R2" })),
            "unknown-or-foreign-handle");
        Expect(Alter(root => root["selectedHandles"] =
            JsonSerializer.SerializeToNode(new[] { "R01" })),
            "unknown-or-foreign-handle");
        Expect(Alter(root => root["selectedHandles"] =
            JsonSerializer.SerializeToNode(new[] { "R0" })),
            "unknown-or-foreign-handle");
        Expect(Alter(root => root["selectedHandles"] =
            JsonSerializer.SerializeToNode(new[] { 1 })),
            "invalid-handle-shape");
        Expect(Alter(root => root["selectedHandles"] =
            JsonSerializer.SerializeToNode(new[] { "R1", "R2", "R3", "R4" })),
            "invalid-handle-count");
        Expect(Alter(root => root["nextQuestion"] = "verified-execution"),
            "unsupported-next-question");
        Expect(Alter(root => root["extra"] = "untrusted"),
            "invalid-root-shape");

        // A correct-looking local handle cannot authenticate foreign, stale,
        // invalid-status or missing-source relations. Never loosen this gate.
        ExpectWithEvidence(package with { SnapshotId = Guid.NewGuid() },
            "stale-or-foreign-snapshot");
        ExpectWithEvidence(package with { FingerprintSha256 = new string('b', 64) },
            "stale-or-foreign-snapshot");
        ExpectWithEvidence(package with
        {
            Relations = package.Relations.Select(r => r.Id == relationId
                ? r with { EvidenceStatus = "observed-syntax" } : r).ToArray()
        }, "unknown-or-foreign-handle");
        ExpectWithEvidence(package with
        {
            Relations = package.Relations.Select(r => r.Id == relationId
                ? r with { EvidenceStatus = "provisional-local" } : r).ToArray()
        }, "unknown-or-foreign-handle");
        ExpectWithEvidence(package with
        {
            Relations = package.Relations.Select(r => r.Id == relationId
                ? r with { Kind = "contains-invocation" } : r).ToArray()
        }, "unknown-or-foreign-handle");
        ExpectWithEvidence(package with
        {
            Relations = package.Relations.Select(r => r.Id == relationId
                ? r with { FromId = "foreign-entry" } : r).ToArray()
        }, "unknown-or-foreign-handle");
        ExpectWithEvidence(package with
        {
            References = package.References.Where(r => r.Id != target).ToArray()
        }, "unknown-or-foreign-handle");
        ExpectWithEvidence(package with
        {
            Relations = package.Relations.Append(package.Relations[0]).ToArray()
        }, "ambiguous-source-relation-id");

        // A second eligible candidate must receive R2 and resolve back to its
        // own IDs, independent of input enumeration order.
        var secondTarget = "method:src/Controller.cs:a:40:15";
        var secondRelation = "second-candidate";
        var two = package with
        {
            References = package.References.Append(new AgentEvidenceReference(
                secondTarget, "syntax-declaration", "observed-syntax", "Other",
                "src/Controller.cs", fingerprint, 4, 1, null)).ToArray(),
            Relations = package.Relations.Append(new AgentEvidenceRelation(
                secondRelation, "annotates-method", "candidate", entry,
                secondTarget, "src/Controller.cs", fingerprint, 4, 1,
                "second candidate", null)).Reverse().ToArray()
        };
        using (var projectedTwo = JsonDocument.Parse(
            GateBInvestigator.BuildAgentEvidenceJson(two)))
        {
            var choices = projectedTwo.RootElement.GetProperty("eligibleRelations");
            if (choices.GetArrayLength() != 2 ||
                choices[0].GetProperty("Handle").GetString() != "R1" ||
                choices[1].GetProperty("Handle").GetString() != "R2")
                throw new InvalidOperationException("Candidate handles not stable in ID order.");
        }
        var secondJson = Alter(root => root["selectedHandles"] =
            JsonSerializer.SerializeToNode(new[] { "R2" }));
        var secondResult = GateBInvestigator.ValidateModelText(secondJson, two);
        if (secondResult.Result?.Findings.Single().RelationId != secondRelation)
            throw new InvalidOperationException("Second host handle resolved wrong relation.");

        Console.WriteLine("GREEN: Gate B handle mapping, scope, invalid selections, and model-free trust checks passed.");

        void Expect(string? text, string expected)
        {
            var actual = GateBInvestigator.ValidateModelText(text, package);
            if ((actual.Result is null ? actual.Diagnostic : actual.Outcome) != expected)
                throw new InvalidOperationException(
                    $"Gate B expected {expected}, got {actual.Diagnostic}/{actual.Outcome}");
        }
        void ExpectWithEvidence(AgentEvidencePackage mutated, string expected)
        {
            var actual = GateBInvestigator.ValidateModelText(validJson, mutated);
            if ((actual.Result is null ? actual.Diagnostic : actual.Outcome) != expected)
                throw new InvalidOperationException(
                    $"Gate B mutated evidence expected {expected}, got {actual.Diagnostic}/{actual.Outcome}");
        }
        string Alter(Action<System.Text.Json.Nodes.JsonObject> change)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(validJson)!.AsObject();
            change(node);
            return node.ToJsonString();
        }
    }
}

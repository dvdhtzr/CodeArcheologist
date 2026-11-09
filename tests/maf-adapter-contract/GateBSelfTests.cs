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
                    "src/Controller.cs", fingerprint, 1, 1, "candidate", null)
            ],
            ["Runtime state is not known"], ["Only source syntax was inspected"]);

        var accepted = new
        {
            snapshotId = snapshotId.ToString("D"),
            fingerprintSha256 = fingerprint,
            summary = "An HTTP attribute candidate appears in saved source.",
            findings = new[] { new {
                statement = "The annotation is linked to a declaration as a candidate.",
                claimKind = "candidate-relationship",
                relationId, fromId = entry, toId = target
            } },
            unknowns = new[] { "Runtime endpoint registration is not established." }
        };
        var validJson = JsonSerializer.Serialize(accepted);
        Expect(validJson, "valid-structural-claim");
        Expect(null, "empty-model-response");
        Expect("", "empty-model-response");
        Expect("not JSON", "invalid-json-or-extra-text");
        Expect(validJson + " trailing", "invalid-json-or-extra-text");
        Expect(new string('x', 8193), "oversize-model-response");
        Expect(Alter(root => root["fingerprintSha256"] = new string('b', 64)),
            "stale-or-foreign-snapshot");
        Expect(Alter(root => root["snapshotId"] = Guid.NewGuid().ToString("D")),
            "stale-or-foreign-snapshot");
        Expect(Alter(root => root["unknowns"] = Array.Empty<string>()),
            "missing-or-invalid-unknowns");
        Expect(Alter(root => root["findings"] = Array.Empty<object>()),
            "invalid-finding-count");
        Expect(Alter(root => root["extra"] = "untrusted"),
            "invalid-root-shape");
        Expect(Alter(root => root["findings"][0]["relationId"] = "fabricated"),
            "unsupported-or-foreign-relationship");
        Expect(Alter(root => root["findings"][0]["fromId"] = "another-source"),
            "unsupported-or-foreign-relationship");
        Expect(Alter(root => root["findings"][0]["claimKind"] = "registered-runtime-endpoint"),
            "invalid-finding-shape");
        Expect(Alter(root => root["findings"][0]["statement"] = "x"),
            "invalid-finding-shape");
        Expect(Alter(root => root["findings"].Add(root["findings"][0].DeepClone())),
            "duplicate-relationship");
        Console.WriteLine("GREEN: Gate B model-free schema, scope and structural rejection cases passed.");

        void Expect(string? text, string expected)
        {
            var actual = GateBInvestigator.ValidateModelText(text, package);
            if ((actual.Result is null ? actual.Diagnostic : actual.Outcome) != expected)
                throw new InvalidOperationException(
                    $"Gate B expected {expected}, got {actual.Diagnostic}/{actual.Outcome}");
        }
        string Alter(Action<System.Text.Json.Nodes.JsonObject> change)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(validJson)!.AsObject();
            change(node);
            return node.ToJsonString();
        }
    }
}

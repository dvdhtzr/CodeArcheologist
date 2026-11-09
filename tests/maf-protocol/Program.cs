using System.Text.Json;
using CodeArcheologist.Api.Investigations;

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException("MAF protocol gate regression: " + message);
}

static string Payload(
    string kind = "source-observation",
    string statement = "This saved declaration exists as observed syntax.",
    string[]? evidenceIds = null,
    string[]? unknowns = null) =>
    JsonSerializer.Serialize(new
    {
        summary = "Bounded investigation: syntax candidate, not runtime proof.",
        findings = new[]
        {
            new
            {
                claimKind = kind,
                statement,
                evidenceIds = evidenceIds ?? ["declaration:source:sha:1:3"],
                unknowns = unknowns ?? ["Runtime execution remains unverified."]
            }
        }
    });

static bool Accept(string response) =>
    LocalMafInvestigation.TryParseRoleResponse(response, out _, out _);

var accepted = Payload();
Check(Accept(accepted), "valid, bounded typed finding must parse");
Check(!Accept("not JSON"), "non-JSON payload must be rejected");
Check(!Accept("{}"), "empty response must be rejected");
Check(!Accept("{\"summary\":\"too short\",\"findings\":[]}"),
    "missing findings must be rejected");
Check(!Accept(Payload(unknowns: [""])),
    "empty unknowns cannot be accepted");
Check(!Accept(Payload(evidenceIds: [""])),
    "blank evidence IDs must be rejected");
Check(!Accept(Payload(evidenceIds: ["same-id", "same-id"])),
    "duplicate citation IDs must be rejected");
Check(!Accept(Payload(statement: "short")),
    "meaningless short claim must be rejected");
Check(!Accept(Payload(statement: new string('x', 1201))),
    "oversized claim text must be rejected");
Check(!Accept(Payload(unknowns: Enumerable.Range(0, 9)
    .Select(i => "Unknown limitation " + i).ToArray())),
    "excessive unknowns must be rejected");
Check(!Accept(new string('x', 16001)),
    "excessive total response must be rejected");
var missingCitation = JsonSerializer.Serialize(new
{
    summary = "The source contains an unresolved relationship candidate.",
    findings = new[]
    {
        new
        {
            claimKind = "source-observation",
            statement = "A syntactic declaration has a source anchor.",
            unknowns = new[] { "Unresolved runtime execution." }
        }
    }
});
Check(!Accept(missingCitation),
    "missing evidenceIds property must be rejected");
Check(Accept("```json\n" + accepted + "\n```"),
    "standard fenced JSON must be accepted when otherwise valid");

// Diagnostics must classify errors without echoing untrusted response data.
Check(LocalMafInvestigation.ClassifyRoleResponseFailure(accepted) ==
    "json-structure-valid-citation-status-unavailable", "valid JSON reaches citation validation");
Check(LocalMafInvestigation.ClassifyRoleResponseFailure("not JSON") ==
    "invalid-json-or-extra-text", "non-JSON diagnostic");
Check(LocalMafInvestigation.ClassifyRoleResponseFailure(
    JsonSerializer.Serialize(new { summary = "long enough summary", findings = Array.Empty<object>() })) ==
    "findings-count-invalid", "empty findings diagnostic");
Check(LocalMafInvestigation.ClassifyRoleResponseFailure(
    Payload(evidenceIds: ["same-id", "same-id"])) ==
    "duplicate-evidence-ids", "duplicate citation diagnostic");
Check(LocalMafInvestigation.ClassifyRoleResponseFailure(
    "```json\n" + accepted + "\n```") ==
    "json-structure-valid-citation-status-unavailable", "fenced JSON diagnostic");

// Host-owned evidence delivery is separate from optional model tool calls.
var scopedEvidence = new AgentEvidencePackage(
    "v1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
    new string('a', 64), "graph-v6", "entry:verified:123",
    "http-minimal-mapping", "app.MapGet(\"/test\", handler);", false,
    0, 0, 0,
    [new AgentEvidenceReference("observed:123", "invocation-site",
        "observed-syntax", "Source anchor", "Program.cs", new string('b', 64),
        10, 3, null)],
    [], ["Handler is not verified."], ["Source is untrusted data."]);
var scopedContext = LocalMafInvestigation.BuildVerifiedEvidenceContext(scopedEvidence);
Check(scopedContext.Contains("entry:verified:123", StringComparison.Ordinal) &&
    scopedContext.Contains("observed:123", StringComparison.Ordinal),
    "host context retains exact scoped evidence identities");
Check(scopedContext.Contains("Handler is not verified.", StringComparison.Ordinal),
    "host context retains uncertainty");
try
{
    var oversized = scopedEvidence with { SourceExcerpt = new string('x', 61000) };
    LocalMafInvestigation.BuildVerifiedEvidenceContext(oversized);
    throw new InvalidOperationException("oversized host evidence was accepted");
}
catch (CodeArcheologist.Api.Workspaces.SnapshotCaptureException)
{
    // Expected: pre-model, bounded context gate.
}

Console.WriteLine("GREEN: MAF structured-role protocol negative cases passed.");

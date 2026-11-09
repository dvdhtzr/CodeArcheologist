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
    "citation-evaluation-failed", "valid JSON reaches citation validation");
Check(LocalMafInvestigation.ClassifyRoleResponseFailure("not JSON") ==
    "invalid-json-or-extra-text", "non-JSON diagnostic");
Check(LocalMafInvestigation.ClassifyRoleResponseFailure(
    "{\\"summary\\":\\"long enough summary\\",\\"findings\\":[]}") ==
    "findings-count-invalid", "empty findings diagnostic");
Check(LocalMafInvestigation.ClassifyRoleResponseFailure(
    Payload(evidenceIds: ["same-id", "same-id"])) ==
    "duplicate-evidence-ids", "duplicate citation diagnostic");
Check(LocalMafInvestigation.ClassifyRoleResponseFailure(
    "```json\\n" + accepted + "\\n```") ==
    "citation-evaluation-failed", "fenced JSON diagnostic");

Console.WriteLine("GREEN: MAF structured-role protocol negative cases passed.");

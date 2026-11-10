using System.Text.Json;
using CodeArcheologist.Api.Investigations;

internal static class GateC3SelfTests
{
    public static void Run()
    {
        var scope = new GateCScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new string('a', 64), "http-mapping:1", SnapshotEvidenceTools.Version);
        var run = Guid.NewGuid();
        if (!GateC3IndependentRunner.TrySelectCapability(
            """{"capability":"inspect_direct_invocations"}""", out var selected) ||
            selected != GateCEvidenceRegistry.DirectInvocations)
            throw new InvalidOperationException("Valid MAF tool selection was rejected.");
        if (GateC3IndependentRunner.TrySelectCapability(
            """{"capability":"read_secret"}""", out _) ||
            GateC3IndependentRunner.TrySelectCapability(
            """{"capability":"inspect_entry_point","extra":1}""", out _) ||
            GateC3IndependentRunner.TrySelectCapability(
            """{"capability":"inspect_entry_point","capability":"inspect_source_relationships"}""", out _) ||
            GateC3IndependentRunner.TrySelectCapability(
            """{"capability":1}""", out _) ||
            GateC3IndependentRunner.TrySelectCapability(
            """{"capability":"inspect_entry_point"} trailing""", out _))
            throw new InvalidOperationException("Invalid discovery model output was accepted.");

        const string investigator = """
            {"selectedHandles":["E1"],"hypotheses":[{
              "text":"Possible HTTP mapping candidate.","handle":"E1",
              "uncertainty":"Effective runtime registration is not established."
            }],"unknown":"Actual handler binding is unknown."}
            """;
        const string skeptic = """
            {"selectedHandles":["E1"],"concerns":[{
              "kind":"insufficient-evidence",
              "explanation":"Source syntax does not confirm runtime registration.",
              "handle":"E1"
            }],"unknown":"The effective route is unknown."}
            """;
        var observed = GateC3IndependentRunner.ParseAssessment(
            investigator, run, scope, GateCRole.Investigator);
        var challenged = GateC3IndependentRunner.ParseAssessment(
            skeptic, run, scope, GateCRole.Skeptic);
        if (observed?.Hypotheses.Count != 1 ||
            observed.Hypotheses[0].Id != "H1" ||
            observed.Role != GateCRole.Investigator ||
            challenged?.Concerns.Count != 1 ||
            challenged.Concerns[0].Id != "S1" ||
            challenged.Role != GateCRole.Skeptic)
            throw new InvalidOperationException("Independent assessment parsing failed.");
        if (GateC3IndependentRunner.ParseAssessment(investigator, run, scope,
                GateCRole.Skeptic) is not null ||
            GateC3IndependentRunner.ParseAssessment(skeptic, run, scope,
                GateCRole.Investigator) is not null)
            throw new InvalidOperationException("Role schema crossover was accepted.");

        // Syntax-only parser cannot know the host session's handle catalog;
        // this negative case is enforced by the C1 validator.
        var foreign = GateC3IndependentRunner.ParseAssessment(
            Replace(investigator, "E1", "foreign"), run, scope,
            GateCRole.Investigator);
        if (foreign is null || GateCContractValidator.ValidateIndependent(
                foreign, run, scope,
                [new GateCEvidenceHandle("E1", "original",
                    GateCEvidenceKind.Candidate)]).Accepted)
            throw new InvalidOperationException("Foreign handle bypassed host validator.");
        Reject(Replace(investigator, "\"selectedHandles\":[\"E1\"]",
            "\"selectedHandles\":[]"));
        Reject(investigator.Replace("\"uncertainty\"", "\"confidence\"",
            StringComparison.Ordinal));
        Reject(investigator.Replace("\"unknown\"", "\"unknown\":\"override\",\"unknown\"",
            StringComparison.Ordinal));
        Reject(investigator + " injected");
        Reject(new string('x', 8193));
        if (GateC3IndependentRunner.ParseAssessment(investigator,
            Guid.Empty, scope, GateCRole.Investigator) is not null)
            throw new InvalidOperationException("Empty run identity was accepted.");

        // Provider outputs omit canonical identities entirely; exact run/scope
        // are host-attached, and the existing C1/C2 gates validate handles.
        var empty = GateC3IndependentRunner.ParseAssessment(
            """{"selectedHandles":[],"hypotheses":[],"unknown":"No supported evidence."}""",
            run, scope, GateCRole.Investigator);
        if (empty is null || empty.Hypotheses.Count != 0 ||
            empty.SelectedEvidenceHandles.Count != 0)
            throw new InvalidOperationException("Honest inconclusive assessment rejected.");

        Console.WriteLine("GREEN: Gate C3 role-specific output parsing and model-free negative contracts.");

        void Reject(string text)
        {
            if (GateC3IndependentRunner.ParseAssessment(text, run,
                    scope, GateCRole.Investigator) is not null)
                throw new InvalidOperationException("Invalid assessment was accepted.");
        }
        static string Replace(string value, string oldText, string newText) =>
            value.Replace(oldText, newText, StringComparison.Ordinal);
    }
}

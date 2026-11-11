using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace CodeArcheologist.EvaluationLab;

/// <summary>Model-free, snapshot-backed positive/negative/adversarial caution contracts.</summary>
internal static class HostCautionSelfTests
{
    public static async Task RunAsync(string evalRoot)
    {
        await Check("unobserved-registration",
            "class Routes { public void Register(dynamic app) { app.MapPost(\"/a\", () => 1); } }",
            "http-minimal-mapping", 1, ["unobserved-registration"]);

        await Check("top-level-registration",
            "var app = WebApplication.Create();\napp.MapPost(\"/a\", () => 1);",
            "http-minimal-mapping", 2, []);

        await Check("direct-group",
            "var app = WebApplication.Create();\napp.MapGroup(\"/x\").MapPost(\"/a\", () => 1);",
            "http-minimal-mapping", 2, ["route-group-receiver"]);

        await Check("local-group",
            "var app = WebApplication.Create();\nvar group = app.MapGroup(\"/x\");\ngroup.MapPost(\"/a\", () => 1);",
            "http-minimal-mapping", 3, ["route-group-receiver"]);

        await Check("reassigned-group",
            "var app = WebApplication.Create();\nvar group = app.MapGroup(\"/x\");\ngroup = other;\ngroup.MapPost(\"/a\", () => 1);",
            "http-minimal-mapping", 4, []);

        await Check("comment-and-string",
            "var app = WebApplication.Create();\n// app.MapGroup(\"/x\")\napp.MapPost(\"/a\", () => \"MapGroup('/x')\");",
            "http-minimal-mapping", 3, []);

        await Check("sibling-modifiers",
            "var app = WebApplication.Create();\napp.MapGet(\"/a\", () => 1).RequireAuthorization();\napp.MapPost(\"/b\", () => 2);",
            "http-minimal-mapping", 2, ["sibling-modifier-difference"]);

        await Check("separate-registration-scopes",
            "class Routes { void First(dynamic app) { app.MapGet(\"/a\", () => 1).RequireAuthorization(); }\n" +
            "void Second(dynamic app) { app.MapPost(\"/b\", () => 2); } }",
            "http-minimal-mapping", 1, ["unobserved-registration"]);

        await Check("conditional-body",
            "class Api { [HttpPost] public void Create() { if (Valid()) { Save(); } }\n" +
            "bool Valid() => true; void Save() {} }",
            "http-endpoint-attribute", 1, ["conditional-invocation"]);

        await Check("conditional-guard-return",
            "class Api { [HttpPost] public void Create() { if (!Valid()) return; Save(); }\n" +
            "bool Valid() => true; void Save() {} }",
            "http-endpoint-attribute", 1, ["conditional-invocation"]);

        await Check("condition-is-not-branch",
            "class Api { [HttpPost] public void Create() { if (Valid()) { } Save(); }\n" +
            "bool Valid() => true; void Save() {} }",
            "http-endpoint-attribute", 1, []);

        await Check("multiple-static-projects",
            "var app = WebApplication.Create();\napp.MapGet(\"/a\", () => 1);",
            "http-minimal-mapping", 2, ["multiple-project-candidates"],
            new Dictionary<string, string>
            {
                ["One/One.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                ["Two/Two.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />"
            });

        await Check("one-static-project",
            "var app = WebApplication.Create();\napp.MapGet(\"/a\", () => 1);",
            "http-minimal-mapping", 2, [],
            new Dictionary<string, string>
            {
                ["One/One.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\" />"
            });

        // Existing owner-approved descriptions are test data only. They must
        // never be interpolated into or used to select production caution text.
        var approved = CorpusValidator.Validate(evalRoot).Cases
            .SelectMany(item => item.Labels.MaterialIssues.Select(issue => issue.Description))
            .Where(text => text.Length >= 20).ToArray();
        foreach (var statement in AllStatements)
            if (approved.Any(label => statement.Contains(label, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Host caution wording copied an approved issue label.");

        Console.WriteLine("GREEN: host-owned syntax caution positive, negative, adversarial, source-anchor, model-isolation and label-independence contracts.");
    }

    private static readonly HashSet<string> AllStatements = new(StringComparer.Ordinal);

    private static async Task Check(
        string name, string code, string entryKind, int entryLine,
        string[] expected, IReadOnlyDictionary<string, string>? additional = null)
    {
        var oldData = Environment.GetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR");
        var basePath = Path.Combine(Path.GetTempPath(),
            "CodeArcheologist-HostCaution-" + Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(basePath, "source");
        var dataPath = Path.Combine(basePath, "data");
        Directory.CreateDirectory(sourcePath);
        File.WriteAllText(Path.Combine(sourcePath, "Program.cs"), code);
        foreach (var (path, content) in additional ?? new Dictionary<string, string>())
        {
            var destination = Path.Combine(sourcePath, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, content);
        }
        try
        {
            var (services, _, _, _, snapshot, graph) =
                await LabHarness.CaptureSourceAsync(sourcePath, dataPath, "Caution test", CancellationToken.None);
            await using (services)
            {
                var entry = graph.Nodes.Single(node =>
                    node.Kind == entryKind && node.FilePath == "Program.cs" && node.Line == entryLine);
                var evidence = services.GetRequiredService<SnapshotEvidenceTools>()
                    .ReadEntryPoint(snapshot, entry.Id, CancellationToken.None);
                if (evidence.HostCautionAnalyzerVersion != CSharpHostCautionAnalyzer.Version ||
                    evidence.HostCautions is null)
                    throw new InvalidOperationException(name + ": missing versioned caution collection.");

                var actual = evidence.HostCautions.Select(c => c.Kind)
                    .Order(StringComparer.Ordinal).ToArray();
                if (!actual.SequenceEqual(expected.Order(StringComparer.Ordinal)))
                    throw new InvalidOperationException(name + ": expected [" +
                        string.Join(", ", expected) + "], found [" + string.Join(", ", actual) + "].");

                var manifest = services.GetRequiredService<SnapshotCaptureService>()
                    .ReadManifest(snapshot);
                foreach (var caution in evidence.HostCautions)
                {
                    AllStatements.Add(caution.Statement);
                    if (caution.Status != CSharpHostCautionAnalyzer.Status ||
                        caution.AnalyzerVersion != CSharpHostCautionAnalyzer.Version ||
                        !caution.Id.Contains(entry.Id, StringComparison.Ordinal) ||
                        caution.Anchors.Count == 0 ||
                        !caution.Anchors.Any(a => a.FilePath == "Program.cs"))
                        throw new InvalidOperationException(name + ": caution provenance is invalid.");
                    foreach (var anchor in caution.Anchors)
                        if (!manifest.Files.Any(file => file.Path == anchor.FilePath &&
                                file.Sha256 == anchor.FileSha256) ||
                            anchor.Line is < 1 || anchor.Column is < 1 ||
                            anchor.SpanStart is < 0 || anchor.SpanLength is < 1)
                            throw new InvalidOperationException(name + ": unverified caution source anchor.");
                }

                var serializedForGateB = GateBInvestigator.BuildAgentEvidenceJson(evidence);
                if (serializedForGateB.Contains("host-syntax-caution", StringComparison.Ordinal) ||
                    serializedForGateB.Contains("host-caution:", StringComparison.Ordinal))
                    throw new InvalidOperationException(name + ": a host caution leaked into Gate B.");

                var scope = new GateCScope(snapshot.WorkspaceId, snapshot.RepositoryId, snapshot.Id,
                    snapshot.FingerprintSha256, entry.Id, SnapshotEvidenceTools.Version);
                var session = new GateCEvidenceSession(evidence, scope, Guid.NewGuid(), GateCRole.Investigator);
                var view = session.Request(GateCEvidenceRegistry.Relationships, CancellationToken.None);
                if (!view.Check.Accepted || view.Response is null ||
                    view.Response.Items.Any(i => i.Kind.Contains("caution", StringComparison.OrdinalIgnoreCase)) ||
                    view.Response.Unknowns.Any(u => u.Contains("host-caution:", StringComparison.Ordinal)))
                    throw new InvalidOperationException(name + ": a host caution leaked into Gate C.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", oldData);
            if (Directory.Exists(basePath)) Directory.Delete(basePath, recursive: true);
        }
    }
}

using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;
using Microsoft.Extensions.DependencyInjection;

internal static class GateC3LocalRun
{
    /// <summary>
    /// Explicit, real MAF/Ollama probe. Not part of model-free check.ps1.
    /// Uses an already captured immutable snapshot; never touches its
    /// registered mutable source or a real repository build.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 2 or > 3 ||
            args[0] != "--gate-c3-run" ||
            !int.TryParse(args[1], out var attempts) ||
            attempts is < 1 or > 5 ||
            (args.Length == 3 && args[2] is not
                ("any" or "http-minimal-mapping" or "http-endpoint-attribute")))
        {
            Console.Error.WriteLine(
                "Usage: --gate-c3-run <attempts 1..5> [any|http-minimal-mapping|http-endpoint-attribute]");
            return 2;
        }
        var kind = args.Length == 3 ? args[2] : "any";
        using var services = new ServiceCollection()
            .AddSingleton<WorkspaceStore>()
            .AddSingleton<SnapshotCaptureService>()
            .AddSingleton<CSharpSyntaxInspector>()
            .AddSingleton<CSharpInvocationSyntaxInspector>()
            .AddSingleton<CSharpProvisionalInvocationBinder>()
            .AddSingleton<CSharpHttpAttributeInspector>()
            .AddSingleton<CSharpMinimalApiInspector>()
            .AddSingleton<CSharpDiRegistrationInspector>()
            .AddSingleton<CSharpTypeRelationshipInspector>()
            .AddSingleton<StaticCSharpProjectInspector>()
            .AddSingleton<CSharpProjectLinkContextInspector>()
            .AddSingleton<CSharpEvidenceGraphBuilder>()
            .AddSingleton<SnapshotEvidenceTools>()
            .AddSingleton<GateCEvidenceDiscovery>()
            .AddSingleton<GateC3IndependentRunner>()
            .BuildServiceProvider();
        var store = services.GetRequiredService<WorkspaceStore>();
        store.Initialize();
        var graphBuilder = services.GetRequiredService<CSharpEvidenceGraphBuilder>();
        var tools = services.GetRequiredService<SnapshotEvidenceTools>();

        SnapshotRecord? selectedSnapshot = null;
        string? selectedEntry = null;
        var inspected = 0;
        foreach (var workspace in store.List(100).Items)
        {
            foreach (var repository in store.ListRepositories(workspace.Id).Items)
            {
                foreach (var snapshot in store.ListSnapshots(
                    workspace.Id, repository.Id).Items)
                {
                    if (inspected++ >= 100) break;
                    try
                    {
                        var graph = graphBuilder.Build(snapshot, CancellationToken.None);
                        foreach (var candidate in graph.Nodes
                            .Where(n => n.Kind is
                                "http-minimal-mapping" or "http-endpoint-attribute")
                            .Where(n => kind == "any" || n.Kind == kind)
                            .OrderBy(n => n.Id, StringComparer.Ordinal))
                        {
                            var package = tools.ReadEntryPoint(snapshot, candidate.Id,
                                CancellationToken.None);
                            if (!package.Relations.Any(r =>
                                    r.FromId == candidate.Id &&
                                    r.EvidenceStatus == "candidate" &&
                                    r.Kind is ("annotates-method" or "candidate-http-mapping")))
                                continue;
                            selectedSnapshot = snapshot;
                            selectedEntry = candidate.Id;
                            break;
                        }
                    }
                    catch (SnapshotCaptureException) { }
                    catch (IOException) { }
                    if (selectedSnapshot is not null) break;
                }
                if (selectedSnapshot is not null) break;
            }
            if (selectedSnapshot is not null) break;
        }
        if (selectedSnapshot is null || selectedEntry is null)
        {
            Console.Error.WriteLine("RED: no eligible saved HTTP candidate in first 100 snapshots.");
            return 1;
        }
        var snapshotScope = new GateCScope(
            selectedSnapshot.WorkspaceId, selectedSnapshot.RepositoryId,
            selectedSnapshot.Id, selectedSnapshot.FingerprintSha256,
            selectedEntry, SnapshotEvidenceTools.Version);
        var runner = services.GetRequiredService<GateC3IndependentRunner>();
        Console.WriteLine("Real MAF C3 independent roles; snapshot: " +
            selectedSnapshot.Id.ToString("D"));
        Console.WriteLine("C3 does not run the Reviewer or mark a full review completed.");
        var failures = 0;
        for (var i = 1; i <= attempts; i++)
        {
            var result = await runner.RunAsync(selectedSnapshot,
                snapshotScope, Guid.NewGuid(), CancellationToken.None);
            var valid = result.Investigator.Status ==
                    GateCRoleStatus.CompletedValidated &&
                result.Skeptic.Status == GateCRoleStatus.CompletedValidated &&
                result.Progress.Status == GateCRunStatus.IndependentAssessment &&
                result.Investigator.Assessment is not null &&
                result.Skeptic.Assessment is not null;
            Console.WriteLine("Attempt " + i + "/" + attempts + ": " +
                (valid ? "valid-independent-assessments" : "INCOMPLETE") +
                "; Investigator=" + result.Investigator.Diagnostic +
                "; Skeptic=" + result.Skeptic.Diagnostic);
            if (i == 1)
            {
                Console.WriteLine("Investigator hypotheses: " +
                    (result.Investigator.Assessment?.Hypotheses.Count ?? 0) +
                    "; evidence calls: " + result.Investigator.ToolCallsUsed);
                Console.WriteLine("Skeptic concerns: " +
                    (result.Skeptic.Assessment?.Concerns.Count ?? 0) +
                    "; evidence calls: " + result.Skeptic.ToolCallsUsed);
                Console.WriteLine("Reminder: references valid != semantic proof.");
            }
            if (!valid) failures++;
        }
        Console.WriteLine(failures == 0
            ? "GREEN: C3 genuine independent MAF roles passed all requested attempts."
            : "RED: C3 independent MAF run incomplete in " + failures +
              " of " + attempts + " attempts.");
        return failures == 0 ? 0 : 1;
    }
}

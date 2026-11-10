using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;
using Microsoft.Extensions.DependencyInjection;

internal static class GateC4LocalRun
{
    /// <summary>
    /// Explicit, real MAF/Ollama probe. Not part of model-free check.ps1.
    /// Uses an already captured immutable snapshot; never touches its
    /// registered mutable source or a real repository build.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 2 or > 3 ||
            args[0] != "--gate-c4-run" ||
            !int.TryParse(args[1], out var attempts) ||
            attempts is < 1 or > 5 ||
            (args.Length == 3 && args[2] is not
                ("any" or "http-minimal-mapping" or "http-endpoint-attribute")))
        {
            Console.Error.WriteLine(
                "Usage: --gate-c4-run <attempts 1..5> [any|http-minimal-mapping|http-endpoint-attribute]");
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
            .AddSingleton<GateC4ReconciliationRunner>()
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
        var runner = services.GetRequiredService<GateC4ReconciliationRunner>();
        Console.WriteLine("Real MAF C4 three-role reconciliation; snapshot: " +
            selectedSnapshot.Id.ToString("D"));
        Console.WriteLine("C4 completion means workflow validity, never semantic proof.");
        var failures = 0;
        for (var i = 1; i <= attempts; i++)
        {
            var result = await runner.RunAsync(selectedSnapshot,
                snapshotScope, Guid.NewGuid(), CancellationToken.None);
            var valid = result.Progress.Status == GateCRunStatus.Completed &&
                result.Independent.Investigator.Status == GateCRoleStatus.CompletedValidated &&
                result.Independent.Skeptic.Status == GateCRoleStatus.CompletedValidated &&
                result.Reviewer.Status == GateCRoleStatus.CompletedValidated &&
                result.Reviewer.Reconciliation?.Check.Accepted == true &&
                result.Reviewer.Reconciliation.Dispositions.All(d => !d.IsVerifiedFact);
            Console.WriteLine("Attempt " + i + "/" + attempts + ": " +
                (valid ? "valid-three-role-workflow" : "INCOMPLETE") +
                "; Investigator=" + result.Independent.Investigator.Diagnostic +
                "; Skeptic=" + result.Independent.Skeptic.Diagnostic +
                "; Reviewer=" + result.Reviewer.Diagnostic);
            if (i == 1)
            {
                Console.WriteLine("Investigator hypotheses: " +
                    (result.Independent.Investigator.Assessment?.Hypotheses.Count ?? 0));
                Console.WriteLine("Skeptic concerns: " +
                    (result.Independent.Skeptic.Assessment?.Concerns.Count ?? 0));
                Console.WriteLine("Host dispositions: " +
                    (result.Reviewer.Reconciliation?.Dispositions.Count ?? 0));
                Console.WriteLine("Reminder: complete workflow != semantic proof.");
            }
            if (!valid) failures++;
        }
        Console.WriteLine(failures == 0
            ? "GREEN: C4 genuine MAF Reviewer reconciliation passed all requested attempts."
            : "RED: C4 MAF Reviewer run incomplete in " + failures +
              " of " + attempts + " attempts.");
        return failures == 0 ? 0 : 1;
    }
}

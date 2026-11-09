using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;
using Microsoft.Extensions.DependencyInjection;

internal static class GateBLocalRun
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], out var attempts) ||
            attempts is < 1 or > 10)
        {
            Console.Error.WriteLine("Usage: --gate-b-run <attempts 1..10>");
            return 2;
        }

        // Trusted, direct in-process harness. No extra service, API credentials,
        // imported code execution, cloud model, or manually supplied source path.
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
            .AddSingleton<GateBInvestigator>()
            .BuildServiceProvider();

        var store = services.GetRequiredService<WorkspaceStore>();
        store.Initialize();
        var graphBuilder = services.GetRequiredService<CSharpEvidenceGraphBuilder>();
        var tools = services.GetRequiredService<SnapshotEvidenceTools>();
        var investigator = services.GetRequiredService<GateBInvestigator>();

        SnapshotRecord? selectedSnapshot = null;
        string? selectedEntryPoint = null;
        string? selectedKind = null;
        var inspected = 0;

        foreach (var workspace in store.List(100).Items)
        {
            foreach (var repository in store.ListRepositories(workspace.Id).Items)
            {
                foreach (var snapshot in store.ListSnapshots(workspace.Id, repository.Id).Items)
                {
                    if (inspected++ >= 20) break;
                    try
                    {
                        var graph = graphBuilder.Build(snapshot, CancellationToken.None);
                        foreach (var candidate in graph.Nodes
                            .Where(n => n.Kind is "http-minimal-mapping" or "http-endpoint-attribute")
                            .OrderBy(n => n.Kind == "http-minimal-mapping" ? 0 : 1)
                            .ThenBy(n => n.Id, StringComparer.Ordinal))
                        {
                            var evidence = tools.ReadEntryPoint(snapshot, candidate.Id,
                                CancellationToken.None);
                            // Match the Investigator evidence size contract before invoking
                            // the model; avoid selecting a known over-budget package.
                            if (System.Text.Json.JsonSerializer.Serialize(evidence).Length > 20000)
                                continue;
                            if (!evidence.Relations.Any(r =>
                                r.Kind is "annotates-method" or "candidate-http-mapping" &&
                                r.EvidenceStatus == "candidate" &&
                                r.FromId == candidate.Id))
                                continue;
                            selectedSnapshot = snapshot;
                            selectedEntryPoint = candidate.Id;
                            selectedKind = candidate.Kind;
                            break;
                        }
                    }
                    catch (SnapshotCaptureException)
                    {
                        // An invalid saved snapshot must never become evidence.
                    }
                    catch (IOException)
                    {
                        // Continue looking for another locally saved snapshot.
                    }
                    if (selectedSnapshot is not null) break;
                }
                if (selectedSnapshot is not null) break;
            }
            if (selectedSnapshot is not null) break;
        }

        if (selectedSnapshot is null || selectedEntryPoint is null)
        {
            Console.Error.WriteLine(
                "RED: no usable saved HTTP candidate found in first 20 saved snapshots. " +
                "Capture a repository containing a C# controller or Minimal API mapping in the UI.");
            return 1;
        }

        Console.WriteLine("Selected saved candidate: " + selectedKind);
        Console.WriteLine("Snapshot ID: " + selectedSnapshot.Id.ToString("D"));
        Console.WriteLine("Snapshot fingerprint: " + selectedSnapshot.FingerprintSha256);
        Console.WriteLine("Running independent real MAF/Ollama investigations (no retry).");

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 1; i <= attempts; i++)
        {
            GateBInvestigation run;
            try
            {
                run = await investigator.InvestigateAsync(
                    selectedSnapshot, selectedEntryPoint, CancellationToken.None);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Console.Error.WriteLine("RED: verified snapshot could not be inspected: " +
                    error.GetType().Name);
                return 1;
            }
            var label = run.Outcome + " / " + run.Diagnostic;
            counts[label] = counts.GetValueOrDefault(label) + 1;
            Console.WriteLine($"Attempt {i}/{attempts}: {label}" +
                (run.Result is null ? "" :
                    $"; findings={run.Result.Findings.Count}; unknowns={run.Result.Unknowns.Count}"));
        }

        var accepted = counts.Where(pair =>
            pair.Key.StartsWith("accepted-reference-valid-not-semantic-proof /",
                StringComparison.Ordinal)).Sum(pair => pair.Value);
        Console.WriteLine($"Gate B summary: accepted={accepted}/{attempts}");
        if (accepted != attempts)
        {
            Console.Error.WriteLine("RED: not all independent investigations passed Gate B.");
            return 1;
        }
        Console.WriteLine("GREEN: repeatable source-scoped MAF results; semantic truth NOT established.");
        return 0;
    }
}

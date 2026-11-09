using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;
using Microsoft.Extensions.DependencyInjection;

internal static class GateBLocalRun
{
    public static async Task<int> RunAsync(string[] args)
    {
        var diagnostic = args.Length == 1 && args[0] == "--gate-b-diagnose";
        var validRun = args.Length >= 2 && args.Length <= 5 &&
            args[0] == "--gate-b-run" &&
            int.TryParse(args[1], out var requested) && requested is >= 1 and <= 10;
        var kind = "any";
        Guid? excludeSnapshot = null;
        if (validRun && args.Length >= 3)
        {
            kind = args[2];
            validRun = kind is "any" or "http-minimal-mapping" or "http-endpoint-attribute";
        }
        if (validRun && args.Length >= 4)
        {
            validRun = args.Length == 5 && args[3] == "--exclude-snapshot" &&
                Guid.TryParse(args[4], out var parsed);
            if (validRun) excludeSnapshot = Guid.Parse(args[4]);
        }
        if (!diagnostic && !validRun)
        {
            Console.Error.WriteLine(
                "Usage: --gate-b-run <1..10> [any|http-minimal-mapping|http-endpoint-attribute] " +
                "[--exclude-snapshot <saved-snapshot-GUID>] OR --gate-b-diagnose");
            return 2;
        }

        var attempts = diagnostic ? 1 : int.Parse(args[1],
            System.Globalization.CultureInfo.InvariantCulture);

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
                    if (inspected++ >= 100) break;
                    if (excludeSnapshot.HasValue && snapshot.Id == excludeSnapshot.Value)
                        continue;
                    try
                    {
                        var graph = graphBuilder.Build(snapshot, CancellationToken.None);
                        foreach (var candidate in graph.Nodes
                            .Where(n => n.Kind is "http-minimal-mapping" or "http-endpoint-attribute")
                            .Where(n => kind == "any" || n.Kind == kind)
                            .OrderBy(n => n.Kind == "http-minimal-mapping" ? 0 : 1)
                            .ThenBy(n => n.Id, StringComparer.Ordinal))
                        {
                            var evidence = tools.ReadEntryPoint(snapshot, candidate.Id,
                                CancellationToken.None);
                            // Match the Investigator evidence size contract before invoking
                            // the model; avoid selecting a known over-budget package.
                            if (GateBInvestigator.BuildAgentEvidenceJson(evidence).Length > 20000)
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
                "RED: no usable " + kind + " candidate in first 100 saved snapshots" +
                (excludeSnapshot is null ? ". " : " excluding the requested snapshot. ") +
                "Capture a repository containing a C# controller or Minimal API mapping in the UI.");
            return 1;
        }

        Console.WriteLine("Selected saved candidate: " + selectedKind);
        Console.WriteLine("Snapshot ID: " + selectedSnapshot.Id.ToString("D"));
        Console.WriteLine("Snapshot fingerprint: " + selectedSnapshot.FingerprintSha256);
        if (diagnostic)
        {
            Console.WriteLine("Diagnostic mode: native /api/chat with identical Gate B schema and evidence.");
            Console.WriteLine("No source content or model text will be printed.");
            GateBNativeDiagnostic result;
            try
            {
                result = await investigator.DiagnoseNativeAsync(
                    selectedSnapshot, selectedEntryPoint, CancellationToken.None);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Console.Error.WriteLine("RED: evidence retrieval failed: " + error.GetType().Name);
                return 1;
            }
            Console.WriteLine("Native chat: " + result.Status);
            Console.WriteLine("Content characters: " + result.ContentLength);
            Console.WriteLine("Thinking characters: " + result.ThinkingLength);
            Console.WriteLine("Done reason: " + result.DoneReason);
            Console.WriteLine("Prompt tokens: " + result.PromptTokens);
            Console.WriteLine("Generation tokens: " + result.GenerationTokens);
            Console.WriteLine("Envelope bytes: " + result.EnvelopeBytes);
            Console.WriteLine("Diagnostic only: Gate B remains RED until MAF succeeds.");
            return result.Status == "native-chat-completed" ? 0 : 1;
        }

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
            // Show *host-generated*, citation-gated findings for human usefulness
            // review. Never print untrusted model prose or raw source excerpts.
            if (i == 1 && run.Result is not null)
            {
                Console.WriteLine("  Host summary: " + run.Result.Summary);
                foreach (var finding in run.Result.Findings)
                    Console.WriteLine("  Checked finding: " + finding.Statement);
                foreach (var unknown in run.Result.Unknowns)
                    Console.WriteLine("  Known limitation: " + unknown);
                Console.WriteLine("  Citation status: " + run.CitationStatus);
                if (run.Result.SuggestedNextQuestion is not null)
                    Console.WriteLine("  AI-selected next question (NOT verified): " +
                        run.Result.SuggestedNextQuestion);
            }
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

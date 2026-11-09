using System.Net;
using System.Net.Sockets;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;

internal static class GateBFaultProbe
{
    public static async Task<int> RunAsync(SnapshotEvidenceTools evidenceTools,
        SnapshotRecord snapshot, string entryPointId)
    {
        // Only loopback sockets are used. No Ollama/model installation is
        // required and no imported source is compiled or executed.
        var port = GetReleasedLoopbackPort();
        var unavailable = new GateBInvestigator(evidenceTools,
            new Uri($"http://127.0.0.1:{port}/"));
        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            var result = await unavailable.InvestigateAsync(
                snapshot, entryPointId, limit.Token);
            Console.WriteLine("Refused provider: " + result.Outcome +
                " / " + result.Diagnostic);
            if (result.Diagnostic is not ("provider-unavailable" or
                    "provider-or-adapter-failure") ||
                !IsFailClosed(result))
            {
                Console.Error.WriteLine("RED: refused loopback provider was not handled safely.");
                return 1;
            }
        }

        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var blockingPort = ((IPEndPoint)server.LocalEndpoint).Port;
        var stalled = new GateBInvestigator(evidenceTools,
            new Uri($"http://127.0.0.1:{blockingPort}/"));
        // An established TCP listener accepts a connection but deliberately
        // never answers HTTP, proving that caller cancellation escapes a
        // stalled real MAF/OllamaSharp request.
        using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        GateBInvestigation cancelledResult;
        try
        {
            cancelledResult = await stalled.InvestigateAsync(
                snapshot, entryPointId, cancelled.Token);
        }
        finally
        {
            server.Stop();
        }
        Console.WriteLine("Cancelled stalled provider: " + cancelledResult.Outcome +
            " / " + cancelledResult.Diagnostic);
        if (cancelledResult.Diagnostic != "cancelled" ||
            !IsFailClosed(cancelledResult))
        {
            Console.Error.WriteLine("RED: cancelled MAF request did not fail closed.");
            return 1;
        }
        Console.WriteLine(
            "GREEN: genuine disconnected and caller-cancelled MAF transport paths were inconclusive.");
        return 0;
    }

    private static int GetReleasedLoopbackPort()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        return port;
    }

    private static bool IsFailClosed(GateBInvestigation result) =>
        result.Outcome == "inconclusive" &&
        result.Result is null &&
        result.CitationStatus == "not-checked" &&
        result.EvidenceContractVersion == SnapshotEvidenceTools.Version;
}

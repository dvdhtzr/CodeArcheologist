using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Investigations;

/// <summary>
/// One local GPU investigation at a time. Work is owned by the host, never by
/// an HTTP request lifetime. No restart/resume promises or implicit retries.
/// </summary>
public sealed class GateC5InvestigationCoordinator(
    WorkspaceStore store, GateC4ReconciliationRunner runner,
    IHostApplicationLifetime lifetime)
{
    public bool TryStart(GateCScope scope, Guid idempotencyKey,
        out InvestigationCreateResult? result)
    {
        // Database transaction owns admission and the single execution slot.
        // Replays return the original record without scheduling another run.
        result = store.CreateInvestigation(scope, idempotencyKey);
        if (result.Busy) return false;
        if (result.Created && !result.Conflict)
            _ = Task.Run(() => ExecuteAsync(result.Record!), CancellationToken.None);
        return true;
    }

    private async Task ExecuteAsync(InvestigationSummary record)
    {
        var runId = record.RunId;
        try
        {
            store.StartAttempt(runId);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                lifetime.ApplicationStopping);
            deadline.CancelAfter(TimeSpan.FromMinutes(6));
            var scope = record.Scope;
            var snapshot = store.GetSnapshot(scope.WorkspaceId,
                scope.RepositoryId, scope.SnapshotId);
            if (snapshot is null ||
                snapshot.FingerprintSha256 != scope.FingerprintSha256)
            {
                store.FailInvestigation(runId, "host-execution-rejected");
                return;
            }

            var outcome = await runner.RunAsync(snapshot, scope, runId, deadline.Token);
            var report = GateC5ReportFactory.Create(outcome,
                record.StartedAtUtc, DateTimeOffset.UtcNow);
            store.FinishInvestigation(runId, report);
        }
        catch (OperationCanceledException)
        {
            store.FailInvestigation(runId, "host-cancelled");
        }
        catch (InvalidOperationException)
        {
            store.FailInvestigation(runId, "host-report-validation-failed");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Do not expose source, prompt, or provider exception text.
            store.FailInvestigation(runId, "host-execution-failure");
        }
    }
}

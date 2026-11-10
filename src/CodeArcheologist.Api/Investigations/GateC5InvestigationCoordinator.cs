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
    private int _active;

    public bool TryStart(GateCScope scope, Guid idempotencyKey,
        out InvestigationCreateResult? result)
    {
        result = null;
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            return false;

        try
        {
            result = store.CreateInvestigation(scope, idempotencyKey);
            if (!result.Created || result.Conflict) return true;
            var record = result.Record!;
            // The operation is deliberately detached from request cancellation.
            // Host shutdown and a global execution deadline still cancel it.
            _ = Task.Run(() => ExecuteAsync(record), CancellationToken.None);
            return true;
        }
        finally
        {
            if (result?.Created != true || result.Conflict)
                Interlocked.Exchange(ref _active, 0);
        }
    }

    private async Task ExecuteAsync(InvestigationSummary record)
    {
        var runId = record.RunId;
        try
        {
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
        finally
        {
            Interlocked.Exchange(ref _active, 0);
        }
    }
}

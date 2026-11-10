using System.Collections.Concurrent;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CodeArcheologist.Api.Realtime;

public static class RealtimeContract
{
    public const string Version = "code-archeologist-realtime-v1";
    public const string HubPath = "/hubs/v1/investigations";
    public const string EventAppended = "eventAppended";
    public const string ExecutionUpdated = "executionUpdated";
    public const int MaxSubscriptionsPerConnection = 16;
}

/// <summary>Client request to follow one run in an exact workspace/repository/snapshot scope.</summary>
public sealed record RunSubscriptionRequest(Guid WorkspaceId, Guid RepositoryId, Guid SnapshotId, Guid RunId);

public sealed record RunSubscriptionAccepted(string ContractVersion, Guid RunId, int BacklogEventCount);

/// <summary>
/// One committed host ledger event, exactly as the REST events endpoint
/// returns it. Delivery is at-least-once and ordered per attempt; clients
/// de-duplicate by EventId and may resync from REST at any time.
/// </summary>
public sealed record EventAppendedMessage(string ContractVersion, Guid WorkspaceId, Guid RepositoryId,
    Guid SnapshotId, InvestigationEvent Event);

public sealed record ExecutionUpdatedMessage(string ContractVersion, Guid WorkspaceId, Guid RepositoryId,
    Guid SnapshotId, ExecutionProjection Execution);

/// <summary>
/// W1 push-only hub. The ONLY client-callable methods manage subscriptions;
/// every command (start, continue, cancel) stays REST with its idempotency
/// and concurrency guarantees. Authorization is the bearer/cookie session,
/// and every subscription is checked against the exact stored run scope.
/// </summary>
[Authorize]
public sealed class InvestigationEventsHub(WorkspaceStore store, RealtimeSubscriptions subscriptions)
    : Hub
{
    public async Task<RunSubscriptionAccepted> Subscribe(RunSubscriptionRequest request)
    {
        if (request is null || store.GetInvestigation(request.WorkspaceId, request.RepositoryId,
                request.SnapshotId, request.RunId) is null)
            throw new HubException("run-not-found-in-scope");
        if (!subscriptions.TryAdd(Context.ConnectionId, request))
            throw new HubException("subscription-limit-exceeded");
        // Join first, then send the backlog: no gap is possible; a duplicate is.
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(request.RunId));
        var backlog = store.ListEvents(request.WorkspaceId, request.RepositoryId, request.SnapshotId, request.RunId);
        foreach (var evt in backlog)
            await Clients.Caller.SendAsync(RealtimeContract.EventAppended, new EventAppendedMessage(
                RealtimeContract.Version, request.WorkspaceId, request.RepositoryId, request.SnapshotId, evt));
        return new(RealtimeContract.Version, request.RunId, backlog.Count);
    }

    public async Task Unsubscribe(Guid runId)
    {
        subscriptions.Remove(Context.ConnectionId, runId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(runId));
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public static string Group(Guid runId) => "run:" + runId.ToString("D");
}

/// <summary>In-memory subscription registry; the database remains the source of truth.</summary>
public sealed class RealtimeSubscriptions
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, RunSubscriptionRequest>> _byConnection = new();

    public bool TryAdd(string connectionId, RunSubscriptionRequest request)
    {
        var runs = _byConnection.GetOrAdd(connectionId, _ => new());
        if (!runs.ContainsKey(request.RunId) && runs.Count >= RealtimeContract.MaxSubscriptionsPerConnection)
            return false;
        runs[request.RunId] = request;
        return true;
    }

    public void Remove(string connectionId, Guid runId)
    {
        if (_byConnection.TryGetValue(connectionId, out var runs)) runs.TryRemove(runId, out _);
    }

    public void RemoveConnection(string connectionId) => _byConnection.TryRemove(connectionId, out _);

    /// <summary>Distinct subscribed runs with their verified scope.</summary>
    public IReadOnlyList<RunSubscriptionRequest> Runs() =>
        _byConnection.Values.SelectMany(r => r.Values).GroupBy(r => r.RunId).Select(g => g.First()).ToArray();
}

/// <summary>
/// Pushes committed ledger events to subscribed runs. It polls SQLite only for
/// runs that have subscribers (500 ms), so nothing uncommitted is ever sent and
/// a missed wake-up cannot lose an event. After new events it also pushes the
/// versioned execution projection. It never writes and never starts work.
/// </summary>
public sealed class RealtimePublisher(WorkspaceStore store, RealtimeSubscriptions subscriptions,
    IHubContext<InvestigationEventsHub> hub, InvestigationExecutionProjectionService projections,
    ILogger<RealtimePublisher> logger) : BackgroundService
{
    private readonly Dictionary<Guid, HashSet<Guid>> _sent = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var runs = subscriptions.Runs();
            foreach (var stale in _sent.Keys.Except(runs.Select(r => r.RunId)).ToArray()) _sent.Remove(stale);
            foreach (var run in runs)
            {
                try { await PublishAsync(run, stoppingToken); }
                catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                {
                    logger.LogWarning("Realtime publish failed for a run ({Type}).", error.GetType().Name);
                }
            }
        }
    }

    private async Task PublishAsync(RunSubscriptionRequest run, CancellationToken ct)
    {
        var events = store.ListEvents(run.WorkspaceId, run.RepositoryId, run.SnapshotId, run.RunId);
        // Never skip on first sight: an event committed between a subscriber's
        // backlog read and this tick would otherwise be lost. Re-sending what
        // the backlog already delivered is the documented at-least-once cost.
        if (!_sent.TryGetValue(run.RunId, out var sent)) _sent[run.RunId] = sent = new HashSet<Guid>();
        var fresh = events.Where(e => !sent.Contains(e.EventId)).ToArray();
        if (fresh.Length == 0) return;
        var group = hub.Clients.Group(InvestigationEventsHub.Group(run.RunId));
        foreach (var evt in fresh)
        {
            await group.SendAsync(RealtimeContract.EventAppended, new EventAppendedMessage(
                RealtimeContract.Version, run.WorkspaceId, run.RepositoryId, run.SnapshotId, evt), ct);
            sent.Add(evt.EventId);
        }
        if (projections.Project(run.WorkspaceId, run.RepositoryId, run.SnapshotId, run.RunId, ct) is { } projection)
            await group.SendAsync(RealtimeContract.ExecutionUpdated, new ExecutionUpdatedMessage(
                RealtimeContract.Version, run.WorkspaceId, run.RepositoryId, run.SnapshotId, projection), ct);
    }
}

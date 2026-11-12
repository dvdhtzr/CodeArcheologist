namespace CodeArcheologist.Api.Investigations;

/// <summary>
/// Passive, opt-in local evaluation meter. A scope is inherited through
/// ExecutionContext by the single managed worker. Production has NO active
/// counter unless the evaluation lab explicitly opens one. No prompts,
/// timeouts, retries, evidence decisions, or provider options are modified.
/// This counts attempted MAF calls, NOT provider-internal retries or tokens.
/// </summary>
public static class EvaluationModelCalls
{
    private static readonly AsyncLocal<Counter?> Current = new();

    public sealed record Snapshot(int Attempts, int Faults);

    private sealed class Counter
    {
        internal int Attempts;
        internal int Faults;
    }

    public sealed class Scope : IDisposable
    {
        private readonly Counter? _previous;
        private readonly Counter _counter;
        private bool _disposed;

        private Scope(Counter? previous, Counter counter)
        {
            _previous = previous;
            _counter = counter;
        }

        public Snapshot Read() => new(
            Volatile.Read(ref _counter.Attempts),
            Volatile.Read(ref _counter.Faults));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Current.Value = _previous;
        }

        internal static Scope Open()
        {
            var previous = Current.Value;
            var counter = new Counter();
            Current.Value = counter;
            return new Scope(previous, counter);
        }
    }

    public static Scope Begin() => Scope.Open();

    /// <summary>Wrap ONLY the actual provider-facing MAF call.
    /// A cancellation or transport error counts as attempted and faulted;
    /// rejected but well-formed responses still count as attempted.</summary>
    public static async Task<T> ObserveAsync<T>(Func<Task<T>> invoke)
    {
        var counter = Current.Value;
        if (counter is null) return await invoke();
        Interlocked.Increment(ref counter.Attempts);
        try { return await invoke(); }
        catch
        {
            Interlocked.Increment(ref counter.Faults);
            throw;
        }
    }
}

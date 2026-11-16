namespace ApolloOverlay;

/// <summary>Shares a short-lived observation without performing file work on a caller's UI thread.</summary>
internal sealed class UpdateHistorySnapshot(Func<IReadOnlyList<InstalledBuild>> read)
{
    private readonly object gate = new();
    private IReadOnlyList<InstalledBuild> snapshot = Array.Empty<InstalledBuild>();
    private Task<IReadOnlyList<InstalledBuild>>? pending;
    private long generation, observedGeneration = -1, refreshedAt;

    internal IReadOnlyList<InstalledBuild> Current
    {
        get
        {
            lock (gate)
                return snapshot;
        }
    }
    internal bool Fresh
    {
        get
        {
            lock (gate)
                return observedGeneration == generation && Environment.TickCount64 - refreshedAt < 5000;
        }
    }

    internal void Invalidate()
    {
        lock (gate)
            generation++;
    }

    internal Task<IReadOnlyList<InstalledBuild>> ReadAsync()
    {
        lock (gate)
        {
            if (Fresh)
                return Task.FromResult(snapshot);
            return pending ??= Task.Run(Refresh);
        }
    }

    private IReadOnlyList<InstalledBuild> Refresh()
    {
        try
        {
            while (true)
            {
                long expected;
                lock (gate)
                    expected = generation;
                var result = read().ToArray();
                lock (gate)
                {
                    if (generation != expected)
                        continue;
                    snapshot = result;
                    observedGeneration = expected;
                    refreshedAt = Environment.TickCount64;
                    return snapshot;
                }
            }
        }
        finally
        {
            lock (gate)
                pending = null;
        }
    }
}

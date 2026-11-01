namespace ApolloOverlay;

/// <summary>Discovery cadence based on monotonic milliseconds, independent of UI and network work.</summary>
internal sealed class UpdateDiscoverySchedule(long now)
{
    private int consecutiveFailures;
    internal long NextCheckAt { get; private set; } = now + 15_000;

    internal bool IsDue(long now) => now >= NextCheckAt;

    internal void Defer(long now) => NextCheckAt = now + 30_000;

    internal void Complete(long now, bool succeeded)
    {
        consecutiveFailures = succeeded ? 0 : Math.Min(consecutiveFailures + 1, 3);
        NextCheckAt = now + (succeeded ? 15 : consecutiveFailures * 5) * 60_000;
    }

    internal void Reset(long now)
    {
        consecutiveFailures = 0;
        NextCheckAt = now;
    }
}

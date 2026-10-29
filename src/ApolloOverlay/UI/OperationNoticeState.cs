namespace ApolloOverlay;

/// <summary>Retains the result independently of its brief, non-interactive acknowledgement.</summary>
internal sealed class OperationNoticeState
{
    private long startedAt;
    private bool expired;
    public OverlayOperation? Operation
    {
        get; private set;
    }
    public bool AllowsActions => Operation is not null && Lifetime is null;
    public bool Visible => Operation is not null && !expired;
    private long? Lifetime => Operation?.State switch
    {
        "Completed" or "Cancelled" or "Simulated" => 4000,
        "Requested" => 6000,
        _ => null
    };

    public bool Update(OverlayOperation? operation, long now)
    {
        var changed = !ReferenceEquals(Operation, operation);
        if (changed)
        {
            Operation = operation;
            startedAt = now;
            expired = false;
        }

        if (Lifetime is { } lifetime && now - startedAt >= lifetime)
            expired = true;
        return changed;
    }

    public double Opacity(long now)
    {
        var elapsed = Math.Max(0, now - startedAt);
        var entrance = Math.Clamp(elapsed / 140d, 0, 1);
        var exit = Lifetime is { } lifetime ? Math.Clamp((lifetime - elapsed) / 180d, 0, 1) : 1;
        return Math.Min(entrance, exit);
    }
}

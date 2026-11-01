namespace ApolloOverlay;

internal sealed record GuideDiagnostic(string Command, ResultKind Outcome, string Reason);

/// <summary>Records semantic Guide changes without logging each polling frame.</summary>
internal sealed class GuideDiagnosticTracker
{
    private bool? valid, held;
    private int? connections;
    private long generation = -1, pressedAt;
    private int pressLimit;
    private bool openingPress;

    internal IReadOnlyList<GuideDiagnostic> Observe(bool snapshotValid, int connectionMask, bool down, bool visible, long cycle, long now, int holdLimit)
    {
        List<GuideDiagnostic>? events = null;
        void Record(GuideDiagnostic entry) => (events ??= []).Add(entry);
        if (valid != snapshotValid)
            Record(new("guide-reading", snapshotValid ? ResultKind.Completed : ResultKind.Unknown,
                snapshotValid ? "valid" : "read-invalid"));
        if (connections != connectionMask)
            Record(new("guide-connections", connectionMask != 0 ? ResultKind.Completed : ResultKind.Unavailable,
                "slot-mask-" + connectionMask));
        var topologyChanged = connections != connectionMask;
        if (!snapshotValid || topologyChanged || generation != cycle)
        {
            if (openingPress)
                Record(new("guide-opening", ResultKind.Cancelled, !snapshotValid ? "reading-invalid"
                    : topologyChanged ? "topology-changed" : "activation-cycle-changed"));
            openingPress = false;
        }
        if (snapshotValid && down != held)
        {
            var reason = down ? held == false && valid == true && !topologyChanged ? "down" : "held-unarmed"
                : held == true ? "up" : "released-baseline";
            Record(new("guide-button", ResultKind.Completed, reason));
            if (down && held == false && !visible && valid == true && connections == connectionMask)
            {
                openingPress = true;
                pressedAt = now;
                pressLimit = holdLimit;
            }
            if (!down)
                openingPress = false;
        }
        if (openingPress && (now < pressedAt || now - pressedAt > pressLimit))
        {
            Record(new("guide-opening", ResultKind.Cancelled, "hold-limit-exceeded"));
            openingPress = false;
        }
        valid = snapshotValid;
        held = snapshotValid ? down : null;
        connections = connectionMask;
        generation = cycle;
        return events is null ? Array.Empty<GuideDiagnostic>() : events;
    }
}

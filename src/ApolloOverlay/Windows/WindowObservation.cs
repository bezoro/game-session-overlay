namespace ApolloOverlay;

public enum WindowObservation
{
    SameIdentity,
    GoneOrReplaced,
    Unknown
}

public readonly record struct WindowProbe(bool Exists, int? Pid, long? Started, long Generation, string Caption);
public static class ClosureObservation
{
    // Caption is display metadata, never evidence that a window was destroyed.
    // Missing identity metadata is unknown unless disappearance/replacement is independently known.
    public static WindowObservation Classify(WindowRef target, WindowProbe probe)
    {
        if (!probe.Exists || target.Generation != probe.Generation)
            return WindowObservation.GoneOrReplaced;
        if (probe.Pid is { } pid && target.Pid != pid)
            return WindowObservation.GoneOrReplaced;
        if (probe.Started is { } started && target.Started != started)
            return WindowObservation.GoneOrReplaced;
        if (probe.Pid is null || probe.Started is null)
            return WindowObservation.Unknown;
        return WindowObservation.SameIdentity;
    }
}

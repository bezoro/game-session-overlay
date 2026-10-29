namespace ApolloOverlay;

public sealed record AudioSnapshot(string? EndpointId, float Volume, bool Muted, string Status)
{
    public bool Available => EndpointId is not null;
}

public readonly record struct AudioRestore(bool Volume, bool Mute, string Reason)
{
    public static AudioRestore Decide(AudioSnapshot original, AudioSnapshot applied, AudioSnapshot current) => !current.Available || current.EndpointId != original.EndpointId || applied.EndpointId != original.EndpointId ? new(false, false, "The device changed or is inaccessible; nothing was restored.") : new(Math.Abs(current.Volume - applied.Volume) < .0001f, current.Muted == applied.Muted, "Restore only values that still match the test.");
}

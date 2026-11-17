namespace ApolloOverlay;

internal sealed record MasterAudioChange(ActionResult Result, AudioSnapshot Snapshot);

/// <summary>Created, called and disposed exclusively by the master-audio MTA owner.</summary>
internal interface IMasterAudioBackend : IDisposable
{
    AudioSnapshot Bind();
    AudioSnapshot Read();
    /// <summary>Revalidate continuing and invoke dispatch immediately before every external write; false revokes permission.</summary>
    MasterAudioChange Set(float? volume, bool? muted, string? expectedEndpoint, Func<bool> dispatch, Func<bool> continuing);
}

public sealed record AudioSnapshot(string? EndpointId, float Volume, bool Muted, string Status)
{
    public bool Available => EndpointId is not null;
}

public readonly record struct AudioRestore(bool Volume, bool Mute, string Reason)
{
    public static AudioRestore Decide(AudioSnapshot original, AudioSnapshot applied, AudioSnapshot current) => !current.Available || current.EndpointId != original.EndpointId || applied.EndpointId != original.EndpointId ? new(false, false, "The device changed or is inaccessible; nothing was restored.") : new(Math.Abs(current.Volume - applied.Volume) < .0001f, current.Muted == applied.Muted, "Restore only values that still match the test.");
}

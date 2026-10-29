namespace ApolloOverlay;

public sealed record AudioSnapshot(string? EndpointId, float Volume, bool Muted, string Status)
{
    public bool Available => EndpointId is not null;
}

public readonly record struct AudioRestore(bool Volume, bool Mute, string Reason)
{
    public static AudioRestore Decide(AudioSnapshot original, AudioSnapshot applied, AudioSnapshot current) => !current.Available || current.EndpointId != original.EndpointId || applied.EndpointId != original.EndpointId ? new(false, false, "Dispositivo mudou ou não está acessível; nada restaurado.") : new(Math.Abs(current.Volume - applied.Volume) < .0001f, current.Muted == applied.Muted, "Restaurar somente valores que ainda correspondem ao teste.");
}

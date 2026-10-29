namespace ApolloOverlay;

public enum ApplicationAudioAvailability
{
    Available,
    Unavailable,
    Partial,
    Unknown
}

public enum ApplicationMuteState
{
    Audible,
    Muted,
    Mixed,
    Unknown
}

/// <summary>A bounded observation of positively identified process sessions, never a future-session policy.</summary>
public sealed record ApplicationAudioSnapshot(
    WindowRef? Target,
    long Generation,
    long Revision,
    ApplicationAudioAvailability Availability,
    ApplicationMuteState MuteState,
    int IdentifiedSessions,
    int SkippedOrFailedSessions,
    string Reason
)
{
    public bool CanSetMute => Target is not null && IdentifiedSessions > 0;
    public float? VolumeMinimum
    {
        get;
        init;
    }
    public float? VolumeMaximum
    {
        get;
        init;
    }
    public int VolumeSessions
    {
        get;
        init;
    }
    public bool CanSetVolume => Target is not null
        && Availability is ApplicationAudioAvailability.Available or ApplicationAudioAvailability.Partial
        && IdentifiedSessions > 0
        && VolumeSessions == IdentifiedSessions;
}

public sealed record ApplicationAudioChange(ActionResult Result, ApplicationAudioSnapshot Snapshot, int Attempted, int Succeeded, int Verified, bool Dispatched);
/// <summary>Changed may run on the owner worker. Consumers marshal immutable snapshots and reject obsolete target/generation.</summary>
public interface IApplicationAudio : IDisposable
{
    event Action<ApplicationAudioSnapshot>? Changed;
    void Track(WindowRef? target, bool visible, long generation);
    Task<ApplicationAudioSnapshot> RefreshAsync(WindowRef target, long generation, CancellationToken cancellation = default);
    Task<ApplicationAudioChange> SetMutedAsync(WindowRef target, bool desiredMute, long generation, CancellationToken cancellation = default);
    Task<ApplicationAudioChange> SetVolumeAsync(WindowRef target, float volume, long generation, CancellationToken cancellation = default) => Task.FromResult(new ApplicationAudioChange(new(ResultKind.Unavailable, "O volume deste aplicativo não está disponível."), new(target, generation, 0, ApplicationAudioAvailability.Unavailable, ApplicationMuteState.Unknown, 0, 0, "Volume indisponível."), 0, 0, 0, false));
    void Suspend();
}

namespace ApolloOverlay;

internal sealed class PreviewApplicationAudio : IApplicationAudio
{
    public event Action<ApplicationAudioSnapshot>? Changed;
    private static ApplicationAudioSnapshot State(WindowRef? target, long generation) => new(target, generation, 1, ApplicationAudioAvailability.Unavailable, ApplicationMuteState.Unknown, 0, 0, "Prévia · sessões de áudio não consultadas");
    public void Track(WindowRef? target, bool visible, long generation)
    {
        if (visible)
            Changed?.Invoke(State(target, generation));
    }

    public Task<ApplicationAudioSnapshot> RefreshAsync(WindowRef target, long generation, CancellationToken cancellation = default) => Task.FromResult(State(target, generation));
    public Task<ApplicationAudioChange> SetMutedAsync(WindowRef target, bool desiredMute, long generation, CancellationToken cancellation = default) => Task.FromResult(new ApplicationAudioChange(new(ResultKind.Simulated, "Prévia · áudio não alterado"), State(target, generation), 0, 0, 0, false));
    public void Suspend()
    {
    }

    public void Dispose()
    {
    }
}

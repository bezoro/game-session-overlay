namespace ApolloOverlay;

public partial class MainWindow
{
    private readonly IApplicationAudio applicationAudio;
    private ApplicationAudioSnapshot appAudio = new(null, 0, 0, ApplicationAudioAvailability.Unavailable, ApplicationMuteState.Unknown, 0, 0, "Audio unavailable");
    private WindowRef? audioTarget;
    private long audioGeneration, audioRevision;
    private readonly WindowsAudio audio = new();
    private void AudioChanged()
    {
        if (!IsVisible)
            return;
        UpdateAudioEntries(audio.Current);
        UpdateSessionFeatureLabels();
        SyncSessionSlider("volume", audio.Current.Available ? audio.Current.Volume : null);
    }

    internal void UpdateAudioEntries(AudioSnapshot state)
    {
        if (view.Current.Route == OverlayRoute.WindowsAudio)
        {
            SetFeatureEntry("volume-down", "Lower volume", MasterVolumeLowerDescription(state), state.Available);
            SetFeatureEntry("volume", MasterVolumeTitle(state), MasterVolumeDescription(state), state.Available);
            SetFeatureEntry("mute", state.Muted ? "Unmute" : "Mute", MasterMuteDescription(state), state.Available);
            SyncSessionSlider("volume", state.Available ? state.Volume : null);
            if (entries.FirstOrDefault(entry => entry.Id == "volume")?.Button.Content is ActionButtonContent { EditorSlider: { } slider })
                slider.Visibility = state.Available ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            SetEntryDescription("volume", MasterVolumeDescription(state),
                helpText: state.Available ? MasterVolumeDescription(state) : state.Status + " " + MasterVolumeDescription(state));
        }
    }

    private static string MasterVolumeTitle(AudioSnapshot state) => "Volume · " + (state.Available ? state.Volume.ToString("P0") : "unavailable");
    private static string MasterVolumeLowerDescription(AudioSnapshot state) => state.Available ? "− 5% · default Windows output" : "No output available to adjust";
    private static string MasterMuteDescription(AudioSnapshot state) => state.Available ? "Mutes or unmutes the Windows output" : "No output available to mute";
    private static string MasterVolumeDescription(AudioSnapshot state) => state.Available
        ? "+ 5% · ← / → adjusts"
        : "Choose Audio devices to check your output";

    private void AdjustVolume(float delta) => ChangeMasterAudio(audio.Current.Volume + delta);
    private void ChangeMasterAudio(float? volume = null, bool? muted = null)
    {
        if (!ValidateNavigationOwnership() || busy || TransitionPending)
            return;
        var endpoint = audio.Current.EndpointId;
        // Volume stays in place: no focus handoff or controller release is required.
        OperationTask = RunSessionAction("audio", token => audio.SetAsync(volume, muted, endpoint, token), null);
    }
    private bool AudioFor(WindowRef target) => audioTarget?.Same(target) == true && appAudio.Target?.Same(target) == true && appAudio.Generation == audioGeneration;
    private WindowRef? HubAudioTarget => view.Current.Route == OverlayRoute.Hub && !Controller.Session.CardPending
        && Controller.Session.CardTarget is { Protected: false } target && Controller.CardTargetAvailable(target) ? target : null;
    private ApplicationAudioSnapshot? HubAudioSnapshot => HubAudioTarget is { } target && AudioFor(target) ? appAudio : null;
    private WindowRef? ApplicationAudioTarget => view.Current.Target ?? (view.Current.Route == OverlayRoute.Hub ? HubAudioTarget : Controller.Session.Anchor);
    private void UpdateSessionCardAudioLabel()
    {
        var target = Controller.Session.CardTarget;
        SessionAudioLabel.Text = target is null ? "Select a window to view its audio"
            : !Controller.CardTargetAvailable(target) ? "Window unavailable"
            : AudioFor(target) ? AudioBadge() : "Status unconfirmed";
    }
    private string AudioBadge() => appAudio.Availability switch
    {
        ApplicationAudioAvailability.Partial => "Audio partially available",
        ApplicationAudioAvailability.Unavailable => "Audio unavailable",
        ApplicationAudioAvailability.Unknown => "Status unconfirmed",
        _ => appAudio.MuteState switch
        {
            ApplicationMuteState.Audible => "Sound on",
            ApplicationMuteState.Muted => "App muted",
            ApplicationMuteState.Mixed => "Mixed audio",
            _ => "Status unconfirmed"
        }
    };
    private string MuteActionTitle() => appAudio.MuteState == ApplicationMuteState.Muted ? "Unmute app audio" : appAudio.Availability != ApplicationAudioAvailability.Available || appAudio.MuteState is ApplicationMuteState.Mixed or ApplicationMuteState.Unknown ? "Mute available audio" : "Mute app audio";
    private string AppAudioDescription() => AudioBadge() + " · " + (appAudio.Availability == ApplicationAudioAvailability.Available ? "Also affects other windows from this app." : appAudio.Availability == ApplicationAudioAvailability.Partial ? "Some outputs were not verified. Affects this app's windows." : "Read the details or try again.");
    private void TrackApplicationAudio()
    {
        if (exiting)
            return;
        var context = InputContext;
        if (!context.Visible || !context.Confirmed || !context.Active || !context.OwnForeground)
        {
            if (audioTarget is not null)
            {
                applicationAudio.Suspend();
                audioTarget = null;
                audioGeneration++;
            }

            return;
        }

        var target = ApplicationAudioTarget;
        if (target is null || !Controller.Platform.Valid(target))
        {
            if (audioTarget is not null)
            {
                applicationAudio.Suspend();
                audioTarget = null;
                audioGeneration++;
            }

            return;
        }

        if (audioTarget?.Same(target) == true)
            return;
        audioTarget = target;
        audioGeneration++;
        audioRevision = -1;
        appAudio = new(target, audioGeneration, 0, ApplicationAudioAvailability.Unknown, ApplicationMuteState.Unknown, 0, 0, "Checking app audio sessions…");
        UpdateSessionCardAudioLabel();
        applicationAudio.Track(target, true, audioGeneration);
    }

    private void ApplicationAudioChanged(ApplicationAudioSnapshot snapshot)
    {
        if (exiting || Dispatcher.HasShutdownStarted)
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ApplicationAudioChanged(snapshot));
            return;
        }

        if (!InputContext.Visible || snapshot.Generation != audioGeneration || audioTarget is null || !audioTarget.Same(snapshot.Target)
            || ApplicationAudioTarget?.Same(snapshot.Target) != true || snapshot.Revision <= audioRevision)
            return;
        audioRevision = snapshot.Revision;
        appAudio = snapshot;
        UpdateSessionCardAudioLabel();
        UpdateSessionFeatureLabels();
        SetFeatureEntry("application-mute", MuteActionTitle(), AppAudioDescription(), snapshot.CanSetMute && Controller.Platform.Valid(audioTarget));
    }

    private void FeedbackStatusChanged(FeedbackStatus status)
    {
        if (exiting || Dispatcher.HasShutdownStarted)
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => FeedbackStatusChanged(status));
            return;
        }

        SetEntryDescription("test-feedback", FeedbackDescription());
    }

    private void MuteApplication(WindowRef target)
    {
        if (ApplicationAudioTarget?.Same(target) != true || !AudioFor(target) || !appAudio.CanSetMute)
        {
            FeedbackText("No audio session identified; nothing was sent.");
            return;
        }

        var desired = appAudio.MuteState != ApplicationMuteState.Muted;
        var generation = audioGeneration;
        RunInPlace(target, async token =>
        {
            var change = await applicationAudio.SetMutedAsync(target, desired, generation, token);
            if (!exiting)
                ApplicationAudioChanged(change.Snapshot);
            return change.Result;
        }, "application-mute", () => generation == audioGeneration && ApplicationAudioTarget?.Same(target) == true && AudioFor(target));
    }
}

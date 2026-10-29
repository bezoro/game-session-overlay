using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ApolloOverlay;

public partial class MainWindow
{
    private readonly IApplicationAudio applicationAudio;
    private ApplicationAudioSnapshot appAudio = new(null, 0, 0, ApplicationAudioAvailability.Unavailable, ApplicationMuteState.Unknown, 0, 0, "Áudio indisponível");
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
        if (subpage == "audio")
        {
            var volume = entries.FirstOrDefault(e => e.Id == "volume");
            if (volume is not null && volume.Available != state.Available)
            {
                RefreshRows();
                return;
            }

            SetEntryText("volume", "Volume · " + (state.Available ? state.Volume.ToString("P0") : "indisponível"));
            SetEntryText("mute", state.Muted ? "Ativar som" : "Silenciar");
        }
        else if (page == 0)
            SetEntryDescription("audio", state.Available ? (state.Muted ? "Sem som" : state.Volume.ToString("P0")) : "Saída indisponível");
    }

    private void AdjustVolume(float delta) => AudioAction(audio.Set(audio.Current.Volume + delta));
    private void AudioAction(ActionResult result) => RecordResult(result, "audio");
    private bool AudioFor(WindowRef target) => audioTarget?.Same(target) == true && appAudio.Target?.Same(target) == true && appAudio.Generation == audioGeneration;
    private string AudioBadge() => appAudio.Availability switch
    {
        ApplicationAudioAvailability.Partial => "Áudio parcialmente disponível",
        ApplicationAudioAvailability.Unavailable => "Áudio indisponível",
        ApplicationAudioAvailability.Unknown => "Estado não confirmado",
        _ => appAudio.MuteState switch
        {
            ApplicationMuteState.Audible => "Som ativo",
            ApplicationMuteState.Muted => "Aplicativo sem som",
            ApplicationMuteState.Mixed => "Áudio misto",
            _ => "Estado não confirmado"
        }
    };
    private string MuteActionTitle() => appAudio.MuteState == ApplicationMuteState.Muted ? "Restaurar áudio do aplicativo" : appAudio.Availability != ApplicationAudioAvailability.Available || appAudio.MuteState is ApplicationMuteState.Mixed or ApplicationMuteState.Unknown ? "Silenciar áudio disponível" : "Silenciar áudio do aplicativo";
    private string AppAudioDescription() => AudioBadge() + " · " + (appAudio.Availability == ApplicationAudioAvailability.Available ? "Afeta também outras janelas deste aplicativo." : appAudio.Availability == ApplicationAudioAvailability.Partial ? "Algumas saídas não foram verificadas. Afeta as janelas deste aplicativo." : "Consulte os detalhes ou tente novamente.");
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

        var target = view.Current.Target ?? Controller.Session.Anchor;
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
        appAudio = new(target, audioGeneration, 0, ApplicationAudioAvailability.Unknown, ApplicationMuteState.Unknown, 0, 0, "Consultando sessões do aplicativo…");
        SessionAudioLabel.Text = AudioBadge();
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

        if (!InputContext.Visible || snapshot.Generation != audioGeneration || audioTarget is null || !audioTarget.Same(snapshot.Target) || snapshot.Revision <= audioRevision)
            return;
        audioRevision = snapshot.Revision;
        appAudio = snapshot;
        SessionAudioLabel.Text = AudioBadge();
        UpdateSessionFeatureLabels();
        var index = entries.FindIndex(e => e.Id == "application-mute");
        if (index >= 0)
        {
            var entry = entries[index];
            var available = snapshot.CanSetMute && Controller.Platform.Valid(audioTarget);
            entry.Button.IsEnabled = available;
            entries[index] = entry with
            {
                Available = available,
                Reason = AppAudioDescription()
            };
            SetEntryText("application-mute", MuteActionTitle());
            SetEntryDescription("application-mute", AppAudioDescription());
            if (entry.Button.Resources["DescriptionTarget"] is Border reason)
                AutomationProperties.SetName(reason, MuteActionTitle() + ". " + AppAudioDescription());
            if (entry.Available != available && navigation.Id == entry.Id)
                SetSelection(false);
        }
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
        if (!AudioFor(target) || !appAudio.CanSetMute)
        {
            FeedbackText("Nenhuma sessão de áudio identificada; nada foi enviado.");
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
        }, "application-mute");
    }
}

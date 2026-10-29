using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ApolloOverlay;

public partial class MainWindow
{
    private SystemAudioDevices systemAudio = null!;
    private IApplicationAudio mixerAudio = null!;
    private readonly Dictionary<string, ApplicationAudioSnapshot> mixerStates = [];
    private WindowRef? mixerTarget;
    private bool synchronizingFeatureSlider;
    private void UpdateMixerLabels()
    {
        if (view.Current.Route == OverlayRoute.AudioDevices)
        {
            foreach (var output in systemAudio.Current.Outputs)
                SetEntryDescription("output:" + output.Id, output.Id == systemAudio.Current.ConsoleOutputId && output.Id == systemAudio.Current.MediaOutputId ? "Em uso para jogos e mídia" : "Usar para jogos e mídia");
            SetEntryDescription("device-refresh", systemAudio.Current.Status);
            return;
        }

        if (view.Current.Route != OverlayRoute.Mixer)
            return;
        foreach (var window in Controller.Apps.Where(item => !item.Protected).Take(12))
        {
            var state = mixerStates.GetValueOrDefault(WindowId(window));
            SetEntryDescription("mix:" + WindowId(window), state?.CanSetVolume == true ? $"{state.VolumeMaximum:P0}" + (state.MuteState == ApplicationMuteState.Muted ? " · sem som" : "") : "Consultar áudio deste aplicativo");
        }

        if (mixerTarget is not { } target || mixerStates.GetValueOrDefault(WindowId(target)) is not { } selected)
            return;
        SetFeatureEntry("mixer-volume", selected.CanSetVolume ? $"Volume · {selected.VolumeMaximum:P0}" : "Volume não informado", "← / → ajusta o volume", selected.CanSetVolume);
        SetFeatureEntry("mixer-mute", selected.MuteState == ApplicationMuteState.Muted ? "Ativar áudio" : "Silenciar aplicativo", target.ProcessName, selected.CanSetMute);
        SyncSessionSlider("mixer-volume", selected.VolumeMaximum);
    }

    private static string VolumeRange(ApplicationAudioSnapshot state) => state.VolumeMaximum - state.VolumeMinimum > .01f ? $"{state.VolumeMinimum:P0}–{state.VolumeMaximum:P0}" : $"{state.VolumeMaximum:P0}";
    private string GameVolumeTitle => appAudio.CanSetMute ? "Jogo · " + (appAudio.MuteState == ApplicationMuteState.Muted ? "sem som" : appAudio.MuteState == ApplicationMuteState.Mixed ? "áudio misto" : appAudio.CanSetVolume ? VolumeRange(appAudio) : "som ativo") : "Som do jogo";
    private string GameVolumeDescription => (appAudio.Availability == ApplicationAudioAvailability.Partial ? "Áudio parcial · " : "") + (appAudio.CanSetVolume ? "← / → volume · A " + (appAudio.MuteState == ApplicationMuteState.Muted ? "ativa" : "silencia") : appAudio.CanSetMute ? "A " + (appAudio.MuteState == ApplicationMuteState.Muted ? "ativa" : "silencia") + " · volume não informado" : "Abra o jogo e inicie o áudio para ajustar");
    private string WindowsVolumeTitle => audio.Current.Available ? audio.Current.Muted ? "Saída · sem som" : $"Saída · {audio.Current.Volume:P0}" : "Saída indisponível";
    private string MicrophoneTitle => systemAudio.Current.MicrophoneMuted switch
    {
        true => "Microfone · sem som",
        false => "Microfone · ligado",
        _ => "Microfone indisponível"
    };

    private void UpdateSessionFeatureLabels()
    {
        SetFeatureEntry("session-volume", GameVolumeTitle, GameVolumeDescription, appAudio.CanSetMute);
        SetFeatureEntry("windows-volume", WindowsVolumeTitle, systemAudio.Current.OutputName, audio.Current.Available);
        SetFeatureEntry("microphone", MicrophoneTitle, systemAudio.Current.MicrophoneName, systemAudio.Current.MicrophoneMuted is not null);
        var media = CurrentMedia;
        SyncSessionSlider("session-volume", appAudio.VolumeMaximum);
        SyncSessionSlider("windows-volume", audio.Current.Available ? audio.Current.Volume : null);
        SetEntryDescription("session-media", media is null ? "Reprodução nos aplicativos compatíveis" : (media.Playing ? "Tocando · " : "Pausado · ") + (media.Title.Length > 0 ? media.Title : "Reprodução de mídia"));
    }

    private void SetFeatureEntry(string id, string title, string description, bool available)
    {
        var index = entries.FindIndex(entry => entry.Id == id);
        if (index < 0)
            return;
        var entry = entries[index];
        entry.Button.IsEnabled = available;
        entries[index] = entry with
        {
            Available = available,
            Reason = description
        };
        SetEntryText(id, title);
        SetEntryDescription(id, description);
        if (entry.Available != available && navigation.Id == id)
            SetSelection(false);
    }

    private void ToggleGameAudio()
    {
        if (Controller.Session.Anchor is { } target)
            MuteApplication(target);
    }

    private void ToggleMicrophone()
    {
        var state = systemAudio.Current;
        if (state.MicrophoneMuted is not { } muted)
            return;
        StartAudioAction("microphone", token => systemAudio.SetMicrophoneMutedAsync(state.MicrophoneId, !muted, token));
    }

    private bool AdjustSessionFeature(NavDirection direction)
    {
        if (direction is not (NavDirection.Left or NavDirection.Right))
            return false;
        var delta = direction == NavDirection.Left ? -.05f : .05f;
        if (navigation.Id == "windows-volume")
        {
            AudioAction(audio.Set(Math.Clamp(audio.Current.Volume + delta, 0, 1), expectedEndpoint: audio.Current.EndpointId));
            return true;
        }

        if (navigation.Id == "session-volume" && Controller.Session.Anchor is { } target)
        {
            if (!appAudio.CanSetVolume)
            {
                FeedbackText("O volume do jogo não está confirmado. Use A para silenciar as sessões identificadas.");
                return true;
            }

            var desired = Math.Clamp(appAudio.VolumeMaximum!.Value + delta, 0, 1);
            var context = audioGeneration;
            SetGameVolume(desired);
            return true;
        }

        if (navigation.Id == "mixer-volume" && mixerTarget is { } selected && mixerStates.GetValueOrDefault(WindowId(selected)) is { CanSetVolume: true } snapshot)
        {
            ChangeMixerVolume(selected, Math.Clamp(snapshot.VolumeMaximum!.Value + delta, 0, 1));
            return true;
        }

        return false;
    }

    private void BuildSessionMixer()
    {
        Paragraph("Ajuste apenas o aplicativo escolhido. Sessões de outros processos ficam separadas.");
        AddRow("mixer-refresh", "Atualizar aplicativos", "Consultar sessões de áudio", () =>
        {
            nextFeatureRead = 0;
            StartFeatureRead();
        });
        if (mixerTarget is { } target && Controller.Platform.Valid(target))
        {
            Section(target.Title);
            var state = mixerStates.GetValueOrDefault(WindowId(target));
            var volume = AddRow("mixer-volume", state?.CanSetVolume == true ? $"Volume · {state.VolumeMaximum:P0}" : "Volume não informado", "← / → ajusta o volume", () =>
            {
            }, state?.CanSetVolume == true);
            if (state?.CanSetVolume == true)
                AddSessionSlider(volume, state.VolumeMaximum!.Value, value => ChangeMixerVolume(target, value));
            AddRow("mixer-mute", state?.MuteState == ApplicationMuteState.Muted ? "Ativar áudio" : "Silenciar aplicativo", target.ProcessName, () =>
            {
                if (mixerStates.GetValueOrDefault(WindowId(target)) is not { CanSetMute: true } current)
                    return;
                StartSessionAction("mixer-mute", async token =>
                {
                    var change = await mixerAudio.SetMutedAsync(target, current.MuteState != ApplicationMuteState.Muted, featureEpoch, token);
                    mixerStates[WindowId(target)] = change.Snapshot;
                    return change.Result;
                }, RefreshRows);
            }, state?.CanSetMute == true);
        }

        Section("Aplicativos");
        foreach (var window in Controller.Apps.Where(item => !item.Protected).DistinctBy(item => (item.Pid, item.Started)).Take(12))
        {
            var selected = window;
            var state = mixerStates.GetValueOrDefault(WindowId(window));
            AddRow("mix:" + WindowId(window), window.Title, state?.CanSetVolume == true ? $"{state.VolumeMaximum:P0}" + (state.MuteState == ApplicationMuteState.Muted ? " · sem som" : "") : "Consultar áudio deste aplicativo", () =>
            {
                mixerTarget = selected;
                RefreshRows();
                navigation = new("mixer-volume");
                SetSelection(false);
                nextFeatureRead = 0;
                StartFeatureRead();
            });
        }

        AddRow("mixer-devices", "Saída e microfone", systemAudio.Current.OutputName, () => OpenFeature(OverlayRoute.AudioDevices, "device-refresh"));
        AddRow("back", "Voltar", "Painel da sessão", Back);
    }

    private void ChangeMixerVolume(WindowRef target, float value)
    {
        if (!NavigationAllowed || busy || TransitionPending)
            return;
        OperationTask = RunSessionAction("mixer-volume", async token =>
        {
            var change = await mixerAudio.SetVolumeAsync(target, value, featureEpoch, token);
            mixerStates[WindowId(target)] = change.Snapshot;
            return change.Result;
        }, UpdateMixerLabels);
    }

    private void SetGameVolume(float value)
    {
        if (!NavigationAllowed || busy || TransitionPending || Controller.Session.Anchor is not { } target || !appAudio.CanSetVolume)
            return;
        var context = audioGeneration;
        OperationTask = RunInPlaceCore(target, async token =>
        {
            var change = await applicationAudio.SetVolumeAsync(target, Math.Clamp(value, 0, 1), context, token);
            if (!exiting)
                ApplicationAudioChanged(change.Snapshot);
            return change.Result;
        }, "session-volume");
    }

    private void AddSessionSlider(Button button, float value, Action<float> commit)
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = Math.Clamp(value, 0, 1),
            SmallChange = .05,
            LargeChange = .1,
            TickFrequency = .05,
            IsSnapToTickEnabled = true,
            Focusable = true,
            Margin = new(0, 8, 0, 0),
            MinHeight = 24
        };
        slider.Style = (Style)FindResource("SessionVolumeSlider");
        button.Resources["SessionSlider"] = slider;
        System.Windows.Automation.AutomationProperties.SetName(slider, "Volume em porcentagem");
        slider.ValueChanged += (_, _) =>
        {
            if (!synchronizingFeatureSlider && slider.IsKeyboardFocusWithin && !slider.IsMouseCaptureWithin && NavigationAllowed && !busy && !TransitionPending)
                commit((float)slider.Value);
        };
        slider.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (NavigationAllowed && !busy && !TransitionPending)
                commit((float)slider.Value);
        };
        (((ActionButtonContent)button.Content).Texts).Children.Add(slider);
    }

    private void SyncSessionSlider(string id, float? value)
    {
        if (entries.FirstOrDefault(entry => entry.Id == id)?.Button.Resources["SessionSlider"] is not Slider slider)
            return;
        slider.IsEnabled = value is not null;
        if (value is null || slider.IsMouseCaptureWithin)
            return;
        synchronizingFeatureSlider = true;
        try
        {
            slider.Value = value.Value;
        }
        finally
        {
            synchronizingFeatureSlider = false;
        }
    }

    private bool HandleSessionSliderKey(KeyEventArgs args)
    {
        if (args.Key != Key.Tab
            || args.OriginalSource is Slider or TextBox
            || KeyboardModifiersReader() != ModifierKeys.None
            || entries.FirstOrDefault(entry => entry.Id == navigation.Id)?.Button.Resources["SessionSlider"] is not Slider slider)
            return false;
        if (NavigationAllowed && navigationKeysArmed && !busy && !TransitionPending)
            slider.Focus();
        args.Handled = true;
        return true;
    }

    private void BuildAudioDevices()
    {
        Paragraph("Escolha a saída do Windows para jogos e mídia. O volume físico da TV é independente.");
        AddRow("device-refresh", "Atualizar dispositivos", systemAudio.Current.Status, () =>
        {
            nextFeatureRead = 0;
            StartFeatureRead();
        });
        foreach (var output in systemAudio.Current.Outputs)
        {
            var chosen = output;
            var current = systemAudio.Current;
            AddRow("output:" + output.Id, output.Name, output.Id == current.ConsoleOutputId && output.Id == current.MediaOutputId ? "Em uso para jogos e mídia" : "Usar para jogos e mídia", () =>
            {
                var expected = systemAudio.Current;
                StartAudioAction("audio-output", token => systemAudio.SelectOutputAsync(expected.ConsoleOutputId, expected.MediaOutputId, chosen.Id, token), RefreshRows);
            });
        }

        AddRow("microphone", MicrophoneTitle, systemAudio.Current.MicrophoneName, ToggleMicrophone, systemAudio.Current.MicrophoneMuted is not null);
        AddRow("back", "Voltar", "Áudio da sessão", Back);
    }
}

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
                SetEntryDescription("output:" + output.Id, output.Id == systemAudio.Current.ConsoleOutputId && output.Id == systemAudio.Current.MediaOutputId ? "In use for games and media" : "Use for games and media");
            SetEntryDescription("device-refresh", systemAudio.Current.Status);
            return;
        }

        if (view.Current.Route != OverlayRoute.Mixer)
            return;
        foreach (var window in Controller.Apps.Where(item => !item.Protected).Take(12))
        {
            var state = mixerStates.GetValueOrDefault(WindowId(window));
            SetEntryDescription("mix:" + WindowId(window), state?.CanSetVolume == true ? $"{state.VolumeMaximum:P0}" + (state.MuteState == ApplicationMuteState.Muted ? " · muted" : "") : "Check this app's audio");
        }

        if (mixerTarget is not { } target || mixerStates.GetValueOrDefault(WindowId(target)) is not { } selected)
            return;
        SetFeatureEntry("mixer-volume", selected.CanSetVolume ? $"Volume · {selected.VolumeMaximum:P0}" : "Volume unavailable", InputHint("← / → adjusts volume", "Arrow keys adjust · Tab leaves the slider", "Drag to adjust volume"), selected.CanSetVolume);
        SetFeatureEntry("mixer-mute", selected.MuteState == ApplicationMuteState.Muted ? "Unmute audio" : "Mute app", target.ProcessName, selected.CanSetMute);
        SyncSessionSlider("mixer-volume", selected.VolumeMaximum);
    }

    private static string VolumeRange(ApplicationAudioSnapshot state) => state.VolumeMaximum - state.VolumeMinimum > .01f ? $"{state.VolumeMinimum:P0}–{state.VolumeMaximum:P0}" : $"{state.VolumeMaximum:P0}";
    private string GameVolumeTitle => appAudio.CanSetMute ? "Game · " + (appAudio.MuteState == ApplicationMuteState.Muted ? "muted" : appAudio.MuteState == ApplicationMuteState.Mixed ? "mixed audio" : appAudio.CanSetVolume ? VolumeRange(appAudio) : "sound on") : "Game audio";
    private string GameVolumeDescription => (appAudio.Availability == ApplicationAudioAvailability.Partial ? "Partial audio · " : "") + (appAudio.CanSetVolume
        ? InputHint("← / → volume · A toggles mute", "Arrow keys adjust · Enter toggles mute", "Drag volume · click to toggle mute")
        : appAudio.CanSetMute ? InputHint("A toggles mute · volume unavailable", "Enter toggles mute · volume unavailable", "Click to toggle mute · volume unavailable")
        : "Start audio in the game to adjust it");
    private string WindowsVolumeTitle => audio.Current.Available ? audio.Current.Muted ? "Output · muted" : $"Output · {audio.Current.Volume:P0}" : "Output unavailable";
    private string MicrophoneTitle => systemAudio.Current.MicrophoneMuted switch
    {
        true => "Microphone · muted",
        false => "Microphone · on",
        _ => "Microphone unavailable"
    };

    private void UpdateSessionFeatureLabels()
    {
        SetFeatureEntry("session-volume", GameVolumeTitle, GameVolumeDescription, appAudio.CanSetMute);
        SetFeatureEntry("windows-volume", WindowsVolumeTitle, systemAudio.Current.OutputName, audio.Current.Available);
        SetFeatureEntry("microphone", MicrophoneTitle, systemAudio.Current.MicrophoneName, systemAudio.Current.MicrophoneMuted is not null);
        var media = CurrentMedia;
        SyncSessionSlider("session-volume", appAudio.VolumeMaximum);
        SyncSessionSlider("windows-volume", audio.Current.Available ? audio.Current.Volume : null);
        SetEntryDescription("session-media", media is null ? "Playback in supported apps" : (media.Playing ? "Playing · " : "Paused · ") + (media.Title.Length > 0 ? media.Title : "Media playback"));
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
            SetSelection();
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
                FeedbackText(InputHint("Volume unconfirmed. Press A to mute.", "Volume unconfirmed. Press Enter to mute.", "Volume unconfirmed. Click to mute."));
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
        Paragraph("Adjust only the selected app. Other processes keep their own audio settings.");
        AddRow("mixer-refresh", "Refresh apps", "Check audio sessions", () =>
        {
            nextFeatureRead = 0;
            StartFeatureRead();
        });
        if (mixerTarget is { } target && Controller.Platform.Valid(target))
        {
            Section(target.Title);
            var state = mixerStates.GetValueOrDefault(WindowId(target));
            var volume = AddRow("mixer-volume", state?.CanSetVolume == true ? $"Volume · {state.VolumeMaximum:P0}" : "Volume unavailable", "← / → adjusts volume", () =>
            {
            }, state?.CanSetVolume == true);
            if (state?.CanSetVolume == true)
                AddSessionSlider(volume, state.VolumeMaximum!.Value, value => ChangeMixerVolume(target, value));
            AddRow("mixer-mute", state?.MuteState == ApplicationMuteState.Muted ? "Unmute audio" : "Mute app", target.ProcessName, () =>
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

        Section("Apps");
        foreach (var window in Controller.Apps.Where(item => !item.Protected).DistinctBy(item => (item.Pid, item.Started)).Take(12))
        {
            var selected = window;
            var state = mixerStates.GetValueOrDefault(WindowId(window));
            AddRow("mix:" + WindowId(window), window.Title, state?.CanSetVolume == true ? $"{state.VolumeMaximum:P0}" + (state.MuteState == ApplicationMuteState.Muted ? " · muted" : "") : "Check this app's audio", () =>
            {
                mixerTarget = selected;
                RefreshRows();
                navigation = new("mixer-volume");
                SetSelection();
                nextFeatureRead = 0;
                StartFeatureRead();
            });
        }

        AddRow("mixer-devices", "Output and microphone", systemAudio.Current.OutputName, () => OpenFeature(OverlayRoute.AudioDevices, "device-refresh"));
        AddRow("back", "Back", "Session panel", Back);
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
        System.Windows.Automation.AutomationProperties.SetName(slider, "Volume as a percentage");
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
        Paragraph("Choose the Windows output for games and media. The TV's own volume is separate.");
        AddRow("device-refresh", "Refresh devices", systemAudio.Current.Status, () =>
        {
            nextFeatureRead = 0;
            StartFeatureRead();
        });
        foreach (var output in systemAudio.Current.Outputs)
        {
            var chosen = output;
            var current = systemAudio.Current;
            AddRow("output:" + output.Id, output.Name, output.Id == current.ConsoleOutputId && output.Id == current.MediaOutputId ? "In use for games and media" : "Use for games and media", () =>
            {
                var expected = systemAudio.Current;
                StartAudioAction("audio-output", token => systemAudio.SelectOutputAsync(expected.ConsoleOutputId, expected.MediaOutputId, chosen.Id, token), RefreshRows);
            });
        }

        AddRow("microphone", MicrophoneTitle, systemAudio.Current.MicrophoneName, ToggleMicrophone, systemAudio.Current.MicrophoneMuted is not null);
        AddRow("back", "Back", "Session audio", Back);
    }
}

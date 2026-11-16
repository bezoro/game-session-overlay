namespace ApolloOverlay;

public partial class MainWindow
{
    private void OpenPrimaryTab(int index)
    {
        if (!ValidateNavigationOwnership() || (busy && !updateBusy) || TransitionPending || Confirming || updateCommitting)
            return;
        var tab = index switch
        {
            1 => OverlayTab.Apps,
            2 => OverlayTab.Audio,
            _ => OverlayTab.Hub
        };
        if (view.Tab != tab || view.Depth != 0)
        {
            var button = tab switch
            {
                OverlayTab.Apps => AppsTab,
                OverlayTab.Audio => SettingsTab,
                _ => HomeTab
            };
            RunInterfaceAction(button, () => NavigateTab(tab), FeedbackCue.Confirmation);
        }
    }

    private void NavigatePrimaryTab(int direction)
    {
        var current = view.Tab switch
        {
            OverlayTab.Apps => 1,
            OverlayTab.Audio => 2,
            _ => 0
        };
        OpenPrimaryTab((current + direction + 3) % 3);
    }

    private void BuildSessionDashboard()
    {
        Section("Session audio");
        var gameAudio = HubAudioSnapshot;
        var gameVolume = AddRow("session-volume", GameVolumeTitle, GameVolumeDescription, ToggleGameAudio, gameAudio?.CanSetMute == true);
        AddSessionSlider(gameVolume, gameAudio?.VolumeMaximum ?? 0, value => SetGameVolume(value));
        SyncSessionSlider("session-volume", gameAudio?.VolumeMaximum);
        var outputVolume = AddRow("windows-volume", WindowsVolumeTitle, systemAudio.Current.OutputName, () => ChangeMasterAudio(muted: !audio.Current.Muted), audio.Current.Available);
        AddSessionSlider(outputVolume, audio.Current.Volume, value => ChangeMasterAudio(value));
        SyncSessionSlider("windows-volume", audio.Current.Available ? audio.Current.Volume : null);
        var first = Rows.RowDefinitions.Count;
        AddRow("microphone", MicrophoneTitle, systemAudio.Current.MicrophoneName, ToggleMicrophone, systemAudio.Current.MicrophoneMuted is not null, row: narrow ? null : first, column: 0);
        AddRow("session-mixer", "Audio mixer", "Audio for each app", () => OpenFeature(OverlayRoute.Mixer, "mixer-refresh"), row: narrow ? null : first, column: 1);
        Section("Your session");
        first = Rows.RowDefinitions.Count;
        AddRow("session-profiles", "Setup", sessionProfiles.Items.FirstOrDefault(profile => profile.Id == sessionProfiles.ActiveId)?.Name ?? "TV or Desktop", () => OpenFeature(OverlayRoute.Profiles, "profile:tv"), row: narrow ? null : first, column: 0);
        AddRow("session-capture", "Capture screen", "Save a screenshot of the display", CaptureSession, row: narrow ? null : first, column: 1);
        first = Rows.RowDefinitions.Count;
        AddRow("session-media", "Media", CurrentMedia?.Title ?? "Playback controls", () => OpenFeature(OverlayRoute.Media, "media-toggle"), row: narrow ? null : first, column: 0);
        AddRow("session-keyboard", "Keyboard", "Type with a controller", () => OpenKeyboard(false), row: narrow ? null : first, column: 1);
        AddRow("session-gallery", "Your captures", "Local session gallery", OpenCaptures);
    }

    private void OpenFeature(OverlayRoute route, string first)
    {
        OpenView(new(route, SelectedId: first));
        StartFeatureRead();
    }

    private bool BuildSessionFeatureView()
    {
        switch (view.Current.Route)
        {
            case OverlayRoute.Mixer:
                BuildSessionMixer();
                return true;
            case OverlayRoute.AudioDevices:
                BuildAudioDevices();
                return true;
            case OverlayRoute.Profiles:
                BuildSessionProfiles();
                return true;
            case OverlayRoute.ProfileEdit:
                BuildProfileEditor();
                return true;
            case OverlayRoute.Media:
                BuildSessionMedia();
                return true;
            case OverlayRoute.Keyboard:
                BuildSessionKeyboard();
                return true;
            case OverlayRoute.Captures:
                BuildCaptures();
                return true;
            case OverlayRoute.CaptureDetail:
                BuildCaptureDetail();
                return true;
            case OverlayRoute.Appearance:
                BuildAppearance();
                return true;
            case OverlayRoute.ColorEdit:
                BuildColorEditor();
                return true;
            default:
                return false;
        }
    }

    private string? SessionFeatureTitle => view.Current.Route switch
    {
        OverlayRoute.Mixer => "App mixer",
        OverlayRoute.AudioDevices => "Audio devices",
        OverlayRoute.Profiles => "Your setup",
        OverlayRoute.ProfileEdit => "Edit profile",
        OverlayRoute.Media => "Your music",
        OverlayRoute.Keyboard => "Keyboard",
        OverlayRoute.Captures => "Your captures",
        OverlayRoute.CaptureDetail => "Session capture",
        OverlayRoute.Appearance => "Appearance",
        OverlayRoute.ColorEdit => appearanceColorRole is { } role ? AppearanceCatalog.Roles.First(item => item.Role == role).Name : "Edit color",
        _ => null
    };
}

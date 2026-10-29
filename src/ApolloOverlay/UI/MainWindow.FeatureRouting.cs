using System.Windows;
using System.Windows.Media;

namespace ApolloOverlay;

public partial class MainWindow
{
    private void OpenPrimaryAudio()
    {
        if (!NavigationAllowed || TransitionPending || Confirming || view.Current.Route == OverlayRoute.WindowsAudio)
            return;
        if (updateBusy)
            CancelUpdateInteraction();
        RunInterfaceAction(SettingsTab, () => Subpage("audio", "volume"), FeedbackCue.Navigation);
    }

    private void NavigatePrimaryTab(int direction)
    {
        if (Confirming || TransitionPending || updateCommitting)
            return;
        var current = view.Current.Route == OverlayRoute.WindowsAudio ? 2 : page == 1 ? 1 : 0;
        var next = (current + direction + 3) % 3;
        if (next == 2)
            OpenPrimaryAudio();
        else
            Page(next);
    }

    private FrameworkElement? SessionFeatureIcon(string id)
    {
        var key = id.StartsWith("output:", StringComparison.Ordinal) ? "speaker" : id.StartsWith("capture:", StringComparison.Ordinal) ? "gallery" : id.StartsWith("mix:", StringComparison.Ordinal) ? "speaker" : id switch
        {
            "session-volume" or "windows-volume" or "session-mixer" or "mixer-volume" or "mixer-refresh" => "speaker",
            "microphone" => "microphone",
            "session-profiles" or "profile-new" => "profile",
            "session-media" => "play",
            "media-toggle" => CurrentMedia?.Playing == true ? "pause" : "play",
            "media-next" => "next",
            "media-previous" => "previous",
            "session-keyboard" or "keyboard-editor" => "keyboard",
            "keyboard-target" => "window",
            "session-capture" => "camera",
            "session-gallery" or "capture-folder" or "capture-refresh" => "gallery",
            "audio-devices" or "mixer-devices" or "device-refresh" => "speaker",
            _ => null
        };
        if (key is null)
            return null;
        var icon = new VectorIcon
        {
            Data = (Geometry)FindResource("Icon." + key),
            Width = 26,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        icon.SetResourceReference(VectorIcon.FillProperty, ThemeSupport.SecondaryText);
        return icon;
    }

    private void BuildSessionDashboard()
    {
        Section("Session audio");
        var gameVolume = AddRow("session-volume", GameVolumeTitle, GameVolumeDescription, ToggleGameAudio, appAudio.CanSetMute);
        AddSessionSlider(gameVolume, appAudio.VolumeMaximum ?? 0, value => SetGameVolume(value));
        SyncSessionSlider("session-volume", appAudio.VolumeMaximum);
        var outputVolume = AddRow("windows-volume", WindowsVolumeTitle, systemAudio.Current.OutputName, () => AudioAction(audio.Set(muted: !audio.Current.Muted, expectedEndpoint: audio.Current.EndpointId)), audio.Current.Available);
        if (audio.Current.Available)
            AddSessionSlider(outputVolume, audio.Current.Volume, value => AudioAction(audio.Set(value, expectedEndpoint: audio.Current.EndpointId)));
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
            default:
                return false;
        }
    }

    private string? SessionFeatureTitle => view.Current.Route switch
    {
        OverlayRoute.Mixer => "App mixer",
        OverlayRoute.AudioDevices => "Output and microphone",
        OverlayRoute.Profiles => "Your setup",
        OverlayRoute.ProfileEdit => "Edit profile",
        OverlayRoute.Media => "Your music",
        OverlayRoute.Keyboard => "Keyboard",
        OverlayRoute.Captures => "Your captures",
        OverlayRoute.CaptureDetail => "Session capture",
        _ => null
    };
}

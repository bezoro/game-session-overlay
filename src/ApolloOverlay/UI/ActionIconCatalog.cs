using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ApolloOverlay;

/// <summary>One source for action meaning, original vector frames and semantic icon colors.</summary>
internal static class ActionIconCatalog
{
    internal static VectorIcon Create(FrameworkElement owner, string action, double size = 26, bool muted = false, bool playing = false, bool rightSide = false)
    {
        var asset = Asset(action, muted, playing, rightSide);
        var tone = action is "exit" or "close" or "close-detail" or "external-close" or "confirm-hold" ? ThemeSupport.Critical : Tone(asset);
        var icon = new VectorIcon
        {
            Data = (Geometry)owner.FindResource("Icon." + asset),
            SourceSize = asset == "playnite" ? new Size(1024, 1024) : new Size(24, 24),
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        void RefreshTone() => icon.SetResourceReference(VectorIcon.FillProperty, icon.IsEnabled ? tone : ThemeSupport.DisabledText);
        icon.IsEnabledChanged += (_, _) => RefreshTone();
        RefreshTone();
        return icon;
    }

    private static string Tone(string asset) => asset switch
    {
        "speaker" or "mute" or "microphone" or "play" or "pause" or "next" or "previous" => ThemeSupport.IconAudio,
        "controller" or "gamebar" or "playnite" or "window" or "save" or "check" or "power" => ThemeSupport.IconGame,
        "camera" or "gallery" or "folder" => ThemeSupport.IconCapture,
        "settings" or "options" or "timer" or "resize" or "pulse" or "book" or "edit" or "profile" or "panel-left" or "panel-right" or "backspace" => ThemeSupport.IconTools,
        "delete" => ThemeSupport.Critical,
        _ => ThemeSupport.IconNavigation
    };

    private static string Asset(string action, bool muted, bool playing, bool rightSide)
    {
        if (action.StartsWith("favorite:", StringComparison.Ordinal))
            action = action[9..];
        if (action.StartsWith("window:", StringComparison.Ordinal) || action.StartsWith("recent:", StringComparison.Ordinal))
            return "window";
        if (action.StartsWith("output:", StringComparison.Ordinal) || action.StartsWith("mix:", StringComparison.Ordinal))
            return "speaker";
        if (action.StartsWith("capture:", StringComparison.Ordinal))
            return "gallery";
        if (action.StartsWith("profile-edit:", StringComparison.Ordinal))
            return "edit";
        if (action.StartsWith("profile:", StringComparison.Ordinal))
            return "profile";
        if (action.StartsWith("release:", StringComparison.Ordinal))
            return "download";
        if (action.StartsWith("rollback:", StringComparison.Ordinal))
            return "history";
        if (action.StartsWith("media-source:", StringComparison.Ordinal))
            return "play";
        if (action.StartsWith("playnite-folder:", StringComparison.Ordinal))
            return "folder";
        if (action.StartsWith("appearance-preset:", StringComparison.Ordinal))
            return "profile";
        if (action.StartsWith("appearance-color:", StringComparison.Ordinal))
            return "edit";
        return action switch
        {
            "session-card" or "previous-window" or "back" or "back-header" => "back",
            "setup-back" or "setup-skip" or "playnite-browser-back" or "color-cancel" => "back",
            "setup-next" or "playnite-page-next" => "next",
            "playnite-page-previous" => "previous",
            "playnite-native" => "folder",
            "playnite-use" or "color-apply" => "check",
            "choose-session" => "window",
            "operation-text" => "info",
            "dismiss-operation" => "close",
            "restore-favorites" or "appearance-defaults" or "appearance-discard" => "undo",
            "appearance-save" => "save",
            "appearance-preview" => "window",
            "color-red" or "color-green" or "color-blue" => "options",
            "color-hex" => "keyboard",
            "steam" or "steam-overlay" or "test-steam" => "steam",
            "guide" or "chord" or "gamebar" => "gamebar",
            "session" or "select-session" or "profile-game" => "controller",
            "apps" or "next-apps" => "apps",
            "refresh-apps" or "mixer-refresh" or "device-refresh" or "media-refresh" or "capture-refresh" or "check-updates" => "refresh",
            "taskview" or "minimize" => "taskview",
            "display" or "profile-monitor" or "profile-presentation" => "profile",
            "switch-detail" => "window",
            "volume" or "audio" or "volume-down" or "volume-up" or "application-mute" or "session-volume" or "windows-volume" or "session-mixer" or "mixer-volume" or "profile-output" or "profile-volume" or "audio-devices" or "mixer-devices" or "sound-feedback" => "speaker",
            "mute" => muted ? "speaker" : "mute",
            "mixer-mute" => "speaker",
            "microphone" => "microphone",
            "close" or "close-detail" or "external-close" or "confirm-hold" => "close",
            "exit" or "startup" or "administrator-restart" or "administrator-startup" => "power",
            "playnite" or "configure-playnite" => "playnite",
            "session-profiles" or "profile-new" or "profile-current" => "profile",
            "session-media" => "play",
            "media-toggle" => playing ? "pause" : "play",
            "media-next" or "capture-next" => "next",
            "media-previous" or "capture-previous" => "previous",
            "session-keyboard" or "keyboard-editor" or "keyboard-insert" or "shortcut" => "keyboard",
            "keyboard-target" => "window",
            "session-capture" => "camera",
            "session-gallery" => "gallery",
            "capture-folder" => "folder",
            "updates" or "prepare-update" or "apply-update" => "download",
            "apply-rollback" or "history-previous" or "history-next" or "last-operation" => "history",
            "update-notes" or "operation-details" or "details" or "window-information" => "info",
            "side" or "profile-side" => rightSide ? "panel-right" : "panel-left",
            "ui-scale" or "profile-scale" => "resize",
            "deadzone" or "customize" or "options" => "options",
            "guide-limit" => "timer",
            "haptic-feedback" or "test-feedback" => "pulse",
            "setup" or "setup-invitation" => "book",
            "integrations" => "usb",
            "save" or "save-pending" or "profile-save" => "save",
            "discard-pending" or "profile-undo" => "undo",
            "profile-name" => "edit",
            "keyboard-delete" => "backspace",
            "keyboard-clear" => "delete",
            "profile-accept" => "check",
            _ => "settings"
        };
    }
}

/// <summary>Compact icon and readable caption without positional child lookups.</summary>
internal sealed class IconLabel : Grid
{
    internal TextBlock Label
    {
        get;
    }

    internal IconLabel(FrameworkElement owner, string action, string caption, double fontSize = 13, double iconSize = 16, double gap = 6)
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        ColumnDefinitions.Add(new());
        var icon = ActionIconCatalog.Create(owner, action, iconSize);
        icon.Margin = new(0, 0, gap, 0);
        Children.Add(icon);
        Label = new()
        {
            Text = caption,
            FontFamily = new("Segoe UI Variable Text, Segoe UI"),
            FontSize = fontSize,
            FontWeight = FontWeights.Medium,
            FontStyle = FontStyles.Normal,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetColumn(Label, 1);
        Children.Add(Label);
    }
}

using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ApolloOverlay;

internal static class ThemeSupport
{
    internal const string Surface = "ApplicationBackgroundBrush", Card = "CardBackgroundFillColorDefaultBrush", Control = "ControlFillColorDefaultBrush", Stroke = "ControlStrokeColorDefaultBrush", Text = "TextFillColorPrimaryBrush", SecondaryText = "TextFillColorSecondaryBrush", DisabledText = "TextFillColorDisabledBrush", Success = "SystemFillColorSuccessBrush", SuccessBackground = "SystemFillColorSuccessBackgroundBrush", Critical = "SystemFillColorCriticalBrush", CriticalBackground = "SystemFillColorCriticalBackgroundBrush";
    internal const string Focus = "SessionFocusBrush", FocusBackground = "SessionFocusBackgroundBrush", Accent = "SessionAccentBrush";
    internal const string IconGame = "SessionIconGameBrush", IconNavigation = "SessionIconNavigationBrush", IconAudio = "SessionIconAudioBrush", IconTools = "SessionIconToolsBrush", IconCapture = "SessionIconCaptureBrush";
    private static ResourceDictionary? validation, contrast, palette;
    private static ResourceDictionary Palette(bool light)
    {
        var result = new ResourceDictionary();
        foreach (var pair in new[]
        {
            (Surface, light ? "#FFF0F2F5" : "#FF15191F"),
            (Card, light ? "#FFFFFFFF" : "#FF1C222B"),
            (Control, light ? "#FFE5EAF0" : "#FF242C37"),
            (Stroke, light ? "#FFC1CBD7" : "#FF384453"),
            (Text, light ? "#FF16202C" : "#FFF2F3F5"),
            (SecondaryText, light ? "#FF4F6074" : "#FFA5B0BD"),
            (DisabledText, light ? "#FF657386" : "#FF8593A5"),
            (Focus, light ? "#FF24577F" : "#FF92BDEA"),
            (FocusBackground, light ? "#FFE0ECF8" : "#FF253B51"),
            (IconGame, light ? "#FF267154" : "#FF86CEA8"),
            (IconNavigation, light ? "#FF3266A0" : "#FF9BBEEC"),
            (IconAudio, light ? "#FF8A5C1D" : "#FFE3BB7F"),
            (IconTools, light ? "#FF7057A1" : "#FFBBA8E1"),
            (IconCapture, light ? "#FFA14C70" : "#FFE2A0B9"),
            (Accent, light ? "#FF81562E" : "#FFC89A70"),
            (Success, light ? "#FF176A3C" : "#FF78C999"),
            (SuccessBackground, light ? "#FFE2F3E9" : "#FF20382D"),
            (Critical, light ? "#FFAC2A36" : "#FFFF9296"),
            (CriticalBackground, light ? "#FFFBE4E6" : "#FF442A32")
        }

        )
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(pair.Item2)!;
            brush.Freeze();
            result[pair.Item1] = brush;
        }

        return result;
    }

    private static bool Light()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    internal static void ApplySystemPalette(Application? application)
    {
        if (application is null)
            return;
        if (contrast is not null)
        {
            application.Resources.MergedDictionaries.Remove(contrast);
            contrast = null;
        }

        if (SystemParameters.HighContrast)
        {
            contrast = new();
            foreach (var key in new[]
            {
                Surface,
                Card,
                Control,
                SuccessBackground,
                CriticalBackground,
                FocusBackground
            }

            )
                contrast[key] = SystemColors.WindowBrush;
            foreach (var key in new[]
            {
                Text,
                SecondaryText,
                Stroke,
                Accent,
                IconGame,
                IconNavigation,
                IconAudio,
                IconTools,
                IconCapture
            }

            )
                contrast[key] = SystemColors.WindowTextBrush;
            contrast[DisabledText] = SystemColors.GrayTextBrush;
            contrast[Success] = SystemColors.HighlightBrush;
            contrast[Focus] = SystemColors.HighlightBrush;
            contrast[Critical] = SystemColors.WindowTextBrush;
            application.Resources.MergedDictionaries.Add(contrast);
            return;
        }

        if (validation is not null)
            return;
        if (palette is not null)
            application.Resources.MergedDictionaries.Remove(palette);
        palette = Palette(Light());
        application.Resources.MergedDictionaries.Add(palette);
    }

    internal static void FollowSystem(Application application)
    {
        if (validation is not null)
        {
            application.Resources.MergedDictionaries.Remove(validation);
            validation = null;
        }

        if (!application.Resources.MergedDictionaries.Any(dictionary => dictionary.Source?.ToString() == "pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml"))
            application.Resources.MergedDictionaries.Add(new()
            {
                Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml", UriKind.Absolute)
            });
        ApplySystemPalette(application);
    }

    internal static void ForceForValidation(Application application, bool light)
    {
        FollowSystem(application);
        validation = Palette(light);
        application.Resources.MergedDictionaries.Add(validation);
    }

    internal static bool FollowsSystem(Application application) => validation is null;
}

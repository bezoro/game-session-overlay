using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ApolloOverlay;

/// <summary>Applies validated local color data to dynamic resources without rebuilding the visual tree.</summary>
internal static class ThemeSupport
{
    internal const string Surface = "ApplicationBackgroundBrush";
    internal const string Card = "CardBackgroundFillColorDefaultBrush";
    internal const string Control = "ControlFillColorDefaultBrush";
    internal const string Stroke = "ControlStrokeColorDefaultBrush";
    internal const string FocusBackground = "SessionFocusBackgroundBrush";
    internal const string Text = "TextFillColorPrimaryBrush";
    internal const string SecondaryText = "TextFillColorSecondaryBrush";
    internal const string MutedText = "SessionMutedTextBrush";
    internal const string DisabledText = "TextFillColorDisabledBrush";
    internal const string TextSelection = "SessionTextSelectionBrush";
    internal const string TextSelectionForeground = "SessionTextSelectionForegroundBrush";
    internal const string Caret = "SessionCaretBrush";
    internal const string Focus = "SessionFocusBrush";
    internal const string Accent = "SessionAccentBrush";
    internal const string SliderActive = "SessionSliderActiveBrush";
    internal const string SliderTrack = "SessionSliderTrackBrush";
    internal const string SliderThumb = "SessionSliderThumbBrush";
    internal const string SliderThumbStroke = "SessionSliderThumbStrokeBrush";
    internal const string ToggleOn = "SessionToggleOnBrush";
    internal const string ToggleOff = "SessionToggleOffBrush";
    internal const string ToggleThumb = "SessionToggleThumbBrush";
    internal const string ToggleThumbOn = "SessionToggleThumbOnBrush", ToggleThumbOff = "SessionToggleThumbOffBrush";
    internal const string Success = "SystemFillColorSuccessBrush";
    internal const string SuccessBackground = "SystemFillColorSuccessBackgroundBrush";
    internal const string Warning = "SessionWarningBrush";
    internal const string WarningBackground = "SessionWarningBackgroundBrush";
    internal const string Critical = "SystemFillColorCriticalBrush";
    internal const string CriticalBackground = "SystemFillColorCriticalBackgroundBrush";
    internal const string Information = "SessionInformationBrush";
    internal const string InformationBackground = "SessionInformationBackgroundBrush";
    internal const string IconGame = "SessionIconGameBrush";
    internal const string IconNavigation = "SessionIconNavigationBrush";
    internal const string IconAudio = "SessionIconAudioBrush";
    internal const string IconTools = "SessionIconToolsBrush";
    internal const string IconCapture = "SessionIconCaptureBrush";
    internal const string IconNeutral = "SessionIconNeutralBrush";
    internal const string PromptA = "SessionPromptABrush";
    internal const string PromptB = "SessionPromptBBrush";
    internal const string PromptX = "SessionPromptXBrush";
    internal const string PromptY = "SessionPromptYBrush";
    internal const string PromptShoulder = "SessionPromptShoulderBrush";
    internal const string PromptDpad = "SessionPromptDpadBrush";
    internal const string PromptGuide = "SessionPromptGuideBrush";
    internal const string PromptKeyboard = "SessionPromptKeyboardBrush";
    internal const string PromptMouse = "SessionPromptMouseBrush";
    internal const string PromptForeground = "SessionPromptForegroundBrush";
    internal const string Hover = "SessionHoverBrush";
    internal const string Press = "SessionPressBrush";
    internal const string Sheen = "SessionSheenBrush";
    internal const string Shadow = "SessionShadowBrush";
    internal const string SheenColor = "SessionSheenColor", ShadowColor = "SessionShadowColor";

    private static AppearanceSettings current = new();
    private static ColorPalette? applied;
    private static ResourceDictionary? palette;
    private static bool? validationLight;
    private static bool validationHighContrast, appliedHighContrast;

    internal static void ApplyAppearance(Application? application, AppearanceSettings? appearance)
    {
        current = AppearanceCatalog.Normalize(appearance);
        ApplySystemPalette(application);
    }

    internal static bool UsesLightPalette(AppearanceSettings? appearance) => appearance?.Mode switch
    {
        "Light" => true,
        "Dark" => false,
        _ => validationLight ?? Light()
    };

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
        var highContrast = SystemParameters.HighContrast || validationHighContrast;
        var resolved = AppearanceCatalog.Resolve(current, UsesLightPalette(current));
        if (!highContrast && !appliedHighContrast && applied == resolved && palette is not null
            && application.Resources.MergedDictionaries.Contains(palette))
            return;

        var replacement = highContrast ? HighContrastPalette() : Palette(resolved);
        var index = palette is null ? -1 : application.Resources.MergedDictionaries.IndexOf(palette);
        if (index >= 0)
            application.Resources.MergedDictionaries[index] = replacement;
        else
            application.Resources.MergedDictionaries.Add(replacement);
        palette = replacement;
        applied = resolved;
        appliedHighContrast = highContrast;
    }

    private static ResourceDictionary Palette(ColorPalette colors)
    {
        var resources = new ResourceDictionary();
        foreach (var info in AppearanceCatalog.Roles)
        {
            var color = (Color)ColorConverter.ConvertFromString(colors.Get(info.Role));
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[info.ResourceKey] = brush;
            resources[ColorKey(info.ResourceKey)] = color;
        }
        resources[ToggleThumbOn] = resources[ToggleThumb];
        resources[ToggleThumbOff] = resources[ToggleThumb];
        return resources;
    }

    private static ResourceDictionary HighContrastPalette()
    {
        var resources = new ResourceDictionary();
        foreach (var info in AppearanceCatalog.Roles)
        {
            var brush = info.Role switch
            {
                PaletteRole.Surface or PaletteRole.Card or PaletteRole.Control or PaletteRole.FocusBackground
                    or PaletteRole.SuccessBackground or PaletteRole.WarningBackground or PaletteRole.CriticalBackground
                    or PaletteRole.InformationBackground or PaletteRole.SliderTrack or PaletteRole.ToggleOff
                    or PaletteRole.PromptForeground => SystemColors.WindowBrush,
                PaletteRole.Focus or PaletteRole.Accent or PaletteRole.SliderThumb or PaletteRole.ToggleOn
                    or PaletteRole.TextSelection => SystemColors.HighlightBrush,
                PaletteRole.TextSelectionForeground => SystemColors.HighlightTextBrush,
                PaletteRole.DisabledText => SystemColors.GrayTextBrush,
                _ => SystemColors.WindowTextBrush
            };
            resources[info.ResourceKey] = brush;
            resources[ColorKey(info.ResourceKey)] = brush.Color;
        }
        // A single custom thumb color is shared in normal themes. Windows high
        // contrast needs the foreground corresponding to each actual track state.
        resources[ToggleThumbOn] = SystemColors.HighlightTextBrush;
        resources[ToggleThumbOff] = SystemColors.WindowTextBrush;
        return resources;
    }

    private static string ColorKey(string brushKey) => brushKey[..^"Brush".Length] + "Color";

    internal static void FollowSystem(Application application)
    {
        validationLight = null;
        validationHighContrast = false;
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
        validationLight = light;
        ApplySystemPalette(application);
    }

    /// <summary>Exercises Windows SystemColors resource mapping without changing the host OS preference.</summary>
    internal static void ForceHighContrastForValidation(Application application)
    {
        FollowSystem(application);
        validationHighContrast = true;
        ApplySystemPalette(application);
    }

    internal static bool FollowsSystem(Application application) => validationLight is null && !validationHighContrast;
}

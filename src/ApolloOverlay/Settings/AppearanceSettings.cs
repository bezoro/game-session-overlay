namespace ApolloOverlay;

/// <summary>Immutable, additive preferences; null palettes inherit the selected preset.</summary>
public sealed record AppearanceSettings(string Mode = "System", string Preset = "Warm", ColorPalette? Dark = null, ColorPalette? Light = null, int Version = 1);

public enum PaletteRole
{
    Surface,
    Card,
    Control,
    Stroke,
    FocusBackground,
    Text,
    SecondaryText,
    MutedText,
    DisabledText,
    TextSelection,
    TextSelectionForeground,
    Caret,
    Focus,
    Accent,
    SliderActive,
    SliderTrack,
    SliderThumb,
    SliderThumbStroke,
    ToggleOn,
    ToggleOff,
    ToggleThumb,
    Success,
    SuccessBackground,
    Warning,
    WarningBackground,
    Critical,
    CriticalBackground,
    Information,
    InformationBackground,
    IconGame,
    IconNavigation,
    IconAudio,
    IconTools,
    IconCapture,
    IconNeutral,
    PromptA,
    PromptB,
    PromptX,
    PromptY,
    PromptShoulder,
    PromptDpad,
    PromptGuide,
    PromptKeyboard,
    PromptMouse,
    PromptForeground,
    Hover,
    Press,
    Sheen,
    Shadow,
}

public sealed record PaletteRoleInfo(PaletteRole Role, string Name, string Group, string ResourceKey);
public sealed record AppearancePreset(string Id, string Name);

/// <summary>Color data only: no mutable collections, brushes, resource paths or executable content.</summary>
public sealed record ColorPalette
{
    public string Surface { get; init; } = "";
    public string Card { get; init; } = "";
    public string Control { get; init; } = "";
    public string Stroke { get; init; } = "";
    public string FocusBackground { get; init; } = "";
    public string Text { get; init; } = "";
    public string SecondaryText { get; init; } = "";
    public string MutedText { get; init; } = "";
    public string DisabledText { get; init; } = "";
    public string TextSelection { get; init; } = "";
    public string TextSelectionForeground { get; init; } = "";
    public string Caret { get; init; } = "";
    public string Focus { get; init; } = "";
    public string Accent { get; init; } = "";
    public string SliderActive { get; init; } = "";
    public string SliderTrack { get; init; } = "";
    public string SliderThumb { get; init; } = "";
    public string SliderThumbStroke { get; init; } = "";
    public string ToggleOn { get; init; } = "";
    public string ToggleOff { get; init; } = "";
    public string ToggleThumb { get; init; } = "";
    public string Success { get; init; } = "";
    public string SuccessBackground { get; init; } = "";
    public string Warning { get; init; } = "";
    public string WarningBackground { get; init; } = "";
    public string Critical { get; init; } = "";
    public string CriticalBackground { get; init; } = "";
    public string Information { get; init; } = "";
    public string InformationBackground { get; init; } = "";
    public string IconGame { get; init; } = "";
    public string IconNavigation { get; init; } = "";
    public string IconAudio { get; init; } = "";
    public string IconTools { get; init; } = "";
    public string IconCapture { get; init; } = "";
    public string IconNeutral { get; init; } = "";
    public string PromptA { get; init; } = "";
    public string PromptB { get; init; } = "";
    public string PromptX { get; init; } = "";
    public string PromptY { get; init; } = "";
    public string PromptShoulder { get; init; } = "";
    public string PromptDpad { get; init; } = "";
    public string PromptGuide { get; init; } = "";
    public string PromptKeyboard { get; init; } = "";
    public string PromptMouse { get; init; } = "";
    public string PromptForeground { get; init; } = "";
    public string Hover { get; init; } = "";
    public string Press { get; init; } = "";
    public string Sheen { get; init; } = "";
    public string Shadow { get; init; } = "";

    public string Get(PaletteRole role) => role switch
    {
        PaletteRole.Surface => Surface,
        PaletteRole.Card => Card,
        PaletteRole.Control => Control,
        PaletteRole.Stroke => Stroke,
        PaletteRole.FocusBackground => FocusBackground,
        PaletteRole.Text => Text,
        PaletteRole.SecondaryText => SecondaryText,
        PaletteRole.MutedText => MutedText,
        PaletteRole.DisabledText => DisabledText,
        PaletteRole.TextSelection => TextSelection,
        PaletteRole.TextSelectionForeground => TextSelectionForeground,
        PaletteRole.Caret => Caret,
        PaletteRole.Focus => Focus,
        PaletteRole.Accent => Accent,
        PaletteRole.SliderActive => SliderActive,
        PaletteRole.SliderTrack => SliderTrack,
        PaletteRole.SliderThumb => SliderThumb,
        PaletteRole.SliderThumbStroke => SliderThumbStroke,
        PaletteRole.ToggleOn => ToggleOn,
        PaletteRole.ToggleOff => ToggleOff,
        PaletteRole.ToggleThumb => ToggleThumb,
        PaletteRole.Success => Success,
        PaletteRole.SuccessBackground => SuccessBackground,
        PaletteRole.Warning => Warning,
        PaletteRole.WarningBackground => WarningBackground,
        PaletteRole.Critical => Critical,
        PaletteRole.CriticalBackground => CriticalBackground,
        PaletteRole.Information => Information,
        PaletteRole.InformationBackground => InformationBackground,
        PaletteRole.IconGame => IconGame,
        PaletteRole.IconNavigation => IconNavigation,
        PaletteRole.IconAudio => IconAudio,
        PaletteRole.IconTools => IconTools,
        PaletteRole.IconCapture => IconCapture,
        PaletteRole.IconNeutral => IconNeutral,
        PaletteRole.PromptA => PromptA,
        PaletteRole.PromptB => PromptB,
        PaletteRole.PromptX => PromptX,
        PaletteRole.PromptY => PromptY,
        PaletteRole.PromptShoulder => PromptShoulder,
        PaletteRole.PromptDpad => PromptDpad,
        PaletteRole.PromptGuide => PromptGuide,
        PaletteRole.PromptKeyboard => PromptKeyboard,
        PaletteRole.PromptMouse => PromptMouse,
        PaletteRole.PromptForeground => PromptForeground,
        PaletteRole.Hover => Hover,
        PaletteRole.Press => Press,
        PaletteRole.Sheen => Sheen,
        PaletteRole.Shadow => Shadow,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    public ColorPalette With(PaletteRole role, string color) => role switch
    {
        PaletteRole.Surface => this with { Surface = color },
        PaletteRole.Card => this with { Card = color },
        PaletteRole.Control => this with { Control = color },
        PaletteRole.Stroke => this with { Stroke = color },
        PaletteRole.FocusBackground => this with { FocusBackground = color },
        PaletteRole.Text => this with { Text = color },
        PaletteRole.SecondaryText => this with { SecondaryText = color },
        PaletteRole.MutedText => this with { MutedText = color },
        PaletteRole.DisabledText => this with { DisabledText = color },
        PaletteRole.TextSelection => this with { TextSelection = color },
        PaletteRole.TextSelectionForeground => this with { TextSelectionForeground = color },
        PaletteRole.Caret => this with { Caret = color },
        PaletteRole.Focus => this with { Focus = color },
        PaletteRole.Accent => this with { Accent = color },
        PaletteRole.SliderActive => this with { SliderActive = color },
        PaletteRole.SliderTrack => this with { SliderTrack = color },
        PaletteRole.SliderThumb => this with { SliderThumb = color },
        PaletteRole.SliderThumbStroke => this with { SliderThumbStroke = color },
        PaletteRole.ToggleOn => this with { ToggleOn = color },
        PaletteRole.ToggleOff => this with { ToggleOff = color },
        PaletteRole.ToggleThumb => this with { ToggleThumb = color },
        PaletteRole.Success => this with { Success = color },
        PaletteRole.SuccessBackground => this with { SuccessBackground = color },
        PaletteRole.Warning => this with { Warning = color },
        PaletteRole.WarningBackground => this with { WarningBackground = color },
        PaletteRole.Critical => this with { Critical = color },
        PaletteRole.CriticalBackground => this with { CriticalBackground = color },
        PaletteRole.Information => this with { Information = color },
        PaletteRole.InformationBackground => this with { InformationBackground = color },
        PaletteRole.IconGame => this with { IconGame = color },
        PaletteRole.IconNavigation => this with { IconNavigation = color },
        PaletteRole.IconAudio => this with { IconAudio = color },
        PaletteRole.IconTools => this with { IconTools = color },
        PaletteRole.IconCapture => this with { IconCapture = color },
        PaletteRole.IconNeutral => this with { IconNeutral = color },
        PaletteRole.PromptA => this with { PromptA = color },
        PaletteRole.PromptB => this with { PromptB = color },
        PaletteRole.PromptX => this with { PromptX = color },
        PaletteRole.PromptY => this with { PromptY = color },
        PaletteRole.PromptShoulder => this with { PromptShoulder = color },
        PaletteRole.PromptDpad => this with { PromptDpad = color },
        PaletteRole.PromptGuide => this with { PromptGuide = color },
        PaletteRole.PromptKeyboard => this with { PromptKeyboard = color },
        PaletteRole.PromptMouse => this with { PromptMouse = color },
        PaletteRole.PromptForeground => this with { PromptForeground = color },
        PaletteRole.Hover => this with { Hover = color },
        PaletteRole.Press => this with { Press = color },
        PaletteRole.Sheen => this with { Sheen = color },
        PaletteRole.Shadow => this with { Shadow = color },
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}

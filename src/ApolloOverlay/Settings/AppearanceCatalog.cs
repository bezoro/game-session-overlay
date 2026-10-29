using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ApolloOverlay;

/// <summary>Resolves fixed local color data and rejects palettes which hide essential content or focus.</summary>
public static class AppearanceCatalog
{
    private sealed record CanonicalAppearance(AppearanceSettings Value);
    private static readonly ConditionalWeakTable<AppearanceSettings, CanonicalAppearance> canonical = new();
    private static readonly AppearanceSettings defaults = new();
    public static IReadOnlyList<AppearancePreset> Presets
    {
        get;
    } = Array.AsReadOnly(new[]
    {
        new AppearancePreset("Warm", "Warm charcoal"),
        new AppearancePreset("Graphite", "Graphite"),
        new AppearancePreset("Ember", "Ember")
    });

    public static IReadOnlyList<PaletteRoleInfo> Roles
    {
        get;
    } = Array.AsReadOnly(new[]
    {
        new PaletteRoleInfo(PaletteRole.Surface, "Panel background", "Surfaces", "ApplicationBackgroundBrush"),
        new PaletteRoleInfo(PaletteRole.Card, "Elevated surface", "Surfaces", "CardBackgroundFillColorDefaultBrush"),
        new PaletteRoleInfo(PaletteRole.Control, "Control surface", "Surfaces", "ControlFillColorDefaultBrush"),
        new PaletteRoleInfo(PaletteRole.Stroke, "Border and divider", "Surfaces", "ControlStrokeColorDefaultBrush"),
        new PaletteRoleInfo(PaletteRole.FocusBackground, "Selected surface", "Surfaces", "SessionFocusBackgroundBrush"),
        new PaletteRoleInfo(PaletteRole.Text, "Primary text", "Text", "TextFillColorPrimaryBrush"),
        new PaletteRoleInfo(PaletteRole.SecondaryText, "Secondary text", "Text", "TextFillColorSecondaryBrush"),
        new PaletteRoleInfo(PaletteRole.MutedText, "Metadata text", "Text", "SessionMutedTextBrush"),
        new PaletteRoleInfo(PaletteRole.DisabledText, "Unavailable text", "Text", "TextFillColorDisabledBrush"),
        new PaletteRoleInfo(PaletteRole.TextSelection, "Selected text background", "Text", "SessionTextSelectionBrush"),
        new PaletteRoleInfo(PaletteRole.TextSelectionForeground, "Selected text foreground", "Text", "SessionTextSelectionForegroundBrush"),
        new PaletteRoleInfo(PaletteRole.Caret, "Text caret", "Text", "SessionCaretBrush"),
        new PaletteRoleInfo(PaletteRole.Focus, "Focus outline", "Controls", "SessionFocusBrush"),
        new PaletteRoleInfo(PaletteRole.Accent, "Primary accent", "Controls", "SessionAccentBrush"),
        new PaletteRoleInfo(PaletteRole.SliderActive, "Slider active track", "Controls", "SessionSliderActiveBrush"),
        new PaletteRoleInfo(PaletteRole.SliderTrack, "Slider remaining track", "Controls", "SessionSliderTrackBrush"),
        new PaletteRoleInfo(PaletteRole.SliderThumb, "Slider thumb", "Controls", "SessionSliderThumbBrush"),
        new PaletteRoleInfo(PaletteRole.SliderThumbStroke, "Slider thumb border", "Controls", "SessionSliderThumbStrokeBrush"),
        new PaletteRoleInfo(PaletteRole.ToggleOn, "Toggle on", "Controls", "SessionToggleOnBrush"),
        new PaletteRoleInfo(PaletteRole.ToggleOff, "Toggle off", "Controls", "SessionToggleOffBrush"),
        new PaletteRoleInfo(PaletteRole.ToggleThumb, "Toggle thumb", "Controls", "SessionToggleThumbBrush"),
        new PaletteRoleInfo(PaletteRole.Success, "Success icon and text", "Status", "SystemFillColorSuccessBrush"),
        new PaletteRoleInfo(PaletteRole.SuccessBackground, "Success surface", "Status", "SystemFillColorSuccessBackgroundBrush"),
        new PaletteRoleInfo(PaletteRole.Warning, "Warning icon and text", "Status", "SessionWarningBrush"),
        new PaletteRoleInfo(PaletteRole.WarningBackground, "Warning surface", "Status", "SessionWarningBackgroundBrush"),
        new PaletteRoleInfo(PaletteRole.Critical, "Error icon and text", "Status", "SystemFillColorCriticalBrush"),
        new PaletteRoleInfo(PaletteRole.CriticalBackground, "Error surface", "Status", "SystemFillColorCriticalBackgroundBrush"),
        new PaletteRoleInfo(PaletteRole.Information, "Information icon and text", "Status", "SessionInformationBrush"),
        new PaletteRoleInfo(PaletteRole.InformationBackground, "Information surface", "Status", "SessionInformationBackgroundBrush"),
        new PaletteRoleInfo(PaletteRole.IconGame, "Game icons", "Icons", "SessionIconGameBrush"),
        new PaletteRoleInfo(PaletteRole.IconNavigation, "Navigation icons", "Icons", "SessionIconNavigationBrush"),
        new PaletteRoleInfo(PaletteRole.IconAudio, "Audio icons", "Icons", "SessionIconAudioBrush"),
        new PaletteRoleInfo(PaletteRole.IconTools, "Tools icons", "Icons", "SessionIconToolsBrush"),
        new PaletteRoleInfo(PaletteRole.IconCapture, "Capture icons", "Icons", "SessionIconCaptureBrush"),
        new PaletteRoleInfo(PaletteRole.IconNeutral, "Neutral icons", "Icons", "SessionIconNeutralBrush"),
        new PaletteRoleInfo(PaletteRole.PromptA, "Controller A", "Input prompts", "SessionPromptABrush"),
        new PaletteRoleInfo(PaletteRole.PromptB, "Controller B", "Input prompts", "SessionPromptBBrush"),
        new PaletteRoleInfo(PaletteRole.PromptX, "Controller X", "Input prompts", "SessionPromptXBrush"),
        new PaletteRoleInfo(PaletteRole.PromptY, "Controller Y", "Input prompts", "SessionPromptYBrush"),
        new PaletteRoleInfo(PaletteRole.PromptShoulder, "Shoulder buttons", "Input prompts", "SessionPromptShoulderBrush"),
        new PaletteRoleInfo(PaletteRole.PromptDpad, "D-pad and sticks", "Input prompts", "SessionPromptDpadBrush"),
        new PaletteRoleInfo(PaletteRole.PromptGuide, "Guide button", "Input prompts", "SessionPromptGuideBrush"),
        new PaletteRoleInfo(PaletteRole.PromptKeyboard, "Keyboard keycaps", "Input prompts", "SessionPromptKeyboardBrush"),
        new PaletteRoleInfo(PaletteRole.PromptMouse, "Mouse hints", "Input prompts", "SessionPromptMouseBrush"),
        new PaletteRoleInfo(PaletteRole.PromptForeground, "Keycap characters", "Input prompts", "SessionPromptForegroundBrush"),
        new PaletteRoleInfo(PaletteRole.Hover, "Hover highlight", "Effects", "SessionHoverBrush"),
        new PaletteRoleInfo(PaletteRole.Press, "Press acknowledgement", "Effects", "SessionPressBrush"),
        new PaletteRoleInfo(PaletteRole.Sheen, "Selection sheen", "Effects", "SessionSheenBrush"),
        new PaletteRoleInfo(PaletteRole.Shadow, "Elevation shadow", "Effects", "SessionShadowBrush"),
    });

    private static readonly ColorPalette warmDark = new()
    {
        Surface = "#1B1918",
        Card = "#252321",
        Control = "#2D2926",
        Stroke = "#60564E",
        FocusBackground = "#3A2E24",
        Text = "#F3F0ED",
        SecondaryText = "#C5B9AE",
        MutedText = "#B1A398",
        DisabledText = "#A99D92",
        TextSelection = "#E8AE74",
        TextSelectionForeground = "#1B1918",
        Caret = "#E8AE74",
        Focus = "#E8AE74",
        Accent = "#E8AE74",
        SliderActive = "#E8AE74",
        SliderTrack = "#584D44",
        SliderThumb = "#F3F0ED",
        SliderThumbStroke = "#725B46",
        ToggleOn = "#E8AE74",
        ToggleOff = "#927F6E",
        ToggleThumb = "#1B1918",
        Success = "#94C79E",
        SuccessBackground = "#25382A",
        Warning = "#E4C18C",
        WarningBackground = "#423326",
        Critical = "#E9A3A3",
        CriticalBackground = "#482B2C",
        Information = "#C9BEDD",
        InformationBackground = "#34303E",
        IconGame = "#A6C7A2",
        IconNavigation = "#D4C2AD",
        IconAudio = "#DABD8D",
        IconTools = "#C5B0D4",
        IconCapture = "#D3A1B2",
        IconNeutral = "#C5B9AE",
        PromptA = "#A6D59B",
        PromptB = "#F3A4A5",
        PromptX = "#A3C4E8",
        PromptY = "#E4C88F",
        PromptShoulder = "#CDC3BB",
        PromptDpad = "#C2B4D8",
        PromptGuide = "#E8AE74",
        PromptKeyboard = "#D4C7BB",
        PromptMouse = "#D4C7BB",
        PromptForeground = "#1B1918",
        Hover = "#E8AE74",
        Press = "#E8AE74",
        Sheen = "#F3D1B0",
        Shadow = "#000000",
    };
    private static readonly ColorPalette warmLight = new()
    {
        Surface = "#F7F3EF",
        Card = "#FFFCF9",
        Control = "#E9E1D8",
        Stroke = "#B5A393",
        FocusBackground = "#EFE0D1",
        Text = "#261F1A",
        SecondaryText = "#62584F",
        MutedText = "#6A5F55",
        DisabledText = "#71655B",
        TextSelection = "#815324",
        TextSelectionForeground = "#FFFCF9",
        Caret = "#815324",
        Focus = "#815324",
        Accent = "#815324",
        SliderActive = "#815324",
        SliderTrack = "#C6B7A9",
        SliderThumb = "#815324",
        SliderThumbStroke = "#F7F3EF",
        ToggleOn = "#815324",
        ToggleOff = "#75665B",
        ToggleThumb = "#FFFCF9",
        Success = "#2A6839",
        SuccessBackground = "#E5F1E6",
        Warning = "#7D5621",
        WarningBackground = "#FFF1D9",
        Critical = "#A32D3C",
        CriticalBackground = "#F8E6E7",
        Information = "#65507D",
        InformationBackground = "#EEE7F5",
        IconGame = "#37683D",
        IconNavigation = "#815324",
        IconAudio = "#7E5F2E",
        IconTools = "#715482",
        IconCapture = "#884B66",
        IconNeutral = "#62584F",
        PromptA = "#316635",
        PromptB = "#A53942",
        PromptX = "#345F8E",
        PromptY = "#77581F",
        PromptShoulder = "#62574D",
        PromptDpad = "#69527E",
        PromptGuide = "#815324",
        PromptKeyboard = "#62574D",
        PromptMouse = "#62574D",
        PromptForeground = "#FFFCF9",
        Hover = "#815324",
        Press = "#815324",
        Sheen = "#FFFCF9",
        Shadow = "#000000",
    };

    private static readonly IReadOnlyDictionary<(string Preset, bool Light), ColorPalette> builtIns =
        Presets.SelectMany(preset => new[] { false, true }.Select(light => (Key: (preset.Id, light), Value: CreateBuiltIn(preset.Id, light))))
            .ToDictionary(item => item.Key, item => item.Value);

    private static readonly (PaletteRole Foreground, PaletteRole Background, double Minimum)[] contrastPairs = ContrastPairs().ToArray();

    public static bool TryColor(string? value, out string canonical)
    {
        canonical = "";
        if (value is null || value.Length > 16)
            return false;
        var hex = value.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];
        if (hex.Length != 6 || !hex.All(char.IsAsciiHexDigit))
            return false;
        canonical = "#" + hex.ToUpperInvariant();
        return true;
    }

    /// <summary>Resolves a proposed palette. Call TryValidate before applying an interactive draft.</summary>
    public static ColorPalette Resolve(AppearanceSettings? settings, bool light)
    {
        var preset = BuiltIn(settings?.Preset, light);
        return Merge(light ? settings?.Light : settings?.Dark, preset);
    }

    public static AppearanceSettings WithColor(AppearanceSettings? settings, bool light, PaletteRole role, string color)
    {
        if (!TryColor(color, out var canonical))
            throw new ArgumentException("Use six hexadecimal digits, for example #E8AE74.", nameof(color));
        var source = settings ?? new();
        var palette = Resolve(source, light).With(role, canonical);
        return light ? source with
        {
            Light = palette
        } : source with
        {
            Dark = palette
        };
    }

    /// <summary>Normalizes persisted/applicable preferences; unsafe variants recover independently.</summary>
    public static AppearanceSettings Normalize(AppearanceSettings? settings)
    {
        if (settings is null)
            return defaults;
        // Immutable preferences are validated at their boundary. Repeated dirty
        // checks and OS theme notifications reuse that result without retaining
        // abandoned editor drafts or repeating the full contrast calculation.
        return canonical.GetValue(settings, static value =>
        {
            var result = NormalizeCore(value);
            canonical.TryAdd(result, new(result));
            return new(result);
        }).Value;
    }

    private static AppearanceSettings NormalizeCore(AppearanceSettings settings)
    {
        var mode = settings.Mode is "System" or "Dark" or "Light" ? settings.Mode : "System";
        if (settings.Version != 1)
            return new(Mode: mode);
        var preset = Presets.Any(p => p.Id == settings.Preset) ? settings.Preset : "Warm";
        ColorPalette? NormalizeVariant(ColorPalette? candidate, bool light)
        {
            if (candidate is null)
                return null;
            var basis = BuiltIn(preset, light);
            var resolved = Merge(candidate, basis);
            return TryValidate(resolved, out _) && resolved != basis ? resolved : null;
        }
        return new(mode, preset, NormalizeVariant(settings.Dark, false), NormalizeVariant(settings.Light, true));
    }

    private static ColorPalette Merge(ColorPalette? candidate, ColorPalette preset)
    {
        if (candidate is null)
            return preset;
        var result = preset;
        foreach (var info in Roles)
            if (TryColor(candidate.Get(info.Role), out var canonical) && canonical != result.Get(info.Role))
                result = result.With(info.Role, canonical);
        return result;
    }

    private static ColorPalette BuiltIn(string? preset, bool light) => builtIns.TryGetValue((preset ?? "Warm", light), out var palette)
        ? palette : light ? warmLight : warmDark;

    private static ColorPalette CreateBuiltIn(string? preset, bool light)
    {
        var basis = light ? warmLight : warmDark;
        return (preset, light) switch
        {
            ("Graphite", false) => basis with
            {
                Surface = "#1C1D1F",
                Card = "#252629",
                Control = "#2D2F32",
                FocusBackground = "#383128",
                Text = "#F0F1F3",
                SecondaryText = "#C0BDB8",
                Focus = "#D6B99C",
                Accent = "#D6B99C",
                SliderActive = "#D6B99C",
                ToggleOn = "#D6B99C",
                PromptGuide = "#D6B99C",
                Hover = "#D6B99C",
                Press = "#D6B99C"
            },
            ("Graphite", true) => basis with
            {
                Surface = "#F5F5F4",
                Card = "#FFFCF9",
                Control = "#E7E7E5",
                FocusBackground = "#E9E1D7",
                Focus = "#725C45",
                Accent = "#725C45",
                SliderActive = "#725C45",
                ToggleOn = "#725C45",
                PromptGuide = "#725C45",
                Hover = "#725C45",
                Press = "#725C45"
            },
            ("Ember", false) => basis with
            {
                Surface = "#211B18",
                Card = "#2B231F",
                Control = "#332923",
                FocusBackground = "#402D22",
                Focus = "#EEAA72",
                Accent = "#EEAA72",
                SliderActive = "#EEAA72",
                ToggleOn = "#EEAA72",
                PromptGuide = "#EEAA72",
                Hover = "#EEAA72",
                Press = "#EEAA72",
                Sheen = "#FFCAA0"
            },
            ("Ember", true) => basis with
            {
                Surface = "#FBF3ED",
                Control = "#F0E2D7",
                FocusBackground = "#F5E3D3",
                Focus = "#86512F",
                Accent = "#86512F",
                SliderActive = "#86512F",
                ToggleOn = "#86512F",
                PromptGuide = "#86512F",
                Hover = "#86512F",
                Press = "#86512F"
            },
            _ => basis
        };
    }

    public static bool TryValidate(ColorPalette palette, out string reason)
    {
        if (palette is null)
        {
            reason = "Every palette color must contain six hexadecimal digits.";
            return false;
        }
        var luminance = new double[Roles.Count];
        foreach (var info in Roles)
        {
            if (!TryColor(palette.Get(info.Role), out var canonical))
            {
                reason = info.Name + " must contain six hexadecimal digits.";
                return false;
            }
            luminance[(int)info.Role] = Luminance(canonical);
        }
        foreach (var pair in contrastPairs)
        {
            var ratio = Ratio(luminance[(int)pair.Foreground], luminance[(int)pair.Background]);
            if (ratio + .00001 < pair.Minimum)
            {
                var foreground = Roles.First(r => r.Role == pair.Foreground).Name;
                var background = Roles.First(r => r.Role == pair.Background).Name;
                reason = string.Create(CultureInfo.InvariantCulture, $"{foreground} against {background.ToLowerInvariant()} needs {pair.Minimum:0.#}:1 contrast; this combination has {ratio:0.00}:1.");
                return false;
            }
        }
        reason = "Text and focus contrast checks pass.";
        return true;
    }

    public static double Contrast(string first, string second)
    {
        if (!TryColor(first, out var a) || !TryColor(second, out var b))
            return 0;
        return Ratio(Luminance(a), Luminance(b));
    }

    private static double Luminance(string color)
    {
        static double Channel(ReadOnlySpan<char> value)
        {
            var channel = int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }
        return .2126 * Channel(color.AsSpan(1, 2)) + .7152 * Channel(color.AsSpan(3, 2)) + .0722 * Channel(color.AsSpan(5, 2));
    }

    private static double Ratio(double first, double second) => (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);

    private static IEnumerable<(PaletteRole Foreground, PaletteRole Background, double Minimum)> ContrastPairs()
    {
        var surfaces = new[] { PaletteRole.Surface, PaletteRole.Card, PaletteRole.Control, PaletteRole.FocusBackground };
        foreach (var foreground in new[] { PaletteRole.Text, PaletteRole.SecondaryText, PaletteRole.MutedText })
            foreach (var background in surfaces)
                yield return (foreground, background, 4.5);
        foreach (var background in surfaces)
        {
            yield return (PaletteRole.DisabledText, background, 3);
            yield return (PaletteRole.Focus, background, 3);
            yield return (PaletteRole.Accent, background, 3);
            yield return (PaletteRole.Caret, background, 3);
        }
        foreach (var background in new[] { PaletteRole.SuccessBackground, PaletteRole.WarningBackground, PaletteRole.CriticalBackground, PaletteRole.InformationBackground })
        {
            yield return (PaletteRole.Text, background, 4.5);
            yield return (PaletteRole.SecondaryText, background, 4.5);
        }
        yield return (PaletteRole.TextSelection, PaletteRole.Control, 3);
        foreach (var pair in new[]
        {
            (PaletteRole.Success, PaletteRole.SuccessBackground), (PaletteRole.Warning, PaletteRole.WarningBackground),
            (PaletteRole.Critical, PaletteRole.CriticalBackground), (PaletteRole.Information, PaletteRole.InformationBackground),
            (PaletteRole.TextSelectionForeground, PaletteRole.TextSelection),
            (PaletteRole.SliderThumb, PaletteRole.SliderTrack), (PaletteRole.SliderActive, PaletteRole.SliderTrack),
            (PaletteRole.ToggleThumb, PaletteRole.ToggleOn), (PaletteRole.ToggleThumb, PaletteRole.ToggleOff)
        })
            yield return (pair.Item1, pair.Item2, pair.Item1 is PaletteRole.SliderThumb or PaletteRole.SliderActive ? 3 : 4.5);
        foreach (var foreground in new[] { PaletteRole.Success, PaletteRole.Warning, PaletteRole.Critical, PaletteRole.Information,
            PaletteRole.IconGame, PaletteRole.IconNavigation, PaletteRole.IconAudio, PaletteRole.IconTools, PaletteRole.IconCapture, PaletteRole.IconNeutral,
            PaletteRole.PromptA, PaletteRole.PromptB, PaletteRole.PromptX, PaletteRole.PromptY, PaletteRole.PromptShoulder, PaletteRole.PromptDpad,
            PaletteRole.PromptGuide, PaletteRole.PromptKeyboard, PaletteRole.PromptMouse })
            foreach (var background in surfaces)
                // Critical also labels destructive actions, battery warnings and
                // validation errors; it must remain readable as body text.
                yield return (foreground, background, foreground == PaletteRole.Critical ? 4.5 : 3);
        foreach (var background in new[] { PaletteRole.PromptA, PaletteRole.PromptB, PaletteRole.PromptX, PaletteRole.PromptY,
            PaletteRole.PromptShoulder, PaletteRole.PromptDpad, PaletteRole.PromptGuide, PaletteRole.PromptKeyboard, PaletteRole.PromptMouse })
            yield return (PaletteRole.PromptForeground, background, 4.5);
    }

    internal static AppearanceSettings ReadJson(JsonElement value, out bool recovered)
    {
        recovered = false;
        if (value.ValueKind == JsonValueKind.Null)
            return new();
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Take(129).Count() > 128)
        {
            recovered = true;
            return new();
        }
        bool changed = false;
        string ReadString(string name, string fallback)
        {
            if (!value.TryGetProperty(name, out var field))
                return fallback;
            if (field.ValueKind == JsonValueKind.String && field.GetString() is { Length: <= 32 } text)
                return text;
            changed = true;
            return fallback;
        }
        var mode = ReadString(nameof(AppearanceSettings.Mode), "System");
        var preset = ReadString(nameof(AppearanceSettings.Preset), "Warm");
        var version = 1;
        if (value.TryGetProperty(nameof(AppearanceSettings.Version), out var v)
            && (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out version)))
        {
            changed = true;
            version = 1;
        }
        ColorPalette? ReadVariant(string name)
        {
            if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null)
                return null;
            if (field.ValueKind != JsonValueKind.Object || field.EnumerateObject().Take(129).Count() > 128)
            {
                changed = true;
                return null;
            }
            var result = new ColorPalette();
            foreach (var info in Roles)
            {
                if (!field.TryGetProperty(info.Role.ToString(), out var color))
                    continue;
                if (color.ValueKind == JsonValueKind.String && TryColor(color.GetString(), out var canonical))
                    result = result.With(info.Role, canonical);
                else
                    changed = true;
            }
            return result;
        }
        var candidate = new AppearanceSettings(mode, preset, ReadVariant(nameof(AppearanceSettings.Dark)), ReadVariant(nameof(AppearanceSettings.Light)), version);
        var normalized = Normalize(candidate);
        var canonical = candidate with
        {
            Dark = candidate.Dark is null ? null : Resolve(candidate, false),
            Light = candidate.Light is null ? null : Resolve(candidate, true)
        };
        // Collapsing a complete preset to null is only canonicalization, not damaged data.
        changed |= normalized.Mode != mode || normalized.Preset != preset || version != 1
            || candidate.Dark is not null && !TryValidate(canonical.Dark!, out _)
            || candidate.Light is not null && !TryValidate(canonical.Light!, out _);
        recovered = changed;
        return normalized;
    }
}

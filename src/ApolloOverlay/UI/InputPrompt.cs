using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ApolloOverlay;

/// <summary>Explicit input tokens become vectors or legible keycaps, with complete accessible names.</summary>
internal static partial class InputPrompt
{
    private sealed record Glyph(string Name, string Tone, string? Keycap = null);
    private sealed class KeycapLabel : TextBlock
    {
        protected override AutomationPeer? OnCreateAutomationPeer() => null;
    }
    private sealed record RenderState(string Template, double FontSize, Inline? FirstInline);
    private static readonly DependencyProperty RenderStateProperty = DependencyProperty.RegisterAttached("RenderState", typeof(RenderState), typeof(InputPrompt));

    private static readonly IReadOnlyDictionary<string, Glyph> glyphs = new Dictionary<string, Glyph>(StringComparer.Ordinal)
    {
        ["A"] = new("A", ThemeSupport.IconGame),
        ["B"] = new("B", ThemeSupport.Critical),
        ["X"] = new("X", ThemeSupport.IconNavigation),
        ["Y"] = new("Y", ThemeSupport.IconAudio),
        ["LB"] = new("LB", ThemeSupport.IconNavigation),
        ["RB"] = new("RB", ThemeSupport.IconNavigation),
        ["Dpad"] = new("D-pad", ThemeSupport.IconTools),
        ["DpadHorizontal"] = new("Left / right", ThemeSupport.IconTools),
        ["Guide"] = new("Guide", ThemeSupport.IconGame),
        ["RS"] = new("Right stick", ThemeSupport.IconTools),
        ["Enter"] = new("Enter", ThemeSupport.IconNavigation, "Enter"),
        ["Esc"] = new("Esc", ThemeSupport.Critical, "Esc"),
        ["Space"] = new("Space", ThemeSupport.IconNavigation, "Space"),
        ["Tab"] = new("Tab", ThemeSupport.IconNavigation, "Tab"),
        ["Arrows"] = new("Arrows", ThemeSupport.IconNavigation),
        ["ArrowsHorizontal"] = new("Left / right", ThemeSupport.IconNavigation),
        ["PgUp"] = new("PgUp", ThemeSupport.IconTools, "PgUp"),
        ["PgDn"] = new("PgDn", ThemeSupport.IconTools, "PgDn"),
        ["KeyX"] = new("X", ThemeSupport.IconNavigation, "X"),
        ["Shift"] = new("Shift", ThemeSupport.IconNavigation, "Shift"),
        ["Ctrl"] = new("Ctrl", ThemeSupport.IconNavigation, "Ctrl"),
        ["MouseLeft"] = new("Left click", ThemeSupport.IconNavigation),
        ["MouseRight"] = new("Right click", ThemeSupport.IconTools),
        ["MouseScroll"] = new("Mouse wheel", ThemeSupport.IconNavigation),
        ["MouseMove"] = new("Mouse", ThemeSupport.IconNavigation)
    };

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    internal static string PlainText(string template) => Tokens().Replace(template, match => Definition(match.Groups[1].Value).Name);

    internal static string Read(TextBlock target) => AutomationProperties.GetName(target) is { Length: > 0 } name ? name : target.Text;

    internal static void Apply(TextBlock target, string template, FrameworkElement resources)
    {
        if (target.GetValue(RenderStateProperty) is RenderState previous
            && previous.Template == template && previous.FontSize == target.FontSize
            && ReferenceEquals(previous.FirstInline, target.Inlines.FirstInline))
            return;
        var accessible = PlainText(template);
        AutomationProperties.SetName(target, accessible);
        target.Inlines.Clear();
        var offset = 0;
        foreach (Match match in Tokens().Matches(template))
        {
            if (match.Index > offset)
                target.Inlines.Add(new Run(template[offset..match.Index]));
            var key = match.Groups[1].Value;
            var height = Math.Clamp(target.FontSize * 1.4, 18, 24);
            var icon = CreateGlyph(resources, key, height);
            icon.Margin = new(0, 0, 2, 0);
            target.Inlines.Add(new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center });
            offset = match.Index + match.Length;
        }
        if (offset < template.Length)
            target.Inlines.Add(new Run(template[offset..]));
        target.SetValue(RenderStateProperty, new RenderState(template, target.FontSize, target.Inlines.FirstInline));
    }

    internal static FrameworkElement CreateGlyph(FrameworkElement resources, string key, double height)
    {
        var definition = Definition(key);
        if (definition.Keycap is { } caption)
        {
            var label = new KeycapLabel
            {
                Text = caption,
                FontSize = Math.Clamp(height * .56, 10, 13),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, ThemeSupport.Surface);
            var keycap = new Border
            {
                Child = label,
                Height = height,
                MinWidth = height,
                Padding = new(4, 0, 4, 0),
                CornerRadius = new(3),
                IsHitTestVisible = false,
                Focusable = false,
                Tag = key
            };
            keycap.SetResourceReference(Border.BackgroundProperty, definition.Tone);
            return keycap;
        }
        var geometry = (Geometry)resources.FindResource("InputGlyph." + key);
        var icon = new VectorIcon
        {
            Data = geometry,
            SourceOrigin = geometry.Bounds.TopLeft,
            SourceSize = geometry.Bounds.Size,
            Width = height * geometry.Bounds.Width / geometry.Bounds.Height,
            Height = height,
            Tag = key
        };
        icon.SetResourceReference(VectorIcon.FillProperty, definition.Tone);
        return icon;
    }

    private static Glyph Definition(string key) => glyphs.TryGetValue(key, out var definition)
        ? definition : throw new ArgumentException("Unknown input prompt token: " + key, nameof(key));
}

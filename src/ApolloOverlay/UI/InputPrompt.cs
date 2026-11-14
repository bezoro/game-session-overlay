using System.Globalization;
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
    private sealed record Glyph(string Name, string Tone, string? Keycap = null, string[]? Group = null);
    private sealed class KeycapBorder(string caption, double fontSize) : Border
    {
        private Geometry? outlines;

        protected override AutomationPeer? OnCreateAutomationPeer() => null;

        protected override Size MeasureOverride(Size constraint)
        {
            var typeface = new Typeface(TextElement.GetFontFamily(this), TextElement.GetFontStyle(this), FontWeights.SemiBold, TextElement.GetFontStretch(this));
            var text = new FormattedText(caption, CultureInfo.GetCultureInfo("en-US"), FlowDirection, typeface, fontSize,
                Brushes.Black, null, TextOptions.GetTextFormattingMode(this), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            outlines = text.BuildGeometry(default);
            outlines.Freeze();
            return new(Math.Max(text.Width, outlines.Bounds.Width) + Padding.Left + Padding.Right, text.Height);
        }

        protected override void OnRender(DrawingContext context)
        {
            base.OnRender(context);
            if (outlines is not { } geometry || geometry.Bounds is not { IsEmpty: false } ink)
                return;

            // The same glyph outlines determine the visible midpoint and the painted ink.
            context.PushTransform(new TranslateTransform((RenderSize.Width - ink.Width) / 2 - ink.X, (RenderSize.Height - ink.Height) / 2 - ink.Y));
            context.DrawGeometry(TextElement.GetForeground(this), null, geometry);
            context.Pop();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == TextElement.ForegroundProperty)
                InvalidateVisual();
            else if (e.Property == TextElement.FontFamilyProperty || e.Property == TextElement.FontStyleProperty
                || e.Property == TextElement.FontStretchProperty || e.Property == FlowDirectionProperty || e.Property == TextOptions.TextFormattingModeProperty)
                InvalidateMeasure();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            InvalidateMeasure();
        }
    }
    private sealed record RenderState(string Template, double FontSize, Inline? FirstInline);
    private static readonly DependencyProperty RenderStateProperty = DependencyProperty.RegisterAttached("RenderState", typeof(RenderState), typeof(InputPrompt));

    private static readonly IReadOnlyDictionary<string, Glyph> glyphs = new Dictionary<string, Glyph>(StringComparer.Ordinal)
    {
        ["A"] = new("A", ThemeSupport.PromptA),
        ["B"] = new("B", ThemeSupport.PromptB),
        ["X"] = new("X", ThemeSupport.PromptX),
        ["Y"] = new("Y", ThemeSupport.PromptY),
        ["LB"] = new("LB", ThemeSupport.PromptShoulder),
        ["RB"] = new("RB", ThemeSupport.PromptShoulder),
        ["Dpad"] = new("D-pad", ThemeSupport.PromptDpad),
        ["DpadHorizontal"] = new("Left / right", ThemeSupport.PromptDpad),
        ["Guide"] = new("Guide", ThemeSupport.PromptGuide),
        ["RS"] = new("Right stick", ThemeSupport.PromptDpad),
        ["Enter"] = new("Enter", ThemeSupport.PromptKeyboard, "Enter"),
        ["Esc"] = new("Esc", ThemeSupport.PromptKeyboard, "Esc"),
        ["Space"] = new("Space", ThemeSupport.PromptKeyboard, "Space"),
        ["Tab"] = new("Tab", ThemeSupport.PromptKeyboard, "Tab"),
        ["Arrows"] = new("Arrows", ThemeSupport.PromptKeyboard, Group: ["Up", "Down", "Left", "Right"]),
        ["Up"] = new("Up arrow", ThemeSupport.PromptKeyboard, "↑"),
        ["Down"] = new("Down arrow", ThemeSupport.PromptKeyboard, "↓"),
        ["Left"] = new("Left arrow", ThemeSupport.PromptKeyboard, "←"),
        ["Right"] = new("Right arrow", ThemeSupport.PromptKeyboard, "→"),
        ["PgUp"] = new("PgUp", ThemeSupport.PromptKeyboard, "PgUp"),
        ["PgDn"] = new("PgDn", ThemeSupport.PromptKeyboard, "PgDn"),
        ["KeyX"] = new("X", ThemeSupport.PromptKeyboard, "X"),
        ["KeyY"] = new("Y", ThemeSupport.PromptKeyboard, "Y"),
        ["Shift"] = new("Shift", ThemeSupport.PromptKeyboard, "Shift"),
        ["Ctrl"] = new("Ctrl", ThemeSupport.PromptKeyboard, "Ctrl"),
        ["MouseLeft"] = new("Left click", ThemeSupport.PromptMouse),
        ["MouseRight"] = new("Right click", ThemeSupport.PromptMouse),
        ["MouseScroll"] = new("Mouse wheel", ThemeSupport.PromptMouse),
        ["MouseMove"] = new("Mouse", ThemeSupport.PromptMouse)
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
        if (definition.Group is { } keys)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false, Tag = key };
            foreach (var member in keys)
            {
                var child = CreateGlyph(resources, member, height);
                child.Margin = new(1, 0, 1, 0);
                group.Children.Add(child);
            }
            return group;
        }
        if (definition.Keycap is { } caption)
        {
            var keycap = new KeycapBorder(caption, Math.Clamp(height * .56, 10, 13))
            {
                Height = height,
                MinWidth = height,
                Padding = new(4, 0, 4, 0),
                CornerRadius = new(3),
                IsHitTestVisible = false,
                Focusable = false,
                Tag = key
            };
            keycap.SetResourceReference(TextElement.ForegroundProperty, ThemeSupport.PromptForeground);
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

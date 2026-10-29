using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ApolloOverlay;

public partial class MainWindow
{
    private void BuildAppearance()
    {
        var draft = appearanceDraft ??= AppearanceCatalog.Normalize(Controller.Settings.Appearance);
        var pane = AppearancePane(out var content, out var commands);
        AppearanceParagraph(content, ThemeSupport.HighContrastActive
            ? "Windows high contrast controls the visible colors. Your custom palettes are preserved."
            : "Make the panel your own. Preview changes here, then save only when you are ready.");
        var valid = AppearanceDraftValid(out var validationReason);
        if (!valid)
            AppearanceParagraph(content, validationReason + " Adjust the colors before saving; navigation keeps its last readable palette.");
        AppearanceRow(content, "appearance-mode", "Theme · " + (draft.Mode == "System" ? "Follow Windows" : draft.Mode),
            "Choose automatic, dark or light appearance", () =>
            {
                var modes = new[] { "System", "Dark", "Light" };
                SetAppearanceDraft(draft with
                {
                    Mode = modes[(Array.IndexOf(modes, draft.Mode) + 1) % modes.Length]
                });
            });
        AppearanceRow(content, "appearance-preview", "Editing · " + (appearancePreviewLight ? "Light palette" : "Dark palette"),
            "Dark and light colors are saved separately", () =>
            {
                appearancePreviewLight = !appearancePreviewLight;
                ApplyAppearanceTheme();
                RefreshRows();
            });
        AppearanceSection(content, "Presets");
        foreach (var preset in AppearanceCatalog.Presets)
        {
            var chosen = preset;
            AppearanceRow(content, "appearance-preset:" + preset.Id, preset.Name,
                draft.Preset == preset.Id ? "Selected preset · custom colors may be applied below" : "Preview this preset for dark and light themes",
                () => SetAppearanceDraft(draft with { Preset = chosen.Id, Dark = null, Light = null }));
        }
        AppearanceSection(content, "Advanced colors");
        AppearanceParagraph(content, "Each color has one role throughout the overlay. Back leaves this page without saving.");
        var palette = AppearanceCatalog.Resolve(draft, appearancePreviewLight);
        foreach (var group in AppearanceCatalog.Roles.GroupBy(role => role.Group))
        {
            AppearanceSection(content, group.Key);
            foreach (var role in group)
            {
                var selected = role.Role;
                var color = palette.Get(role.Role);
                var button = AppearanceRow(content, "appearance-color:" + role.Role, role.Name, color, () => OpenAppearanceColor(selected));
                if (button.Content is ActionButtonContent body)
                    body.ReplaceIcon(ColorSwatch(color, 24));
            }
        }
        AppearanceRow(content, "appearance-defaults", "Restore defaults", "Preview the original warm charcoal and amber palette", () =>
            SetAppearanceDraft(new AppearanceSettings(Mode: draft.Mode)));
        AppearanceCommand(commands, "appearance-save", "Save colors", SaveAppearanceDraft,
            !valid ? validationReason : AppearanceDirty ? "Save this appearance without saving other pending settings" : "No color changes to save", AppearanceDirty && valid, 0);
        AppearanceCommand(commands, "appearance-discard", "Discard", DiscardAppearanceDraft,
            "Restore the appearance in use before this editor", true, 1);
        AddVisual(pane);
    }

    private Grid AppearancePane(out StackPanel content, out Grid commands)
    {
        var pane = new Grid();
        pane.SetBinding(HeightProperty, new Binding(nameof(ScrollViewer.ViewportHeight)) { Source = ContentScroll });
        pane.RowDefinitions.Add(new()
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        pane.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        var body = new StackPanel();
        content = body;
        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Margin = new(0, 0, 0, 8)
        };
        // This pane owns a smaller viewport than ContentScroll because its actions
        // stay pinned. Reveal a complete control here before the request can scroll
        // the outer page or add clearance calculated for that larger viewport.
        body.RequestBringIntoView += (_, args) => RevealAppearanceControl(scroll, body, args);
        AutomationProperties.SetName(scroll, "Appearance controls");
        pane.Children.Add(scroll);
        commands = new Grid();
        commands.ColumnDefinitions.Add(new());
        commands.ColumnDefinitions.Add(new());
        Grid.SetRow(commands, 1);
        pane.Children.Add(commands);
        return pane;
    }

    private Button AppearanceRow(StackPanel content, string id, string title, string description, Action action, bool available = true)
    {
        var button = MakeButton(id, title, description, action, available, false);
        button.MinHeight = 56;
        button.Margin = new(0, 4, 0, 4);
        if (button.Content is ActionButtonContent body)
        {
            body.Title.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.Body");
            body.Description.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.Caption");
        }
        content.Children.Add(AppearanceHost(button, available));
        return button;
    }

    private void AppearanceCommand(Grid commands, string id, string title, Action action, string description, bool available, int column)
    {
        var button = MakeButton(id, title, description, action, available, false);
        button.MinHeight = 44;
        button.Padding = new(8);
        button.Margin = new(column == 0 ? 0 : 4, 0, column == 0 ? 4 : 0, 0);
        if (button.Content is ActionButtonContent body)
        {
            body.AllowsDescription = false;
            body.Title.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.CompactBody");
            body.Title.TextWrapping = TextWrapping.NoWrap;
            body.Description.Visibility = Visibility.Collapsed;
            body.ColumnDefinitions[0].Width = new GridLength(24);
            body.Icon.Width = body.Icon.Height = 18;
        }
        if (button.Resources["ABadge"] is Border badge)
        {
            badge.Width = badge.Height = 22;
            badge.Child = InputPrompt.CreateGlyph(this, "A", 20);
        }
        var host = AppearanceHost(button, available);
        Grid.SetColumn(host, column);
        commands.Children.Add(host);
    }

    private static Border AppearanceHost(Button button, bool available)
    {
        var margin = button.Margin;
        button.Margin = new(0);
        var host = new Border
        {
            Child = button,
            Margin = margin,
            Focusable = !available,
            FocusVisualStyle = null,
            Tag = "description:" + button.Tag
        };
        AutomationProperties.SetName(host, AutomationProperties.GetName(button) + ". " + AutomationProperties.GetHelpText(button));
        AutomationProperties.SetHelpText(host, AutomationProperties.GetHelpText(button));
        button.Resources["DescriptionTarget"] = host;
        return host;
    }

    private static void RevealAppearanceControl(ScrollViewer scroll, StackPanel body, RequestBringIntoViewEventArgs args)
    {
        if (args.TargetObject is not FrameworkElement target || !body.IsAncestorOf(target)
            || target.ActualHeight <= 0 || scroll.ViewportHeight <= 0)
            return;
        var bounds = target.TransformToAncestor(body).TransformBounds(new Rect(target.RenderSize));
        var clearance = bounds.Height + 4 <= scroll.ViewportHeight ? 2 : 0;
        var top = bounds.Top - clearance;
        var bottom = bounds.Bottom + clearance;
        var offset = scroll.VerticalOffset;
        if (top < offset || bounds.Height > scroll.ViewportHeight)
            offset = top;
        else if (bottom > offset + scroll.ViewportHeight)
            offset = bottom - scroll.ViewportHeight;
        scroll.ScrollToVerticalOffset(Math.Clamp(offset, 0, scroll.ScrollableHeight));
        args.Handled = true;
    }

    private static void AppearanceSection(StackPanel panel, string title)
    {
        var block = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Margin = new(0, 16, 0, 4)
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, ThemeSupport.Text);
        block.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.Section");
        panel.Children.Add(block);
    }

    private static TextBlock AppearanceParagraph(StackPanel panel, string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 8) };
        block.SetResourceReference(TextBlock.ForegroundProperty, ThemeSupport.SecondaryText);
        block.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.CompactBody");
        AutomationProperties.SetName(block, text);
        panel.Children.Add(block);
        return block;
    }

    private static Border ColorSwatch(string color, double size)
    {
        var fill = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        fill.Freeze();
        var swatch = new Border { Width = size, Height = size, CornerRadius = new(4), BorderThickness = new(1), Background = fill, VerticalAlignment = VerticalAlignment.Center };
        swatch.SetResourceReference(Border.BorderBrushProperty, ThemeSupport.SecondaryText);
        AutomationProperties.SetName(swatch, "Color " + color);
        return swatch;
    }
}

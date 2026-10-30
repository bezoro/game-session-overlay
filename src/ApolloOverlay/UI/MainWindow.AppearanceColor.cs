using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ApolloOverlay;

public partial class MainWindow
{
    private Border? appearanceColorSwatch;
    private TextBlock? appearanceColorLabel, appearanceColorStatus, appearanceColorDetail;

    private void BuildColorEditor()
    {
        if (appearanceDraft is not { } draft || appearanceColorRole is not { } role)
        {
            Paragraph("Choose a palette color in Appearance.");
            AddRow("back", "Back", "Return to Appearance", Back);
            return;
        }
        var pane = AppearancePane(out var content, out var commands);
        var preview = new Grid { Margin = new(0, 8, 0, 8) };
        preview.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(64)
        });
        preview.ColumnDefinitions.Add(new());
        appearanceColorSwatch = ColorSwatch(appearanceColorValue, 48);
        appearanceColorSwatch.HorizontalAlignment = HorizontalAlignment.Left;
        preview.Children.Add(appearanceColorSwatch);
        appearanceColorLabel = new TextBlock
        {
            Text = appearanceColorValue,
            FontFamily = new("Consolas"),
            VerticalAlignment = VerticalAlignment.Center
        };
        appearanceColorLabel.SetResourceReference(TextBlock.ForegroundProperty, ThemeSupport.Text);
        appearanceColorLabel.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.Title");
        Grid.SetColumn(appearanceColorLabel, 1);
        preview.Children.Add(appearanceColorLabel);
        content.Children.Add(preview);
        AppearanceParagraph(content, "Adjust a channel from 0–255 or enter a hex color. Keep color adds it to your draft; Save colors makes it permanent.");
        var color = ReadAppearanceColor();
        AddAppearanceChannel(content, "color-red", "Red", color.R, value => Color.FromRgb(value, ReadAppearanceColor().G, ReadAppearanceColor().B));
        AddAppearanceChannel(content, "color-green", "Green", color.G, value => Color.FromRgb(ReadAppearanceColor().R, value, ReadAppearanceColor().B));
        AddAppearanceChannel(content, "color-blue", "Blue", color.B, value => Color.FromRgb(ReadAppearanceColor().R, ReadAppearanceColor().G, value));
        AppearanceRow(content, "color-hex", "Hex color", appearanceColorValue, OpenAppearanceHexKeyboard);
        AppearanceRow(content, "color-default", "Reset color", "Use this preset's original value for this role", () =>
        {
            var source = draft with
            {
                Dark = null,
                Light = null
            };
            appearanceColorValue = AppearanceCatalog.Resolve(source, appearancePreviewLight).Get(role);
            UpdateAppearanceColorPreview();
            RefreshRows();
        });
        appearanceColorDetail = AppearanceParagraph(content, "");
        appearanceColorStatus = new TextBlock
        {
            Tag = "color-status",
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 4, 0, 8)
        };
        appearanceColorStatus.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.Caption");
        pane.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        Grid.SetRow(commands, 2);
        Grid.SetRow(appearanceColorStatus, 1);
        pane.Children.Add(appearanceColorStatus);
        var valid = TryColorProposal(out _, out var reason);
        AppearanceCommand(commands, "color-apply", "Keep color", ApplyAppearanceColor,
            valid ? "Keep this color in the unsaved appearance draft" : "Keep this draft color; fix contrast before saving. " + reason, true, 0);
        AppearanceCommand(commands, "color-cancel", "Cancel", Back, "Return without applying this color", true, 1);
        AddVisual(pane);
        UpdateAppearanceColorPreview();
    }

    private void AddAppearanceChannel(StackPanel content, string id, string name, byte value, Func<byte, Color> changed)
    {
        Slider? slider = null;
        var row = AppearanceRow(content, id, name + " · " + value, "0–255 · adjust with left and right", () => slider?.Focus());
        slider = new Slider
        {
            Minimum = 0,
            Maximum = 255,
            Value = value,
            SmallChange = 1,
            LargeChange = 16,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Focusable = true,
            MinHeight = 28,
            Margin = new(0, 4, 0, 0),
            Style = (Style)FindResource("SessionVolumeSlider")
        };
        AutomationProperties.SetName(slider, name + " color channel, zero to 255");
        row.Resources["SessionSlider"] = slider;
        if (row.Content is ActionButtonContent body)
        {
            body.Description.Visibility = Visibility.Collapsed;
            body.Texts.Children.Add(slider);
        }
        slider.ValueChanged += (_, _) =>
        {
            if (!NavigationAllowed || busy || TransitionPending)
                return;
            var channel = (byte)Math.Clamp(Math.Round(slider.Value), 0, 255);
            var color = changed(channel);
            appearanceColorValue = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            SetEntryText(id, name + " · " + channel);
            UpdateAppearanceColorPreview();
        };
    }

    private Color ReadAppearanceColor() => (Color)ColorConverter.ConvertFromString(appearanceColorValue);

    private bool AdjustAppearanceColor(NavDirection direction)
    {
        if (view.Current.Route != OverlayRoute.ColorEdit || direction is not (NavDirection.Left or NavDirection.Right)
            || navigation.Id is not ("color-red" or "color-green" or "color-blue")
            || entries.FirstOrDefault(entry => entry.Id == navigation.Id)?.Button.Resources["SessionSlider"] is not Slider slider)
            return false;
        slider.Value = Math.Clamp(slider.Value + (direction == NavDirection.Left ? -1 : 1), 0, 255);
        return true;
    }

    private void UpdateAppearanceColorPreview()
    {
        var valid = TryColorProposal(out var proposal, out var reason);
        appearanceColorProposal = proposal;
        ApplyAppearanceTheme();
        if (appearanceColorLabel is not null)
            appearanceColorLabel.Text = appearanceColorValue;
        if (appearanceColorSwatch is not null)
        {
            var brush = new SolidColorBrush(ReadAppearanceColor());
            brush.Freeze();
            appearanceColorSwatch.Background = brush;
            AutomationProperties.SetName(appearanceColorSwatch, "Color " + appearanceColorValue);
        }
        if (appearanceColorStatus is not null)
        {
            appearanceColorStatus.Text = valid
                ? ThemeSupport.HighContrastActive ? "Readable palette · Windows colors stay active" : "Readable contrast · Preview active"
                : "Draft only · preview unchanged\nFix contrast before saving.";
            appearanceColorStatus.SetResourceReference(TextBlock.ForegroundProperty, valid ? ThemeSupport.SecondaryText : ThemeSupport.Critical);
            AutomationProperties.SetName(appearanceColorStatus, valid ? appearanceColorStatus.Text : appearanceColorStatus.Text + " " + reason);
        }
        if (appearanceColorDetail is not null)
        {
            appearanceColorDetail.Text = reason;
            appearanceColorDetail.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(appearanceColorDetail, reason);
        }
        SetFeatureEntry("color-apply", "Keep color", valid ? "Keep this color in the unsaved appearance draft" : "Keep this draft color; fix contrast before saving. " + reason, true);
        SetEntryDescription("color-hex", appearanceColorValue);
    }

    private void OpenAppearanceHexKeyboard()
    {
        keyboardPurpose = TextEntryPurpose.ColorHex;
        keyboardText = appearanceColorValue.TrimStart('#');
        keyboardUpper = true;
        keyboardSymbols = false;
        keyboardTarget = null;
        OpenFeature(OverlayRoute.Keyboard, "key:0");
    }

    private bool SubmitAppearanceHex()
    {
        if (!AppearanceCatalog.TryColor(keyboardText.Trim(), out var color))
        {
            FeedbackText("Enter six hexadecimal digits, for example E7B47C.", important: true);
            return false;
        }
        appearanceColorValue = color;
        UpdateAppearanceColorPreview();
        Back();
        return true;
    }
}

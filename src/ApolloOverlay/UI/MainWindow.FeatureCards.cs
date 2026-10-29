using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ApolloOverlay;

public partial class MainWindow
{
    private void FeatureHeading(string title, string description, string actionIcon)
    {
        var body = new Grid();
        body.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        body.ColumnDefinitions.Add(new());
        var icon = ActionIconCatalog.Create(this, actionIcon, 32);
        icon.Margin = new(0, 0, 16, 0);
        body.Children.Add(icon);
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(FeatureText(title, "SessionType.Section", ThemeSupport.Text, FontWeights.SemiBold));
        if (!string.IsNullOrWhiteSpace(description))
        {
            var detail = FeatureText(description, "SessionType.Body", ThemeSupport.SecondaryText);
            detail.Margin = new(0, 4, 0, 0);
            copy.Children.Add(detail);
        }
        Grid.SetColumn(copy, 1);
        body.Children.Add(copy);
        var card = FeatureCard(body);
        AutomationProperties.SetName(card, title + (description.Length == 0 ? "" : ". " + description));
        AddVisual(card);
    }

    private void FeatureEmpty(string title, string description, string actionIcon)
    {
        var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var icon = ActionIconCatalog.Create(this, actionIcon, 40);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Margin = new(0, 8, 0, 16);
        body.Children.Add(icon);
        var heading = FeatureText(title, "SessionType.Section", ThemeSupport.Text, FontWeights.SemiBold);
        heading.TextAlignment = TextAlignment.Center;
        body.Children.Add(heading);
        var detail = FeatureText(description, "SessionType.Body", ThemeSupport.SecondaryText);
        detail.TextAlignment = TextAlignment.Center;
        detail.Margin = new(0, 8, 0, 8);
        body.Children.Add(detail);
        AddVisual(FeatureCard(body));
    }

    private void FeatureFact(string label, string value)
    {
        var body = new Grid { Margin = new(4, 4, 4, 8) };
        body.ColumnDefinitions.Add(new()
        {
            Width = new(1, GridUnitType.Star)
        });
        body.ColumnDefinitions.Add(new()
        {
            Width = new(2, GridUnitType.Star)
        });
        var name = FeatureText(label, "SessionType.Caption", ThemeSupport.SecondaryText);
        name.Margin = new(0, 0, 16, 0);
        body.Children.Add(name);
        var content = FeatureText(value, "SessionType.Body", ThemeSupport.Text);
        Grid.SetColumn(content, 1);
        body.Children.Add(content);
        AddVisual(body);
    }

    private Border FeatureCard(UIElement child)
    {
        var card = new Border
        {
            Child = child,
            Padding = new(ResourceNumber("SessionSpace.16", 16)),
            Margin = new(0, 4, 0, 12),
            BorderThickness = new(1),
            SnapsToDevicePixels = true
        };
        card.SetResourceReference(Border.BackgroundProperty, ThemeSupport.Card);
        card.SetResourceReference(Border.BorderBrushProperty, ThemeSupport.Stroke);
        card.SetResourceReference(Border.CornerRadiusProperty, "SessionRadius.Card");
        return card;
    }

    private TextBlock FeatureText(string value, string type, string color, FontWeight? weight = null)
    {
        var text = new TextBlock
        {
            Text = value,
            FontSize = ResourceNumber(type, 15),
            FontWeight = weight ?? FontWeights.Normal,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, color);
        return text;
    }
}

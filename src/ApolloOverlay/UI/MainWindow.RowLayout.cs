using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ApolloOverlay;

public partial class MainWindow
{
    private void Section(string title)
    {
        var text = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new(2, 18, 0, 8)
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, ThemeSupport.SecondaryText);
        AddVisual(text);
    }

    private TextBlock Paragraph(string text, int size = 16)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            Margin = new(2, 6, 2, 14)
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, ThemeSupport.SecondaryText);
        AutomationProperties.SetName(block, text);
        AddVisual(block);
        return block;
    }

    private void AddVisual(FrameworkElement control)
    {
        var row = Rows.RowDefinitions.Count;
        Rows.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        Grid.SetRow(control, row);
        Grid.SetColumnSpan(control, 2);
        Rows.Children.Add(control);
    }

    private Button AddRow(string id, string title, string description, Action action, bool available = true, bool danger = false, int? row = null, int column = 0, bool compactGrid = false)
    {
        if (narrow && !compactGrid)
            row = null;
        var index = row ?? Rows.RowDefinitions.Count;
        while (index >= Rows.RowDefinitions.Count)
            Rows.RowDefinitions.Add(new()
            {
                Height = GridLength.Auto
            });
        var button = MakeButton(id, title, description, action, available, danger);
        Grid.SetRow(button, index);
        Grid.SetColumn(button, row is null ? 0 : column);
        Grid.SetColumnSpan(button, row is null ? 2 : 1);
        if (row is not null)
            button.Margin = new(column == 0 ? 0 : 4, 4, column == 0 ? 4 : 0, 4);
        var reason = new Border
        {
            Focusable = true,
            Tag = "description:" + id,
            Margin = new(-2)
        };
        AutomationProperties.SetName(reason, title + ". " + description);
        AutomationProperties.SetHelpText(reason, description);
        button.Resources["DescriptionTarget"] = reason;
        Grid.SetRow(reason, index);
        Grid.SetColumn(reason, row is null ? 0 : column);
        Grid.SetColumnSpan(reason, row is null ? 2 : 1);
        Rows.Children.Add(reason);
        Rows.Children.Add(button);
        return button;
    }

    private void RegisterFixed(string id, Button button, Action action, bool available, string reason)
    {
        button.Tag = id;
        button.IsEnabled = available;
        entries.Add(new(id, button, action, available, reason));
    }
}

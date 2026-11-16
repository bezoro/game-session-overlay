using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace ApolloOverlay;

public partial class MainWindow
{
    private void Section(string title, bool compact = false)
    {
        var text = new TextBlock
        {
            Text = title,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("SessionFont.Display"),
            FontSize = ResourceNumber("SessionType.Section", 17),
            FontWeight = FontWeights.SemiBold,
            Margin = compact ? new(0, 0, 0, 4) : new(0, Rows.RowDefinitions.Count == 0 ? 4 : ResourceNumber("SessionSpace.16"), 0, ResourceNumber("SessionSpace.8"))
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, ThemeSupport.Text);
        AddVisual(text);
    }

    private TextBlock Paragraph(string text, int size = 15)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 4, 0, ResourceNumber("SessionSpace.12"))
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
        {
            var gap = compactGrid && (narrow || compactChrome) ? 2 : 4;
            button.Margin = new(column == 0 ? 0 : gap, gap, column == 0 ? gap : 0, gap);
        }
        var reason = new Border
        {
            Focusable = true,
            FocusVisualStyle = null,
            Tag = "description:" + id
        };
        SetActionHost(button, reason);
        UpdateEntryAutomation(button);
        reason.SetBinding(MarginProperty, new Binding(nameof(Margin)) { Source = button });
        Grid.SetRow(reason, index);
        Grid.SetColumn(reason, row is null ? 0 : column);
        Grid.SetColumnSpan(reason, row is null ? 2 : 1);
        Rows.Children.Add(reason);
        Rows.Children.Add(button);
        return button;
    }

    private void RegisterFixed(string id, Button button, Action action, bool available, string reason, Border? focusHost = null, Border? layoutHost = null)
    {
        button.Tag = id;
        button.IsEnabled = available;
        entries.Add(new(id, button, action, available, reason, focusHost, layoutHost));
    }

    internal Border? FindActionHost(Button button) => entries.FirstOrDefault(entry => ReferenceEquals(entry.Button, button))?.FocusHost;

    private void SetActionHost(Button button, Border host)
    {
        var index = entries.FindIndex(entry => ReferenceEquals(entry.Button, button));
        if (index < 0)
            throw new InvalidOperationException("An action must be registered before its focus host is assigned.");
        entries[index] = entries[index] with
        {
            FocusHost = host
        };
    }
}

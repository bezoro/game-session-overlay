using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace ApolloOverlay;

public partial class MainWindow
{
    private void BuildResult()
    {
        if (Controller.Operation is not { } result)
        {
            AddRow("back", "Back", "No retained result", Back);
            return;
        }

        // Only the message scrolls. Reader entry and recovery actions remain
        // reachable without moving a second page viewport around the reader.
        var pane = new Grid();
        pane.SetBinding(HeightProperty, new Binding(nameof(ScrollViewer.ViewportHeight)) { Source = ContentScroll });
        pane.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        pane.RowDefinitions.Add(new()
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        pane.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        var read = ResultButton("operation-text", "Read message", "Arrows to scroll · B / Esc to go back", () => operationText?.Focus());
        pane.Children.Add(read);
        operationText = new TextBox
        {
            Text = result.Message,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MinHeight = 0,
            Padding = new(12),
            Margin = new(0, 8, 0, 8)
        };
        operationText.SetResourceReference(TextBox.FontSizeProperty, "SessionType.Body");
        operationText.SetResourceReference(BackgroundProperty, ThemeSupport.Card);
        operationText.SetResourceReference(ForegroundProperty, ThemeSupport.Text);
        AutomationProperties.SetName(operationText, "Full result · " + result.State);
        AutomationProperties.SetHelpText(operationText, "Arrows scroll. Right returns to dismissal; Esc goes back.");
        Grid.SetRow(operationText, 1);
        pane.Children.Add(operationText);
        var commands = new Grid();
        commands.ColumnDefinitions.Add(new());
        commands.ColumnDefinitions.Add(new());
        var dismiss = ResultButton("dismiss-operation", "Dismiss", "Removes only this message", DismissOperation);
        dismiss.Margin = new(0, 0, 4, 0);
        commands.Children.Add(dismiss);
        var back = ResultButton("back", "Back", "Keeps the result available", Back);
        back.Margin = new(4, 0, 0, 0);
        Grid.SetColumn(back, 1);
        commands.Children.Add(back);
        Grid.SetRow(commands, 2);
        pane.Children.Add(commands);
        AddVisual(pane);
    }

    private Button ResultButton(string id, string title, string description, Action action)
    {
        var button = MakeButton(id, title, description, action, true, false);
        button.MinHeight = 44;
        button.Margin = new(0);
        button.Padding = new(8);
        return button;
    }
}

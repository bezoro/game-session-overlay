using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace ApolloOverlay;

public partial class MainWindow
{
    private string keyboardText = "";
    private bool keyboardUpper, keyboardSymbols, keyboardForProfile;
    private WindowRef? keyboardTarget;
    private TextBox? keyboardEditor;
    private void OpenKeyboard(bool forProfile)
    {
        keyboardForProfile = forProfile;
        keyboardText = forProfile ? profileDraft?.Name ?? "" : "";
        keyboardUpper = false;
        keyboardSymbols = false;
        keyboardTarget = Controller.ReturnTarget is { } previous && Controller.Platform.Valid(previous) && !previous.Protected ? previous : Controller.Session.Anchor;
        OpenFeature(OverlayRoute.Keyboard, "key:q");
    }

    private void BuildSessionKeyboard()
    {
        var compact = narrow || Rows.ActualWidth < 400;
        var keyHeight = compact ? 36 : 44;
        var actionHeight = compact ? 32 : 38;
        var pane = new Grid();
        pane.SetBinding(HeightProperty, new Binding(nameof(ScrollViewer.ViewportHeight)) { Source = ContentScroll });
        pane.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
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
        pane.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });

        if (!keyboardForProfile)
        {
            var title = "To: " + (keyboardTarget?.Title ?? "Choose a window");
            var destination = KeyboardButton("keyboard-target", title, () =>
            {
                var targets = Controller.Apps.Where(target => !target.Protected && Controller.Platform.Valid(target)).ToArray();
                var index = Array.FindIndex(targets, target => target.Same(keyboardTarget));
                keyboardTarget = targets.Length > 0 ? targets[(index + 1) % targets.Length] : null;
                RefreshRows();
            }, "Choose the exact window that receives the text", compact ? 28 : 32);
            ((TextBlock)destination.Content).TextAlignment = TextAlignment.Left;
            destination.FontSize = 14;
            destination.ToolTip = "Target window: " + (keyboardTarget?.Title ?? "None selected");
            AddKeyboardButton(pane, destination, 0);
        }

        var editorButton = KeyboardButton("keyboard-editor", "Edit text", () => keyboardEditor?.Focus(), "Type or choose keys; Enter is never sent", compact ? 42 : 48);
        editorButton.Padding = new(2);
        var editorContent = new Grid();
        editorContent.ColumnDefinitions.Add(new());
        editorContent.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(48)
        });
        keyboardEditor = new TextBox
        {
            Text = keyboardText,
            MaxLength = keyboardForProfile ? 48 : 512,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new(6, 2, 4, 2),
            BorderThickness = new(0),
            FontSize = compact ? 18 : 20,
            ToolTip = keyboardForProfile ? "Profile name" : "Text to insert; Enter is never sent"
        };
        AutomationProperties.SetName(keyboardEditor, keyboardForProfile ? "Profile name" : "Text to insert");
        keyboardEditor.SetResourceReference(ForegroundProperty, ThemeSupport.Text);
        keyboardEditor.SetResourceReference(BackgroundProperty, ThemeSupport.Surface);
        keyboardEditor.SetResourceReference(TextBox.CaretBrushProperty, ThemeSupport.Focus);
        keyboardEditor.SetResourceReference(TextBox.SelectionBrushProperty, ThemeSupport.FocusBackground);
        var count = new TextBlock
        {
            Text = keyboardText.Length + "/" + (keyboardForProfile ? 48 : 512),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        count.SetResourceReference(ForegroundProperty, ThemeSupport.SecondaryText);
        Grid.SetColumn(count, 1);
        editorContent.Children.Add(keyboardEditor);
        editorContent.Children.Add(count);
        var placeholder = new TextBlock
        {
            Text = keyboardForProfile ? "Profile name" : "Type or choose keys",
            Margin = new(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = keyboardText.Length == 0 ? Visibility.Visible : Visibility.Collapsed
        };
        placeholder.SetResourceReference(ForegroundProperty, ThemeSupport.SecondaryText);
        editorContent.Children.Add(placeholder);
        editorButton.Content = editorContent;
        var editor = keyboardEditor;
        editor.TextChanged += (_, _) =>
        {
            keyboardText = editor.Text;
            SetEntryDescription("keyboard-editor", keyboardText.Length + " characters");
            count.Text = keyboardText.Length + "/" + (keyboardForProfile ? 48 : 512);
            placeholder.Visibility = keyboardText.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        AddKeyboardButton(pane, editorButton, 1);

        var characters = keyboardSymbols ? "1234567890@#$%&*()-_!?/:;+=%" : "qwertyuiopasdfghjklzxcvbnm,.?";
        const int columns = 10;
        var board = new Grid();
        for (var column = 0; column < columns; column++)
            board.ColumnDefinitions.Add(new());
        for (var row = 0; row < (characters.Length + columns - 1) / columns; row++)
            board.RowDefinitions.Add(new()
            {
                Height = GridLength.Auto
            });
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index].ToString();
            var value = keyboardUpper ? character.ToUpperInvariant() : character;
            var id = "key:" + character;
            var button = KeyboardButton(id, value, () => AppendKeyboard(value), "Insert " + value, keyHeight);
            button.FontSize = compact ? 17 : 19;
            button.Padding = new(0);
            AddKeyboardButton(board, button, index / columns, index % columns);
        }

        // The key grid alone can scroll on short displays; destination, text and Insert stay visible.
        var keyScroll = new ScrollViewer
        {
            Content = board,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new(0, 4, 0, 4),
            Focusable = false
        };
        AutomationProperties.SetName(keyScroll, "Keyboard keys");
        Grid.SetRow(keyScroll, 2);
        pane.Children.Add(keyScroll);

        var edits = new Grid();
        foreach (var width in new[] { 1.5, 1, 1, 1, 1 })
            edits.ColumnDefinitions.Add(new()
            {
                Width = new GridLength(width, GridUnitType.Star)
            });
        AddKeyboardButton(edits, KeyboardButton("keyboard-space", "Space", () => AppendKeyboard(" "), "Insert a space", actionHeight), 0, 0);
        AddKeyboardButton(edits, KeyboardButton("keyboard-delete", "Delete", () =>
        {
            keyboardText = SessionTextEntry.Backspace(keyboardText);
            if (keyboardEditor is not null)
                keyboardEditor.Text = keyboardText;
        }, "Remove the last character", actionHeight), 0, 1);
        AddKeyboardButton(edits, KeyboardButton("keyboard-case", keyboardUpper ? "abc" : "ABC", () =>
        {
            keyboardUpper = !keyboardUpper;
            RefreshRows();
        }, "Change letter case", actionHeight), 0, 2);
        AddKeyboardButton(edits, KeyboardButton("keyboard-symbols", keyboardSymbols ? "Keys" : "123", () =>
        {
            keyboardSymbols = !keyboardSymbols;
            RefreshRows();
        }, "Switch letters and symbols", actionHeight), 0, 3);
        AddKeyboardButton(edits, KeyboardButton("keyboard-clear", "Clear", () =>
        {
            keyboardText = "";
            if (keyboardEditor is not null)
                keyboardEditor.Text = "";
        }, "Clear the text being edited", actionHeight), 0, 4);
        Grid.SetRow(edits, 3);
        pane.Children.Add(edits);

        var commands = new Grid { Margin = new(0, 4, 0, 0) };
        commands.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(64)
        });
        commands.ColumnDefinitions.Add(new());
        AddKeyboardButton(commands, KeyboardButton("back", "Back", Back, "Return without sending text", compact ? 44 : 48), 0, 0);
        var insert = KeyboardButton("keyboard-insert", keyboardForProfile ? "Use name" : "Insert text", SubmitSessionKeyboard,
            keyboardForProfile ? "Return to the profile without sending text to another app" : "Sends text only; Enter is never sent", compact ? 44 : 48, keyboardForProfile || keyboardTarget is not null);
        var insertContent = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        insertContent.Children.Add(new TextBlock { Text = keyboardForProfile ? "Use name" : "Insert text", FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center });
        var insertionHint = new TextBlock { Text = keyboardForProfile ? "Return to profile" : "Text only · never Enter", FontSize = 11, TextAlignment = TextAlignment.Center };
        insertionHint.SetResourceReference(ForegroundProperty, ThemeSupport.SecondaryText);
        insertContent.Children.Add(insertionHint);
        insert.Content = insertContent;
        AddKeyboardButton(commands, insert, 0, 1);
        Grid.SetRow(commands, 4);
        pane.Children.Add(commands);
        AddVisual(pane);
    }

    private Button KeyboardButton(string id, string title, Action action, string description, double height, bool available = true)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            Tag = id,
            Height = height,
            MinHeight = height,
            MinWidth = 0,
            Padding = new(2),
            Margin = new(1),
            FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = description
        };
        button.Click += (_, _) => ActivatePointerEntry(button);
        button.GotKeyboardFocus += (_, _) =>
        {
            navigation = new(id);
            SetSelection(false, false);
        };
        AutomationProperties.SetName(button, title);
        AutomationProperties.SetHelpText(button, description);
        var host = new Border { Child = button, Focusable = !available, Tag = "description:" + id };
        AutomationProperties.SetName(host, title + ". " + description);
        button.Resources["KeyboardHost"] = host;
        if (!available)
            button.Resources["DescriptionTarget"] = host;
        RegisterFixed(id, button, action, available, description);
        return button;
    }

    private static void AddKeyboardButton(Grid grid, Button button, int row, int column = 0)
    {
        var host = (Border)button.Resources["KeyboardHost"];
        Grid.SetRow(host, row);
        Grid.SetColumn(host, column);
        grid.Children.Add(host);
    }

    private void AppendKeyboard(string value)
    {
        var maximum = keyboardForProfile ? 48 : 512;
        if (keyboardText.Length + value.Length > maximum)
        {
            FeedbackText("Text limit reached.");
            return;
        }

        keyboardText += value;
        if (keyboardEditor is not null)
        {
            keyboardEditor.Text = keyboardText;
            keyboardEditor.CaretIndex = keyboardText.Length;
            keyboardEditor.ScrollToHorizontalOffset(double.MaxValue);
        }
    }

    private void SubmitSessionKeyboard()
    {
        if (keyboardForProfile)
        {
            var name = keyboardText.Trim();
            if (name.Length == 0)
            {
                FeedbackText("Choose a profile name.");
                return;
            }

            if (profileDraft is not null)
                profileDraft = profileDraft with
                {
                    Name = name
                };
            Back();
            return;
        }

        if (keyboardTarget is not { } target || keyboardText.Length == 0)
        {
            FeedbackText("Choose a window and enter some text before inserting.");
            return;
        }

        var text = keyboardText;
        if (diagnostic || Controller.Platform.IsPreview)
        {
            RecordResult(new(ResultKind.Simulated, "Preview: no text was sent to another app."), "keyboard");
            return;
        }

        var writer = new SessionTextEntry(new WindowsTextEntryEffects(Controller.Platform));
        RunEffect((token, keep) => writer.InsertAsync(target, text, keep, token), () => Controller.Platform.Valid(target), target);
    }

    private bool HandleSessionKeyboardKey(KeyEventArgs args)
    {
        if (view.Current.Route != OverlayRoute.Keyboard || keyboardEditor?.IsKeyboardFocusWithin != true || args.Key != Key.Tab)
            return false;
        if (NavigationAllowed && navigationKeysArmed)
            Sequential(KeyboardModifiersReader().HasFlag(ModifierKeys.Shift) ? -1 : 1);
        args.Handled = true;
        return true;
    }
}

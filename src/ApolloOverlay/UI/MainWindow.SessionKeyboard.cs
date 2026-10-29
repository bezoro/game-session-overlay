using System.Windows;
using System.Windows.Controls;
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
        Paragraph(keyboardForProfile ? "Profile name" : "Place the cursor in the target field before opening the overlay. Inserting text does not press Enter.");
        if (!keyboardForProfile)
            AddRow("keyboard-target", keyboardTarget?.Title ?? "No window selected", "Text destination · A changes the window", () =>
            {
                var targets = Controller.Apps.Where(target => !target.Protected && Controller.Platform.Valid(target)).ToArray();
                var index = Array.FindIndex(targets, target => target.Same(keyboardTarget));
                keyboardTarget = targets.Length > 0 ? targets[(index + 1) % targets.Length] : null;
                RefreshRows();
            });
        var editorButton = AddRow("keyboard-editor", "Your text", keyboardText.Length + " / " + (keyboardForProfile ? 48 : 512) + " characters", () => keyboardEditor?.Focus());
        keyboardEditor = new TextBox
        {
            Text = keyboardText,
            MaxLength = keyboardForProfile ? 48 : 512,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            Margin = new(0, 8, 0, 0),
            FontSize = 20
        };
        keyboardEditor.SetResourceReference(ForegroundProperty, ThemeSupport.Text);
        keyboardEditor.SetResourceReference(BackgroundProperty, ThemeSupport.Surface);
        keyboardEditor.SetResourceReference(BorderBrushProperty, ThemeSupport.Stroke);
        keyboardEditor.SetResourceReference(TextBox.CaretBrushProperty, ThemeSupport.Focus);
        keyboardEditor.SetResourceReference(TextBox.SelectionBrushProperty, ThemeSupport.FocusBackground);
        keyboardEditor.Padding = new(10);
        keyboardEditor.BorderThickness = new(1);
        var editor = keyboardEditor;
        editor.TextChanged += (_, _) =>
        {
            keyboardText = editor.Text;
            SetEntryDescription("keyboard-editor", keyboardText.Length + " characters");
        };
        ((ActionButtonContent)editorButton.Content).Texts.Children.Add(editor);
        var characters = keyboardSymbols ? "1234567890@#$%&*()-_!?/:;+=%" : "qwertyuiopasdfghjklzxcvbnm,.?";
        var columns = narrow || Rows.ActualWidth < 480 ? 8 : 10;
        var board = new Grid
        {
            Margin = new(0, 10, 0, 10)
        };
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
            var button = new Button
            {
                Content = value,
                Tag = "key:" + character,
                Padding = new(0),
                Margin = new(2),
                MinHeight = 44,
                FontSize = 19,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            var id = "key:" + character;
            button.Click += (_, _) => ActivatePointerEntry(button);
            button.GotKeyboardFocus += (_, _) =>
            {
                navigation = new(id);
                SetSelection(false, false);
            };
            System.Windows.Automation.AutomationProperties.SetName(button, "Insert " + value);
            Grid.SetRow(button, index / columns);
            Grid.SetColumn(button, index % columns);
            board.Children.Add(button);
            RegisterFixed(id, button, () => AppendKeyboard(value), true, "Insert " + value);
        }

        var boardRow = Rows.RowDefinitions.Count;
        Rows.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        Grid.SetRow(board, boardRow);
        Grid.SetColumnSpan(board, 2);
        Rows.Children.Add(board);
        var first = Rows.RowDefinitions.Count;
        AddRow("keyboard-space", "Space", "Insert a space", () => AppendKeyboard(" "), row: narrow ? null : first, column: 0);
        AddRow("keyboard-delete", "Delete", "Remove the last character", () =>
        {
            keyboardText = SessionTextEntry.Backspace(keyboardText);
            if (keyboardEditor is not null)
                keyboardEditor.Text = keyboardText;
        }, row: narrow ? null : first, column: 1);
        first = Rows.RowDefinitions.Count;
        AddRow("keyboard-case", keyboardUpper ? "Lowercase" : "Uppercase", "Change letter case", () =>
        {
            keyboardUpper = !keyboardUpper;
            RefreshRows();
        }, row: narrow ? null : first, column: 0);
        AddRow("keyboard-symbols", keyboardSymbols ? "ABC" : "123 and symbols", "Switch the keyboard layout", () =>
        {
            keyboardSymbols = !keyboardSymbols;
            RefreshRows();
        }, row: narrow ? null : first, column: 1);
        AddRow("keyboard-clear", "Clear text", "Clear the text being edited", () =>
        {
            keyboardText = "";
            if (keyboardEditor is not null)
                keyboardEditor.Text = "";
        });
        AddRow("keyboard-insert", keyboardForProfile ? "Use this name" : "Insert into window", keyboardForProfile ? "Return to the profile without sending text to another app" : "Sends text only; check the destination field in the app", SubmitSessionKeyboard, keyboardForProfile || keyboardTarget is not null);
        AddRow("back", "Back", "Do not send the text", Back);
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
            keyboardEditor.Text = keyboardText;
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

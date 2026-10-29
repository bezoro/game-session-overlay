using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace ApolloOverlay;

public partial class MainWindow
{
    private Point? pointerPosition;

    private string InputHint(string controller, string keyboard, string mouse) => inputMethod switch
    {
        "controller" => controller,
        "mouse" => mouse,
        _ => keyboard
    };

    private void SetInputMethod(string method)
    {
        if (inputMethod == method)
            return;
        inputMethod = method;
        Cursor = method == "controller" ? Cursors.None : Cursors.Arrow;
        UpdateHints();
        // Update button prompts without moving focus, scrolling or rebuilding editors.
        SetSelection(false, false);
    }

    internal void ObservePointerPosition(Point screenPosition)
    {
        if (!IsVisible || exiting)
            return;
        if (pointerPosition is not { } previous)
        {
            pointerPosition = screenPosition;
            return;
        }

        // Screen coordinates exclude layout-induced movement; ignore small pointer jitter.
        if ((screenPosition - previous).LengthSquared < 16)
            return;
        pointerPosition = screenPosition;
        SetInputMethod("mouse");
    }

    private void UpdateContextualHints()
    {
        var read = InputHint("A to read · D-pad scrolls · B goes back", "Enter to read · arrows scroll · Esc goes back", "Click to read · scroll for more");
        SetEntryDescription("operation-text", read);
        SetEntryDescription("window-information", read);
        SetEntryDescription("update-notes", read);
        SetEntryDescription("shortcut", InputHint("A switches Shift+Tab / Ctrl+Tab", "Enter switches Shift+Tab / Ctrl+Tab", "Click to switch Shift+Tab / Ctrl+Tab"));
        SetEntryDescription("keyboard-target", InputHint("Text destination · A changes the window", "Text destination · Enter changes the window", "Click to choose the text destination"));
        SetEntryDescription("prepare-update", InputHint("B cancels download · current installation preserved", "Esc cancels download · current installation preserved", "Cancel stops the download · current installation preserved"));
        SetEntryDescription("mixer-volume", InputHint("← / → adjusts volume", "Arrow keys adjust · Tab leaves the slider", "Drag to adjust volume"));
        SetEntryDescription("session-volume", GameVolumeDescription);
        SetEntryDescription("test-feedback", FeedbackDescription());
        var target = Controller.Session.Anchor;
        AutomationProperties.SetHelpText(SessionCardButton, target is not null && Controller.Platform.Valid(target)
            ? InputHint("A resumes this window · X opens options", "Enter resumes this window · X opens options", "Click to resume · ·· opens options")
            : "Choose a window in Apps");
        if (HoldCloseButton.Content is TextBlock hold)
            hold.Text = InputHint("Hold A · 1.5 s", "Hold Enter / Space · 1.5 s", "Press and hold · 1.5 s");
    }
}

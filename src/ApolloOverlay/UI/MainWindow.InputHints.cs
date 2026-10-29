using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace ApolloOverlay;

public partial class MainWindow
{
    private Point? pointerPosition;

    private void RenderHint(System.Windows.Controls.TextBlock target, string template) => InputPrompt.Apply(target, template, this);

    private void SetEntryPrompt(string id, string template) => SetEntryDescription(id, InputPrompt.PlainText(template), template);

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
        SetSelection(restoreFocus: false);
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
        var read = InputHint("{A} to read · {Dpad} scrolls · {B} goes back", "{Enter} to read · {Arrows} scroll · {Esc} goes back", "{MouseLeft} to read · {MouseScroll} for more");
        SetEntryPrompt("operation-text", read);
        SetEntryPrompt("window-information", read);
        SetEntryPrompt("update-notes", read);
        SetEntryPrompt("shortcut", InputHint("{A} switches {Shift}+{Tab} / {Ctrl}+{Tab}", "{Enter} switches {Shift}+{Tab} / {Ctrl}+{Tab}", "{MouseLeft} to switch {Shift}+{Tab} / {Ctrl}+{Tab}"));
        SetEntryPrompt("keyboard-target", InputHint("Text destination · {A} changes the window", "Text destination · {Enter} changes the window", "{MouseLeft} to choose the text destination"));
        SetEntryPrompt("prepare-update", (updateReview is { } candidate ? UpdateActionContext(candidate) + " · " : "")
            + InputHint("{B} cancels download", "{Esc} cancels download", "{MouseLeft} Cancel stops the download"));
        SetEntryPrompt("mixer-volume", InputHint("{DpadHorizontal} adjusts volume", "{Left}/{Right} adjust · {Tab} leaves the slider", "{MouseLeft} drag to adjust volume"));
        SetEntryPrompt("guide-limit", InputHint("{DpadHorizontal} adjusts · 0.5–3.0 s", "{Left}/{Right} adjusts · 0.5–3.0 s", "{MouseLeft} drag · 0.5–3.0 s"));
        SetEntryDescription("session-volume", GameVolumeDescription);
        SetEntryDescription("test-feedback", FeedbackDescription());
        var target = Controller.Session.Anchor;
        AutomationProperties.SetHelpText(SessionCardButton, target is not null && Controller.Platform.Valid(target)
            ? InputHint("A resumes this window · X opens options", "Enter resumes this window · X opens options", "Click resumes · right-click opens options")
            : "Choose a window in Apps");
        if (HoldCloseButton.Content is IconLabel hold)
            RenderHint(hold.Label, InputHint("Hold {A} · 1.5 s", "Hold {Enter} / {Space} · 1.5 s", "Hold {MouseLeft} · 1.5 s"));
    }
}

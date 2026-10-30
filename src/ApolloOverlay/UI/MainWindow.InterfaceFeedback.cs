using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ApolloOverlay;

public partial class MainWindow
{
    private int interfaceActionDepth;
    private void ClickTab(Button button, int index)
    {
        SetInputMethod("mouse");
        if (!NavigationAllowed || TransitionPending || Confirming || page == index && view.Depth == 0)
            return;
        var previousSource = dispatchSource;
        dispatchSource = -1;
        try
        {
            RunInterfaceAction(button, () => Page(index), FeedbackCue.Navigation);
        }
        finally
        {
            dispatchSource = previousSource;
        }
    }

    private void ActivatePointerEntry(Button button)
    {
        SetInputMethod("mouse");
        if (button.Tag is not string id)
            return;
        var previousSource = dispatchSource;
        dispatchSource = -1;
        try
        {
            navigation = new(id);
            SetSelection();
            if (id == "settings" && NavigationAllowed && !TransitionPending && !Confirming)
                RunInterfaceAction(button, () => Page(2), FeedbackCue.Confirmation);
            else if (NavigationAllowed && (id == "back-header" || Confirming && id == "confirm-cancel"))
                RunInterfaceAction(button, Back, FeedbackCue.Cancellation);
            else
                ActivateEntry();
        }
        finally
        {
            dispatchSource = previousSource;
        }
    }

    private void RunInterfaceAction(Button button, Action action, FeedbackCue? cue)
    {
        if (interfaceActionDepth == 0 && cue is { } signal)
            RequestCue(signal);
        PulseActivation(button);
        interfaceActionDepth++;
        try
        {
            action();
        }
        finally
        {
            interfaceActionDepth--;
            // Settings rebuild their row. Keep the acknowledgement on the replacement too.
            if (button.Tag is string id && entries.FirstOrDefault(e => e.Id == id)?.Button is { } current && current != button)
                PulseActivation(current);
        }
    }

    private void PulseActivation(Button button)
    {
        InteractionMotion.Press(button);
        button.ApplyTemplate();
        if (button.Template.FindName("ActivationFeedback", button) is not Border highlight)
            return;
        if (button.Resources["ActivationTimer"] is DispatcherTimer previous)
            previous.Stop();
        highlight.BeginAnimation(OpacityProperty, null);
        highlight.Opacity = .6;
        if (OpenAnimationAllowed)
        {
            highlight.BeginAnimation(OpacityProperty, new DoubleAnimation(.6, 0, ResourceDuration("SessionMotion.Notice", 140)));
            return;
        }

        // A system animation restriction still receives a brief static acknowledgement.
        var reset = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        reset.Tick += (_, _) =>
        {
            reset.Stop();
            highlight.Opacity = 0;
            button.Resources.Remove("ActivationTimer");
        };
        button.Resources["ActivationTimer"] = reset;
        reset.Start();
    }

    private void TestFeedback()
    {
        var sent = RequestCue(FeedbackCue.Test);
        var controllerSource = dispatchSource > 0 || dispatchSource is null && inputMethod == "controller";
        FeedbackText((sent.SoundRequested, sent.HapticRequested) switch
        {
            (true, true) => "Test requested. Check the sound and vibration on the active controller.",
            (true, false) => "Test requested. Check the sound on the active audio output.",
            (false, true) => "Test requested. Check vibration on the active controller.",
            _ => Controller.Settings.HapticFeedbackEnabled && !Controller.Settings.SoundFeedbackEnabled && !controllerSource ? "Press A on the active controller to test vibration." : sent.Reason
        });
    }

    private string FeedbackDescription()
    {
        if (!Controller.Settings.SoundFeedbackEnabled && !Controller.Settings.HapticFeedbackEnabled)
            return "Enable sounds or vibration to test feedback";
        if (Controller.Settings.SoundFeedbackEnabled && !feedbackService.Status.SoundAvailable)
            return feedbackService.Status.Reason;
        return Controller.Settings.SoundFeedbackEnabled ? "Hear navigation and confirmation · use the controller to test vibration" : InputHint("Press A to test a short pulse on the active controller", "Use the active controller to test vibration", "Use the active controller to test vibration");
    }
}

namespace ApolloOverlay;

public partial class MainWindow
{
    private readonly ReleaseGate releaseGate = new();
    private readonly HeldInputTracker handoffInputs = new();
    private Action? handoff;
    private CommandLogOperation? handoffCommand;
    private Func<bool>? handoffValid;
    private bool handoffDismiss, handoffDegraded, degradedExplicit, committing;
    private long? degradedSince;
    private ReleaseSample? lastInputSample;
    internal ReleaseSample? HandoffSampleFixture
    {
        set => lastInputSample = value;
    }
    internal bool TransitionPending => handoff is not null;

    private void QueueHandoff(Action action, Func<bool>? valid = null, bool dismiss = false)
    {
        if (exiting || busy)
        {
            LogCommandResult("input-handoff", ResultKind.Unavailable, "busy-or-exiting");
            return;
        }
        if (handoff is not null)
        {
            if (!dismiss || handoffDismiss)
                return;
            CancelHandoff();
        }

        var correlation = commandCorrelation ?? Guid.NewGuid().ToString("N");
        var origin = CommandSource;
        handoffCommand = BeginCommand(dismiss ? "dismiss-handoff" : "command-handoff", correlation: correlation);
        handoff = () =>
        {
            var previousCorrelation = commandCorrelation;
            var previousOrigin = commandOrigin;
            commandCorrelation = correlation;
            commandOrigin = origin;
            try
            {
                action();
            }
            finally
            {
                commandCorrelation = previousCorrelation;
                commandOrigin = previousOrigin;
            }
        };
        handoffValid = valid;
        handoffDismiss = dismiss;
        releaseGate.Reset();
        if (lastInputSample is { } sample && sample.Timestamp <= Environment.TickCount64)
        {
            releaseGate.Prime(sample);
            CaptureHandoffBaseline(sample);
            ObserveHandoffInputs(sample, Environment.TickCount64);
        }

        CancelHold();
        input.Cancel();
        if (!dismiss)
            RetainOperation("Pending", InputHint("Release the controller to continue · B cancels before sending.", "Release the keys to continue · Esc cancels before sending.", "Release the mouse button to continue."));
        FeedbackText(dismiss ? "Release the controls to hide the panel." : InputHint("Release the controller to continue · B cancels.", "Release the keys to continue · Esc cancels.", "Release the mouse button to continue."), important: dismiss);
    }

    private void CancelHandoff(bool terminalize = true)
    {
        if (terminalize)
        {
            handoffCommand?.Complete(ResultKind.Cancelled, "cancelled-before-dispatch");
            handoffCommand?.Dispose();
        }
        handoffCommand = null;
        var cancelled = terminalize && handoff is not null && !handoffDismiss && Controller.Operation is { State: "Pending", Dispatched: false };
        handoff = null;
        handoffValid = null;
        handoffDismiss = false;
        handoffDegraded = false;
        degradedExplicit = false;
        degradedSince = null;
        handoffInputs.Reset();
        releaseGate.Reset();
        if (cancelled)
            RetainOperation("Cancelled", "Request cancelled before sending; no external command was sent.");
    }

    private void RejectHandoff(string message)
    {
        if (!handoffDismiss)
        {
            handoffCommand?.Complete(ResultKind.Cancelled, "input-or-context-changed");
            handoffCommand?.Dispose();
            CancelHandoff(false);
            input.Cancel();
            RetainOperation("Cancelled", message);
            FeedbackText(message);
            return;
        }

        if (handoffDegraded)
            return;
        handoffDegraded = true;
        handoffValid = null;
        handoff = CompleteDegradedDismiss;
        degradedSince = null;
        releaseGate.Reset();
        input.Cancel();
        FeedbackText("Input or context uncertain. Release the controls to hide without returning to the app.", important: true);
    }

    private void CompleteDegradedDismiss()
    {
        HideNow(false);
        FeedbackText("Panel hidden without requesting a return to the app.");
        LogCommandResult("dismiss-without-focus-return", ResultKind.Cancelled, "input-unavailable");
    }

    private bool PadHeld(PadFrame pad) => HeldInputTracker.IsHeld(pad, Controller.Settings.Deadzone);
    private void CaptureHandoffBaseline(ReleaseSample sample) => handoffInputs.CaptureBaseline(sample, Controller.Settings.Deadzone);
    private void ObserveHandoffInputs(ReleaseSample sample, long now) => handoffInputs.Observe(sample, now, Controller.Settings.Deadzone);
    private void AdvanceDegradedDismiss(ReleaseSample sample, long now)
    {
        if (!sample.KeysNeutral || heldKeys.Count != 0 || handoffInputs.KnownActive)
        {
            degradedSince = null;
            return;
        }

        if (handoffInputs.UnknownHeld && !degradedExplicit)
        {
            degradedSince = null;
            FeedbackText("Input unavailable. Press and release Esc to hide safely.", important: true);
            return;
        }

        if (degradedSince is null || now < degradedSince)
            degradedSince = now;
        if (now - degradedSince.Value >= 100)
            CommitHandoff();
    }

    private void ReaffirmDismiss(bool trusted)
    {
        if (!trusted || !handoffDegraded)
            return;
        degradedExplicit = true;
        degradedSince = null;
        FeedbackText("Release available inputs to hide without returning to the app.", important: true);
    }

    private void CommitHandoff()
    {
        var action = handoff;
        using var command = handoffCommand;
        CancelHandoff(false);
        committing = true;
        try
        {
            action?.Invoke();
            command?.Complete(ResultKind.Requested, "handoff-dispatched");
        }
        catch (Exception error)
        {
            command?.Fail(error, "handoff-dispatch-threw");
            throw;
        }
        finally
        {
            committing = false;
        }
    }

    internal void AdvanceHandoff(ReleaseSample sample, long now)
    {
        if (handoff is null)
            return;
        sample = sample with
        {
            KeysNeutral = sample.KeysNeutral && heldKeys.Count == 0 && PhysicalKeysNeutral()
        };
        ObserveHandoffInputs(sample, now);
        if (handoffDegraded)
        {
            AdvanceDegradedDismiss(sample, now);
            return;
        }

        var context = InputContext;
        var state = releaseGate.Update(sample, now, context.Visible && context.Active && context.OwnForeground && !context.Exiting, Controller.Settings.Deadzone);
        if (state == ReleaseState.Cancelled || handoffValid?.Invoke() == false)
        {
            RejectHandoff("Transition cancelled; focus, controller or target changed.");
            if (handoffDegraded)
                AdvanceDegradedDismiss(sample, now);
            return;
        }

        if (state == ReleaseState.Waiting)
            return;
        CommitHandoff();
    }
}

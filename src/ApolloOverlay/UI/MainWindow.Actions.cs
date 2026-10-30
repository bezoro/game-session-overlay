using System.Windows;
using System.Windows.Automation;

namespace ApolloOverlay;

public partial class MainWindow
{
    internal Action<string>? ResultNotificationRequest
    {
        private get;
        set;
    }

    private long operationGeneration;
    private readonly CloseHold closeHold = new();
    private bool pointerDown, pointerCaptured;
    private int? closeSource, dispatchSource;
    private CancellationTokenSource? pending;
    private CancellationTokenSource? effectCancellation;
    private WindowRef? effectFocusTarget;
    private bool effectFocusTransferred;
    private int effectPadConnections, effectGuideConnections;
    internal Func<PadFrame[]>? EffectFramesFixture
    {
        private get;
        set;
    }

    private bool EffectTargetValid(WindowRef target) => Controller.Platform.Valid(target) || Controller.Platform.CanSteamMenu(target);
    private bool EffectInputsNeutral(IReadOnlyList<PadFrame> frames)
    {
        if (frames.Count != 4
            || frames.Any(f => f.ReadFailed
            || f.Connected
            && (f.Buttons != 0
            || Math.Abs((int)f.X) >= Controller.Settings.Deadzone * 2 / 3
            || Math.Abs((int)f.Y) >= Controller.Settings.Deadzone * 2 / 3
            || Math.Abs((int)f.RX) >= Controller.Settings.Deadzone * 2 / 3
            || Math.Abs((int)f.RY) >= Controller.Settings.Deadzone * 2 / 3
            || f.LT >= 30
            || f.RT >= 30)))
            return false;
        var connected = 0;
        for (var i = 0; i < frames.Count; i++)
            if (frames[i].Connected)
                connected |= 1 << i;
        if ((effectPadConnections & ~connected) != 0)
            return false;
        effectPadConnections |= connected;
        if (guideBackend is { Active: true } backend)
        {
            if (!backend.SnapshotValid || backend.Held || (effectGuideConnections & ~backend.Connections) != 0)
                return false;
            effectGuideConnections |= backend.Connections;
        }
        else if (effectGuideConnections != 0)
            return false;
        return PhysicalKeysNeutral();
    }

    private void ReleasePointer()
    {
        pointerDown = false;
        pointerCaptured = false;
        if (HoldCloseButton.IsMouseCaptured)
            HoldCloseButton.ReleaseMouseCapture();
    }

    private void CancelHold()
    {
        nextConfirmationScroll = 0;
        var restore = view.Current.Route == OverlayRoute.Confirmation;
        ReleasePointer();
        closeHold.Cancel();
        HeaderArea.Visibility = Visibility.Visible;
        TabArea.Visibility = Visibility.Visible;
        closeSource = null;
        keyboardDown = false;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        ContentScroll.Visibility = Visibility.Visible;
        if (restore && view.Back())
        {
            SyncView();
            RefreshRows();
            ContentScroll.ScrollToVerticalOffset(view.Current.ScrollOffset);
        }

        UpdatePersistentState();
        UpdateHints();
    }

    internal bool Confirming => closeHold.Target is not null;
    internal WindowRef? HeldCloseTarget => closeHold.Target;
    internal double CloseProgress => closeHold.Progress;

    internal void Confirm(WindowRef target)
    {
        if (!target.CanClose || !Controller.Platform.Valid(target))
        {
            FeedbackText("Window protected or identity changed; closing blocked.");
            return;
        }

        SaveView();
        view.Open(new(OverlayRoute.Confirmation, target, navigation.Id, ContentScroll.VerticalOffset));
        SyncView();
        closeSource = dispatchSource ?? 0;
        closeHold.Open(target);
        entries.Clear();
        RegisterFixed("confirm-hold", HoldCloseButton, () =>
        {
        }, true, "Release, then hold for 1.5 s");
        RegisterFixed("confirm-cancel", CancelCloseButton, Back, true, "Cancel closing");
        navigation = new("confirm-hold");
        HeaderArea.Visibility = Visibility.Collapsed;
        TabArea.Visibility = Visibility.Collapsed;
        UpdatePersistentState();
        ConfirmTarget.Text = target.Title;
        ConfirmTarget.ToolTip = target.Title;
        AutomationProperties.SetName(ConfirmTarget, target.Title);
        ConfirmPanel.Visibility = Visibility.Visible;
        ContentScroll.Visibility = Visibility.Collapsed;
        HoldProgress.Value = 0;
        ConfirmHint.Text = "Waiting for release…";
        FeedbackText("No close command sent.");
        UpdateHints();
        SetSelection();
    }

    internal bool AdvancePointerHold(bool down, bool captured, bool inside, long now)
    {
        if (!Confirming || closeSource != -1)
            return false;
        var target = closeHold.Target!;
        var context = InputContext;
        var valid = Controller.Platform.Valid(target) && target.Same(view.Current.Target ?? Controller.Session.Anchor) ? target : null;
        var completed = closeHold.Tick(down, false, context.Active && context.OwnForeground && context.Confirmed, captured && (!down || inside), valid, now);
        HoldProgress.Value = closeHold.Progress;
        ConfirmHint.Text = closeHold.Progress > 0 ? $"Keep holding… {closeHold.Progress:P0}" : "Release, then hold again for 1.5 s.";
        if (completed)
        {
            CancelHold();
            RunClose(target);
        }
        else if (!Confirming)
            CancelHold();
        return completed;
    }

    private async void RunClose(WindowRef target)
    {
        if (busy || exiting)
            return;
        busy = true;
        pending = new();
        RetainOperation("Pending", "Waiting for closing confirmation…", "close", target, true);
        ActionResult result;
        try
        {
            result = await Controller.Platform.Close(target, pending.Token);
        }
        catch (OperationCanceledException)
        {
            result = new(ResultKind.Unknown, "Observation interrupted after sending. Check the window and any save dialogs.");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            result = new(ResultKind.Failed, "Could not request closing.");
        }
        finally
        {
            pending.Dispose();
            pending = null;
            busy = false;
        }

        if (exiting)
            return;
        RecordResult(result, "close", target);
        try
        {
            await Controller.Refresh();
            if (!exiting)
                RefreshRows();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RecordResult(ActionResult result, string action, WindowRef? target = null)
    {
        RetainOperation(result.ToString().Split(" · ")[0], result.Message, action, target, true, result.Summary);
        FeedbackText(result.ToString());
        if (result.Kind == ResultKind.Completed)
            RequestCue(FeedbackCue.Completion);
        else if (result.Kind is ResultKind.Failed or ResultKind.Unknown or ResultKind.Partial or ResultKind.Unavailable)
            RequestCue(FeedbackCue.Unavailable);
        if (!Controller.Platform.IsPreview)
            Controller.Diagnostics.Log(action, result.Kind);
        if (!IsVisible && result.Kind is ResultKind.Failed or ResultKind.Unknown or ResultKind.Partial or ResultKind.Unavailable or ResultKind.Cancelled)
            RequestResultNotification(result.ToString());
    }

    private void RequestResultNotification(string message)
    {
        if (exiting)
            return;
        if (ResultNotificationRequest is { } request)
        {
            request(message);
            return;
        }

        if (!diagnostic && !Controller.Platform.IsPreview)
            tray?.NotifyResult(message);
    }

    private void RunEffect(Func<Task<ActionResult>> effect, Func<bool>? valid = null) => RunEffect((_, _) => effect(), valid);
    private void RunEffect(Func<CancellationToken, Func<bool>, Task<ActionResult>> effect, Func<bool>? valid = null, WindowRef? focusTarget = null)
    {
        var action = navigation.Id;
        var target = view.Current.Target ?? Controller.Session.Anchor;
        QueueHandoff(() => OperationTask = BeginEffectCore(effect, action, target, focusTarget), valid);
    }

    internal Task OperationTask
    {
        get;
        private set;
    } = Task.CompletedTask;

    private async Task BeginEffectCore(Func<CancellationToken, Func<bool>, Task<ActionResult>> effect, string action, WindowRef? target, WindowRef? focusTarget)
    {
        if (busy || exiting)
            return;
        busy = true;
        input.Cancel();
        using var cancellation = new CancellationTokenSource();
        effectCancellation = cancellation;
        effectFocusTarget = focusTarget;
        effectFocusTransferred = false;
        effectPadConnections = 0;
        effectGuideConnections = 0;
        if (lastInputSample is { } baseline)
        {
            for (var i = 0; i < Math.Min(4, baseline.Pads.Count); i++)
                if (baseline.Pads[i].Connected)
                    effectPadConnections |= 1 << i;
            effectGuideConnections = baseline.GuideConnections;
        }

        bool Continuing()
        {
            if (exiting || closeRequested || cancellation.IsCancellationRequested)
                return false;
            var context = InputContext;
            var own = context.Confirmed && context.Visible && context.Active && context.OwnForeground && !context.OwnedDialog;
            var targetForeground = focusTarget is not null && FocusForeground() == focusTarget.Handle && EffectTargetValid(focusTarget);
            if (effectFocusTransferred && !targetForeground)
            {
                cancellation.Cancel();
                return false;
            }

            if (!own && !targetForeground)
            {
                cancellation.Cancel();
                return false;
            }

            if (!own && targetForeground && !effectFocusTransferred)
            {
                effectFocusTransferred = true;
                UpdatePolling();
            }

            guideBackend?.PollForOverlay(IsVisible, focusCycle, Environment.TickCount64, Controller.Settings.GuideOpenMaxHoldMs);
            if (!EffectInputsNeutral(EffectFramesFixture?.Invoke() ?? Enumerable.Range(0, 4).Select(i => Native.ReadPad((uint)i)).ToArray()))
            {
                cancellation.Cancel();
                return false;
            }

            return true;
        }

        RetainOperation("Pending", "Request sent; waiting for the result.", action, target, true);
        ActionResult result;
        try
        {
            result = await effect(cancellation.Token, Continuing);
        }
        catch (OperationCanceledException)
        {
            result = new(ResultKind.Unknown, "Observation interrupted; check the app before trying again.");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            result = new(ResultKind.Failed, "The request could not be completed.");
        }
        finally
        {
            effectCancellation = null;
            effectFocusTarget = null;
            effectFocusTransferred = false;
            busy = false;
        }

        if (exiting)
            return;
        RecordResult(result, action, target);
        if (!cancellation.IsCancellationRequested && result.Kind is (ResultKind.Requested or ResultKind.Completed or ResultKind.Simulated))
        {
            if (IsVisible)
                HideNow(false);
        }

        UpdatePolling();
        UpdatePersistentState();
    }

    private void Shortcut(string keys)
    {
        var target = keys == "Win+Tab" ? Controller.ReturnTarget : Controller.Session.Anchor;
        if (target is null)
        {
            FeedbackText("No valid window for this shortcut.");
            return;
        }

        var steam = keys is "Shift+Tab" or "Ctrl+Tab";
        if (steam && !Controller.SteamOverlayEligible(target))
        {
            FeedbackText("Steam Overlay unconfirmed in this window.");
            return;
        }

        if (steam)
            QueueSteam(target, () => Controller.Session.Anchor?.Same(target) == true && Controller.SteamOverlayEligible(target));
        else
            RunEffect((c, keep) => Controller.Platform.Shortcut(target, keys, c, keep), () => Controller.Platform.Valid(target), target);
    }

    private void RunInPlace(WindowRef target, Func<CancellationToken, Task<ActionResult>> effect, string action) => QueueHandoff(() => OperationTask = RunInPlaceCore(target, effect, action), () => Controller.Platform.Valid(target) && (view.Current.Target ?? Controller.Session.Anchor)?.Same(target) == true);
    private async Task RunInPlaceCore(WindowRef target, Func<CancellationToken, Task<ActionResult>> effect, string action)
    {
        if (busy || exiting)
            return;
        var generation = ++operationGeneration;
        busy = true;
        pending = new();
        RetainOperation("Pending", "Request sent; waiting for confirmation.", action, target, true);
        ActionResult result;
        try
        {
            result = await effect(pending.Token);
        }
        catch (OperationCanceledException)
        {
            result = new(ResultKind.Unknown, "Observation interrupted; the sent command cannot be undone. Check the app.");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            result = new(ResultKind.Failed, "Could not confirm the operation; there will be no automatic retry.");
        }
        finally
        {
            pending.Dispose();
            pending = null;
            busy = false;
        }

        if (exiting || generation != operationGeneration)
            return;
        RecordResult(result, action, target);
        UpdatePersistentState();
    }

    private void WireCloseConfirmation()
    {
        HoldCloseButton.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!NavigationAllowed || !Confirming)
                return;
            e.Handled = true;
            if (closeSource != -1)
            {
                CancelHold();
                FeedbackText("Input source changed; closing cancelled.");
                return;
            }

            pointerDown = true;
            pointerCaptured = HoldCloseButton.CaptureMouse();
            if (!pointerCaptured)
                CancelHold();
        };
        HoldCloseButton.PreviewMouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            pointerDown = false;
            if (Confirming && closeSource == -1)
                AdvancePointerHold(false, true, true, Environment.TickCount64);
            ReleasePointer();
        };
        HoldCloseButton.MouseLeave += (_, _) =>
        {
            if (pointerDown)
            {
                CancelHold();
                FeedbackText("Hold cancelled: the pointer left the button.");
            }
        };
        HoldCloseButton.LostMouseCapture += (_, _) =>
        {
            if (pointerDown)
            {
                CancelHold();
                FeedbackText("Hold cancelled: mouse capture lost.");
            }
        };
    }
}

using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;

namespace ApolloOverlay;

public partial class MainWindow
{
    private readonly IControllerDevices controllerDevices;
    private readonly IFeedbackService feedbackService;
    private long nextBatteryRead;
    private uint? activeSlot;
    private long activeEpoch;
    private int connectedControllers;
    private string controllerStatus = "Waiting for controller input";
    private InputCoordinator input;
    private readonly DispatcherTimer timer;
    private XInputGuideBackend? guideBackend;
    private GuideConnection? guideConnection;
    private bool guideToggleRequested;
    private void UpdatePolling(bool connected = true) => timer.Interval = TimeSpan.FromMilliseconds(steamOwner is not null || effectFocusTransferred ? 16 : GuidePolling.Interval(guideBackend?.Active == true, IsVisible, connected));
    private void ConfigureGuide()
    {
        if (!GuidePolling.Allowed(Controller.Settings.GuideXInputEnabled, diagnostic, Controller.Platform.IsPreview))
        {
            guideConnection?.Dispose();
            guideConnection = null;
            guideBackend = null;
            guideToggleRequested = false;
        }
        else if (guideBackend is null)
        {
            guideBackend = new();
            guideConnection = new(guideBackend);
            guideConnection.Start(RequestGuideToggle);
        }

        UpdatePolling();
    }

    internal void RequestGuideToggle() => guideToggleRequested = true;

    internal void PollGuideStatus(XInputGuideBackend backend)
    {
        backend.PollForOverlay(IsVisible, focusCycle, Environment.TickCount64, Controller.Settings.GuideOpenMaxHoldMs);
        if (!IsVisible || (subpage != "integrations" && page != 2))
            return;
        UpdateGuideStatus(backend.Status);
    }

    internal void UpdateGuideStatus(string status) => SetEntryDescription("guide", status);
    private PadFrame[]? reviewFrames;
    internal void ReviewTick(PadFrame[] frames)
    {
        reviewFrames = frames;
        try
        {
            Tick(null, EventArgs.Empty);
        }
        finally
        {
            reviewFrames = null;
        }
    }

    private void Tick(object? sender, EventArgs args)
    {
        if (exiting)
            return;
        if (guideBackend is not null)
            PollGuideStatus(guideBackend);
        var frames = reviewFrames ?? Enumerable.Range(0, 4).Select(i => Native.ReadPad((uint)i)).ToArray();
        var now = Environment.TickCount64;
        if (effectFocusTransferred
            && effectCancellation is not null
            && (effectFocusTarget is null
            || FocusForeground() != effectFocusTarget.Handle
            || !EffectTargetValid(effectFocusTarget)
            || !EffectInputsNeutral(frames)))
            effectCancellation.Cancel();
        if (fadingOut && !FadeInputsNeutral(frames))
            fadeInterrupted = true;
        if (fadingOut)
        {
            if (GuidePolling.Consume(ref guideToggleRequested, false))
            {
                SetInputMethod("controller");
                OpenPanel();
            }
            return;
        }

        lastInputSample = new(frames, now, GuideHeld: guideBackend?.Held == true, GuideValid: guideBackend is null || !guideBackend.Active || guideBackend.SnapshotValid, GuideConnections: guideBackend?.Connections ?? 0);
        UpdatePolling(frames.Any(f => f.Connected));
        var current = input.Update(frames, IsVisible, now);
        UpdateControllerTelemetry(frames, current.Slot, now);
        if (steamOwner is { } owner
            && ((owner.InputQualified
            && !Neutral(SteamSample(), Environment.TickCount64))
            || frames.Any(f => f.Buttons.HasFlag(PadButtons.B))
            || (PhysicalKeyReader(0x1B) & 0x8000) != 0
            || !SteamAlive(owner)))
            CancelSteamOwner("New input or context changed while focusing.");
        if (TransitionPending)
        {
            if (GuidePolling.Consume(ref guideToggleRequested, current.Toggle))
            {
                SetInputMethod("controller");
                Toggle(trustedDismiss: current.Toggle || guideBackend?.SnapshotValid == true);
                return;
            }

            if (current.Events.Pressed.HasFlag(PadButtons.B))
                Back(trustedDismiss: current.Slot is { } slot && frames[slot].Connected && !frames[slot].ReadFailed);
            else
                AdvanceHandoff(lastInputSample.Value, now);
            return;
        }

        if (GuidePolling.Consume(ref guideToggleRequested, current.Toggle))
        {
            SetInputMethod("controller");
            Toggle(trustedDismiss: current.Toggle || guideBackend?.SnapshotValid == true);
            return;
        }

        if (!IsVisible)
            return;
        RefreshOperationNotice(now);
        PollSessionFeatures(now);
        var events = current.Events;
        if (events.Pressed != 0 || events.Direction != 0)
        {
            SetInputMethod("controller");
        }

        if (!NavigationAllowed)
        {
            if (!ownedDialog && events.Pressed.HasFlag(PadButtons.B))
                Back();
            return;
        }

        if (!navigationKeysArmed && heldKeys.Count == 0 && PhysicalKeysNeutral())
            navigationKeysArmed = true;
        if (Confirming && closeSource == -1)
        {
            var point = Mouse.GetPosition(HoldCloseButton);
            var inside = point.X >= 0 && point.Y >= 0 && point.X < HoldCloseButton.ActualWidth && point.Y < HoldCloseButton.ActualHeight;
            var mixed = keyboardDown || heldKeys.Count > 0 || events.Pressed != 0 || events.Direction != 0;
            AdvancePointerHold(pointerDown, (!pointerDown || pointerCaptured && HoldCloseButton.IsMouseCaptured) && !mixed, inside, now);
            return;
        }

        if (Confirming
            && current.Slot is { } scrollSlot
            && scrollSlot >= 0
            && scrollSlot < frames.Length
            && !events.ADown
            && events.Pressed == 0
            && Math.Abs((int)frames[scrollSlot].RY) > Controller.Settings.Deadzone
            && now >= nextConfirmationScroll)
        {
            SetInputMethod("controller");
            ConfirmScroll.ScrollToVerticalOffset(ConfirmScroll.VerticalOffset + (frames[scrollSlot].RY < 0 ? 48 : -48));
            nextConfirmationScroll = now + 150;
        }

        if (Confirming && navigation.Id == "confirm-cancel" && events.Pressed.HasFlag(PadButtons.A))
        {
            Back();
            return;
        }

        if (Confirming && events.Direction != 0 && closeSource != -1)
        {
            Move((NavDirection)events.Direction);
            return;
        }

        if (Confirming && closeHold.Target is { } target)
        {
            var sourceValid = closeSource == 0 || current.Slot is { } slot && closeSource == slot + 1 && current.Connected;
            var down = closeSource == 0 ? keyboardDown : sourceValid && events.ADown;
            if ((closeSource != 0 && keyboardDown) || (closeSource == 0 && events.Pressed.HasFlag(PadButtons.A)))
                sourceValid = false;
            var valid = Controller.Platform.Valid(target) ? target : null;
            if (closeHold.Tick(down, events.Pressed.HasFlag(PadButtons.B), IsActive, sourceValid, valid, Environment.TickCount64))
            {
                CancelHold();
                RunClose(target);
            }
            else if (!Confirming)
                CancelHold();
            HoldProgress.Value = closeHold.Progress;
            ConfirmHint.Text = closeHold.Progress > 0 ? $"Keep holding… {closeHold.Progress:P0}" : "Release, then hold again for 1.5 s.";
            return;
        }

        if (busy)
        {
            if (events.Pressed.HasFlag(PadButtons.B))
                Back();
            return;
        }

        dispatchSource = current.Slot + 1;
        if (events.Direction != 0)
            Move((NavDirection)events.Direction);
        if (events.Pressed.HasFlag(PadButtons.B))
            Back();
        else if (events.Pressed.HasFlag(PadButtons.A) && events.Direction == 0)
            ActivateEntry();
        else if (events.Pressed.HasFlag(PadButtons.X) && events.Direction == 0)
            Details();
        else if (events.Pressed.HasFlag(PadButtons.LB))
            NavigatePrimaryTab(-1);
        else if (events.Pressed.HasFlag(PadButtons.RB))
            NavigatePrimaryTab(1);
        dispatchSource = null;
    }

    private int controllerMask;
    internal void UpdateControllerTelemetry(IReadOnlyList<PadFrame> frames, int? slot, long now)
    {
        controllerDevices.UpdateConnections(frames.Select(frame => frame.Connected && !frame.ReadFailed).ToArray());
        var mask = 0;
        for (var i = 0; i < frames.Count; i++)
            if (frames[i].Connected && !frames[i].ReadFailed)
                mask |= 1 << i;
        var connectionsChanged = mask != controllerMask;
        if (connectionsChanged)
        {
            controllerMask = mask;
            nextBatteryRead = 0;
            activeEpoch = 0;
            feedbackService.Suspend();
        }

        connectedControllers = frames.Count(frame => frame.Connected && !frame.ReadFailed);
        var failed = frames.Any(frame => frame.ReadFailed);
        var owner = slot is { } value && value >= 0 && value < frames.Count && frames[value].Connected && !frames[value].ReadFailed ? (uint?)value : null;
        var ownerChanged = owner != activeSlot;
        if (connectionsChanged)
            ResetControllerBadge();
        else if (ownerChanged)
        {
            badgeBattery = null;
            badgeRenderState = null;
            UpdateControllerBadge();
        }

        if (ownerChanged)
        {
            feedbackService.Suspend();
            activeSlot = owner;
            activeEpoch = 0;
            nextBatteryRead = 0;
        }

        if (mask == 0 && failed)
        {
            activeEpoch = 0;
            nextBatteryRead = 0;
            SetControllerStatus("Controller input unavailable");
            feedbackService.Suspend();
            return;
        }

        if (connectionsChanged || ownerChanged || activeSlot is null)
            SetControllerStatus(activeSlot is { } selected ? "Controller " + (selected + 1) + " · Battery unavailable" : connectedControllers switch
            {
                0 => "No controller connected",
                1 => failed ? "1 controller confirmed · other inputs unavailable" : "1 controller connected · waiting for input",
                _ => failed ? $"{connectedControllers} controllers confirmed · other inputs unavailable" : $"{connectedControllers} controllers connected · waiting for input"
            });
        var context = InputContext;
        var confirmed = context.Visible && context.Confirmed && context.Active && context.OwnForeground && !context.Exiting && !context.OwnedDialog;
        if (!confirmed)
        {
            if (activeSlot is { } hiddenSlot)
                SetControllerStatus("Controller " + (hiddenSlot + 1) + " · Battery unavailable");
            nextBatteryRead = 0;
            feedbackService.Suspend();
            return;
        }

        PollControllerIdentity(now);
        if (activeSlot is { } active && now >= nextBatteryRead)
        {
            nextBatteryRead = now + 5000;
            var status = controllerDevices.Read(active);
            badgeBattery = status;
            activeEpoch = status.ConnectionEpoch;
            SetControllerStatus("Controller " + (active + 1) + " · " + BatteryText(status));
        }

        feedbackService.SetContext(new(context.Visible, confirmed, activeSlot, activeEpoch));
    }

    private void SetControllerStatus(string text)
    {
        controllerStatus = text;
        ControllerStatusLabel.Text = text.Replace(" · waiting for input", "");
        ControllerStatusLabel.ToolTip = text;
        UpdateControllerBadge();
        if (controllerStatusParagraph is not null)
        {
            controllerStatusParagraph.Text = text;
            AutomationProperties.SetName(controllerStatusParagraph, text);
        }
    }

    private static string BatteryText(ControllerDeviceSnapshot status) => !status.Connected ? "Disconnected" : status.BatteryType == ControllerBatteryType.Wired ? "Wired" : status.BatteryType is ControllerBatteryType.Alkaline or ControllerBatteryType.NickelMetalHydride ? status.BatteryLevel switch
    {
        ControllerBatteryLevel.Empty => "Battery empty",
        ControllerBatteryLevel.Low => "Battery low",
        ControllerBatteryLevel.Medium => "Battery medium",
        ControllerBatteryLevel.Full => "Battery full",
        _ => "Battery unavailable"
    } : "Battery unavailable";
    private FeedbackDispatch RequestCue(FeedbackCue cue)
    {
        if (exiting)
            return new(false, false, "Panel closed.");
        var context = InputContext;
        feedbackService.SetContext(new(context.Visible, context.Confirmed && context.Active && context.OwnForeground && !context.OwnedDialog && !context.Exiting, activeSlot, activeEpoch));
        var sourceKind = dispatchSource switch
        {
            > 0 => FeedbackSourceKind.Controller,
            -1 => FeedbackSourceKind.Pointer,
            0 => FeedbackSourceKind.Keyboard,
            _ => inputMethod == "controller" ? FeedbackSourceKind.Controller : inputMethod == "mouse" ? FeedbackSourceKind.Pointer : FeedbackSourceKind.Keyboard
        };
        return feedbackService.Request(cue, new(sourceKind, sourceKind == FeedbackSourceKind.Controller ? activeSlot : null, activeEpoch));
    }
}

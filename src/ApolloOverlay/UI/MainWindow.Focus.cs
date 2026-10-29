using System.Windows.Interop;

namespace ApolloOverlay;

public partial class MainWindow
{
    internal Func<OverlayInputContext>? InputContextReader
    {
        private get;
        set;
    }
    private OverlayInputContext InputContext => InputContextReader?.Invoke() ?? new(focusConfirmed, IsVisible, IsActive, OwnForeground, ownedDialog, exiting);
    internal Func<nint> FocusForeground
    {
        private get;
        set;
    } = Native.GetForegroundWindow;
    internal Func<nint, bool> FocusRequest
    {
        private get;
        set;
    } = Native.SetForegroundWindow;
    internal Func<nint, nint, CancellationToken, Task<bool>>? FocusFallbackRequest
    {
        private get;
        set;
    }

    private CancellationTokenSource? activationCancellation;
    internal Func<Task>? RefreshFocusFixture
    {
        private get;
        set;
    }
    internal Task OpeningTask
    {
        get;
        private set;
    } = Task.CompletedTask;

    private long focusCycle;
    private bool focusConfirmed, navigationKeysArmed = true;
    internal void Toggle() => Toggle(false);
    private void Toggle(bool trustedDismiss)
    {
        if (updateBusy)
        {
            CancelUpdateInteraction();
            if (!IsVisible && !updateCommitting)
                OpeningTask = OpenPanelCore();
            return;
        }

        CancelSteamOwner("Reopen requested; no new shortcut sent.");
        if (busy && steamOwner is null)
        {
            effectCancellation?.Cancel();
            return;
        }

        if (fadingOut)
            OpenPanel();
        else if (IsVisible)
            HidePanel(true, trustedDismiss);
        else if (!TransitionPending)
            OpenPanel();
    }

    internal void OpenPanel()
    {
        if (!diagnostic && !Controller.Platform.IsPreview)
            ShowUpdateRecoveryNotice();
        CancelSteamOwner("Panel reopened; previous request cancelled.");
        if (exiting || closeRequested)
            return;
        if (updateBusy && !updateCommitting)
        {
            OpeningTask = OpenPanelCore();
            return;
        }

        if (busy && steamOwner is null)
        {
            if (effectCancellation is not null)
            {
                effectCancellation.Cancel();
                if (!IsVisible || fadingOut)
                    OpeningTask = OpenPanelCore();
                else
                    FeedbackText("Request cancelled; waiting for the result.");
            }

            return;
        }

        OpeningTask = OpenPanelCore();
    }

    private async Task OpenPanelCore()
    {
        var foreground = FocusForeground();
        var wasVisible = IsVisible;
        fadeGeneration++;
        fadingOut = false;
        CancelOpenAnimation();
        RevokeFocus("request");
        var cycle = focusCycle;
        CancelHandoff();
        CancelHold();
        input.Cancel();
        if (!wasVisible)
            Controller.CaptureReturn();
        opening = true;
        try
        {
            if (!wasVisible)
            {
                Place();
                PrepareOpenAnimation();
                Show();
            }
        }
        finally
        {
            opening = false;
        }

        UpdatePolling();
        audio.SetVisible(!Controller.Platform.IsPreview);
        RefreshRows();
        if (!wasVisible)
            AnimateOpen();
        if (!await AcquireFocus(cycle, foreground))
            return;
        ResumeSessionFeatures();
        try
        {
            await (RefreshFocusFixture?.Invoke() ?? Controller.Refresh());
            if (!CurrentCycle(cycle))
                return;
            TraceFocus("refresh", ResultKind.Completed);
            if (!NavigationAllowed)
                return;
            Place();
            RefreshRows();
            SetSelection();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (CurrentCycle(cycle))
                FeedbackText("Could not refresh windows. Try opening again from the tray.");
        }
    }

    private bool CurrentCycle(long cycle) => cycle == focusCycle && !exiting && IsVisible;
    private bool OwnForeground => FocusForeground() == new WindowInteropHelper(this).Handle;

    private void TraceFocus(string checkpoint, ResultKind kind, string detail = "")
    {
        if (Controller.Platform.IsPreview)
            return;
        var actual = FocusForeground();
        var window = new WindowInteropHelper(this).Handle;
        Controller.Diagnostics.Log($"focus/{focusCycle}/{checkpoint}/foreground={actual == window}/active={IsActive}/actual=0x{actual:X}/window=0x{window:X}{detail}", kind);
    }

    private void RevokeFocus(string reason, bool supersede = true)
    {
        ResetControllerBadge();
        foreach (var entry in entries)
            InteractionMotion.Stop(entry.Button);
        activationCancellation?.Cancel();
        SuspendSessionFeatures();
        CancelPlayniteScan();
        feedbackService.Suspend();
        applicationAudio.Suspend();
        audioGeneration++;
        audioTarget = null;
        nextBatteryRead = 0;
        CancelOpenAnimation();
        if (supersede)
            focusCycle++;
        focusConfirmed = false;
        navigationKeysArmed = false;
        input.Cancel();
        CancelHold();
        TraceFocus(reason, ResultKind.Cancelled);
    }

    private bool NavigationAllowed
    {
        get
        {
            var context = InputContext;
            if (!context.Confirmed || !context.Visible || context.OwnedDialog || context.Exiting)
                return false;
            if (context.Active && context.OwnForeground)
                return true;
            RevokeFocus("lost");
            FeedbackFocusUnavailable("Window focus lost.");
            return false;
        }
    }

    private async Task<bool> AcquireFocus(long cycle, nint foreground)
    {
        if (!CurrentCycle(cycle) || ownedDialog)
            return false;
        using var activation = new CancellationTokenSource();
        activationCancellation = activation;
        try
        {
            bool activated, requested;
            opening = true;
            try
            {
                activated = Activate();
                requested = FocusRequest(new WindowInteropHelper(this).Handle);
            }
            finally
            {
                opening = false;
            }

            // An accepted request can still be awaiting delivery to WPF. Observe it
            // before deciding whether this explicit opening needs the broker.
            if (requested && !OwnForeground)
                await Task.Delay(50, activation.Token);
            if (!CurrentCycle(cycle) || ownedDialog || activation.IsCancellationRequested)
                return false;
            if (!OwnForeground && FocusForeground() == foreground && FocusFallbackRequest is { } fallback)
            {
                requested = await fallback(new WindowInteropHelper(this).Handle, foreground, activation.Token);
                if (!CurrentCycle(cycle) || ownedDialog || activation.IsCancellationRequested)
                    return false;
            }

            TraceFocus("request-native", ResultKind.Requested, $"/activate={activated}/request={requested}");
            for (var attempt = 0; attempt <= 6; attempt++)
            {
                if (!CurrentCycle(cycle) || ownedDialog || activation.IsCancellationRequested)
                    return false;
                if (IsActive && OwnForeground)
                {
                    focusConfirmed = true;
                    navigationKeysArmed = heldKeys.Count == 0 && PhysicalKeysNeutral();
                    input.Cancel();
                    UpdateHints();
                    TrackApplicationAudio();
                    TraceFocus("confirmed", ResultKind.Completed);
                    FeedbackText("Panel ready. Navigate with your preferred input.");
                    return true;
                }

                // A native return value is not the final foreground observation: WPF or
                // Windows can complete the explicit activation on a later dispatcher turn.
                if (attempt == 6)
                {
                    TraceFocus("unconfirmed", requested ? ResultKind.Unknown : ResultKind.Failed);
                    FeedbackFocusUnavailable("Windows did not confirm focus.");
                    return false;
                }

                await Task.Delay(50);
            }

            return false;
        }
        catch (OperationCanceledException) when (activation.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(activationCancellation, activation))
                activationCancellation = null;
        }
    }

    private bool CanRecoverLocalFocus => !focusConfirmed && IsVisible && IsActive && OwnForeground && !ownedDialog && !Confirming && !TransitionPending && !busy && !opening && !exiting && !closeRequested;
}

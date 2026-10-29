using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ApolloOverlay;

public partial class MainWindow
{
    private HwndSource? source;
    private TrayHost? tray;
    private bool ownedDialog;
    private bool placementQueued;
    private nint WindowMessage(nint hwnd, int message, nint w, nint l, ref bool handled)
    {
        if (message == SingleInstanceActivation.OpenMessage)
        {
            OpenPanel();
            handled = true;
        }

        if ((message is 0x7E or 0x2E0 || message == 0x1A && w == 47) && IsVisible && !placing && !placementQueued)
        {
            placementQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                placementQueued = false;
                if (!exiting && IsVisible)
                    Place();
            });
        }
        return 0;
    }

    internal void ExitFromTray()
    {
        if (updateCommitting && !updateCloseAuthorized)
        {
            FeedbackText("The version change is being prepared. Wait for the restart.");
            return;
        }

        if (!exiting)
            Close();
    }

    private bool closeRequested, closeFinalizing;
    private Task? closeDrain;
    internal Task ClosingTask => closeDrain ?? Task.CompletedTask;

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        if (updateCommitting && !updateCloseAuthorized)
        {
            args.Cancel = true;
            FeedbackText("Wait while the version change is prepared.");
            return;
        }

        if (exiting)
            return;
        if (closeFinalizing)
        {
            Cleanup();
            return;
        }

        if (closeRequested)
        {
            args.Cancel = true;
            return;
        }

        if (steamOwner is not { } owner)
        {
            Cleanup();
            return;
        }

        args.Cancel = true;
        closeRequested = true;
        CancelSteamOwner("Shutdown requested; releasing held keys.");
        closeDrain = DrainAndClose(owner, OperationTask);
    }

    private async Task DrainAndClose(SteamOwner owner, Task operation)
    {
        await owner.Drained.Task;
        try
        {
            await operation;
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or OperationCanceledException)
        {
        }

        await Dispatcher.InvokeAsync(() =>
        {
            closeFinalizing = true;
            Close();
        });
    }

    internal void ShowDiagnostic()
    {
        opening = true;
        Show();
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        opening = false;
        focusCycle++;
        focusConfirmed = IsActive && OwnForeground;
        navigationKeysArmed = true;
        SetSelection(false);
    }

    internal void ValidationHost(bool visible)
    {
        if (visible)
        {
            ShowDiagnostic();
            audio.SetVisible(true);
        }
        else
        {
            Hide();
            audio.SetVisible(false);
        }

        UpdatePolling();
        timer.Start();
    }

    private void Cleanup()
    {
        if (exiting)
            return;
        DisposeSessionFeatures();
        DisposeUpdates();
        effectCancellation?.Cancel();
        fadeGeneration++;
        fadingOut = false;
        CancelSteamOwner("Overlay closed.");
        automationHost?.Dispose();
        exiting = true;
        operationGeneration++;
        RevokeFocus("cleanup");
        CancelHandoff();
        heldKeys.Clear();
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        timer.Stop();
        timer.Tick -= Tick;
        guideConnection?.Dispose();
        guideConnection = null;
        guideBackend = null;
        guideToggleRequested = false;
        pending?.Cancel();
        ResultNotificationRequest = null;
        applicationAudio.Changed -= ApplicationAudioChanged;
        feedbackService.Changed -= FeedbackStatusChanged;
        applicationAudio.Dispose();
        feedbackService.Dispose();
        controllerDevices.Dispose();
        audio.Dispose();
        source?.RemoveHook(WindowMessage);
        tray?.Dispose();
        DisposeIconUpdates();
        Controller.Dispose();
    }

    private void WireWindowLifecycle()
    {
        Deactivated += (_, _) =>
        {
            if (fadingOut)
            {
                fadeInterrupted = true;
                return;
            }

            if (steamHiding || opening || exiting || (committing && !busy))
                return;
            var expected = effectCancellation is not null && effectFocusTarget is { } target && FocusForeground() == target.Handle && EffectTargetValid(target);
            if (expected)
                effectFocusTransferred = true;
            else
                effectCancellation?.Cancel();
            RevokeFocus("deactivated", !ownedDialog);
            CancelHandoff();
            heldKeys.Clear();
            CancelHold();
            input.Cancel();
            if (IsVisible && !ownedDialog)
            {
                HideNow(false, preserveEffect: expected);
            }
        };
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            source.AddHook(WindowMessage);
            if (!diagnostic)
            {
                SingleInstanceActivation.AllowRequests(source.Handle);
                tray = new(Dispatcher, () =>
                {
                    SetInputMethod("mouse");
                    OpenPanel();
                }, ExitFromTray);
            }
        };
        Closing += OnClosing;
    }
}

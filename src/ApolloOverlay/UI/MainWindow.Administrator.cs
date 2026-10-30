using ApolloOverlay.Automation;

namespace ApolloOverlay;

public partial class MainWindow
{
    internal Task AdministratorRestartTask { get; private set; } = Task.CompletedTask;

    private void AddAdministratorMode(StartupStatus startup)
    {
        if (diagnostic || Controller.Platform.IsPreview)
        {
            AddRow("administrator-restart", "Restart as administrator", "Available outside preview · Windows approval is required", () => { }, false);
            return;
        }

        if (!AutomationPipeSecurity.Elevated())
        {
            AddRow("administrator-restart", "Restart as administrator",
                Controller.SettingsDirty ? "Save or discard settings before restarting"
                    : "For elevated games · Windows approval required · this session only",
                () => BeginAdministratorRestart(fromTray: false), !Controller.SettingsDirty);
            return;
        }

        AddRow("administrator-startup", "Administrator startup · " + (startup.Elevated ? "On" : "Off"),
            startup.Available
                ? "Current session: administrator · " + (startup.Enabled ? "applies at the next sign-in" : "Windows startup remains off")
                : "Current session: administrator · install with Setup to remember this mode",
            () => StartSessionAction("administrator-startup", token => Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return StartupRegistration.SetElevated(!startup.Elevated);
            }, token), RefreshRows), startup.Available);
    }

    private void RestartAdministratorFromTray() => BeginAdministratorRestart(fromTray: true);

    private void BeginAdministratorRestart(bool fromTray)
    {
        if (diagnostic || Controller.Platform.IsPreview || AutomationPipeSecurity.Elevated() || exiting || closeRequested)
            return;
        if (busy || updateBusy || updateCommitting || ownedDialog || TransitionPending || Confirming)
        {
            ReportAdministratorRestartBlocked("Finish or cancel the current operation before restarting as administrator.");
            return;
        }
        if (Controller.SettingsDirty || view.Current.Route is OverlayRoute.ProfileEdit or OverlayRoute.Keyboard)
        {
            ReportAdministratorRestartBlocked("Save or discard your settings, profile or text draft before restarting as administrator.");
            return;
        }

        if (fromTray)
            AdministratorRestartTask = RestartAdministratorCore();
        else if (NavigationAllowed)
            QueueHandoff(() => AdministratorRestartTask = RestartAdministratorCore());
    }

    private void ReportAdministratorRestartBlocked(string message)
    {
        FeedbackText(message);
        RequestCue(FeedbackCue.Unavailable);
        if (!NavigationAllowed)
            RequestResultNotification(message);
    }

    private async Task RestartAdministratorCore()
    {
        if (exiting || closeRequested || busy || ownedDialog || updateBusy || updateCommitting)
            return;
        using var command = BeginCommand("administrator-restart");
        var visible = IsVisible;
        var generation = ++operationGeneration;
        using var request = new CancellationTokenSource();
        pending = request;
        busy = true;
        ownedDialog = true;
        RevokeFocus("administrator-restart");
        var cycle = focusCycle;
        RetainOperation("Pending", "Waiting for Windows administrator approval…", "administrator-restart");
        ActionResult result;
        try
        {
            result = await AdministratorRestart.RequestAsync(request.Token);
            command?.Complete(result.Kind, "administrator-restart-result");
        }
        finally
        {
            if (ReferenceEquals(pending, request))
                pending = null;
            ownedDialog = false;
            busy = false;
        }

        if (exiting || generation != operationGeneration)
            return;
        if (result.Kind == ResultKind.Completed)
        {
            // The verified child waits for this process to exit before acquiring the singleton.
            Close();
            return;
        }

        RecordResult(result, "administrator-restart");
        if (visible && CurrentCycle(cycle))
        {
            var focus = AcquireFocus(cycle, FocusForeground());
            OpeningTask = focus;
            if (await focus && CurrentCycle(cycle))
            {
                RefreshRows();
                SetSelection();
            }
        }
    }
}

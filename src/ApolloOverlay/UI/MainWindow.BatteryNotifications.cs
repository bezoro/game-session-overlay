namespace ApolloOverlay;

public partial class MainWindow
{
    private ControllerBatteryMonitor? batteryMonitor;
    internal Func<ControllerBatteryAlert, bool>? BatteryNotificationRequest
    {
        private get; set;
    }

    internal void StartBatteryNotifications()
    {
        if (exiting || diagnostic || Controller.Platform.IsPreview || batteryMonitor is not null)
            return;
        batteryMonitor = new(new ControllerDevices(), new WindowsControllerIdentitySource(), alert =>
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(() =>
                {
                    if (!exiting && batteryMonitor is not null)
                        DeliverBatteryNotification(alert);
                });
        }, (reason, error) => Controller.Diagnostics.Commands.Record("controller-battery-monitor",
            error is null ? ResultKind.Completed : ResultKind.Failed, "system", reason, error));
        Controller.Diagnostics.Commands.Record("controller-battery-monitor", ResultKind.Completed, "system", "started/interval-seconds=30");
    }

    internal void DeliverBatteryNotification(ControllerBatteryAlert alert)
    {
        if (exiting || (diagnostic || Controller.Platform.IsPreview) && BatteryNotificationRequest is null)
            return;
        using var command = BeginCommand("controller-battery-notification", origin: "system");
        try
        {
            var requested = BatteryNotificationRequest?.Invoke(alert) ?? tray?.NotifyBattery(alert) ?? false;
            command?.Complete(requested ? ResultKind.Requested : ResultKind.Unavailable,
                $"slot={alert.Slot}/level={alert.Level}/percent={alert.BatteryPercent}/shell-request={requested}");
        }
        catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
        {
            command?.Fail(error, "shell-notification-failed");
        }
    }

    private void DisposeBatteryNotifications()
    {
        var monitor = batteryMonitor;
        batteryMonitor = null;
        BatteryNotificationRequest = null;
        monitor?.Dispose();
    }
}

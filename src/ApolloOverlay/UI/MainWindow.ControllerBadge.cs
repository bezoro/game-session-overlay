using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace ApolloOverlay;

public partial class MainWindow
{
    private IControllerIdentitySource controllerIdentitySource = null!;
    private IReadOnlyList<ControllerIdentity> controllerIdentities = [];
    private ControllerDeviceSnapshot? badgeBattery;
    private long badgeEpoch, nextIdentityRead;
    private bool controllerPreviouslyObserved, controllerIdentityConfirmed;
    private (int Connected, int Listed, ControllerIdentity? Identity, ControllerDeviceSnapshot? Battery, string Status, bool Recovery)? badgeRenderState;
    private Task<IReadOnlyList<ControllerIdentity>>? identityRead;
    internal Task ControllerBadgeTask
    {
        get;
        private set;
    } = Task.CompletedTask;

    private void ResetControllerBadge()
    {
        badgeEpoch++;
        nextIdentityRead = 0;
        controllerIdentities = [];
        controllerIdentityConfirmed = false;
        badgeBattery = null;
        badgeRenderState = null;
        UpdateControllerBadge();
    }

    private void PollControllerIdentity(long now)
    {
        var context = InputContext;
        if (now < nextIdentityRead || identityRead is { IsCompleted: false } || !context.Visible || !context.Confirmed || !context.Active || !context.OwnForeground || exiting)
            return;
        nextIdentityRead = now + 5000;
        var epoch = badgeEpoch;
        var work = Task.Run(controllerIdentitySource.Read);
        identityRead = work;
        ControllerBadgeTask = ObserveControllerIdentity(work, epoch);
    }

    private async Task ObserveControllerIdentity(Task<IReadOnlyList<ControllerIdentity>> work, long epoch)
    {
        try
        {
            var value = await work.WaitAsync(TimeSpan.FromSeconds(2));
            var context = InputContext;
            if (exiting || epoch != badgeEpoch || !context.Visible || !context.Confirmed || !context.Active || !context.OwnForeground)
                return;
            controllerIdentities = value.Take(8).ToArray();
            controllerIdentityConfirmed = true;
            UpdateControllerBadge();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or TimeoutException or ArgumentException)
        {
        }
        finally
        {
            if (!work.IsCompleted)
                _ = work.ContinueWith(task =>
                {
                    _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void UpdateControllerBadge()
    {
        if (ControllerInfoBar is null)
            return;
        controllerPreviouslyObserved |= connectedControllers > 0 || controllerIdentities.Count > 0;
        // A slot disappearing does not prove that Windows has no controller. Wait
        // for this epoch's actual identity read before offering disconnect recovery.
        var recovery = controllerPreviouslyObserved && controllerIdentityConfirmed
            && connectedControllers == 0 && controllerIdentities.Count == 0;
        var known = connectedControllers <= 1 && controllerIdentities.Count == 1 ? controllerIdentities[0] : null;
        var state = (connectedControllers, controllerIdentities.Count, known, badgeBattery, controllerStatus, recovery);
        if (badgeRenderState is { } previous && previous == state)
            return;
        badgeRenderState = state;
        var model = ControllerBadgeModel.Create(connectedControllers, controllerIdentities, badgeBattery, controllerStatus, recovery);
        ControllerStatusLabel.Visibility = Visibility.Collapsed;
        ControllerInfoBar.Visibility = model.Visible ? Visibility.Visible : Visibility.Collapsed;
        ControllerModelLabel.Text = model.Name;
        ControllerModelIcon.Data = (Geometry)FindResource("Icon." + model.IconKey);
        ControllerBatteryLabel.Text = model.BatteryText;
        ControllerBatteryLabel.Visibility = model.Disconnected ? Visibility.Collapsed : Visibility.Visible;
        ControllerBatteryLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, model.LowBattery ? ThemeSupport.Critical : ThemeSupport.SecondaryText);
        ControllerBatteryMeter.Visibility = model.ChargeKnown ? Visibility.Visible : Visibility.Collapsed;
        BatteryFill.Width = 14 * model.BatteryFraction;
        BatteryFill.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, model.LowBattery ? ThemeSupport.Critical : ThemeSupport.SecondaryText);
        var powerIcon = model.Power switch
        {
            ControllerPowerState.Charging => "charging",
            ControllerPowerState.Wired => "usb",
            ControllerPowerState.Unknown => "info",
            _ => null
        };
        ControllerPowerGlyph.Visibility = powerIcon is null || model.Disconnected ? Visibility.Collapsed : Visibility.Visible;
        ControllerPowerGlyph.Data = powerIcon is null ? null : (Geometry)FindResource("Icon." + powerIcon);
        ControllerInfoBar.ToolTip = model.Description;
        AutomationProperties.SetName(ControllerInfoBar, model.Description);
        if (model.Disconnected && inputMethod == "controller")
            SetInputMethod("keyboard");
    }
}

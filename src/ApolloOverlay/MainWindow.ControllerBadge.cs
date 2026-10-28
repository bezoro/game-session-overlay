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
    private (int Connected, int Listed, ControllerIdentity? Identity, ControllerDeviceSnapshot? Battery, string Status)? badgeRenderState;
    private Task<IReadOnlyList<ControllerIdentity>>? identityRead;
    internal Task ControllerBadgeTask { get; private set; } = Task.CompletedTask;

    private void ResetControllerBadge()
    {
        badgeEpoch++;
        nextIdentityRead = 0;
        controllerIdentities = [];
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
        var known = connectedControllers <= 1 && controllerIdentities.Count == 1 ? controllerIdentities[0] : null;
        var state = (connectedControllers, controllerIdentities.Count, known, badgeBattery, controllerStatus);
        if (badgeRenderState is { } previous && previous == state)
            return;
        badgeRenderState = state;
        ControllerStatusLabel.Visibility = Visibility.Collapsed;
        ControllerInfoBar.Visibility = connectedControllers > 0 || controllerIdentities.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ControllerModelLabel.Text = known?.Name ?? (Math.Max(connectedControllers, controllerIdentities.Count) > 1 ? $"{Math.Max(connectedControllers, controllerIdentities.Count)} controles" : "Controle XInput");
        ControllerModelIcon.Data = Geometry.Parse(known?.Family switch
        {
            ControllerFamily.PlayStation4 or ControllerFamily.PlayStation5 or ControllerFamily.PlayStation => "M5,3 C2,5 1,12 2,16 Q3,19 6,16 L10,12 H20 L24,16 Q27,19 28,16 C29,12 28,5 25,3 Z M8,6 V10 M6,8 H10 M22,6 V6.1 M24,8 V8.1 M22,10 V10.1 M20,8 V8.1 M11,11 A2,2 0 1 0 11.1,11 M18,11 A2,2 0 1 0 18.1,11",
            ControllerFamily.Xbox => "M6,3 Q3,3 2,9 L1,15 Q1,19 5,16 L10,12 H20 L25,16 Q29,19 29,15 L28,9 Q27,3 24,3 Z M8,5 A2,2 0 1 0 8.1,5 M14,10 A2,2 0 1 0 14.1,10 M9,10 V14 M7,12 H11 M23,6 V6.1 M25,8 V8.1 M23,10 V10.1 M21,8 V8.1",
            _ => "M6,3 H24 Q27,3 29,15 Q29,19 25,17 L20,12 H10 L5,17 Q1,19 1,15 Q3,3 6,3 Z M8,6 V11 M5.5,8.5 H10.5 M22,6 V6.1 M25,9 V9.1 M12,6 H18"
        });
        var percent = known?.BatteryPercent;
        var category = badgeBattery?.Connected == true ? badgeBattery.BatteryLevel : null;
        var wired = percent is null && (known is { Wireless: false } || badgeBattery?.BatteryType == ControllerBatteryType.Wired);
        ControllerBatteryLabel.Text = percent is { } value ? $"{value}%" : wired ? "USB" : category switch
        {
            ControllerBatteryLevel.Empty => "Vazia",
            ControllerBatteryLevel.Low => "Baixa",
            ControllerBatteryLevel.Medium => "Média",
            ControllerBatteryLevel.Full => "Cheia",
            _ => "—"
        };
        BatteryFill.Width = percent is { } amount ? 14d * amount / 100 : category is { } level ? 14d * (int)level / 3 : 0;
        BatteryFill.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, percent is <= 20 || category is ControllerBatteryLevel.Empty or ControllerBatteryLevel.Low ? ThemeSupport.Critical : ThemeSupport.SecondaryText);
        ControllerPowerGlyph.Visibility = known?.Charging == true || wired ? Visibility.Visible : Visibility.Collapsed;
        ControllerPowerGlyph.Data = known?.Charging == true ? Geometry.Parse("M6,0 L1,7 H4 L3,12 L9,4 H6 Z") : wired ? Geometry.Parse("M4,12 V1 M2,3 L4,1 L6,3 M4,9 L1,6 V4 M4,8 L8,5 V3") : null;
        if (known?.Charging == true)
            ControllerPowerGlyph.SetResourceReference(System.Windows.Shapes.Path.FillProperty, ThemeSupport.SecondaryText);
        else
            ControllerPowerGlyph.Fill = Brushes.Transparent;
        var source = known is null ? "Modelo não associado ao controle ativo." : "Identificação informada pelo Windows; em streaming pode corresponder ao controle virtual.";
        var battery = percent is { } charge ? $"Bateria {charge} por cento" + (known?.Charging == true ? ", carregando" : "") : wired ? "Alimentação por USB ou dispositivo virtual; carga da bateria não informada" : category is not null ? "Bateria " + ControllerBatteryLabel.Text.ToLowerInvariant() : "Bateria não informada pelo dispositivo";
        var description = ControllerModelLabel.Text + " · " + battery + ". " + source + " " + (connectedControllers > 0 ? controllerStatus : "Navegação por XInput deste dispositivo não confirmada.");
        ControllerInfoBar.ToolTip = description;
        AutomationProperties.SetName(ControllerInfoBar, description);
    }
}

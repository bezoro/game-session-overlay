using Windows.Gaming.Input;

namespace ApolloOverlay;

public enum ControllerFamily
{
    Unknown,
    Xbox,
    PlayStation4,
    PlayStation5,
    PlayStation,
    Nintendo
}

public sealed record ControllerIdentity(string Id, string Name, ControllerFamily Family, bool Wireless, int? BatteryPercent, bool Charging);
public interface IControllerIdentitySource
{
    IReadOnlyList<ControllerIdentity> Read();
}

/// <summary>Read-only Windows metadata, independent from the XInput input routing.</summary>
public sealed class WindowsControllerIdentitySource : IControllerIdentitySource
{
    public IReadOnlyList<ControllerIdentity> Read()
    {
        var result = new List<ControllerIdentity>();
        foreach (var device in RawGameController.RawGameControllers.Take(12))
        {
            try
            {
                if (device.ButtonCount < 8 || device.AxisCount < 2)
                    continue;
                var (family, name) = Describe(device.HardwareVendorId, device.HardwareProductId, device.DisplayName);
                int? percent = null;
                var charging = false;
                try
                {
                    var report = device.TryGetBatteryReport();
                    if (report is not null)
                    {
                        if (report.Status != Windows.System.Power.BatteryStatus.NotPresent)
                            percent = Percent(report.RemainingCapacityInMilliwattHours, report.FullChargeCapacityInMilliwattHours);
                        charging = report.Status == Windows.System.Power.BatteryStatus.Charging;
                    }
                }
                catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException)
                {
                }

                var id = device.NonRoamableId;
                if (string.IsNullOrEmpty(id))
                    continue;
                result.Add(new(id, name, family, device.IsWireless, percent, charging));
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
            {
            }
        }

        return Array.AsReadOnly(result.DistinctBy(item => item.Id).Take(8).ToArray());
    }

    internal static int? Percent(int? remaining, int? full) => remaining is >= 0 && full is > 0 && (long)remaining.Value <= full.Value ? (int)Math.Round(100d * remaining.Value / full.Value) : null;
    internal static (ControllerFamily Family, string Name) Describe(ushort vendor, ushort product, string? reported)
    {
        if (vendor == 0x054C)
            return product switch
            {
                0x05C4 or 0x09CC or 0x0BA0 => (ControllerFamily.PlayStation4, "PlayStation 4"),
                0x0CE6 => (ControllerFamily.PlayStation5, "PlayStation 5"),
                0x0DF2 => (ControllerFamily.PlayStation5, "DualSense Edge"),
                _ => (ControllerFamily.PlayStation, "PlayStation")
            };
        if (vendor == 0x045E)
            return (ControllerFamily.Xbox, product switch
            {
                0x028E or 0x028F or 0x02A1 => "Xbox 360",
                0x02D1 or 0x02DD or 0x02E0 or 0x02EA or 0x02FD or 0x02FF or 0x0B20 => "Xbox One",
                0x02E3 => "Xbox Elite",
                0x0B00 or 0x0B05 or 0x0B21 => "Xbox Elite 2",
                0x0B12 or 0x0B13 or 0x0B22 => "Xbox Series",
                _ => "Xbox"
            });
        if (vendor == 0x057E && product == 0x2009)
            return (ControllerFamily.Nintendo, "Switch Pro");
        var name = new string((reported ?? "").Where(character => !char.IsControl(character)).Take(48).ToArray()).Trim();
        return (ControllerFamily.Unknown, name.Length > 0 ? name : "Controle");
    }
}

public sealed class PreviewControllerIdentitySource : IControllerIdentitySource
{
    public IReadOnlyList<ControllerIdentity> Items { get; set; } = [];

    public IReadOnlyList<ControllerIdentity> Read() => Items;
}

namespace ApolloOverlay;

internal enum ControllerBatteryAlertLevel
{
    None,
    Low,
    Critical
}

internal sealed record ControllerBatteryAlert(uint Slot, string ControllerName, int? BatteryPercent, ControllerBatteryAlertLevel Level)
{
    internal string Title => Level == ControllerBatteryAlertLevel.Critical ? "Controller battery critical" : "Controller battery low";
    internal string Message => (BatteryPercent is { } percent ? $"{ControllerName} has {percent}% battery remaining."
        : $"{ControllerName} reports {(Level == ControllerBatteryAlertLevel.Critical ? "an empty" : "a low")} battery.")
        + " Charge your controller or replace its batteries.";
}

/// <summary>One low and one critical alert per discharge, with recovery hysteresis and reconnect suppression.</summary>
internal sealed class ControllerBatteryAlerts
{
    private sealed class State
    {
        internal string? Identity;
        internal ControllerBatteryAlertLevel Notified, LastLevel;
        internal long? LastNotification;
    }

    private readonly State[] states = [new(), new(), new(), new()];
    private static readonly long RecoveryCooldown = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

    internal IReadOnlyList<ControllerBatteryAlert> Observe(IReadOnlyList<ControllerDeviceSnapshot> devices,
        IReadOnlyList<ControllerIdentity> identities, long now)
    {
        var connected = devices.Where(device => device.Slot < 4 && device.Connected).DistinctBy(device => device.Slot).ToArray();
        // Windows metadata has no XInput slot mapping. Never assign it among several candidates.
        var identity = connected.Length == 1 && identities.Count == 1 && !string.IsNullOrWhiteSpace(identities[0].Id) ? identities[0] : null;
        var result = new List<ControllerBatteryAlert>();
        foreach (var device in connected)
        {
            if (device.BatteryType is ControllerBatteryType.Wired or ControllerBatteryType.Disconnected
                || device.BatteryType == ControllerBatteryType.Unknown && device.NativeResult != 0
                || identity is { Charging: true } or { Wireless: false })
                continue;
            var state = states[device.Slot];
            if (!string.IsNullOrEmpty(identity?.Id))
            {
                if (state.Identity is not null && state.Identity != identity.Id)
                    states[device.Slot] = state = new();
                state.Identity = identity.Id;
            }

            var percent = identity?.BatteryPercent is >= 0 and <= 100 ? identity.BatteryPercent : null;
            var category = device.BatteryType is ControllerBatteryType.Alkaline or ControllerBatteryType.NickelMetalHydride
                ? device.BatteryLevel : null;
            var healthy = percent is { } charge ? charge > 25 : category is ControllerBatteryLevel.Medium or ControllerBatteryLevel.Full;
            if (healthy)
                state.Notified = ControllerBatteryAlertLevel.None;
            var level = percent is { } measured ? measured <= 5 ? ControllerBatteryAlertLevel.Critical
                : measured <= 20 ? ControllerBatteryAlertLevel.Low : ControllerBatteryAlertLevel.None
                : category == ControllerBatteryLevel.Empty ? ControllerBatteryAlertLevel.Critical
                : category == ControllerBatteryLevel.Low ? ControllerBatteryAlertLevel.Low : ControllerBatteryAlertLevel.None;
            if (level <= state.Notified || state.LastNotification is { } last && level <= state.LastLevel && now - last < RecoveryCooldown)
                continue;
            state.Notified = state.LastLevel = level;
            state.LastNotification = now;
            var name = identity is null ? $"Controller {device.Slot + 1}" : new string(identity.Name.Where(character => !char.IsControl(character)).Take(48).ToArray()).Trim();
            result.Add(new(device.Slot, name.Length == 0 ? "Controller" : name, percent, level));
        }

        return result;
    }
}

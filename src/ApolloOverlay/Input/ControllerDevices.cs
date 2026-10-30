namespace ApolloOverlay;

internal interface IControllerNative
{
    uint Connected(uint slot);
    uint Battery(uint slot, out Native.XBattery battery);
    uint Capabilities(uint slot, out Native.XCapabilities capabilities);
    uint Vibrate(uint slot, ushort left, ushort right);
}

internal sealed class XInputControllerNative : IControllerNative
{
    public uint Connected(uint slot) => Native.XInputGetState(slot, out _);
    public uint Battery(uint slot, out Native.XBattery battery) => Native.XInputGetBatteryInformation(slot, 0, out battery);
    public uint Capabilities(uint slot, out Native.XCapabilities capabilities) => Native.XInputGetCapabilities(slot, 0, out capabilities);
    public uint Vibrate(uint slot, ushort left, ushort right)
    {
        var vibration = new Native.XVibration
        {
            Left = left,
            Right = right
        };
        return Native.XInputSetState(slot, ref vibration);
    }
}

/// <summary>Connection edges come from the established input source. Battery reads never poll while hidden by themselves.</summary>
public sealed class ControllerDevices : IControllerDevices
{
    private sealed class SlotState
    {
        public bool Connected;
        public long Epoch;
        public long? SampledAt;
        public ControllerDeviceSnapshot? Snapshot;
    }

    private readonly object gate = new();
    private readonly SlotState[] slots = [new(), new(), new(), new()];
    private readonly IControllerNative native;
    private readonly Func<long> clock;
    private bool disposed;
    public ControllerDevices() : this(new XInputControllerNative(), () => Environment.TickCount64)
    {
    }

    internal ControllerDevices(IControllerNative native, Func<long> clock)
    {
        this.native = native;
        this.clock = clock;
    }

    private static void Connection(SlotState state, bool value)
    {
        if (state.Connected == value)
            return;
        state.Connected = value;
        state.Epoch++;
        state.SampledAt = null;
        state.Snapshot = null;
    }

    public void UpdateConnections(IReadOnlyList<bool> connected)
    {
        lock (gate)
        {
            if (disposed)
                return;
            for (var i = 0; i < slots.Length; i++)
                Connection(slots[i], i < connected.Count && connected[i]);
        }
    }

    private static ControllerDeviceSnapshot Unknown(uint slot, SlotState state, uint result, string reason) => new(slot, state.Epoch, state.Connected, state.Connected ? ControllerBatteryType.Unknown : ControllerBatteryType.Disconnected, null, ControllerVibrationCapability.Unknown, result, reason);
    public ControllerDeviceSnapshot Read(uint slot)
    {
        lock (gate)
        {
            if (slot >= slots.Length)
                return new(slot, 0, false, ControllerBatteryType.Disconnected, null, ControllerVibrationCapability.Unavailable, 87, "Invalid controller slot.");
            var state = slots[slot];
            if (disposed)
                return Unknown(slot, state, 1167, "The controller service has stopped.");
            try
            {
                var connection = native.Connected(slot);
                if (connection == 1167)
                {
                    Connection(state, false);
                    return Unknown(slot, state, connection, "Controller disconnected.");
                }

                if (connection != 0)
                {
                    state.Snapshot = null;
                    state.SampledAt = null;
                    return Unknown(slot, state, connection, "Controller connection unconfirmed.");
                }

                Connection(state, true);
                var now = clock();
                if (state.Snapshot is { } cached && state.SampledAt is { } sampled && now >= sampled && now - sampled < 5000)
                    return cached;
                var batteryResult = native.Battery(slot, out var battery);
                if (batteryResult == 1167)
                {
                    Connection(state, false);
                    return Unknown(slot, state, batteryResult, "The controller disconnected during the query.");
                }

                var capabilityResult = native.Capabilities(slot, out var capabilities);
                var capability = capabilityResult == 0 ? ((capabilities.Flags & 1) != 0 ? ControllerVibrationCapability.Available : ControllerVibrationCapability.Unavailable) : ControllerVibrationCapability.Unknown;
                var type = batteryResult != 0 ? ControllerBatteryType.Unknown : battery.Type switch
                {
                    0 => ControllerBatteryType.Disconnected,
                    1 => ControllerBatteryType.Wired,
                    2 => ControllerBatteryType.Alkaline,
                    3 => ControllerBatteryType.NickelMetalHydride,
                    _ => ControllerBatteryType.Unknown
                };
                ControllerBatteryLevel? level = type is ControllerBatteryType.Alkaline or ControllerBatteryType.NickelMetalHydride && battery.Level <= 3 ? (ControllerBatteryLevel)battery.Level : null;
                if (type == ControllerBatteryType.Disconnected)
                {
                    Connection(state, false);
                    return Unknown(slot, state, batteryResult, "The controller disconnected during the query.");
                }

                var reason = type == ControllerBatteryType.Wired ? "Wired" : level is { } category ? category switch
                {
                    ControllerBatteryLevel.Empty => "Empty battery",
                    ControllerBatteryLevel.Low => "Low battery",
                    ControllerBatteryLevel.Medium => "Medium battery",
                    _ => "Full battery"
                } : "Battery not reported";
                state.SampledAt = now;
                return state.Snapshot = new(slot, state.Epoch, true, type, level, capability, batteryResult != 0 ? batteryResult : capabilityResult, reason);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                state.Snapshot = null;
                state.SampledAt = null;
                return Unknown(slot, state, 50, "XInput is unavailable on this system.");
            }
        }
    }

    public ControllerVibrationResult SetVibration(uint slot, long connectionEpoch, ushort leftMotor, ushort rightMotor)
    {
        lock (gate)
        {
            if (disposed || slot >= slots.Length)
                return new(slot, connectionEpoch, 1167, false, "Service or slot unavailable.");
            var state = slots[slot];
            if (!state.Connected || state.Epoch != connectionEpoch)
                return new(slot, connectionEpoch, 1167, false, "The controller connection changed; haptics were blocked.");
            try
            {
                var connection = native.Connected(slot);
                if (connection != 0)
                {
                    if (connection == 1167)
                        Connection(state, false);
                    return new(slot, connectionEpoch, connection, false, "Controller unconfirmed; haptics were blocked.");
                }

                var result = native.Vibrate(slot, leftMotor, rightMotor);
                if (result == 1167)
                    Connection(state, false);
                return new(slot, connectionEpoch, result, result == 0, result == 0 ? "The haptic request was accepted; physical feedback is unconfirmed." : "The controller rejected haptics.");
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return new(slot, connectionEpoch, 50, false, "XInput is unavailable on this system.");
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var state in slots)
            {
                Connection(state, false);
                state.Snapshot = null;
            }
        }
    }
}

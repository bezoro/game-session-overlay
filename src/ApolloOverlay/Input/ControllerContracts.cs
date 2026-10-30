namespace ApolloOverlay;

public enum ControllerBatteryType
{
    Disconnected,
    Unknown,
    Wired,
    Alkaline,
    NickelMetalHydride
}

public enum ControllerBatteryLevel
{
    Empty,
    Low,
    Medium,
    Full
}

public enum ControllerVibrationCapability
{
    Unknown,
    Available,
    Unavailable
}

public sealed record ControllerDeviceSnapshot(
    uint Slot,
    long ConnectionEpoch,
    bool Connected,
    ControllerBatteryType BatteryType,
    ControllerBatteryLevel? BatteryLevel,
    ControllerVibrationCapability Vibration,
    uint NativeResult,
    string Reason
);
public sealed record ControllerVibrationResult(uint Slot, long ConnectionEpoch, uint NativeResult, bool Accepted, string Reason);
public interface IControllerDevices : IDisposable
{
    /// <summary>Feed established input-frame connection edges, including hidden frames, so reconnects invalidate cached batteries.</summary>
    void UpdateConnections(IReadOnlyList<bool> connected);
    ControllerDeviceSnapshot Read(uint slot);
    ControllerVibrationResult SetVibration(uint slot, long connectionEpoch, ushort leftMotor, ushort rightMotor);
}

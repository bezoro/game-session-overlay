namespace ApolloOverlay;

internal sealed class PreviewControllerDevices : IControllerDevices
{
    public void UpdateConnections(IReadOnlyList<bool> connected)
    {
    }

    public ControllerDeviceSnapshot Read(uint slot) => new(slot, 0, false, ControllerBatteryType.Unknown, null, ControllerVibrationCapability.Unknown, 0, "Preview · battery was not queried");
    public ControllerVibrationResult SetVibration(uint slot, long connectionEpoch, ushort leftMotor, ushort rightMotor) => new(slot, connectionEpoch, 0, false, "Preview · haptics were not sent");
    public void Dispose()
    {
    }
}

namespace ApolloOverlay;

internal sealed class PreviewControllerDevices : IControllerDevices
{
    public void UpdateConnections(IReadOnlyList<bool> connected)
    {
    }

    public ControllerDeviceSnapshot Read(uint slot) => new(slot, 0, false, ControllerBatteryType.Unknown, null, ControllerVibrationCapability.Unknown, 0, "Prévia · bateria não consultada");
    public ControllerVibrationResult SetVibration(uint slot, long connectionEpoch, ushort leftMotor, ushort rightMotor) => new(slot, connectionEpoch, 0, false, "Prévia · vibração não enviada");
    public void Dispose()
    {
    }
}

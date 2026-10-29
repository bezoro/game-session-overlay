using System.Runtime.InteropServices;

namespace ApolloOverlay;

[ComVisible(true), Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioVolumeCallback
{
    [PreserveSig]
    int OnNotify(nint notification);
}

[ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioDeviceCallback
{
    [PreserveSig]
    int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig]
    int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig]
    int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, AudioPropertyKey key);
}

[StructLayout(LayoutKind.Sequential)]
public struct AudioPropertyKey
{
    public Guid Format;
    public uint Id;
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class AudioNotifications(Action<bool> changed) : IAudioVolumeCallback, IAudioDeviceCallback
{
    public int OnNotify(nint data)
    {
        changed(false);
        return 0;
    }

    public int OnDeviceStateChanged(string id, uint state)
    {
        changed(true);
        return 0;
    }

    public int OnDeviceAdded(string id)
    {
        changed(true);
        return 0;
    }

    public int OnDeviceRemoved(string id)
    {
        changed(true);
        return 0;
    }

    public int OnDefaultDeviceChanged(int flow, int role, string? id)
    {
        if (flow == 0 && role == 0)
            changed(true);
        return 0;
    }

    public int OnPropertyValueChanged(string id, AudioPropertyKey key)
    {
        changed(false);
        return 0;
    }
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int flow, uint mask, out nint collection);
    [PreserveSig]
    int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig]
    int RegisterEndpointNotificationCallback(IAudioDeviceCallback callback);
    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IAudioDeviceCallback callback);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig]
    int OpenPropertyStore(uint access, out nint store);
    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig]
    int GetState(out uint state);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IAudioVolumeCallback callback);
    [PreserveSig]
    int UnregisterControlChangeNotify(IAudioVolumeCallback callback);
    [PreserveSig]
    int GetChannelCount(out uint count);
    [PreserveSig]
    int SetMasterVolumeLevel(float db, ref Guid context);
    [PreserveSig]
    int SetMasterVolumeLevelScalar(float volume, ref Guid context);
    [PreserveSig]
    int GetMasterVolumeLevel(out float db);
    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float volume);
    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float db, ref Guid context);
    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float volume, ref Guid context);
    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float db);
    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float volume);
    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

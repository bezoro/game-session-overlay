using System.Runtime.InteropServices;

namespace ApolloOverlay;

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct AudioPropVariant
{
    [FieldOffset(0)]
    public ushort Type;
    [FieldOffset(8)]
    public nint Pointer;
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOverlayPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);
    [PreserveSig]
    int GetAt(uint index, out AudioPropertyKey key);
    [PreserveSig]
    int GetValue(ref AudioPropertyKey key, out AudioPropVariant value);
    [PreserveSig]
    int SetValue(ref AudioPropertyKey key, ref AudioPropVariant value);
    [PreserveSig]
    int Commit();
}

// Windows has no public API for changing the default output for both roles.
// Keep the private PolicyConfig ABI isolated; callers verify endpoint IDs by
// reading the public enumerator and never infer completion from this HRESULT.
[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOverlayAudioPolicy
{
    [PreserveSig]
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string device, out nint format);
    [PreserveSig]
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, int defaults, out nint format);
    [PreserveSig]
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device);
    [PreserveSig]
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, nint endpoint, nint mix);
    [PreserveSig]
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, int defaults, out long period, out long minimum);
    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, ref long period);
    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, nint mode);
    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, nint mode);
    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, ref AudioPropertyKey key, out AudioPropVariant value);
    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, ref AudioPropertyKey key, ref AudioPropVariant value);
    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string device, int role);
    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string device, int visible);
}

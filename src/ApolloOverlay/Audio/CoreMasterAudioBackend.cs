using System.Runtime.InteropServices;

namespace ApolloOverlay;

internal sealed class CoreMasterAudioBackend : IMasterAudioBackend
{
    private IMMDeviceEnumerator? enumerator;
    private IMMDevice? device;
    private IAudioEndpointVolume? endpoint;
    private readonly AudioNotifications notifications;
    private bool volumeRegistered, deviceRegistered, initialized;

    internal CoreMasterAudioBackend(Action<bool> changed)
    {
        Check(CoInitializeEx(0, 0));
        initialized = true;
        notifications = new(changed);
    }

    private static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    public AudioSnapshot Bind()
    {
        Release();
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var iid = typeof(IMMDeviceEnumerator).GUID;
        Check(CoCreateInstance(ref clsid, 0, 1, ref iid, out var created));
        enumerator = (IMMDeviceEnumerator)created;
        Check(enumerator.RegisterEndpointNotificationCallback(notifications));
        deviceRegistered = true;
        Check(enumerator.GetDefaultAudioEndpoint(0, 0, out device));
        var volumeIid = typeof(IAudioEndpointVolume).GUID;
        Check(device.Activate(ref volumeIid, 23, 0, out var activated));
        endpoint = (IAudioEndpointVolume)activated;
        Check(endpoint.RegisterControlChangeNotify(notifications));
        volumeRegistered = true;
        return ReadBound();
    }

    private string DefaultId()
    {
        IMMDevice? current = null;
        try
        {
            Check(enumerator!.GetDefaultAudioEndpoint(0, 0, out current));
            Check(current.GetId(out var id));
            return id;
        }
        finally
        {
            if (current is not null)
                Marshal.ReleaseComObject(current);
        }
    }

    public AudioSnapshot Read()
    {
        if (endpoint is null || device is null)
            return Bind();
        Check(device.GetId(out var id));
        return id == DefaultId() ? ReadBound() : Bind();
    }

    private AudioSnapshot ReadBound()
    {
        Check(device!.GetId(out var id));
        Check(endpoint!.GetMasterVolumeLevelScalar(out var volume));
        Check(endpoint.GetMute(out var muted));
        return new(id, volume, muted, "Windows default output volume");
    }

    public MasterAudioChange Set(float? volume, bool? muted, string? expectedEndpoint, Func<bool> dispatch, Func<bool> continuing)
    {
        var before = Read();
        if (expectedEndpoint is null || before.EndpointId != expectedEndpoint)
            return new(new(ResultKind.Unavailable, "The audio output changed; no change was sent."), before);
        var attempted = 0;
        var succeeded = 0;
        try
        {
            var context = Guid.Empty;
            bool Allowed() => continuing() && DefaultId() == expectedEndpoint;
            if (volume is { } scalar)
            {
                if (!Allowed() || !dispatch())
                    return new(new(ResultKind.Cancelled, "No audio change was sent."), before);
                attempted++;
                Check(endpoint!.SetMasterVolumeLevelScalar(scalar, ref context));
                succeeded++;
            }
            if (muted is { } mute)
            {
                if (!Allowed() || !dispatch())
                    return new(new(attempted > 0 ? ResultKind.Partial : ResultKind.Cancelled,
                        "Audio observation changed; no further commands were sent."), Read());
                attempted++;
                Check(endpoint!.SetMute(mute, ref context));
                succeeded++;
            }
            var after = Read();
            return new(Verify(volume, muted, expectedEndpoint, after, continuing()), after);
        }
        catch (COMException)
        {
            var after = new AudioSnapshot(null, 0, false, "The audio change could not be confirmed.");
            return new(new(succeeded > 0 ? ResultKind.Partial : attempted > 0 ? ResultKind.Unknown : ResultKind.Unavailable,
                "Windows did not confirm the audio change. Refresh before trying again."), after);
        }
    }

    internal static ActionResult Verify(float? volume, bool? muted, string expectedEndpoint, AudioSnapshot after, bool continuing)
    {
        if (!continuing || !after.Available || after.EndpointId != expectedEndpoint
            || volume is { } level && (!float.IsFinite(after.Volume) || Math.Abs(after.Volume - level) >= .005f)
            || muted is { } mute && after.Muted != mute)
            return new(ResultKind.Unknown, "The requested audio state was not confirmed. Check the output before trying again.");
        return new(ResultKind.Completed, $"Windows output · {after.Volume:P0}" + (after.Muted ? " · muted" : ""));
    }

    private void Release()
    {
        if (endpoint is not null && volumeRegistered)
            try
            {
                endpoint.UnregisterControlChangeNotify(notifications);
            }
            catch (COMException) { }
        volumeRegistered = false;
        if (enumerator is not null && deviceRegistered)
            try
            {
                enumerator.UnregisterEndpointNotificationCallback(notifications);
            }
            catch (COMException) { }
        deviceRegistered = false;
        if (endpoint is not null)
            Marshal.ReleaseComObject(endpoint);
        if (device is not null)
            Marshal.ReleaseComObject(device);
        if (enumerator is not null)
            Marshal.ReleaseComObject(enumerator);
        endpoint = null;
        device = null;
        enumerator = null;
    }

    public void Dispose()
    {
        Release();
        if (initialized)
        {
            CoUninitialize();
            initialized = false;
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object instance);
}

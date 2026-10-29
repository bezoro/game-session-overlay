using System.Runtime.InteropServices;

namespace ApolloOverlay;

internal sealed class CoreSystemAudioBackend : ISystemAudioBackend
{
    private static void Check(int value)
    {
        if (value != 0)
            throw new COMException("Core Audio did not return S_OK.", value);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    private static IHubDeviceEnumerator Create()
    {
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var iid = typeof(IHubDeviceEnumerator).GUID;
        Check(CoCreateInstance(ref clsid, 0, 1, ref iid, out var value));
        return (IHubDeviceEnumerator)value;
    }

    private static string Default(IHubDeviceEnumerator enumerator, int flow, int role)
    {
        IMMDevice? device = null;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(flow, role, out device) != 0)
                return "";
            Check(device.GetId(out var id));
            return id;
        }
        finally
        {
            Release(device);
        }
    }

    public SystemAudioSnapshot Read()
    {
        CheckApartment();
        IHubDeviceEnumerator? enumerator = null;
        IHubDeviceCollection? collection = null;
        try
        {
            enumerator = Create();
            Check(enumerator.EnumAudioEndpoints(0, 1, out collection));
            Check(collection.GetCount(out var count));
            var choices = new List<AudioDeviceChoice>();
            for (uint index = 0; index < Math.Min(count, 64); index++)
            {
                IMMDevice? device = null;
                try
                {
                    Check(collection.Item(index, out device));
                    Check(device.GetId(out var id));
                    choices.Add(new(id, DeviceName(device, "Output " + (index + 1))));
                }
                finally
                {
                    Release(device);
                }
            }

            var microphone = Default(enumerator, 1, 2);
            var name = "Microphone unavailable";
            bool? muted = null;
            if (microphone.Length > 0)
            {
                IMMDevice? capture = null;
                object? activated = null;
                try
                {
                    Check(enumerator.GetDevice(microphone, out capture));
                    name = DeviceName(capture, "Default microphone");
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    Check(capture.Activate(ref iid, 23, 0, out activated));
                    Check(((IAudioEndpointVolume)activated).GetMute(out var state));
                    muted = state;
                }
                catch (Exception error) when (error is COMException or InvalidCastException)
                {
                    muted = null;
                }
                finally
                {
                    Release(activated);
                    Release(capture);
                }
            }

            return new(choices.ToArray(), Default(enumerator, 0, 0), Default(enumerator, 0, 1), microphone, name, muted, "Game and media outputs; default communications microphone.");
        }
        finally
        {
            Release(collection);
            Release(enumerator);
            CoUninitialize();
        }
    }

    public ActionResult SetMicrophoneMuted(string expectedId, bool muted, Func<bool> continuing)
    {
        CheckApartment();
        IHubDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? activated = null;
        var sent = false;
        try
        {
            enumerator = Create();
            if (string.IsNullOrEmpty(expectedId) || Default(enumerator, 1, 2) != expectedId)
                return new(ResultKind.Unavailable, "The microphone changed. Refresh before changing its state.");
            Check(enumerator.GetDevice(expectedId, out device));
            Check(device.GetState(out var state));
            if (state != 1)
                return new(ResultKind.Unavailable, "The microphone is disconnected.");
            var iid = typeof(IAudioEndpointVolume).GUID;
            Check(device.Activate(ref iid, 23, 0, out activated));
            var volume = (IAudioEndpointVolume)activated;
            if (!continuing())
                return new(ResultKind.Cancelled, "No microphone change was sent.");
            var context = Guid.Empty;
            sent = true;
            Check(volume.SetMute(muted, ref context));
            if (!continuing() || Default(enumerator, 1, 2) != expectedId)
                return new(ResultKind.Unknown, "The microphone may have changed after dispatch. Check its state.");
            Check(volume.GetMute(out var after));
            return new(after == muted ? ResultKind.Completed : ResultKind.Unknown, after == muted ? muted ? "Microphone muted." : "Microphone unmuted." : "The microphone did not confirm the requested state.");
        }
        catch (COMException)
        {
            return new(sent ? ResultKind.Unknown : ResultKind.Unavailable, "Windows did not confirm the microphone change.");
        }
        finally
        {
            Release(activated);
            Release(device);
            Release(enumerator);
            CoUninitialize();
        }
    }

    public ActionResult SetOutputs(string expectedConsole, string expectedMedia, string console, string media, Func<bool> continuing)
    {
        CheckApartment();
        IHubDeviceEnumerator? enumerator = null;
        object? policy = null;
        var sent = 0;
        try
        {
            enumerator = Create();
            if (expectedConsole.Length == 0 || Default(enumerator, 0, 0) != expectedConsole || Default(enumerator, 0, 1) != expectedMedia)
                return new(ResultKind.Unavailable, "The audio output changed. Refresh before selecting another device.");
            foreach (var id in new[]
            {
                console,
                media
            }.Distinct())
            {
                IMMDevice? device = null;
                try
                {
                    Check(enumerator.GetDevice(id, out device));
                    Check(device.GetState(out var state));
                    if (state != 1)
                        return new(ResultKind.Unavailable, "The selected output is disconnected.");
                }
                finally
                {
                    Release(device);
                }
            }

            var clsid = new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
            var iid = typeof(IOverlayAudioPolicy).GUID;
            Check(CoCreateInstance(ref clsid, 0, 1, ref iid, out policy));
            var setter = (IOverlayAudioPolicy)policy;
            if (!continuing())
                return new(ResultKind.Cancelled, "No output changes were sent.");
            sent++;
            Check(setter.SetDefaultEndpoint(console, 0));
            if (!continuing())
                return new(ResultKind.Partial, "The game output was requested. The media output was not changed after cancellation.");
            sent++;
            Check(setter.SetDefaultEndpoint(media, 1));
            if (!continuing())
                return new(ResultKind.Unknown, "Check the outputs after dispatch. Observation was interrupted.");
            var confirmed = Default(enumerator, 0, 0) == console && Default(enumerator, 0, 1) == media;
            return new(confirmed ? ResultKind.Completed : ResultKind.Unknown, confirmed ? "Game and media outputs confirmed." : "Windows did not confirm all requested outputs.");
        }
        catch (COMException)
        {
            return new(sent > 0 ? ResultKind.Partial : ResultKind.Unavailable, sent > 0 ? "The output change was partial. Refresh to check the state." : "Output switching is unavailable on this version of Windows.");
        }
        finally
        {
            Release(policy);
            Release(enumerator);
            CoUninitialize();
        }
    }

    private static void CheckApartment()
    {
        var value = CoInitializeEx(0, 0);
        if (value < 0)
            Marshal.ThrowExceptionForHR(value);
    }

    private static string DeviceName(IMMDevice device, string fallback)
    {
        nint raw = 0;
        object? store = null;
        var value = default(AudioPropVariant);
        try
        {
            Check(device.OpenPropertyStore(0, out raw));
            store = Marshal.GetObjectForIUnknown(raw);
            var key = new AudioPropertyKey
            {
                Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                Id = 14
            };
            Check(((IOverlayPropertyStore)store).GetValue(ref key, out value));
            var name = value.Type == 31 && value.Pointer != 0 ? Marshal.PtrToStringUni(value.Pointer) : null;
            return string.IsNullOrWhiteSpace(name) ? fallback : new string(name.Where(c => !char.IsControl(c)).Take(180).ToArray());
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            return fallback;
        }
        finally
        {
            PropVariantClear(ref value);
            Release(store);
            if (raw != 0)
                Marshal.Release(raw);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref AudioPropVariant value);
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object instance);
}

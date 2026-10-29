using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ApolloOverlay;

public sealed class WindowsAudio : IDisposable
{
    private IMMDeviceEnumerator? enumerator;
    private IMMDevice? device;
    private IAudioEndpointVolume? endpoint;
    private readonly AudioNotifications notifications;
    private readonly Dispatcher dispatcher;
    private bool visible, disposed, queued, volumeRegistered, deviceRegistered;
    public AudioSnapshot Current
    {
        get;
        private set;
    } = new(null, 0, false, "Áudio não consultado.");

    public event Action? Changed;
    public WindowsAudio()
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        notifications = new(Queue);
    }

    private static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    public void SetVisible(bool value)
    {
        if (disposed)
            return;
        visible = value;
        if (value)
            Bind();
        else
            Release();
    }

    private void Queue(bool rebind)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;
        try
        {
            dispatcher.BeginInvoke(() =>
            {
                if (!visible || disposed)
                    return;
                if (queued)
                    return;
                queued = true;
                try
                {
                    if (rebind)
                        Bind();
                    else
                    {
                        Read();
                        Changed?.Invoke();
                    }
                }
                finally
                {
                    queued = false;
                }
            });
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Bind()
    {
        Release();
        if (!visible || disposed)
            return;
        try
        {
            var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
            var iid = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
            Check(CoCreateInstance(ref clsid, 0, 1, ref iid, out var created));
            enumerator = (IMMDeviceEnumerator)created;
            Check(enumerator.RegisterEndpointNotificationCallback(notifications));
            deviceRegistered = true;
            Check(enumerator.GetDefaultAudioEndpoint(0, 0, out device));
            var volumeIid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
            Check(device.Activate(ref volumeIid, 23, 0, out var activated));
            endpoint = (IAudioEndpointVolume)activated;
            Check(endpoint.RegisterControlChangeNotify(notifications));
            volumeRegistered = true;
            Read();
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            Current = new(null, 0, false, "Saída padrão indisponível. Selecione um dispositivo no Windows.");
        }

        Changed?.Invoke();
    }

    public AudioSnapshot Read()
    {
        try
        {
            if (endpoint is null || device is null)
                return Current = new(null, 0, false, "Nenhuma saída padrão disponível.");
            Check(device.GetId(out var id));
            Check(endpoint.GetMasterVolumeLevelScalar(out var volume));
            Check(endpoint.GetMute(out var muted));
            return Current = new(id, volume, muted, "Volume da saída padrão do Windows");
        }
        catch (COMException)
        {
            return Current = new(null, 0, false, "Dispositivo removido ou leitura recusada.");
        }
    }

    public ActionResult Set(float? volume = null, bool? muted = null, string? expectedEndpoint = null)
    {
        var current = Read();
        if (!current.Available || endpoint is null || expectedEndpoint is not null && current.EndpointId != expectedEndpoint)
            return new(ResultKind.Unavailable, "Saída de áudio mudou; alteração bloqueada.");
        try
        {
            var context = Guid.Empty;
            if (volume is { } scalar)
                Check(endpoint.SetMasterVolumeLevelScalar(Math.Clamp(scalar, 0, 1), ref context));
            if (muted is { } mute)
                Check(endpoint.SetMute(mute, ref context));
            var after = Read();
            Changed?.Invoke();
            if (!after.Available || after.EndpointId != current.EndpointId)
                return new(ResultKind.Unknown, "Dispositivo mudou após a solicitação.");
            return new(ResultKind.Completed, $"Saída Windows · {after.Volume:P0}" + (after.Muted ? " · sem som" : ""));
        }
        catch (COMException)
        {
            Read();
            return new(ResultKind.Failed, "Windows recusou a alteração do áudio.");
        }
    }

    private void Release()
    {
        if (endpoint is not null && volumeRegistered)
            try
            {
                endpoint.UnregisterControlChangeNotify(notifications);
            }
            catch (COMException)
            {
            }

        volumeRegistered = false;
        if (enumerator is not null && deviceRegistered)
            try
            {
                enumerator.UnregisterEndpointNotificationCallback(notifications);
            }
            catch (COMException)
            {
            }

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
        if (disposed)
            return;
        visible = false;
        disposed = true;
        Release();
        Changed = null;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object instance);
}

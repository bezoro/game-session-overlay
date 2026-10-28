using System.Runtime.InteropServices;

namespace ApolloOverlay;

public sealed record AudioDeviceChoice(string Id, string Name);
public sealed record SystemAudioSnapshot(IReadOnlyList<AudioDeviceChoice> Outputs, string ConsoleOutputId, string MediaOutputId,
    string MicrophoneId, string MicrophoneName, bool? MicrophoneMuted, string Status)
{
    public static SystemAudioSnapshot Empty { get; } = new([], "", "", "", "Microfone indisponível", null, "Áudio não consultado.");
    public string OutputName => Outputs.FirstOrDefault(output => output.Id == ConsoleOutputId)?.Name ?? "Saída indisponível";
}
public interface ISystemAudioBackend
{
    SystemAudioSnapshot Read();
    ActionResult SetMicrophoneMuted(string expectedId, bool muted, Func<bool> continuing);
    ActionResult SetOutputs(string expectedConsole, string expectedMedia, string console, string media, Func<bool> continuing);
}

/// <summary>One caller owns each native operation. In-flight cancellation never claims a write was undone.</summary>
public sealed class SystemAudioDevices(ISystemAudioBackend? backend = null)
{
    private readonly ISystemAudioBackend backend = backend ?? new CoreSystemAudioBackend();
    private readonly SemaphoreSlim gate = new(1, 1);
    public SystemAudioSnapshot Current { get; private set; } = SystemAudioSnapshot.Empty;
    public void InvalidateObservation()=>Current=Current with{MicrophoneMuted=null,Status="Atualizando os dispositivos de áudio…"};
    public async Task<SystemAudioSnapshot> RefreshAsync(CancellationToken cancellation = default)
    {
        if (!await gate.WaitAsync(0, cancellation)) return Current;
        var work=Task.Run(backend.Read,CancellationToken.None);
        try { Current = await work.WaitAsync(TimeSpan.FromSeconds(2),cancellation); return Current; }
        catch(TimeoutException){return Current=SystemAudioSnapshot.Empty with{Status="O áudio demorou a responder. Tente atualizar novamente."};}
        catch (Exception error) when (error is COMException or InvalidCastException or InvalidOperationException or UnauthorizedAccessException)
        { return Current = SystemAudioSnapshot.Empty with { Status = "Dispositivos de áudio indisponíveis. Tente atualizar a lista." }; }
        finally { ReleaseWhenDrained(work); }
    }
    public Task<ActionResult> SetMicrophoneMutedAsync(string expectedId, bool muted, CancellationToken cancellation = default) =>
        Mutate(keep => backend.SetMicrophoneMuted(expectedId, muted, keep), cancellation);
    public Task<ActionResult> SelectOutputAsync(string expectedConsole, string expectedMedia, string output, CancellationToken cancellation = default) =>
        RestoreOutputsAsync(expectedConsole, expectedMedia, output, output, cancellation);
    public Task<ActionResult> RestoreOutputsAsync(string expectedConsole, string expectedMedia, string console, string media, CancellationToken cancellation = default) =>
        Mutate(keep => backend.SetOutputs(expectedConsole, expectedMedia, console, media, keep), cancellation);
    private async Task<ActionResult> Mutate(Func<Func<bool>, ActionResult> action, CancellationToken cancellation)
    {
        if (!await gate.WaitAsync(0, cancellation)) return new(ResultKind.Unavailable, "Outra operação de áudio está em andamento.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(3000);
        Task<(ActionResult Result,SystemAudioSnapshot Snapshot)>? work=null;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            work=Task.Run(()=>{var result=action(()=>!deadline.IsCancellationRequested);return(result,deadline.IsCancellationRequested?Current:backend.Read());},CancellationToken.None);
            var observed=await work.WaitAsync(deadline.Token);Current=observed.Snapshot;return observed.Result;
        }
        catch (OperationCanceledException) { return new(work is not null ? ResultKind.Unknown : ResultKind.Cancelled, work is not null ? "Operação interrompida. Confira o dispositivo antes de repetir." : "Nenhuma alteração de áudio enviada."); }
        catch (Exception error) when (error is COMException or InvalidCastException or InvalidOperationException or UnauthorizedAccessException)
        { return new(work is not null ? ResultKind.Unknown : ResultKind.Unavailable, "Não foi possível confirmar a alteração. Atualize a lista antes de tentar novamente."); }
        finally { if(work is null)gate.Release();else ReleaseWhenDrained(work); }
    }
    private void ReleaseWhenDrained(Task work)
    {
        if(work.IsCompleted){_ = work.Exception;gate.Release();return;}
        _=work.ContinueWith(completed=>{_ = completed.Exception;gate.Release();},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
}

internal sealed class CoreSystemAudioBackend : ISystemAudioBackend
{
    private static void Check(int value) { if (value != 0) throw new COMException("Core Audio não retornou S_OK.", value); }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private static IHubDeviceEnumerator Create()
    {
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"); var iid = typeof(IHubDeviceEnumerator).GUID;
        Check(CoCreateInstance(ref clsid, 0, 1, ref iid, out var value)); return (IHubDeviceEnumerator)value;
    }
    private static string Default(IHubDeviceEnumerator enumerator, int flow, int role)
    {
        IMMDevice? device = null;
        try { if (enumerator.GetDefaultAudioEndpoint(flow, role, out device) != 0) return ""; Check(device.GetId(out var id)); return id; }
        finally { Release(device); }
    }
    public SystemAudioSnapshot Read()
    {
        CheckApartment(); IHubDeviceEnumerator? enumerator = null; IHubDeviceCollection? collection = null;
        try
        {
            enumerator = Create(); Check(enumerator.EnumAudioEndpoints(0, 1, out collection)); Check(collection.GetCount(out var count));
            var choices = new List<AudioDeviceChoice>();
            for (uint index = 0; index < Math.Min(count, 64); index++)
            {
                IMMDevice? device = null;
                try { Check(collection.Item(index, out device)); Check(device.GetId(out var id)); choices.Add(new(id, DeviceName(device, "Saída " + (index + 1)))); }
                finally { Release(device); }
            }
            var microphone = Default(enumerator, 1, 2); var name = "Microfone indisponível"; bool? muted = null;
            if (microphone.Length > 0)
            {
                IMMDevice? capture = null; object? activated = null;
                try
                {
                    Check(enumerator.GetDevice(microphone, out capture)); name = DeviceName(capture, "Microfone padrão");
                    var iid = typeof(IAudioEndpointVolume).GUID; Check(capture.Activate(ref iid, 23, 0, out activated));
                    Check(((IAudioEndpointVolume)activated).GetMute(out var state)); muted = state;
                }
                catch(Exception error)when(error is COMException or InvalidCastException){muted=null;}
                finally { Release(activated); Release(capture); }
            }
            return new(choices.ToArray(), Default(enumerator, 0, 0), Default(enumerator, 0, 1), microphone, name, muted, "Saídas para jogos e mídia; microfone padrão de comunicação.");
        }
        finally { Release(collection); Release(enumerator); CoUninitialize(); }
    }
    public ActionResult SetMicrophoneMuted(string expectedId, bool muted, Func<bool> continuing)
    {
        CheckApartment(); IHubDeviceEnumerator? enumerator = null; IMMDevice? device = null; object? activated = null; var sent = false;
        try
        {
            enumerator = Create();
            if (string.IsNullOrEmpty(expectedId) || Default(enumerator, 1, 2) != expectedId) return new(ResultKind.Unavailable, "O microfone mudou. Atualize antes de alterar o estado.");
            Check(enumerator.GetDevice(expectedId, out device)); Check(device.GetState(out var state));
            if (state != 1) return new(ResultKind.Unavailable, "O microfone está desconectado.");
            var iid = typeof(IAudioEndpointVolume).GUID; Check(device.Activate(ref iid, 23, 0, out activated)); var volume = (IAudioEndpointVolume)activated;
            if (!continuing()) return new(ResultKind.Cancelled, "Nenhuma alteração enviada ao microfone.");
            var context = Guid.Empty; sent = true; Check(volume.SetMute(muted, ref context));
            if (!continuing() || Default(enumerator, 1, 2) != expectedId) return new(ResultKind.Unknown, "O microfone pode ter mudado após o envio. Confira seu estado.");
            Check(volume.GetMute(out var after));
            return new(after == muted ? ResultKind.Completed : ResultKind.Unknown, after == muted ? muted ? "Microfone silenciado." : "Microfone ativado." : "O microfone não confirmou o estado solicitado.");
        }
        catch (COMException) { return new(sent ? ResultKind.Unknown : ResultKind.Unavailable, "O Windows não confirmou a alteração do microfone."); }
        finally { Release(activated); Release(device); Release(enumerator); CoUninitialize(); }
    }
    public ActionResult SetOutputs(string expectedConsole, string expectedMedia, string console, string media, Func<bool> continuing)
    {
        CheckApartment(); IHubDeviceEnumerator? enumerator = null; object? policy = null; var sent = 0;
        try
        {
            enumerator = Create();
            if (expectedConsole.Length == 0 || Default(enumerator, 0, 0) != expectedConsole || Default(enumerator, 0, 1) != expectedMedia)
                return new(ResultKind.Unavailable, "A saída de áudio mudou. Atualize antes de selecionar outro dispositivo.");
            foreach (var id in new[] { console, media }.Distinct())
            {
                IMMDevice? device = null;
                try { Check(enumerator.GetDevice(id, out device)); Check(device.GetState(out var state)); if (state != 1) return new(ResultKind.Unavailable, "A saída escolhida não está conectada."); }
                finally { Release(device); }
            }
            var clsid = new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"); var iid = typeof(IOverlayAudioPolicy).GUID;
            Check(CoCreateInstance(ref clsid, 0, 1, ref iid, out policy)); var setter = (IOverlayAudioPolicy)policy;
            if (!continuing()) return new(ResultKind.Cancelled, "Nenhuma alteração enviada às saídas.");
            sent++; Check(setter.SetDefaultEndpoint(console, 0));
            if (!continuing()) return new(ResultKind.Partial, "A saída dos jogos foi solicitada. A saída da mídia não foi alterada após o cancelamento.");
            sent++; Check(setter.SetDefaultEndpoint(media, 1));
            if (!continuing()) return new(ResultKind.Unknown, "Confira as saídas após o envio. A observação foi interrompida.");
            var confirmed = Default(enumerator, 0, 0) == console && Default(enumerator, 0, 1) == media;
            return new(confirmed ? ResultKind.Completed : ResultKind.Unknown, confirmed ? "Saídas de jogos e mídia confirmadas." : "O Windows não confirmou todas as saídas solicitadas.");
        }
        catch (COMException) { return new(sent > 0 ? ResultKind.Partial : ResultKind.Unavailable, sent > 0 ? "A alteração das saídas foi parcial. Atualize para conferir o estado." : "A troca de saída não está disponível neste Windows."); }
        finally { Release(policy); Release(enumerator); CoUninitialize(); }
    }
    private static void CheckApartment() { var value = CoInitializeEx(0, 0); if (value < 0) Marshal.ThrowExceptionForHR(value); }
    private static string DeviceName(IMMDevice device, string fallback)
    {
        nint raw = 0; object? store = null; var value = default(AudioPropVariant);
        try
        {
            Check(device.OpenPropertyStore(0, out raw)); store = Marshal.GetObjectForIUnknown(raw);
            var key = new AudioPropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
            Check(((IOverlayPropertyStore)store).GetValue(ref key, out value));
            var name = value.Type == 31 && value.Pointer != 0 ? Marshal.PtrToStringUni(value.Pointer) : null;
            return string.IsNullOrWhiteSpace(name) ? fallback : new string(name.Where(c => !char.IsControl(c)).Take(180).ToArray());
        }
        catch (Exception error) when (error is COMException or InvalidCastException) { return fallback; }
        finally { PropVariantClear(ref value); Release(store); if (raw != 0) Marshal.Release(raw); }
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref AudioPropVariant value);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object instance);
}

[StructLayout(LayoutKind.Explicit, Size = 24)] internal struct AudioPropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public nint Pointer; }
[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOverlayPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out AudioPropertyKey key);
    [PreserveSig] int GetValue(ref AudioPropertyKey key, out AudioPropVariant value);
    [PreserveSig] int SetValue(ref AudioPropertyKey key, ref AudioPropVariant value);
    [PreserveSig] int Commit();
}
[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOverlayAudioPolicy
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string device, out nint format);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, int defaults, out nint format);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, nint endpoint, nint mix);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, int defaults, out long period, out long minimum);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, ref long period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, nint mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, nint mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, ref AudioPropertyKey key, out AudioPropVariant value);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, ref AudioPropertyKey key, ref AudioPropVariant value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string device, int role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string device, int visible);
}

public sealed class PreviewSystemAudioBackend : ISystemAudioBackend
{
    public SystemAudioSnapshot State { get; set; } = new([new("preview-output", "Saída de áudio de exemplo")], "preview-output", "preview-output", "preview-mic", "Microfone de exemplo", false, "Prévia de áudio.");
    public SystemAudioSnapshot Read() => State;
    public ActionResult SetMicrophoneMuted(string expectedId, bool muted, Func<bool> continuing)
    { if (!continuing() || expectedId != State.MicrophoneId) return new(ResultKind.Cancelled, "Contexto da prévia mudou."); State = State with { MicrophoneMuted = muted }; return new(ResultKind.Simulated, "Prévia: estado do microfone alterado."); }
    public ActionResult SetOutputs(string expectedConsole, string expectedMedia, string console, string media, Func<bool> continuing)
    { if (!continuing() || expectedConsole != State.ConsoleOutputId || expectedMedia != State.MediaOutputId) return new(ResultKind.Cancelled, "Contexto da prévia mudou."); State = State with { ConsoleOutputId = console, MediaOutputId = media }; return new(ResultKind.Simulated, "Prévia: saída selecionada."); }
}

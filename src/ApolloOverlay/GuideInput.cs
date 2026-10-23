using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ApolloOverlay;

// Contract: retained official include/v2/GameInput.h, SHA256 2df7aa891696c72e1c7e6e3570e8aa3b2cc5629aefe5ae614386b5ad087f0830.
// IUnknown slots 0..2; IGameInput v2 system callback=9, stop=11, unregister=12, focus=16.
public interface IGuideBackend
{
    bool Initialize(out string reason);
    bool Register(Action pressed, out string reason);
    void Stop();
    bool Unregister();
    void Release();
}

public sealed class GuideConnection : IDisposable
{
    private readonly IGuideBackend backend;
    private bool initialized,registered,disposed;
    public string Status { get; private set; }="Guide não verificado.";
    public bool Observed { get; private set; }
    public GuideConnection(IGuideBackend backend) => this.backend=backend;
    public bool Start(Action pressed)
    {
        if (disposed || initialized) return false;
        if (!backend.Initialize(out var reason)) { Status=reason; backend.Release(); return false; }
        initialized=true;
        if (!backend.Register(()=>{ Observed=true; pressed(); },out reason)) { Status=reason; backend.Release(); initialized=false; return false; }
        registered=true; Status="Guide inscrito; entrega ainda não observada. Ctrl+Alt+Espaço disponível."; return true;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed=true;
        if (registered) {
            backend.Stop();
            if (!backend.Unregister()) { Status="Guide desativado; runtime retido até a saída por falha ao remover callback."; return; }
        }
        if (initialized) backend.Release();
        initialized=false; registered=false;
    }
}

public unsafe sealed class GameInputBackend : IGuideBackend
{
    private nint library,instance;
    private ulong token;
    private GCHandle context;
    private Action? onPress;
    private volatile bool active;
    public const int SystemButtonSlot=9, StopSlot=11, UnregisterSlot=12, FocusSlot=16;
    public static readonly Guid V2Id=new("BBAA66D2-837A-40F7-A303-917D500955F4");
    public GameInputBackend() { }
    internal GameInputBackend(nint testInstance) => instance=testInstance;
    public bool Initialize(out string reason)
    {
        reason="GameInput v2 indisponível. Use Ctrl+Alt+Espaço.";
        if(instance!=0) {reason="Interface v2 já inicializada.";return true;}
        foreach (var file in new[]{"GameInputRedist.dll","GameInput.dll"}) {
            var path=Path.Combine(Environment.SystemDirectory,file);
            if (!NativeLibrary.TryLoad(path,out library)) continue;
            if (!NativeLibrary.TryGetExport(library,"GameInputCreate",out var address)) { Release(); continue; }
            nint legacy=0;
            var create=(delegate* unmanaged[Stdcall]<nint*,int>)address;
            var hr=create(&legacy);
            if (hr>=0 && legacy!=0) {
                var table=*(nint**)legacy;
                var query=(delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)table[0];
                var id=V2Id; nint v2=0;
                hr=query(legacy,&id,&v2);
                ((delegate* unmanaged[Stdcall]<nint,uint>)table[2])(legacy);
                if (hr>=0 && v2!=0) { instance=v2; reason=$"GameInput v2 disponível ({file}); entrega Guide não verificada."; return true; }
            } else if (legacy!=0) { ((delegate* unmanaged[Stdcall]<nint,uint>)(*(nint**)legacy)[2])(legacy); }
            Release();
        }
        return false;
    }
    public bool Register(Action pressed,out string reason)
    {
        reason="Falha ao registrar Guide. Use Ctrl+Alt+Espaço.";
        if (instance==0) return false;
        onPress=pressed; context=GCHandle.Alloc(this); active=true;
        var table=*(nint**)instance;
        ((delegate* unmanaged[Stdcall]<nint,uint,void>)table[FocusSlot])(instance,0x80);
        ulong newToken=0;
        var register=(delegate* unmanaged[Stdcall]<nint,nint,uint,nint,delegate* unmanaged[Stdcall]<ulong,nint,nint,ulong,uint,uint,void>,ulong*,int>)table[SystemButtonSlot];
        var hr=register(instance,0,1,GCHandle.ToIntPtr(context),&Callback,&newToken);
        token=newToken;
        if (hr>=0 && token!=0) { reason="Guide inscrito, não verificado."; return true; }
        active=false;
        // Even a failed registration with a token must be drained before freeing its context/runtime.
        if (token!=0) { Stop(); if (!Unregister()) { reason="Falha ao registrar/remover Guide; runtime retido até saída."; return false; } }
        if (context.IsAllocated) context.Free();
        return false;
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static void Callback(ulong token,nint context,nint device,ulong timestamp,uint current,uint previous)
    {
        try { if (GCHandle.FromIntPtr(context).Target is GameInputBackend self && self.active && (current&1)!=0 && (previous&1)==0) self.onPress?.Invoke(); } catch { /* Never unwind managed exceptions across the native callback. */ }
    }
    public void Stop() { active=false; if (instance!=0 && token!=0) ((delegate* unmanaged[Stdcall]<nint,ulong,void>)(*(nint**)instance)[StopSlot])(instance,token); }
    public bool Unregister()
    {
        if (instance==0 || token==0) return true;
        var removed=((delegate* unmanaged[Stdcall]<nint,ulong,byte>)(*(nint**)instance)[UnregisterSlot])(instance,token)!=0;
        if (removed) { token=0; if (context.IsAllocated) context.Free(); onPress=null; }
        return removed;
    }
    public void Release()
    {
        if (token!=0) return; // Failed unregister: never unload a live callback or its context.
        if (context.IsAllocated) context.Free();
        if (instance!=0) { ((delegate* unmanaged[Stdcall]<nint,uint>)(*(nint**)instance)[2])(instance); instance=0; }
        if (library!=0) { NativeLibrary.Free(library); library=0; }
    }
}

// Explicit opt-in compatibility route authorized by the user. This ordinal is undocumented;
// compatibility remains empirical. Only Guide/status are consumed; ordinary input stays public.
public unsafe sealed class XInputGuideBackend : IGuideBackend
{
    [StructLayout(LayoutKind.Explicit,Size=16)]
    internal struct State { [FieldOffset(0)] internal uint Packet; [FieldOffset(4)] internal ushort Buttons; }
    private readonly Func<(nint Module,nint Address)> load;
    private readonly Action<nint> unload;
    private readonly bool[] armed=new bool[4];
    private nint module,address;private Action? pressed;private bool active,disposed;
    public bool Active=>active&&address!=0;
    public string Status{get;private set;}="Guide legado desligado. Atalho e combinação disponíveis.";
    public XInputGuideBackend():this(Load,NativeLibrary.Free) { }
    internal XInputGuideBackend(Func<(nint Module,nint Address)> load,Action<nint> unload){this.load=load;this.unload=unload;}
    private static (nint Module,nint Address) Load()
    {
        if(!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory,"XInput1_4.dll"),out var library))return default;
        return(library,GetProcAddress(library,(nint)100));
    }
    [DllImport("kernel32.dll",ExactSpelling=true)]private static extern nint GetProcAddress(nint module,nint ordinal);
    public bool Initialize(out string reason)
    {
        reason="Leitura Guide legada indisponível. Use atalho ou combinação.";
        if(disposed)return false;if(address!=0)return true;
        (module,address)=load();if(module!=0&&address!=0){reason="Leitura Guide legada disponível; entrega ainda não observada.";return true;}
        Status=reason;return false;
    }
    public bool Register(Action callback,out string reason)
    {
        reason="Guide legado indisponível.";if(disposed||address==0||active)return false;
        pressed=callback;Array.Clear(armed);active=true;Status=reason="Guide legado experimental ativo; aguardando soltar e pressionar.";return true;
    }
    public void Poll()
    {
        if(!Active||disposed)return;var edge=false;var connected=false;var read=(delegate* unmanaged[Stdcall]<uint,State*,uint>)address;
        for(uint slot=0;slot<4;slot++){
            State state=default;var result=read(slot,&state);
            if(result!=0){armed[slot]=false;continue;}connected=true;
            if((state.Buttons&0x0400)==0)armed[slot]=true;
            else if(armed[slot]){armed[slot]=false;edge=true;}
        }
        Status=connected?"Guide legado experimental ativo · toque para abrir ou fechar.":"Nenhum controle lido pelo Guide legado. Atalho e combinação disponíveis.";
        if(edge&&active)pressed?.Invoke();
    }
    public void Stop(){active=false;pressed=null;Array.Clear(armed);}
    public bool Unregister(){Stop();return true;}
    public void Release(){if(disposed)return;Stop();disposed=true;address=0;var owned=module;module=0;if(owned!=0)unload(owned);}
}

internal static class GuidePolling
{
    internal static bool Allowed(bool enabled,bool diagnostic,bool preview)=>enabled&&!diagnostic&&!preview;
    internal static int Interval(bool active,bool visible,bool connected)=>active?20:visible?30:connected?120:500;
    internal static bool Consume(ref bool guideRequest,bool chordRequest){var toggle=guideRequest||chordRequest;guideRequest=false;return toggle;}
}

using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ApolloOverlay;

public interface IPlatform : IDisposable
{
    bool IsPreview { get; }
    WindowRef? Foreground();
    IReadOnlyList<WindowRef> Windows();
    bool Valid(WindowRef window);
    IReadOnlyList<DisplayRef> Displays();
    ModuleEvidence SteamEvidence(WindowRef? window);
    Task<ActionResult> Switch(WindowRef window);
    Task<ActionResult> Close(WindowRef window,CancellationToken cancellation);
    Task<ActionResult> Shortcut(WindowRef window,string keys);
    ActionResult Launch(bool playnite,string? path);
}

public sealed class PreviewPlatform : IPlatform
{
    public bool IsPreview=>true;
    private readonly List<WindowRef> windows=[new(101,41,100,"Jornada entre estrelas · sessão de exemplo","sample"),new(102,42,200,"Navegador · exemplo","sample-browser"),new(103,43,300,"Música · exemplo","sample-music")];
    public WindowRef? Foreground()=>windows.FirstOrDefault();
    public IReadOnlyList<WindowRef> Windows()=>windows.ToArray();
    public bool Valid(WindowRef window)=>windows.Any(window.Same);
    public IReadOnlyList<DisplayRef> Displays()=>[new("TELA DE EXEMPLO",0,0,1920,1080,true),new("SEGUNDA TELA DE EXEMPLO",-1280,0,1280,720,false)];
    public ModuleEvidence SteamEvidence(WindowRef? window)=>ModuleEvidence.Unknown;
    public Task<ActionResult> Switch(WindowRef window)=>Task.FromResult(new ActionResult(ResultKind.Simulated,$"Voltaria para {window.Title}."));
    public Task<ActionResult> Close(WindowRef window,CancellationToken cancellation) {
        if (cancellation.IsCancellationRequested) return Task.FromResult(new ActionResult(ResultKind.Cancelled,"Nada foi enviado."));
        windows.RemoveAll(window.Same); return Task.FromResult(new ActionResult(ResultKind.Simulated,$"Fecharia somente {window.Title}."));
    }
    public Task<ActionResult> Shortcut(WindowRef window,string keys)=>Task.FromResult(new ActionResult(ResultKind.Simulated,$"Solicitaria {keys}; nenhum comando externo foi enviado."));
    public ActionResult Launch(bool playnite,string? path)=>new(ResultKind.Simulated,playnite ? "Solicitaria o Playnite em tela cheia." : "Solicitaria Steam Big Picture.");
    public void Dispose() { }
}

public sealed class WindowsPlatform : IPlatform,IWindowEffects
{
    public bool IsPreview=>false;
    private readonly Dictionary<nint,long> generations=[];
    private readonly Native.WinEvent destroyCallback;
    private readonly nint hook;
    private static readonly HashSet<string> ProtectedProcesses=new(StringComparer.OrdinalIgnoreCase) { "explorer","dwm","ShellExperienceHost","StartMenuExperienceHost","SearchHost","ApplicationFrameHost","GameBar","GameBarFTServer","GameBarPresenceWriter","gameoverlayui","XboxGameBarWidgets","steamwebhelper","winlogon","csrss" };
    public WindowsPlatform()
    {
        destroyCallback=(_,_,hwnd,obj,child,_,_)=> { if (hwnd!=0 && obj==0 && child==0) generations[hwnd]=generations.GetValueOrDefault(hwnd)+1; };
        hook=Native.SetWinEventHook(0x8001,0x8001,0,destroyCallback,0,0,0); // EVENT_OBJECT_DESTROY, OUTOFCONTEXT on this dispatcher.
    }
    private WindowRef? Read(nint hwnd)
    {
        if (hwnd==0 || !Native.IsWindow(hwnd)) return null;
        Native.GetWindowThreadProcessId(hwnd,out var pid);
        var title=new StringBuilder(513); Native.GetWindowText(hwnd,title,title.Capacity);
        if (title.Length==0) return null;
        var className=new StringBuilder(128); Native.GetClassName(hwnd,className,className.Capacity);
        try {
            using var process=Process.GetProcessById((int)pid);
            var name=process.ProcessName;
            var protectedTarget=pid==Environment.ProcessId || ProtectedProcesses.Contains(name) || className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd";
            return new(hwnd,(int)pid,process.StartTime.ToUniversalTime().Ticks,title.ToString(),name,protectedTarget,generations.GetValueOrDefault(hwnd));
        } catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException) { return new(hwnd,(int)pid,0,title.ToString(),"identidade indisponível",true); }
    }
    public WindowRef? Foreground()=>Read(Native.GetForegroundWindow());
    public IReadOnlyList<WindowRef> Windows()
    {
        var windows=new List<WindowRef>();
        Native.EnumWindows((hwnd,_)=> { if (Native.IsWindowVisible(hwnd) && Native.GetWindow(hwnd,4)==0 && Read(hwnd) is { Protected:false } window) windows.Add(window); return true; },0);
        return windows;
    }
    public bool Valid(WindowRef window)=>window.Same(Read(window.Handle)) && !window.Protected && hook!=0;
    public IReadOnlyList<DisplayRef> Displays()
    {
        var displays=new List<DisplayRef>();
        Native.EnumDisplayMonitors(0,0,(monitor,_,_,_)=> { var info=new Native.MonitorInfo { Size=Marshal.SizeOf<Native.MonitorInfo>(),Device="" }; if (Native.GetMonitorInfo(monitor,ref info)) displays.Add(new(info.Device,info.Monitor.Left,info.Monitor.Top,info.Monitor.Right-info.Monitor.Left,info.Monitor.Bottom-info.Monitor.Top,(info.Flags&1)!=0)); return true; },0);
        return displays;
    }
    public ModuleEvidence SteamEvidence(WindowRef? window)
    {
        if (window is null || !Valid(window)) return ModuleEvidence.Unknown;
        try {
            using var process=Process.GetProcessById(window.Pid);
            foreach (ProcessModule module in process.Modules) {
                var loaded=module.ModuleName.Equals("gameoverlayrenderer.dll",StringComparison.OrdinalIgnoreCase) || module.ModuleName.Equals("gameoverlayrenderer64.dll",StringComparison.OrdinalIgnoreCase);
                if (loaded) return Valid(window) ? ModuleEvidence.Loaded : ModuleEvidence.Unknown;
            }
            return ModuleEvidence.Absent;
        } catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException) { return ModuleEvidence.Denied; }
    }
    public Task<ActionResult> Switch(WindowRef window)=>new GuardedActions(this).Switch(window);
    public Task<ActionResult> Close(WindowRef window,CancellationToken cancellation)=>new GuardedActions(this).Close(window,cancellation);
    public Task<ActionResult> Shortcut(WindowRef window,string keys)=>new GuardedActions(this).Shortcut(window,keys);
    public WindowObservation ObserveWindow(WindowRef window)=>ClosureObservation.Classify(window,Probe(window.Handle));
    private WindowProbe Probe(nint hwnd)
    {
        var generation=generations.GetValueOrDefault(hwnd);
        if(!Native.IsWindow(hwnd)) return new(false,null,null,generation,"");
        Native.GetWindowThreadProcessId(hwnd,out var pid);
        var caption=new StringBuilder(513);Native.GetWindowText(hwnd,caption,caption.Capacity);
        try {
            using var process=Process.GetProcessById((int)pid);
            return new(true,(int)pid,process.StartTime.ToUniversalTime().Ticks,generation,caption.ToString());
        } catch(Exception e) when(e is Win32Exception or InvalidOperationException or ArgumentException) {
            return new(Native.IsWindow(hwnd),pid==0 ? null : (int)pid,null,generation,caption.ToString());
        }
    }
    public bool IsForeground(WindowRef window)=>Native.GetForegroundWindow()==window.Handle;
    public bool RequestFocus(WindowRef window) { Native.ShowWindow(window.Handle,9);return Native.SetForegroundWindow(window.Handle); }
    public bool PostClose(WindowRef window)=>Native.PostMessage(window.Handle,0x10,0,0);
    public Task Wait(int milliseconds)=>Task.Delay(milliseconds);
    public uint SendKeys(string keys)
    {
        ushort modifier=keys switch { "Win+Tab" or "Win+G" => 0x5B, "Ctrl+Tab" => 0x11, _=>0x10 };
        ushort key=keys=="Win+G" ? (ushort)0x47 : (ushort)0x09;
        Native.Input[] inputs=[new(){Type=1,Key=modifier},new(){Type=1,Key=key},new(){Type=1,Key=key,Flags=2},new(){Type=1,Key=modifier,Flags=2}];
        var sent=Native.SendInput(4,inputs,40);
        if (sent!=4) Native.SendInput(2,[new(){Type=1,Key=key,Flags=2},new(){Type=1,Key=modifier,Flags=2}],40);
        return sent;
    }
    public ActionResult Launch(bool playnite,string? path)
    {
        try {
            ProcessStartInfo info;
            if (playnite) {
                if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path) || !Path.GetFileName(path).Equals("Playnite.FullscreenApp.exe",StringComparison.OrdinalIgnoreCase)) return new(ResultKind.Unavailable,"Escolha Playnite.FullscreenApp.exe em Ajustes.");
                info=new(path){UseShellExecute=false}; info.ArgumentList.Add("--startfullscreen");
            } else info=new("steam://open/bigpicture"){UseShellExecute=true};
            using var process=Process.Start(info);
            return new(ResultKind.Requested,playnite ? "Inicialização do Playnite solicitada; modo e foco ainda não confirmados." : "URI do Steam enviada; Big Picture ainda não confirmado.");
        } catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException) { return new(ResultKind.Failed,"O Windows não aceitou a inicialização. Verifique a instalação em Ajustes."); }
    }
    public void Dispose() { if (hook!=0) Native.UnhookWinEvent(hook); GC.KeepAlive(destroyCallback); }
}

using System.IO;
using Microsoft.Win32;

namespace ApolloOverlay;

public sealed class OverlayController : IDisposable
{
    public IPlatform Platform {get;}
    public LocalDiagnostics Diagnostics {get;}
    public UserSettings Settings {get;private set;}
    public UserSettings SavedSettings {get;private set;}
    public bool SettingsDirty=>Settings!=SavedSettings;
    public OverlayOperation? Operation {get;private set;}
    public void SetOperation(string state,string message,string action="",WindowRef? target=null,bool dispatched=false)=>Operation=new(state,message,action,target,dispatched);
    public void DismissOperation()=>Operation=null;
    public void Discard()=>Settings=SavedSettings;
    public SessionTracker Session {get;}=new();
    public WindowRef? ReturnTarget {get;private set;}
    public IReadOnlyList<WindowRef> Apps {get;private set;}=[];
    public IReadOnlyList<DisplayRef> Displays {get;private set;}=[];
    public ModuleEvidence SteamEvidence {get;private set;}
    public SteamPermission Steam {get;}=new();
    public bool SteamAvailable {get;private set;}
    public string Status {get;private set;}
    private long revision;private bool disposed;private CancellationTokenSource? refresh;
    public OverlayController(IPlatform? platform=null,LocalDiagnostics? diagnostics=null)
    {
        Platform=platform??new WindowsPlatform();Diagnostics=diagnostics??new();if(Platform.IsPreview){Settings=new();Status="Prévia · ações de janela simuladas";}else{Settings=Diagnostics.Load(out var status);Status=status;}
        if(!Platform.IsPreview&&Settings.PlaynitePath is null){var candidates=new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Playnite","Playnite.FullscreenApp.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Playnite","Playnite.FullscreenApp.exe")};Settings=Settings with{PlaynitePath=candidates.FirstOrDefault(File.Exists)};}SavedSettings=Settings;
    }
    public void CaptureReturn(){ReturnTarget=Platform.Foreground();Session.ClearIfStale(Platform.Valid);Session.Observe(ReturnTarget);}
    public IReadOnlyList<DisplayRef> RefreshDisplays()=>Displays=Platform.Displays();
    public async Task Refresh()
    {
        refresh?.Cancel();refresh?.Dispose();refresh=new();var token=refresh.Token;var epoch=++revision;var target=Session.Anchor;
        var result=await Task.Run(()=>{token.ThrowIfCancellationRequested();var apps=Platform.Windows();var evidence=Platform.SteamEvidence(target);return(apps,evidence);},token);
        if(disposed||token.IsCancellationRequested||epoch!=revision)return;
        Apps=result.apps;RefreshDisplays();SteamEvidence=result.evidence;Session.ClearIfStale(Platform.Valid);if(target is not null&&!target.Same(Session.Anchor))Steam.Invalidate();if(SteamEvidence!=ModuleEvidence.Loaded)Steam.Invalidate();
        if(!Platform.IsPreview){try{using var registration=Registry.ClassesRoot.OpenSubKey("steam");SteamAvailable=registration is not null;}catch(System.Security.SecurityException){SteamAvailable=false;}}
    }
    public void Select(WindowRef window){Session.Select(window);Steam.Invalidate();}
    public void Change(UserSettings value){_=UserSettings.Parse(System.Text.Json.JsonSerializer.Serialize(value,SettingsJson.Default.UserSettings));Settings=value;}
    public void Save(){if(Platform.IsPreview){Status="Prévia · nada foi salvo; alterações apenas nesta sessão.";return;}Diagnostics.Save(Settings);SavedSettings=Settings;Status="Ajustes salvos em AppData.";}
    public async Task<ActionResult> Launch(bool playnite)
    {
        if(disposed)return new(ResultKind.Cancelled,"Overlay encerrada; nenhuma inicialização enviada.");
        var existing=Apps.FirstOrDefault(w=>playnite?w.ProcessName.Equals("Playnite.FullscreenApp",StringComparison.OrdinalIgnoreCase):w.ProcessName.Equals("steam",StringComparison.OrdinalIgnoreCase));
        if(playnite&&existing is not null&&Platform.Valid(existing))return await Platform.Switch(existing);
        if(!playnite&&existing is not null&&Platform.Valid(existing)){var focused=await Platform.Switch(existing);if(disposed)return new(ResultKind.Cancelled,"Overlay encerrada durante a ativação; nenhuma inicialização enviada.");if(focused.Kind!=ResultKind.Completed&&focused.Kind!=ResultKind.Simulated)return focused;}
        if(!playnite&&!SteamAvailable&&!Platform.IsPreview)return new(ResultKind.Unavailable,"Steam não instalada ou protocolo indisponível.");
        return Platform.Launch(playnite,Settings.PlaynitePath);
    }
    public void Dispose(){if(disposed)return;disposed=true;revision++;refresh?.Cancel();refresh?.Dispose();Platform.Dispose();}
}
public sealed record OverlayOperation(string State,string Message,string Action,WindowRef? Target,bool Dispatched);

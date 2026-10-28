using ApolloOverlay.Automation;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using System.Windows.Input;

namespace ApolloOverlay;

public partial class MainWindow
{
    private sealed record SteamOwner(long Generation,string Id,WindowRef Target,CancellationTokenSource Cancellation,long Deadline,Func<bool> Context,string Origin){public bool InputQualified=Origin=="UI";public int OwnedKeys;public OverlayOperation? PendingRecord;public readonly TaskCompletionSource Drained=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    private SteamOwner? steamOwner;private bool steamHiding;private long automationRevision,automationTargetsAt;private IReadOnlyList<WindowRef>? automationObservedApps;private AutomationHost? automationHost;private WindowRef? automationGrantTarget;
    private void NoteAutomationTargets(){if(!ReferenceEquals(automationObservedApps,Controller.Apps)){automationObservedApps=Controller.Apps;automationTargetsAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();}}
    internal Func<ReleaseSample>? AutomationInputReader {get;set;}
    private static string TargetId(WindowRef target)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{target.Handle}:{target.Pid}:{target.Started}:{target.Generation}")))[..32];
    private WindowRef[] AutomationWindows()=>Controller.Apps.Concat(new[]{Controller.Session.Anchor,Controller.ReturnTarget}.OfType<WindowRef>()).DistinctBy(TargetId).Where(Controller.Platform.Valid).Take(64).ToArray();
    private ReleaseSample SteamSample()
    {
        var owned=steamOwner?.OwnedKeys??0;
        bool KeyboardNeutral()=>heldKeys.All(k=>SteamKeyOwned(KeyInterop.VirtualKeyFromKey(k),owned))&&PhysicalKeysNeutral(owned);
        if(AutomationInputReader is{} fixture){var sample=fixture();return sample with{KeysNeutral=sample.KeysNeutral&&KeyboardNeutral()};}var at=Environment.TickCount64;guideBackend?.PollForOverlay(IsVisible,focusCycle,at,Controller.Settings.GuideOpenMaxHoldMs);
        return new(Enumerable.Range(0,4).Select(i=>Native.ReadPad((uint)i)).ToArray(),at,KeyboardNeutral(),guideBackend?.Held==true,guideBackend is null||!guideBackend.Active||guideBackend.SnapshotValid,guideBackend?.Connections??0);
    }
    private bool Neutral(ReleaseSample sample,long now)=>sample.Pads.Count==4&&sample.GuideValid&&!sample.GuideHeld&&!sample.Pads.Any(p=>p.ReadFailed)&&sample.Timestamp<=now&&now-sample.Timestamp<=80&&sample.KeysNeutral&&sample.Pads.All(p=>!p.Connected||(p.Buttons==0&&Math.Abs((int)p.X)<Controller.Settings.Deadzone*2/3&&Math.Abs((int)p.Y)<Controller.Settings.Deadzone*2/3&&Math.Abs((int)p.RX)<Controller.Settings.Deadzone*2/3&&Math.Abs((int)p.RY)<Controller.Settings.Deadzone*2/3&&p.LT<30&&p.RT<30));
    private void CancelSteamOwner(string reason)
    {
        if(steamOwner is not{} old||old.Cancellation.IsCancellationRequested)return;operationGeneration++;old.Cancellation.Cancel();UpdatePolling();
    }
    private bool SteamAlive(SteamOwner owner)=>ReferenceEquals(steamOwner,owner)&&owner.Generation==operationGeneration&&!exiting&&!closeRequested&&!ownedDialog&&!owner.Cancellation.IsCancellationRequested&&Environment.TickCount64<=owner.Deadline&&owner.Context()&&Controller.Platform.Valid(owner.Target);
    private void QueueSteam(WindowRef target,Func<bool> context)=>QueueHandoff(()=>OperationTask=RunUiSteam(target,context),()=>context()&&Controller.SteamOverlayEligible(target));
    private async Task RunUiSteam(WindowRef target,Func<bool> context)
    {
        var id=automationHost is null?"ui:"+Guid.NewGuid().ToString("N"):automationHost.BeginUi("steam-overlay",TargetId(target));if(id is null){RetainOperation("Falhou","Não foi possível registrar a intenção; nenhum efeito enviado.");return;}
        var generation=operationGeneration+1;try{var result=await RunSteam(target,context,id,"UI",false,CancellationToken.None,phase=>automationHost?.UiResult(id,TargetId(target),phase));automationHost?.UiResult(id,TargetId(target),result);}catch(Exception e)when(e is System.IO.IOException or UnauthorizedAccessException){if(!exiting&&generation==operationGeneration)RetainOperation("Não confirmado","Registro indisponível; não haverá repetição automática.");}
    }
    private async Task<AutomationReply> RunSteam(WindowRef target,Func<bool> context,string id,string origin,bool focusOnly,CancellationToken cancellation,Action<AutomationReply> phase)
    {
        if(busy||exiting||closeRequested||steamOwner is not null)return AutomationReply.Error("Busy","Outra operação está ativa ou drenando.",id);
        var cts=CancellationTokenSource.CreateLinkedTokenSource(cancellation);cts.CancelAfter(10000);var owner=new SteamOwner(++operationGeneration,id,target,cts,Environment.TickCount64+10000,context,origin);steamOwner=owner;pending=cts;busy=true;UpdatePolling();
        var initial=SteamSample();var connections=initial.Pads.Select((p,i)=>(p,i)).Where(x=>x.p.Connected).Aggregate(0,(mask,x)=>mask|1<<x.i);var guides=initial.GuideConnections;
        bool Continue(){if(!SteamAlive(owner))return false;var sample=SteamSample();var mask=sample.Pads.Select((p,i)=>(p,i)).Where(x=>x.p.Connected).Aggregate(0,(bits,x)=>bits|1<<x.i);return (connections&~mask)==0&&(guides&~sample.GuideConnections)==0&&Neutral(sample,Environment.TickCount64);}
        var previous=Controller.Platform.LastShortcutDispatch?.Revision;
        AutomationReply Receipt(ActionResult result){
            var evidence=Controller.Platform.LastShortcutDispatch;var fresh=evidence is{} dispatch&&dispatch.Revision!=previous;var accepted=fresh?evidence!.Value.Accepted:0;
            bool? sent=accepted is null?(evidence?.Sequence?.Events.Any(e=>!e.Cleanup&&e.Accepted==1)==true?true:null):accepted>0;
            return new(){RequestId=id,Outcome=result.Kind.ToString(),Reason=result.Message,Dispatched=sent,Expected=fresh?evidence!.Value.Expected:focusOnly?0:4,Accepted=accepted,CleanupExpected=fresh?evidence!.Value.CleanupExpected:null,CleanupAccepted=fresh?evidence!.Value.CleanupAccepted:null,Sequence=fresh?evidence!.Value.Sequence:null};
        }
        try{
            if(origin=="CLI"){
                var gate=new ReleaseGate();while(SteamAlive(owner)){var sample=SteamSample();var state=gate.Update(sample,Environment.TickCount64,true,Controller.Settings.Deadzone);if(state==ReleaseState.Cancelled)return AutomationReply.Error("Cancelled","Entrada desconhecida ou conexão perdida.",id);if(state==ReleaseState.Ready){owner.InputQualified=true;break;}await Task.Delay(10,cts.Token);}
            }
            if(!Continue())return AutomationReply.Error("Cancelled","Entrada ou alvo não confirmado; nenhum foco/atalho enviado.",id);
            RetainOperation("Pendente","Focando o jogo; nenhum atalho enviado.","steam-overlay",target,false);owner.PendingRecord=Controller.Operation;phase(new(){Outcome="Pending",Reason="Focusing",RequestId=id,Terminal=false,Dispatched=false});
            steamHiding=true;try{HideNow(false,preserveSteam:true);}finally{steamHiding=false;}
            var result=focusOnly?await Controller.Platform.Focus(target,cts.Token,Continue):await Controller.Platform.Shortcut(target,Controller.Settings.SteamShortcut,cts.Token,Continue,dispatch=>{owner.OwnedKeys=dispatch.Sequence?.PossiblyHeld??0;phase(Receipt(new(ResultKind.Requested,"Sequência em andamento; resultado ainda não terminal.")) with{Outcome="Pending",Terminal=false});});
            var reply=Receipt(result);
            if(ReferenceEquals(steamOwner,owner)&&owner.PendingRecord is not null&&ReferenceEquals(Controller.Operation,owner.PendingRecord)&&!exiting&&!closeRequested){RecordResult(result,focusOnly?"focus":"steam-overlay",target);if(Controller.Operation is{} operation)Controller.SetOperation(operation.State,operation.Message,operation.Action,operation.Target,reply.Dispatched==true);UpdatePersistentState();}
            return reply;
        }catch(OperationCanceledException){return Receipt(new(ResultKind.Cancelled,"Solicitação interrompida; consulte contagens e liberação antes de nova tentativa."));}
        finally{if(ReferenceEquals(steamOwner,owner)){steamOwner=null;if(ReferenceEquals(pending,cts))pending=null;busy=false;UpdatePolling();}cts.Dispose();owner.Drained.TrySetResult();}
    }
    private static readonly HashSet<string> SynchronousActions=new(StringComparer.Ordinal){"settings","apps","audio","session-options","choose-session","clear-session","select-session","details","back","back-header","integrations","customize","setup-invitation","setup-next","setup-back","setup-skip","setup-finish","save","save-pending","discard-pending","display","ui-scale","side","deadzone","shortcut","guide-limit","sound-feedback","haptic-feedback","volume","volume-down","mute"};
    private string Capability(Entry entry)=>entry.Id is "close" or "close-detail" or "confirm-hold"?"RequiresGesture":entry.Id is "steam-overlay" or "test-steam"?(Controller.Platform.CanSteamMenu(Controller.ReturnTarget)&&entry.Id=="steam-overlay"?"RequiresInteractive":"Execute"):SynchronousActions.Contains(entry.Id)||view.Current.ChooseSession&&entry.Id.StartsWith("window:",StringComparison.Ordinal)||entry.Id.StartsWith("options:",StringComparison.Ordinal)||entry.Id.StartsWith("favorite:",StringComparison.Ordinal)?"Execute":"RequiresInteractive";
    internal AutomationSnapshot AutomationSnapshot()=>new("",Environment.ProcessId,System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),automationRevision,view.Current.Route.ToString(),IsVisible,busy,AutomationWindows().Select(w=>new AutomationTarget(TargetId(w),w.Handle.ToInt64(),w.Pid,w.Started,w.Generation,w.ProcessName)).ToArray(),entries.Select(e=>new AutomationAction(e.Id,e.Available,e.Reason,Capability(e),e.Id is "volume" or "guide-limit"?["activate","left","right"]:["activate"])).Concat(new[]{"home","apps","system","audio","integrations","options","details","customize","setup","result"}.Select(r=>new AutomationAction("route:"+r,!busy,"Navegação de rota explícita","Execute",["activate"]))).Concat([new("focus",!busy,"Foco do alvo validado","Execute",["activate"]),new("steam-overlay",!busy,"Atalho configurado; sem garantia de interface visível","Execute",["activate"]) ]).DistinctBy(a=>a.Id).ToArray(),"unknown",PendingRequestId:steamOwner?.Id??"",Phase:Controller.Operation?.State??"",TargetsAtUtc:automationTargetsAt);
    internal async Task<AutomationReply> AutomationExecute(AutomationRequest request,CancellationToken token,Action<AutomationReply> phase,Func<bool> continuing)
    {
        if(token.IsCancellationRequested||!continuing())return AutomationReply.Error("Cancelled","Cancelado.");if(request.Preset.Length>0)return AutomationReply.Error("Unsupported","Preset não integrado.");if(request.Revision!=automationRevision)return AutomationReply.Error("Rejected","Revisão de contexto mudou.");if(busy||TransitionPending||Confirming)return AutomationReply.Error("Busy","Operação ou interação ativa.");
        if(request.Intent!="activate"&&request.ActionId is not("volume" or "guide-limit"))return AutomationReply.Error("Unsupported","Intenção não disponível neste controle.");
        var target=AutomationWindows().FirstOrDefault(w=>TargetId(w)==request.TargetId);var entry=entries.FirstOrDefault(e=>e.Id==request.ActionId);
        if(request.TargetId.Length>0&&target is null)return AutomationReply.Error("Rejected","Identidade do alvo mudou; obtenha targets novamente.");
        if(request.ActionId is "focus" or "steam-overlay" or "test-steam"){
            target??=request.ActionId=="test-steam"?Controller.Session.Anchor:Controller.ReturnTarget;
            if(target is null||!Controller.Platform.Valid(target)||target.Protected||target.Launcher)return AutomationReply.Error("Unavailable","Alvo exato indisponível.");
            if(request.ActionId!="focus"&&!Controller.SteamOverlayEligible(target))return AutomationReply.Error("Unavailable","Contexto Steam não confirmado.");
            if(request.DryRun)return AutomationReply.Error("DryRun","Capacidade disponível; nenhum efeito ou escrita solicitado.");
            var captured=target;var result=RunSteam(captured,()=>continuing()&&request.Revision==automationRevision&&(Controller.Apps.Any(captured.Same)||captured.Same(Controller.Session.Anchor)||captured.Same(Controller.ReturnTarget)),request.RequestId,"CLI",request.ActionId=="focus",token,phase);OperationTask=result;return await result;
        }
        if(request.ActionId.StartsWith("route:",StringComparison.Ordinal)){
            var route=request.ActionId[6..];if(!new[]{"home","apps","system","audio","integrations","options","details","customize","setup","result"}.Contains(route))return AutomationReply.Error("Rejected","Rota desconhecida.");
            if(route is "options" or "details"&&target is null)return AutomationReply.Error("Unavailable","Informe targetId válido.");if(request.DryRun)return AutomationReply.Error("DryRun","Rota disponível; sem alteração.");
            switch(route){case "home":Page(0);break;case "apps":Page(1);break;case "system":Page(2);break;case "options":OpenOptions(target!);break;case "details":OpenView(new(OverlayRoute.Details,target,"switch-detail"));break;default:OpenView(new(route switch{"audio"=>OverlayRoute.WindowsAudio,"integrations"=>OverlayRoute.Integrations,"customize"=>OverlayRoute.Customize,"setup"=>OverlayRoute.Setup,_=>OverlayRoute.OperationResult}));break;}
            return AutomationReply.Error("Completed","Rota atualizada.");
        }
        if(entry is null)return AutomationReply.Error("Unavailable","ID não registrado nesta rota.");if(!entry.Available)return AutomationReply.Error("Unavailable",entry.Reason);var capability=Capability(entry);if(capability!="Execute")return AutomationReply.Error(capability,"Use interação normal; esta ação não tem continuação CLI cancelável.");
        if(request.Intent!="activate"&&entry.Id is not("volume" or "guide-limit"))return AutomationReply.Error("Unsupported","Intenção não disponível neste controle.");if(request.DryRun)return AutomationReply.Error("DryRun","Handler disponível; nenhum efeito ou escrita solicitado.");token.ThrowIfCancellationRequested();if(!continuing())return AutomationReply.Error("Cancelled","Cliente não está mais conectado.");var previousOperation=Controller.Operation;var selected=view.Current.ChooseSession&&entry.Id.StartsWith("window:",StringComparison.Ordinal)?Controller.Apps.FirstOrDefault(w=>WindowId(w)==entry.Id):null;if(view.Current.ChooseSession&&entry.Id.StartsWith("window:",StringComparison.Ordinal)&&(selected is null||!Controller.Platform.Valid(selected)))return AutomationReply.Error("Unavailable","Janela da seleção mudou.");navigation=new(entry.Id);
        if(request.Intent is "left" or "right"){if(entry.Id=="volume")AdjustVolume(request.Intent=="left"?-.05f:.05f);else AdjustGuideLimit(request.Intent=="left"?NavDirection.Left:NavDirection.Right);}else entry.Run();
        if(selected is not null)return AutomationReply.Error(selected.Same(Controller.Session.Anchor)?"Completed":"Failed","Resultado da seleção observado.");
        if(!ReferenceEquals(previousOperation,Controller.Operation)&&Controller.Operation is{} operation)return new(){Outcome=operation.State switch{"Concluído"=>"Completed","Solicitado"=>"Requested","Simulado"=>"Simulated","Falhou"=>"Failed","Não confirmado"=>"Unknown","Parcial"=>"Partial","Cancelado"=>"Cancelled","Indisponível"=>"Unavailable",_=>"Unknown"},Reason=operation.Message,Dispatched=operation.Dispatched};
        return AutomationReply.Error("Completed","Handler síncrono concluído.");
    }
    internal async Task StartAutomation(string? pipeName=null,AutomationGrant? grant=null)
    {
        Controller.CaptureReturn();if(automationGrantTarget is not null)Controller.Select(automationGrantTarget);await Controller.Refresh();RefreshRows();
        try
        {
            automationHost=new(new WindowAutomation(this),System.IO.Path.Combine(Controller.Diagnostics.Root,"automation",System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString()),pipeName,grant);
        }
        catch(Exception error) when(error is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.SecurityException)
        {
            // Never discard or replace an unreadable ledger: older effects may already have occurred.
            if(grant is not null)throw new InvalidOperationException("Ponte de automação indisponível; histórico preservado.",error);
            Controller.Diagnostics.Log("automation-start",ResultKind.Unavailable);
            RetainOperation("Indisponível","Automação indisponível. O histórico foi preservado; a overlay continua disponível.");
        }
    }
    internal AutomationGrant ResolveAutomationGrant(int pid,long started,long handle,bool shutdown)
    {
        var target=Controller.Platform.Windows().SingleOrDefault(w=>w.Pid==pid&&w.Started==started&&w.Handle.ToInt64()==handle);
        if(target is null||target.Protected||target.Launcher||!Controller.Platform.Valid(target))throw new InvalidOperationException("Concessão não corresponde a uma janela atual válida.");automationGrantTarget=target;return new(TargetId(target),shutdown);
    }
    private sealed class WindowAutomation(MainWindow window):IAutomationBackend
    {
        public async Task<AutomationReply> Update(AutomationRequest request,CancellationToken token,Func<bool> continuing)=>await await window.Dispatcher.InvokeAsync(()=>window.AgentUpdate(request,token,continuing),DispatcherPriority.Normal,token);
        public async Task CommitUpdate(AutomationRequest request,CancellationToken token,Func<bool> continuing)=>await await window.Dispatcher.InvokeAsync(()=>window.CommitAgentUpdate(request,token,continuing),DispatcherPriority.Normal,token);
        public async Task<AutomationSnapshot> Snapshot(CancellationToken token,bool refreshTargets=false)=>await await window.Dispatcher.InvokeAsync(async()=>{if(refreshTargets&&!window.busy){await window.Controller.Refresh();token.ThrowIfCancellationRequested();if(!window.busy)window.RefreshRows();}return window.AutomationSnapshot();},DispatcherPriority.Normal,token);
        public async Task<AutomationReply> Execute(AutomationRequest request,CancellationToken token,Action<AutomationReply> phase,Func<bool> continuing)=>await await window.Dispatcher.InvokeAsync(()=>window.AutomationExecute(request,token,phase,continuing),DispatcherPriority.Normal,token);
        public async Task Shutdown(CancellationToken token,Func<bool> continuing)=>await window.Dispatcher.InvokeAsync(()=>{token.ThrowIfCancellationRequested();if(continuing())window.ExitFromTray();},DispatcherPriority.Normal,token);
        public async Task<bool> Cancel(string id,string? grantedTarget,CancellationToken token)=>await window.Dispatcher.InvokeAsync(()=>{token.ThrowIfCancellationRequested();if(window.steamOwner is not{} owner||owner.Id!=id||grantedTarget is not null&&TargetId(owner.Target)!=grantedTarget)return false;window.CancelSteamOwner("Cancelamento solicitado pela CLI; nenhum novo atalho enviado.");return true;},DispatcherPriority.Normal,token);
    }
}


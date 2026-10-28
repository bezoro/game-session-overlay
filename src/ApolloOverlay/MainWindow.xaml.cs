using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ApolloOverlay;

internal readonly record struct OverlayInputContext(bool Confirmed,bool Visible,bool Active,bool OwnForeground,bool OwnedDialog=false,bool Exiting=false);

public partial class MainWindow : Window
{
    internal readonly OverlayController Controller;
    internal Action<string>? ResultNotificationRequest {private get;set;}
    private TextBox? operationText;
    private readonly OverlayViewState view=new();
    private readonly IApplicationAudio applicationAudio;
    private readonly IControllerDevices controllerDevices;
    private readonly IFeedbackService feedbackService;
    private ApplicationAudioSnapshot appAudio=new(null,0,0,ApplicationAudioAvailability.Unavailable,ApplicationMuteState.Unknown,0,0,"Áudio indisponível");
    private WindowRef? audioTarget;private long audioGeneration,audioRevision,operationGeneration,nextBatteryRead;private uint? activeSlot;private long activeEpoch;private int connectedControllers;private string controllerStatus="Aguardando leitura do controle";
    internal Func<OverlayInputContext>? InputContextReader {private get;set;}
    private OverlayInputContext InputContext=>InputContextReader?.Invoke()??new(focusConfirmed,IsVisible,IsActive,OwnForeground,ownedDialog,exiting);
    internal Func<int,short> PhysicalKeyReader {private get;set;}=Native.GetAsyncKeyState;
    internal Func<ModifierKeys> KeyboardModifiersReader {private get;set;}=static()=>Keyboard.Modifiers;
    internal Func<nint,uint> DpiReader {private get;set;}=Native.GetDpiForWindow;
    internal Func<nint> FocusForeground {private get;set;}=Native.GetForegroundWindow;
    internal Func<nint,bool> FocusRequest {private get;set;}=Native.SetForegroundWindow;
    internal Func<nint,nint,CancellationToken,Task<bool>>? FocusFallbackRequest {private get;set;}
    private CancellationTokenSource? activationCancellation;
    internal Func<Task>? RefreshFocusFixture {private get;set;}
    internal Func<Task<string?>>? PlayniteDialogFixture {private get;set;}
    internal Task OpeningTask {get;private set;}=Task.CompletedTask;
    internal Task DialogTask {get;private set;}=Task.CompletedTask;
    private long focusCycle;private bool focusConfirmed,navigationKeysArmed=true;
    private long nextConfirmationScroll;
    private readonly WindowsAudio audio=new();private InputCoordinator input;private UserSettings appliedSettings;private readonly CloseHold closeHold=new();private readonly DispatcherTimer timer;
    private bool revealPending,compactChrome;
    private bool fadingOut,fadeInterrupted;private long fadeGeneration;
    private int fadePadConnections,fadeGuideConnections;
    internal bool ForceFadeFixture {private get;set;}
    internal Func<PadFrame[]>? FadeFramesFixture {private get;set;}
    internal bool FadePending=>fadingOut;
    internal void BeginFadeFixture()=>HideNow(false);
    private bool pointerDown,pointerCaptured,narrow;private string inputMethod="keyboard";
    private MouseButton? recoveryMouseRelease;
    private readonly bool diagnostic;private HwndSource? source;private TrayHost? tray;private bool busy,exiting,opening,keyboardDown;private int? closeSource,dispatchSource;private CancellationTokenSource? pending;
    private XInputGuideBackend? guideBackend;private GuideConnection? guideConnection;private bool guideToggleRequested;
    private CancellationTokenSource? effectCancellation;private WindowRef? effectFocusTarget;private bool effectFocusTransferred;private int effectPadConnections,effectGuideConnections;
    private readonly ReleaseGate releaseGate=new();private readonly HashSet<Key> heldKeys=[];private readonly bool[] handoffPadsHeld=new bool[4],handoffPadsKnown=new bool[4];private Action? handoff;private Func<bool>? handoffValid;private bool handoffDismiss,handoffDegraded,degradedExplicit,handoffGuideHeld,handoffGuideKnown,committing;private long? degradedSince;private ReleaseSample? lastInputSample;
    internal ReleaseSample? HandoffSampleFixture {set=>lastInputSample=value;}
    internal Func<PadFrame[]>? EffectFramesFixture {private get;set;}
    internal bool TransitionPending=>handoff is not null;
    internal static Key NormalizeKey(Key key,Key system,Key ime,Key dead)
    {
        var actual=key switch{Key.System=>system,Key.ImeProcessed=>ime,Key.DeadCharProcessed=>dead,_=>key};
        return actual is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed?Key.None:actual;
    }
    private void QueueHandoff(Action action,Func<bool>? valid=null,bool dismiss=false)
    {
        if(exiting||busy)return;if(handoff is not null){if(!dismiss||handoffDismiss)return;CancelHandoff();}handoff=action;handoffValid=valid;handoffDismiss=dismiss;releaseGate.Reset();if(lastInputSample is {} sample&&sample.Timestamp<=Environment.TickCount64){releaseGate.Prime(sample);CaptureHandoffBaseline(sample);ObserveHandoffInputs(sample,Environment.TickCount64);}CancelHold();input.Cancel();if(!dismiss)RetainOperation("Pendente","Solte os controles para continuar. B / Esc cancela antes do envio.");FeedbackText(dismiss?"Solte os controles para ocultar o painel.":"Solte os controles para continuar · B / Esc cancela.");
    }
    private void CancelHandoff(bool terminalize=true){
        var cancelled=terminalize&&handoff is not null&&!handoffDismiss&&Controller.Operation is {State:"Pendente",Dispatched:false};
        handoff=null;handoffValid=null;handoffDismiss=false;handoffDegraded=false;degradedExplicit=false;degradedSince=null;handoffGuideHeld=false;handoffGuideKnown=false;Array.Clear(handoffPadsHeld);Array.Clear(handoffPadsKnown);releaseGate.Reset();
        if(cancelled)RetainOperation("Cancelado","Solicitação cancelada antes do envio; nenhum comando externo enviado.");}
    private void RejectHandoff(string message)
    {
        if(!handoffDismiss){CancelHandoff(false);input.Cancel();RetainOperation("Cancelado",message);FeedbackText(message);return;}if(handoffDegraded)return;
        handoffDegraded=true;handoffValid=null;handoff=CompleteDegradedDismiss;degradedSince=null;releaseGate.Reset();input.Cancel();FeedbackText("Leitura ou contexto incerto. Solte os controles para ocultar sem retornar ao aplicativo.");
    }
    private void CompleteDegradedDismiss(){HideNow(false);FeedbackText("Painel ocultado sem solicitar retorno ao aplicativo.");if(!Controller.Platform.IsPreview)Controller.Diagnostics.Log("dismiss-without-focus-return",ResultKind.Cancelled);}
    private bool PadHeld(PadFrame pad)=>pad.Connected&&(pad.Buttons!=0||Math.Abs((int)pad.X)>=Controller.Settings.Deadzone*2/3||Math.Abs((int)pad.Y)>=Controller.Settings.Deadzone*2/3||Math.Abs((int)pad.RX)>=Controller.Settings.Deadzone*2/3||Math.Abs((int)pad.RY)>=Controller.Settings.Deadzone*2/3||pad.LT>=30||pad.RT>=30);
    private void CaptureHandoffBaseline(ReleaseSample sample)
    {
        for(var i=0;i<Math.Min(4,sample.Pads.Count);i++)if(!sample.Pads[i].ReadFailed&&PadHeld(sample.Pads[i]))handoffPadsHeld[i]=true;
        handoffGuideHeld=sample.GuideHeld;
    }
    private void ObserveHandoffInputs(ReleaseSample sample,long now)
    {
        Array.Clear(handoffPadsKnown);handoffGuideKnown=false;if(sample.Timestamp>now||now-sample.Timestamp>80)return;
        for(var i=0;i<Math.Min(4,sample.Pads.Count);i++){var pad=sample.Pads[i];if(pad.ReadFailed)continue;handoffPadsKnown[i]=true;handoffPadsHeld[i]=PadHeld(pad);}
        if(sample.GuideValid||sample.GuideHeld){handoffGuideKnown=true;handoffGuideHeld=sample.GuideHeld;}
    }
    private void AdvanceDegradedDismiss(ReleaseSample sample,long now)
    {
        var knownActive=handoffGuideKnown&&handoffGuideHeld;var unknownHeld=!handoffGuideKnown&&handoffGuideHeld;
        for(var i=0;i<4;i++){knownActive|=handoffPadsKnown[i]&&handoffPadsHeld[i];unknownHeld|=!handoffPadsKnown[i]&&handoffPadsHeld[i];}
        if(!sample.KeysNeutral||heldKeys.Count!=0||knownActive){degradedSince=null;return;}
        if(unknownHeld&&!degradedExplicit){degradedSince=null;FeedbackText("Leitura indisponível. Pressione e solte Esc para ocultar sem retornar ao aplicativo.");return;}
        if(degradedSince is null||now<degradedSince)degradedSince=now;if(now-degradedSince.Value>=100)CommitHandoff();
    }
    private void ReaffirmDismiss(bool trusted){if(!trusted||!handoffDegraded)return;degradedExplicit=true;degradedSince=null;FeedbackText("Solte as entradas disponíveis para ocultar sem retornar ao aplicativo.");}
    private void CommitHandoff(){var action=handoff;CancelHandoff(false);committing=true;try{action?.Invoke();}finally{committing=false;}}
    internal void AdvanceHandoff(ReleaseSample sample,long now)
    {
        if(handoff is null)return;sample=sample with{KeysNeutral=sample.KeysNeutral&&heldKeys.Count==0&&PhysicalKeysNeutral()};ObserveHandoffInputs(sample,now);if(handoffDegraded){AdvanceDegradedDismiss(sample,now);return;}
        var context=InputContext;var state=releaseGate.Update(sample,now,context.Visible&&context.Active&&context.OwnForeground&&!context.Exiting,Controller.Settings.Deadzone);
        if(state==ReleaseState.Cancelled||handoffValid?.Invoke()==false){RejectHandoff("Transição cancelada; foco, controle ou alvo mudou.");if(handoffDegraded)AdvanceDegradedDismiss(sample,now);return;}if(state==ReleaseState.Waiting)return;CommitHandoff();
    }
    private static bool SteamKeyOwned(int key,int mask)=>(mask&1)!=0&&key is 16 or 0xA0 or 0xA1||(mask&2)!=0&&key==9;
    internal bool PhysicalKeysNeutral(int owned=0)
    {
        if((PhysicalKeyReader(0x03)&0x8000)!=0)return false;
        for(var key=0x08;key<=0xFE;key++)if(!SteamKeyOwned(key,owned)&&(PhysicalKeyReader(key)&0x8000)!=0)return false;
        return heldKeys.All(k=>SteamKeyOwned(KeyInterop.VirtualKeyFromKey(k),owned)||(PhysicalKeyReader(KeyInterop.VirtualKeyFromKey(k))&0x8000)==0);
    }
    private void UpdatePolling(bool connected=true)=>timer.Interval=TimeSpan.FromMilliseconds(steamOwner is not null||effectFocusTransferred?16:GuidePolling.Interval(guideBackend?.Active==true,IsVisible,connected));
    private void ConfigureGuide()
    {
        if(!GuidePolling.Allowed(Controller.Settings.GuideXInputEnabled,diagnostic,Controller.Platform.IsPreview)){
            guideConnection?.Dispose();guideConnection=null;guideBackend=null;guideToggleRequested=false;
        }else if(guideBackend is null){guideBackend=new();guideConnection=new(guideBackend);guideConnection.Start(()=>guideToggleRequested=true);}
        UpdatePolling();
    }
    private sealed record Entry(string Id,Button Button,Action Run,bool Available,string Reason);
    private readonly List<Entry> entries=[];private NavState navigation=new("session-card");private string lastContentSelection="session-card";private int page;private string subpage="";private WindowRef? detail;
    public MainWindow(bool pilotRequested=false,bool renderOnly=false,IPlatform? platform=null,LocalDiagnostics? diagnostics=null,IApplicationAudio? appAudioService=null,IControllerDevices? devices=null,IFeedbackService? feedback=null,IUpdateSource? updates=null)
    {
        diagnostic=renderOnly;FocusFallbackRequest=renderOnly?null:WindowActivation.RequestFallbackAsync;updateSource=updates??(renderOnly||platform?.IsPreview==true?new PreviewUpdateSource():new GitHubUpdateSource());InitializeComponent();Controller=new(platform??(renderOnly?new PreviewPlatform():null),diagnostics);appliedSettings=Controller.Settings;input=new(Controller.Settings);if(!diagnostic&&!Controller.Platform.IsPreview)updateChannel=UpdateChannelSettings.Read(Controller.Diagnostics.Root);InitializeIconUpdates();
        controllerDevices=devices??(diagnostic||Controller.Platform.IsPreview?new PreviewControllerDevices():new ControllerDevices());
        applicationAudio=appAudioService??(diagnostic||Controller.Platform.IsPreview?new PreviewApplicationAudio():new ApplicationAudio(Controller.Platform.Valid));
        feedbackService=feedback??(diagnostic||Controller.Platform.IsPreview?new PreviewFeedbackService():new FeedbackService(controllerDevices));applicationAudio.Changed+=ApplicationAudioChanged;feedbackService.Changed+=FeedbackStatusChanged;feedbackService.Configure(Controller.Settings.SoundFeedbackEnabled,Controller.Settings.HapticFeedbackEnabled);
        timer=new DispatcherTimer(DispatcherPriority.Input){Interval=TimeSpan.FromMilliseconds(120)};timer.Tick+=Tick;
        HomeTab.Click+=(_,_)=>ClickTab(HomeTab,0);AppsTab.Click+=(_,_)=>ClickTab(AppsTab,1);SettingsTab.Click+=(_,_)=>ClickTab(SettingsTab,2);SettingsGear.Click+=(_,_)=>ActivatePointerEntry(SettingsGear);
        BackButton.Click+=(_,_)=>ActivatePointerEntry(BackButton);
        foreach(var fixedButton in new[]{SessionCardButton,SessionOptionsButton}){fixedButton.Click+=(_,_)=>{ActivatePointerEntry(fixedButton);};fixedButton.GotKeyboardFocus+=(_,_)=>{if(fixedButton.Tag is string id&&navigation.Id!=id){navigation=new(id);SetSelection(false);}};}
        ContextCard.Focusable=true;SessionCardButton.Resources["DescriptionTarget"]=ContextCard;
        CancelCloseButton.Click+=(_,_)=>ActivatePointerEntry(CancelCloseButton);
        SaveSettingsButton.Click+=(_,_)=>ActivatePointerEntry(SaveSettingsButton);
        DiscardSettingsButton.Click+=(_,_)=>ActivatePointerEntry(DiscardSettingsButton);
        DismissOperationButton.Click+=(_,_)=>ActivatePointerEntry(DismissOperationButton);
        OperationDetailsButton.Click+=(_,_)=>ActivatePointerEntry(OperationDetailsButton);
        foreach(var fixedEntry in new[]{(OperationDetailsButton,"operation-details"),(DismissOperationButton,"dismiss-operation"),(SaveSettingsButton,"save-pending"),(DiscardSettingsButton,"discard-pending"),(HoldCloseButton,"confirm-hold"),(CancelCloseButton,"confirm-cancel")}){fixedEntry.Item1.Tag=fixedEntry.Item2;fixedEntry.Item1.GotKeyboardFocus+=(_,_)=>{navigation=new(fixedEntry.Item2);SaveView();};}
        HoldCloseButton.PreviewMouseLeftButtonDown+=(_,e)=>{if(!NavigationAllowed||!Confirming)return;e.Handled=true;if(closeSource!=-1){CancelHold();FeedbackText("Origem da pressão mudou; fechamento cancelado.");return;}pointerDown=true;pointerCaptured=HoldCloseButton.CaptureMouse();if(!pointerCaptured)CancelHold();};
        HoldCloseButton.PreviewMouseLeftButtonUp+=(_,e)=>{e.Handled=true;pointerDown=false;if(Confirming&&closeSource==-1)AdvancePointerHold(false,true,true,Environment.TickCount64);ReleasePointer();};
        HoldCloseButton.MouseLeave+=(_,_)=>{if(pointerDown){CancelHold();FeedbackText("Pressão cancelada: o ponteiro saiu do botão.");}};
        HoldCloseButton.LostMouseCapture+=(_,_)=>{if(pointerDown){CancelHold();FeedbackText("Pressão cancelada: captura do mouse perdida.");}};
        PreviewMouseDown+=OnLocalPreviewMouseDown;PreviewMouseUp+=OnLocalPreviewMouseUp;
        ((FrameworkElement)Content).SizeChanged+=(_,e)=>UpdateCompactChrome(e.NewSize.Height);
        Rows.LayoutUpdated+=(_,_)=>{if(revealPending&&Rows.IsArrangeValid&&ContentScroll.ViewportHeight>0){revealPending=false;if(Keyboard.FocusedElement is FrameworkElement editor&&editor is TextBox or Slider&&Rows.IsAncestorOf(editor))editor.BringIntoView();else SetSelection(false);}};
        Rows.SizeChanged+=(_,e)=>{var compact=e.NewSize.Width<390;if(compact!=narrow){narrow=compact;if(!Confirming&&view.Current.Route==OverlayRoute.Hub){var offset=ContentScroll.VerticalOffset;RefreshRows();ContentScroll.ScrollToVerticalOffset(offset);}}};
        PreviewKeyDown+=OnKeyDown;PreviewKeyUp+=(_,e)=>{var key=NormalizeKey(e.Key,e.SystemKey,e.ImeProcessedKey,e.DeadCharProcessedKey);if(key!=Key.None)heldKeys.Remove(key);else if(TransitionPending)RejectHandoff("Transição cancelada; tecla não identificada.");if(!navigationKeysArmed&&heldKeys.Count==0&&PhysicalKeysNeutral())navigationKeysArmed=true;if(e.Key is Key.Enter or Key.Space){keyboardDown=false;e.Handled=true;}};
        Deactivated+=(_,_)=>{if(fadingOut){fadeInterrupted=true;return;}if(steamHiding||opening||exiting||(committing&&!busy))return;var expected=effectCancellation is not null&&effectFocusTarget is {} target&&FocusForeground()==target.Handle&&EffectTargetValid(target);if(expected)effectFocusTransferred=true;else effectCancellation?.Cancel();RevokeFocus("deactivated",!ownedDialog);CancelHandoff();heldKeys.Clear();CancelHold();input.Cancel();if(IsVisible&&!ownedDialog){HideNow(false,preserveEffect:expected);}};
        SourceInitialized+=(_,_)=>{source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);source.CompositionTarget.BackgroundColor=Colors.Transparent;source.AddHook(WindowMessage);if(!diagnostic)tray=new(Dispatcher,OpenPanel,ExitFromTray);};
        Closing+=OnClosing;audio.Changed+=AudioChanged;
        SystemParameters.StaticPropertyChanged+=OnSystemParametersChanged;UpdateAppearance();
        new WindowInteropHelper(this).EnsureHandle();ConfigureGuide();if(!diagnostic)timer.Start();RefreshRows();FeedbackText(Controller.Status);
    }
    private bool ownedDialog,placing;
    private void AudioChanged()
    {
        if(!IsVisible)return;UpdateAudioEntries(audio.Current);
    }
    internal void UpdateAudioEntries(AudioSnapshot state)
    {
        if(subpage=="audio"){var volume=entries.FirstOrDefault(e=>e.Id=="volume");if(volume is not null&&volume.Available!=state.Available){RefreshRows();return;}SetEntryText("volume","Volume · "+(state.Available?state.Volume.ToString("P0"):"indisponível"));SetEntryText("mute",state.Muted?"Ativar som":"Silenciar");}
        else if(page==0)SetEntryDescription("audio",state.Available?(state.Muted?"Sem som":state.Volume.ToString("P0")):"Saída indisponível");
    }
    private void SetEntryText(string id,string title)
    {
        if(entries.FirstOrDefault(e=>e.Id==id)?.Button is not {} button)return;
        if(button.Content is Grid grid&&grid.Children[1] is StackPanel texts)((TextBlock)texts.Children[0]).Text=title;
        AutomationProperties.SetName(button,title);
    }
    private void SetEntryDescription(string id,string description)
    {
        var index=entries.FindIndex(e=>e.Id==id);if(index<0)return;var entry=entries[index];
        if(entry.Button.Content is Grid grid&&grid.Children[1] is StackPanel texts&&texts.Children.Count>1&&texts.Children[1] is TextBlock subtitle)subtitle.Text=description;
        AutomationProperties.SetHelpText(entry.Button,description);entries[index]=entry with{Reason=description};
    }
    private nint WindowMessage(nint hwnd,int message,nint w,nint l,ref bool handled)
    {
        if(message==0x8002){OpenPanel();handled=true;}
        if(message is 0x7E or 0x2E0 && IsVisible&&!placing)Dispatcher.BeginInvoke(()=>Place());return 0;
    }
    internal void Toggle()=>Toggle(false);
    private void Toggle(bool trustedDismiss){if(updateBusy){CancelUpdateInteraction();if(!IsVisible&&!updateCommitting)OpeningTask=OpenPanelCore();return;}CancelSteamOwner("Reabertura solicitada; nenhum novo atalho enviado.");if(busy&&steamOwner is null){effectCancellation?.Cancel();return;}if(fadingOut)OpenPanel();else if(IsVisible){if(!TransitionPending&&!ownedDialog&&!NavigationAllowed)OpenPanel();else HidePanel(true,trustedDismiss);}else if(!TransitionPending)OpenPanel();}
    internal void OpenPanel(){if(!diagnostic&&!Controller.Platform.IsPreview)ShowUpdateRecoveryNotice();CancelSteamOwner("Painel reaberto; solicitação anterior cancelada.");if(exiting||closeRequested)return;if(updateBusy&&!updateCommitting){OpeningTask=OpenPanelCore();return;}if(busy&&steamOwner is null){if(effectCancellation is not null){effectCancellation.Cancel();if(!IsVisible||fadingOut)OpeningTask=OpenPanelCore();else FeedbackText("Solicitação cancelada; aguardando o resultado.");}return;}OpeningTask=OpenPanelCore();}
    private async Task OpenPanelCore()
    {
        var wasVisible=IsVisible;fadeGeneration++;fadingOut=false;CancelOpenAnimation();RevokeFocus("request");var cycle=focusCycle;CancelHandoff();CancelHold();input.Cancel();if(!wasVisible)Controller.CaptureReturn();
        opening=true;try{if(!wasVisible){Place();PrepareOpenAnimation();Show();}}finally{opening=false;}UpdatePolling();audio.SetVisible(!Controller.Platform.IsPreview);RefreshRows();if(!wasVisible)AnimateOpen();
        if(!await AcquireFocus(cycle))return;
        try{await (RefreshFocusFixture?.Invoke()??Controller.Refresh());if(!CurrentCycle(cycle))return;TraceFocus("refresh",ResultKind.Completed);if(!NavigationAllowed)return;Place();RefreshRows();SetSelection(false);}
        catch(OperationCanceledException){}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){if(CurrentCycle(cycle))FeedbackText("Não foi possível atualizar as janelas. Tente abrir novamente pela bandeja.");}
    }
    private bool CurrentCycle(long cycle)=>cycle==focusCycle&&!exiting&&IsVisible;
    private bool OwnForeground=>FocusForeground()==new WindowInteropHelper(this).Handle;
    private void TraceFocus(string checkpoint,ResultKind kind,string detail=""){if(!Controller.Platform.IsPreview)Controller.Diagnostics.Log($"focus/{focusCycle}/{checkpoint}/foreground={OwnForeground}/active={IsActive}{detail}",kind);}
    private void RevokeFocus(string reason,bool supersede=true){activationCancellation?.Cancel();CancelPlayniteScan();feedbackService.Suspend();applicationAudio.Suspend();audioGeneration++;audioTarget=null;nextBatteryRead=0;CancelOpenAnimation();if(supersede)focusCycle++;focusConfirmed=false;navigationKeysArmed=false;input.Cancel();CancelHold();TraceFocus(reason,ResultKind.Cancelled);}
    private bool NavigationAllowed
    {
        get{var context=InputContext;if(!context.Confirmed||!context.Visible||context.OwnedDialog||context.Exiting)return false;if(context.Active&&context.OwnForeground)return true;RevokeFocus("lost");FeedbackFocusUnavailable("Foco perdido. Guide tenta novamente; B ou Esc oculta; bandeja: abrir.");return false;}
    }
    private async Task<bool> AcquireFocus(long cycle)
    {
        if(!CurrentCycle(cycle)||ownedDialog)return false;
        using var activation=new CancellationTokenSource();activationCancellation=activation;
        try
        {
        var foreground=FocusForeground();bool activated,requested;opening=true;try{activated=Activate();requested=FocusRequest(new WindowInteropHelper(this).Handle);}finally{opening=false;}
        if(!requested&&!OwnForeground&&CurrentCycle(cycle)&&!ownedDialog&&FocusFallbackRequest is {} fallback)
        {
            requested=await fallback(new WindowInteropHelper(this).Handle,foreground,activation.Token);
            if(!CurrentCycle(cycle)||ownedDialog||activation.IsCancellationRequested)return false;
        }
        TraceFocus("request-native",ResultKind.Requested,$"/activate={activated}/request={requested}");
        for(var attempt=0;attempt<=6;attempt++){
            if(!CurrentCycle(cycle)||ownedDialog)return false;
            if(IsActive&&OwnForeground){focusConfirmed=true;navigationKeysArmed=heldKeys.Count==0&&PhysicalKeysNeutral();input.Cancel();TrackApplicationAudio();TraceFocus("confirmed",ResultKind.Completed);FeedbackText("Painel ativo. Use o controle para navegar.");return true;}
            // A native return value is not the final foreground observation: WPF or
            // Windows can complete the explicit activation on a later dispatcher turn.
            if(attempt==6){TraceFocus("unconfirmed",requested?ResultKind.Unknown:ResultKind.Failed);FeedbackFocusUnavailable("O Windows não confirmou o foco. Guide tenta novamente; B ou Esc oculta; bandeja: abrir.");return false;}
            await Task.Delay(50);
        }return false;
        }finally{if(ReferenceEquals(activationCancellation,activation))activationCancellation=null;}
    }
    internal void ExitFromTray(){if(updateCommitting&&!updateCloseAuthorized){FeedbackText("A troca de versão está sendo preparada. Aguarde o reinício.");return;}if(!exiting)Close();}
    private bool closeRequested,closeFinalizing;private Task? closeDrain;
    internal Task ClosingTask=>closeDrain??Task.CompletedTask;
    private void OnClosing(object? sender,System.ComponentModel.CancelEventArgs args)
    {
        if(updateCommitting&&!updateCloseAuthorized){args.Cancel=true;FeedbackText("Aguarde a preparação da troca de versão.");return;}
        if(exiting)return;
        if(closeFinalizing){Cleanup();return;}
        if(closeRequested){args.Cancel=true;return;}
        if(steamOwner is not{} owner){Cleanup();return;}
        args.Cancel=true;closeRequested=true;CancelSteamOwner("Encerramento solicitado; drenando teclas.");closeDrain=DrainAndClose(owner,OperationTask);
    }
    private async Task DrainAndClose(SteamOwner owner,Task operation)
    {
        await owner.Drained.Task;
        try{await operation;}catch(Exception e)when(e is System.IO.IOException or UnauthorizedAccessException or OperationCanceledException){}
        await Dispatcher.InvokeAsync(()=>{closeFinalizing=true;Close();});
    }
    internal void ShowDiagnostic(){opening=true;Show();Activate();Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);opening=false;focusCycle++;focusConfirmed=IsActive&&OwnForeground;navigationKeysArmed=true;SetSelection(false);}
    internal void ValidationHost(bool visible){if(visible){ShowDiagnostic();audio.SetVisible(true);}else{Hide();audio.SetVisible(false);}UpdatePolling();timer.Start();}
    private bool OpenAnimationAllowed=>!Controller.Settings.ReducedMotion&&!SystemParameters.HighContrast&&SystemParameters.ClientAreaAnimation;
    private void CancelOpenAnimation(){BeginAnimation(OpacityProperty,null);Opacity=1;}
    private void PrepareOpenAnimation(){CancelOpenAnimation();if(OpenAnimationAllowed)Opacity=0;}
    internal void AnimateOpen(){if(!OpenAnimationAllowed){CancelOpenAnimation();return;}Opacity=1;BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(150)){FillBehavior=FillBehavior.Stop});}
    private void OnSystemParametersChanged(object? sender,PropertyChangedEventArgs args){if(exiting||Dispatcher.HasShutdownStarted)return;if(Dispatcher.CheckAccess())UpdateAppearance();else Dispatcher.BeginInvoke(UpdateAppearance);}
    private void UpdateAppearance(){if(!OpenAnimationAllowed)CancelOpenAnimation();ThemeSupport.ApplySystemPalette(Application.Current);GlassSurface.Opacity=1;GlassSurface.Effect=SystemParameters.HighContrast?null:new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=18,ShadowDepth=4,Opacity=.18};}
    internal void Place()
    {
        if(placing)return;placing=true;try{
        var screens=Controller.RefreshDisplays();if(screens.Count==0)return;
        var foreground=Controller.ReturnTarget;var anchor=Controller.Session.Anchor;var target=foreground is {Protected:false}?foreground:anchor is not null&&Controller.Platform.Valid(anchor)?anchor:foreground;var monitor=target is null?0:Native.MonitorFromWindow(target.Handle,2);var info=new Native.MonitorInfo{Size=System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>(),Device=""};var active=monitor!=0&&Native.GetMonitorInfo(monitor,ref info)?screens.FirstOrDefault(d=>d.Id==info.Device):null;
        var chosen=screens.FirstOrDefault(d=>d.Id==Controller.Settings.DisplayId)??active??screens.FirstOrDefault(d=>d.Primary)??screens[0];var hwnd=new WindowInteropHelper(this).Handle;
        if(hwnd!=0)Native.SetWindowPos(hwnd,0,chosen.X+16,chosen.Y+16,0,0,0x15);
        var dpi=DpiReader(hwnd);if(dpi==0)dpi=96;var scale=dpi/96d;
        if(pointerDown)CancelHold();var density=SidebarLayout.EffectiveScale(chosen,dpi,Controller.Settings.UiScalePercent);((FrameworkElement)Content).LayoutTransform=new ScaleTransform(density,density);var placement=SidebarLayout.Place(chosen,dpi,Controller.Settings.UiScalePercent,Controller.Settings.PanelSide);Width=placement.Width/scale;Height=placement.Height/scale;
        if(hwnd!=0)Native.SetWindowPos(hwnd,(nint)(-1),placement.X,placement.Y,placement.Width,placement.Height,0x10);
        }finally{placing=false;}
    }
    private void HidePanel(bool restore,bool trustedDismiss=false)
    {
        if(handoffDismiss)ReaffirmDismiss(trustedDismiss);
        var target=Controller.ReturnTarget;QueueHandoff(()=>HideNow(restore,target),()=>target is null?Controller.ReturnTarget is null:Controller.ReturnTarget?.Same(target)==true&&Controller.Platform.Valid(target),dismiss:true);
    }
    private bool FadeInputsNeutral(IReadOnlyList<PadFrame> frames)
    {
        if(frames.Count!=4||frames.Any(f=>f.ReadFailed||f.Connected&&(f.Buttons!=0||Math.Abs((int)f.X)>=Controller.Settings.Deadzone*2/3||Math.Abs((int)f.Y)>=Controller.Settings.Deadzone*2/3||Math.Abs((int)f.RX)>=Controller.Settings.Deadzone*2/3||Math.Abs((int)f.RY)>=Controller.Settings.Deadzone*2/3||f.LT>=30||f.RT>=30)))return false;
        var connected=0;for(var i=0;i<frames.Count;i++)if(frames[i].Connected)connected|=1<<i;
        if((fadePadConnections&~connected)!=0)return false;
        fadePadConnections|=connected;
        if(guideBackend is {Active:true} backend){if(!backend.SnapshotValid||backend.Held||(fadeGuideConnections&~backend.Connections)!=0)return false;fadeGuideConnections|=backend.Connections;}
        else if(fadeGuideConnections!=0)return false;
        return PhysicalKeysNeutral();
    }
    private bool EffectTargetValid(WindowRef target)=>Controller.Platform.Valid(target)||Controller.Platform.CanSteamMenu(target);
    private bool EffectInputsNeutral(IReadOnlyList<PadFrame> frames)
    {
        if(frames.Count!=4||frames.Any(f=>f.ReadFailed||f.Connected&&(f.Buttons!=0||Math.Abs((int)f.X)>=Controller.Settings.Deadzone*2/3||Math.Abs((int)f.Y)>=Controller.Settings.Deadzone*2/3||Math.Abs((int)f.RX)>=Controller.Settings.Deadzone*2/3||Math.Abs((int)f.RY)>=Controller.Settings.Deadzone*2/3||f.LT>=30||f.RT>=30)))return false;
        var connected=0;for(var i=0;i<frames.Count;i++)if(frames[i].Connected)connected|=1<<i;
        if((effectPadConnections&~connected)!=0)return false;effectPadConnections|=connected;
        if(guideBackend is {Active:true} backend){if(!backend.SnapshotValid||backend.Held||(effectGuideConnections&~backend.Connections)!=0)return false;effectGuideConnections|=backend.Connections;}else if(effectGuideConnections!=0)return false;
        return PhysicalKeysNeutral();
    }
    private async void HideNow(bool restore,WindowRef? returnTarget=null,bool preserveSteam=false,bool preserveEffect=false)
    {
        if(!preserveSteam)CancelSteamOwner("Painel ocultado; solicitação anterior cancelada.");if(!IsVisible||fadingOut)return;var wasForeground=Native.GetForegroundWindow()==new WindowInteropHelper(this).Handle;RevokeFocus("hide");CancelHandoff();heldKeys.Clear();CancelHold();if(!preserveSteam)pending?.Cancel();if(!preserveEffect)effectCancellation?.Cancel();input.Cancel();Sidebar.BeginAnimation(OpacityProperty,null);
        var generation=++fadeGeneration;fadeInterrupted=false;fadePadConnections=0;fadeGuideConnections=0;
        if(lastInputSample is {} baseline){for(var i=0;i<Math.Min(4,baseline.Pads.Count);i++)if(baseline.Pads[i].Connected)fadePadConnections|=1<<i;fadeGuideConnections=baseline.GuideConnections;}
        // Steam requires the window to disappear before its focus handshake. Diagnostics stay synchronous.
        if(!preserveSteam&&(ForceFadeFixture||!diagnostic&&OpenAnimationAllowed)){
            fadingOut=true;var animation=new DoubleAnimation(Opacity,0,TimeSpan.FromMilliseconds(180)){FillBehavior=FillBehavior.HoldEnd,EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};BeginAnimation(OpacityProperty,animation);
            await Task.Delay(180);
            if(exiting||generation!=fadeGeneration)return;
            // Recheck at the handoff itself: the timer may have been delayed on the UI thread.
            guideBackend?.PollForOverlay(IsVisible,focusCycle,Environment.TickCount64,Controller.Settings.GuideOpenMaxHoldMs);
            fadeInterrupted|=!FadeInputsNeutral(FadeFramesFixture?.Invoke()??Enumerable.Range(0,4).Select(i=>Native.ReadPad((uint)i)).ToArray());
            fadingOut=false;
        }
        var stillForeground=Native.GetForegroundWindow()==new WindowInteropHelper(this).Handle;
        if(stillForeground&&fadeInterrupted){OpenPanel();FeedbackText("Entrada detectada ao fechar. Solte os controles e tente novamente.");return;}
        Hide();CancelOpenAnimation();audio.SetVisible(false);UpdatePolling();
        if(restore&&wasForeground&&stillForeground&&!fadeInterrupted&&PhysicalKeysNeutral()&&returnTarget is {} target&&Controller.Platform.Valid(target)){var result=await Controller.Platform.Switch(target);if(result.Kind!=ResultKind.Completed&&result.Kind!=ResultKind.Simulated&&!exiting){if(!Controller.Platform.IsPreview)Controller.Diagnostics.Log("focus-return",result.Kind);FeedbackText(result.Message);}}
    }
    private void Cleanup(){if(exiting)return;DisposeUpdates();effectCancellation?.Cancel();fadeGeneration++;fadingOut=false;CancelSteamOwner("Overlay encerrada.");automationHost?.Dispose();exiting=true;operationGeneration++;RevokeFocus("cleanup");CancelHandoff();heldKeys.Clear();SystemParameters.StaticPropertyChanged-=OnSystemParametersChanged;timer.Stop();timer.Tick-=Tick;guideConnection?.Dispose();guideConnection=null;guideBackend=null;guideToggleRequested=false;pending?.Cancel();ResultNotificationRequest=null;applicationAudio.Changed-=ApplicationAudioChanged;feedbackService.Changed-=FeedbackStatusChanged;applicationAudio.Dispose();feedbackService.Dispose();controllerDevices.Dispose();audio.Dispose();source?.RemoveHook(WindowMessage);tray?.Dispose();DisposeIconUpdates();Controller.Dispose();}
    private void FeedbackText(string text)
    {
        StatusLabel.TextWrapping=TextWrapping.NoWrap;StatusLabel.TextTrimming=TextTrimming.CharacterEllipsis;StatusLabel.Text=text;
    }
    private void FeedbackFocusUnavailable(string text)
    {
        FeedbackText(text);
        if(!compactChrome||exiting||!IsVisible||Dispatcher.HasShutdownStarted)return;
        StatusLabel.TextWrapping=TextWrapping.Wrap;StatusLabel.TextTrimming=TextTrimming.None;
        var cycle=focusCycle;
        // Layout and selection may still be settling. Do not reveal an old failure
        // after a later open, recovered focus, dismissal or replacement message.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,()=>{
            if(exiting||!IsVisible||!compactChrome||focusCycle!=cycle||focusConfirmed||StatusLabel.Text!=text)return;
            StatusLabel.BringIntoView();
        });
    }
    private void RetainOperation(string state,string message,string action="",WindowRef? target=null,bool dispatched=false){Controller.SetOperation(state,message,action,target,dispatched);UpdatePersistentState();}
    private void UpdatePersistentState()
    {
        PlaceCompactStatus();
        if(Rows.Children.Count>0&&!Confirming){
            var previous=entries.Select(e=>e.Id).ToArray();
            var selectedFooter=IsFooterAction(navigation.Id)&&previous.Contains(navigation.Id);
            entries.RemoveAll(e=>e.Id is "save-pending" or "discard-pending" or "operation-details"||(e.Id=="dismiss-operation"&&subpage!="operation"));
            if(Controller.Operation is not null&&subpage!="operation"){
                entries.Add(new("operation-details",OperationDetailsButton,OpenOperation,!busy&&!TransitionPending,"Ler o resultado completo"));entries.Add(new("dismiss-operation",DismissOperationButton,DismissOperation,!busy&&!TransitionPending,"Dispensar resultado"));
            }
            if(Controller.SettingsDirty){entries.Add(new("save-pending",SaveSettingsButton,Save,true,"Salvar alterações"));entries.Add(new("discard-pending",DiscardSettingsButton,()=>{Controller.Discard();ApplySettings();RefreshRows();},true,"Restaurar ajustes salvos"));}
            if(selectedFooter&&!entries.Any(e=>e.Id==navigation.Id))navigation=navigation with{Id=Navigation.Preserve(navigation.Id,previous,entries.Select(e=>e.Id).ToArray())};
        }
        SetEntryDescription("exit",Controller.SettingsDirty?"Alterações não salvas serão perdidas":"Encerra observadores e remove o ícone da bandeja");
        DirtyStrip.Visibility=Controller.SettingsDirty&&!Confirming?Visibility.Visible:Visibility.Collapsed;
        DirtyLabel.Text=Controller.Platform.IsPreview?"Prévia · alterações não persistentes":"Alterações não salvas";
        var operation=Controller.Operation;UpdateOperationAppearance(operation?.State);OperationPanel.Visibility=operation is null||Confirming||subpage=="operation"?Visibility.Collapsed:Visibility.Visible;
        var text=operation is null?"":operation.State+" · "+operation.Message;
        if(OperationLabel.Text!=text){OperationLabel.Text=text;System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(OperationLabel)?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);}
        OperationDetailsButton.IsEnabled=!busy&&!TransitionPending;OperationLabel.ToolTip=text;SaveSettingsButton.IsEnabled=!busy&&!TransitionPending;DiscardSettingsButton.IsEnabled=!busy&&!TransitionPending;DismissOperationButton.IsEnabled=!busy&&!TransitionPending;
    }
    private void OpenOperation(){if(!NavigationAllowed||busy||TransitionPending||Confirming||Controller.Operation is null)return;OpenView(new(OverlayRoute.OperationResult,view.Current.Target,"operation-text"));}
    private void DismissOperation(){if(!NavigationAllowed||busy||TransitionPending||Confirming)return;Controller.DismissOperation();if(subpage=="operation")Back();else{UpdatePersistentState();SetSelection(false);}}
    private void UpdateHints()
    {
        if(Confirming){ModeLabel.Text=inputMethod=="controller"?"A segurar · B cancelar · analógico direito rolar":inputMethod=="mouse"?"Segure para confirmar · Cancelar interrompe":"Enter / Espaço segurar · Esc cancelar · PgUp/PgDn rolar";return;}
        var sessionTarget=Controller.Session.Anchor;
        SessionHint.Text=(sessionTarget is null||!Controller.Platform.Valid(sessionTarget))?inputMethod switch{"controller"=>"A · Escolher janela","mouse"=>"Clique para escolher janela",_=>"Enter · Escolher janela"}:inputMethod switch{"controller"=>"A · Retomar   X · Opções","mouse"=>"Clique para retomar · ·· Opções",_=>"Enter · Retomar   X · Opções"};
        var details=page==1||subpage=="details";
        ModeLabel.Text=inputMethod switch{
            "mouse"=>"Clique para selecionar · bandeja: duplo clique para abrir",
            "controller"=>"A selecionar · B voltar"+(details?" · X opções":"")+"\nLB/RB abas · direcional mover"+(Controller.Settings.GuideXInputEnabled&&guideBackend?.Active==true?" · Guide abrir":"")+" · bandeja: abrir",
            _=>"Enter selecionar · Esc voltar"+(details?" · X opções":"")+"\nPgUp/PgDn abas · Setas / Tab mover · bandeja: abrir"};
        if(compactChrome)ModeLabel.Text=inputMethod switch{"mouse"=>"Clique selecionar · bandeja: abrir","controller"=>"A selecionar · B voltar"+(details?" · X opções":"")+"\nLB/RB abas · direcional mover",_=>"Enter selecionar · Esc voltar\nTab / Setas mover · PgUp/PgDn abas"};
    }
    private void PlaceCompactStatus()
    {
        // In a short window, keep the controller warning and instructions pinned;
        // auxiliary status can scroll with the page or confirmation text instead.
        Panel destination=compactChrome?(Confirming?ConfirmationInfo:ContentStack):FooterArea;
        if(ReferenceEquals(StatusLabel.Parent,destination))return;
        if(StatusLabel.Parent is Panel previous)previous.Children.Remove(StatusLabel);
        if(ReferenceEquals(destination,FooterArea))destination.Children.Insert(2,StatusLabel);
        else if(ReferenceEquals(destination,ContentStack))destination.Children.Insert(Math.Min(1,destination.Children.Count),StatusLabel);
        else destination.Children.Add(StatusLabel);
    }
    private void UpdateCompactChrome(double height)
    {
        var compact=height>0&&height<650;if(compact==compactChrome)return;compactChrome=compact;
        if(pointerDown)CancelHold();HeaderIdentity.Visibility=compact?Visibility.Collapsed:Visibility.Visible;ControllerStatusLabel.Visibility=compact?Visibility.Collapsed:Visibility.Visible;DirtyLabel.Visibility=compact?Visibility.Collapsed:Visibility.Visible;HeaderArea.Margin=new(0,0,0,compact?8:12);DirtyStrip.Margin=new(0,compact?8:12,0,0);DirtyStrip.Padding=new(12,compact?6:8,12,compact?6:8);TabArea.Margin=new(0,0,0,compact?8:16);Sidebar.Padding=compact?new(20,14,20,14):new(24,22,24,22);FooterArea.Margin=new(0,compact?6:10,0,0);FooterDivider.Margin=new(0,0,0,compact?4:8);if(!compact){StatusLabel.TextWrapping=TextWrapping.NoWrap;StatusLabel.TextTrimming=TextTrimming.CharacterEllipsis;}
        // Tabs and Save/Discard stay fixed. The header and status move into the
        // existing scroller so selected cards have room without shrinking text.
        if(compact&&HeaderArea.Parent is Grid headerGrid){headerGrid.Children.Remove(HeaderArea);ContentStack.Children.Insert(0,HeaderArea);}
        else if(!compact&&HeaderArea.Parent is StackPanel headerStack){headerStack.Children.Remove(HeaderArea);((Grid)Sidebar.Child).Children.Add(HeaderArea);}
        if(compact&&OperationPanel.Parent is Grid grid){grid.Children.Remove(OperationPanel);ContentStack.Children.Add(OperationPanel);}
        else if(!compact&&OperationPanel.Parent is StackPanel stack){stack.Children.Remove(OperationPanel);((Grid)Sidebar.Child).Children.Add(OperationPanel);}
        PlaceCompactStatus();revealPending=true;UpdateHints();
    }
    private void ReleasePointer(){pointerDown=false;pointerCaptured=false;if(HoldCloseButton.IsMouseCaptured)HoldCloseButton.ReleaseMouseCapture();}
    private void CancelHold(){nextConfirmationScroll=0;var restore=view.Current.Route==OverlayRoute.Confirmation;ReleasePointer();closeHold.Cancel();HeaderArea.Visibility=Visibility.Visible;TabArea.Visibility=Visibility.Visible;closeSource=null;keyboardDown=false;ConfirmPanel.Visibility=Visibility.Collapsed;ContentScroll.Visibility=Visibility.Visible;CloseArea.Visibility=Visibility.Visible;if(restore&&view.Back()){SyncView();RefreshRows();ContentScroll.ScrollToVerticalOffset(view.Current.ScrollOffset);}UpdatePersistentState();UpdateHints();}
    private void SaveView()=>view.Remember(navigation.Id,ContentScroll.VerticalOffset);
    private void SyncView()
    {
        page=(int)view.Tab;detail=view.Current.Target;subpage=view.Current.Route switch{OverlayRoute.WindowsAudio=>"audio",OverlayRoute.Details=>"details",OverlayRoute.TargetOptions=>"options",OverlayRoute.Integrations=>"integrations",OverlayRoute.Customize=>"customize",OverlayRoute.Setup=>"setup",OverlayRoute.OperationResult=>"operation",OverlayRoute.Confirmation=>"confirmation",OverlayRoute.PlayniteBrowser=>"playnite-browser",OverlayRoute.Updates=>"updates",OverlayRoute.UpdateReview=>"update-review",_=>""};navigation=new(view.Current.SelectedId);
    }
    private void OpenView(OverlayViewFrame next){if(busy||TransitionPending||Confirming)return;SaveView();view.Open(next);SyncView();RefreshRows();ContentScroll.ScrollToVerticalOffset(next.ScrollOffset);}
    internal void OpenOptions(WindowRef target)=>OpenView(new(OverlayRoute.TargetOptions,target,"application-mute"));
    private void ChooseSession()=>OpenView(new(OverlayRoute.Apps,SelectedId:Controller.Apps.FirstOrDefault() is {} w?WindowId(w):"refresh-apps",ChooseSession:true));
    internal void Page(int index){if(TransitionPending||Confirming||updateCommitting)return;if(updateBusy)CancelUpdateInteraction();if((index+3)%3!=page&&interfaceActionDepth==0)RequestCue(FeedbackCue.Navigation);if(view.Current.Route==OverlayRoute.PlayniteBrowser)CancelPlayniteScan();SaveView();view.SwitchTab((OverlayTab)((index+3)%3));SyncView();RefreshRows();ContentScroll.ScrollToVerticalOffset(view.Current.ScrollOffset);}
    private void Subpage(string name,string first)=>OpenView(new(name switch{"audio"=>OverlayRoute.WindowsAudio,"details"=>OverlayRoute.Details,"options"=>OverlayRoute.TargetOptions,"integrations"=>OverlayRoute.Integrations,"customize"=>OverlayRoute.Customize,"setup"=>OverlayRoute.Setup,_=>OverlayRoute.OperationResult},detail,first));
    internal void RefreshRows()
    {
        automationRevision++;NoteAutomationTargets();HeaderIdentity.Text="Game Session Overlay · "+AppBuild.Current.Display;HeaderIdentity.ToolTip=AppBuild.Current.BuildId+" · "+AppBuild.Current.Commit;
        if(Confirming){UpdatePersistentState();return;}
        var previous=entries.Select(e=>e.Id).ToArray();Rows.Children.Clear();Rows.RowDefinitions.Clear();Rows.ColumnDefinitions.Clear();Rows.ColumnDefinitions.Add(new());Rows.ColumnDefinitions.Add(new());CloseArea.Children.Clear();entries.Clear();operationText=null;
        entries.Add(new("settings",SettingsGear,()=>Page(2),true,"Abrir Sistema"));ContextCard.Visibility=view.Current.Route==OverlayRoute.Hub?Visibility.Visible:Visibility.Collapsed;
        var target=Controller.Session.Anchor;var valid=target is not null&&Controller.Platform.Valid(target);
        SessionKicker.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;SessionCardButton.Padding=new Thickness(12,narrow?10:16,12,narrow?10:16);TargetLabel.Text=target?.Title??"Escolha uma janela";TargetLabel.ToolTip=target?.Title;AutomationProperties.SetName(ContextCard,target?.Title??"Nenhuma janela selecionada");
        SessionEvidence.Text=target?.ProcessName??"Aplicativos";ContextStatus.Text=target is null?"Sem seleção":valid?"Disponível":"Janela indisponível";ContextStatusDot.Opacity=valid?1:.3;
        ContextIcon.Source=target is null?null:Controller.Platform.Icon(target);ContextIcon.Visibility=ContextIcon.Source is null?Visibility.Collapsed:Visibility.Visible;ContextGlyph.Visibility=ContextIcon.Source is null?Visibility.Visible:Visibility.Collapsed;SessionAudioLabel.Text=target is null?"Selecione uma janela para ver o áudio":!valid?"Janela indisponível":AudioFor(target)?AudioBadge():"Estado não confirmado";ControllerStatusLabel.Text=controllerStatus;
        BackButton.Visibility=view.Depth>0?Visibility.Visible:Visibility.Collapsed;if(view.Depth>0)RegisterFixed("back-header",BackButton,Back,true,"Voltar ao contexto anterior");UpdateHints();
        foreach(var pair in new[]{(HomeTab,0),(AppsTab,1),(SettingsTab,2)}){var selected=pair.Item2==page;pair.Item1.BorderThickness=new(0,0,0,2);if(selected)pair.Item1.SetResourceReference(BorderBrushProperty,ThemeSupport.Success);else pair.Item1.BorderBrush=Brushes.Transparent;pair.Item1.SetResourceReference(BackgroundProperty,selected?ThemeSupport.Control:ThemeSupport.Card);pair.Item1.SetResourceReference(ForegroundProperty,selected?ThemeSupport.Text:ThemeSupport.SecondaryText);AutomationProperties.SetName(pair.Item1,(pair.Item2 switch{0=>"Sessão",1=>"Aplicativos",_=>"Sistema"})+(selected?" · selecionada":""));AutomationProperties.SetItemStatus(pair.Item1,selected?"Selecionada":"Não selecionada");}
        PageTitle.Text=view.Current.Route switch{OverlayRoute.WindowsAudio=>"Áudio do Windows",OverlayRoute.Details=>"Detalhes da janela",OverlayRoute.TargetOptions=>"Opções da janela",OverlayRoute.Integrations=>"Integrações",OverlayRoute.Customize=>"Acesso rápido",OverlayRoute.Setup=>"Seu painel, do seu jeito",OverlayRoute.OperationResult=>"Resultado da operação",OverlayRoute.Apps=>view.Current.ChooseSession?"Escolher janela":"Aplicativos",OverlayRoute.System=>"Sistema",OverlayRoute.PlayniteBrowser=>"Escolher Playnite",OverlayRoute.Updates=>"Versão e atualizações",OverlayRoute.UpdateReview=>rollbackReview is null?"Atualizar overlay":"Voltar de versão",_=>"Controle da sessão"};
        BuildCurrentView();UpdatePersistentState();navigation=navigation with{Id=Navigation.Preserve(navigation.Id,previous,entries.Select(e=>e.Id).ToArray())};SaveView();TrackApplicationAudio();revealPending=true;SetSelection(false);automationHost?.ObserveSnapshot(AutomationSnapshot());
    }
    private string GuideDescription()=>Controller.Platform.IsPreview?"Prévia · conexão Guide não é consultada":!Controller.Settings.GuideXInputEnabled?"Desativado · abra pelo duplo clique na bandeja":guideBackend?.Active==true?guideBackend.Status:guideConnection?.Status??"Controle ou leitura Guide indisponível";
    private Button MakeButton(string id,string title,string description,Action action,bool available,string icon,bool danger)
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(38)});grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});var glyph=ActionIcon(id.StartsWith("favorite:",StringComparison.Ordinal)?id[9..]:id,icon);if(page==2&&subpage==""&&id is "deadzone" or "motion" or "guide-limit" or "shortcut" or "integrations" or "save" or "side" or "ui-scale" or "sound-feedback" or "haptic-feedback" or "test-feedback" or "customize" or "setup"){glyph.Visibility=Visibility.Collapsed;grid.ColumnDefinitions[0].Width=new(0);}grid.Children.Add(glyph);var texts=new StackPanel{VerticalAlignment=VerticalAlignment.Center};texts.Children.Add(new TextBlock{Text=title,FontSize=18,TextWrapping=TextWrapping.Wrap,FontWeight=FontWeights.SemiBold});if(description.Length>0)texts.Children.Add(new TextBlock{Text=description,FontSize=15,TextWrapping=TextWrapping.Wrap,Margin=new(0,4,0,0)});Grid.SetColumn(texts,1);grid.Children.Add(texts);
        var badge=new Border{CornerRadius=new(18),Width=30,Height=30,HorizontalAlignment=HorizontalAlignment.Left,Visibility=Visibility.Hidden,Child=new TextBlock{Text="A",FontSize=18,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Foreground=new SolidColorBrush(Color.FromRgb(15,38,20))}};badge.SetResourceReference(BackgroundProperty,ThemeSupport.Success);Grid.SetColumn(badge,0);grid.Children.Add(badge);
        if(id=="apps"){var count=new Border{CornerRadius=new(15),Padding=new(9,4,9,4),Margin=new(8,0,0,0),VerticalAlignment=VerticalAlignment.Center,Child=new TextBlock{Text=Controller.Apps.Count.ToString(),FontSize=17}};count.SetResourceReference(BackgroundProperty,ThemeSupport.Card);Grid.SetColumn(count,2);grid.Children.Add(count);grid.Resources["CountBadge"]=count;}
        ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty,SystemParameters.HighContrast?SystemColors.HighlightTextBrushKey:ThemeSupport.Surface);
        var button=new Button{Content=grid,Tag=id,MinHeight=id=="return"?76:56,IsEnabled=available,Padding=new(12,8,12,8)};
        if(page==0&&subpage==""&&id=="apps"){button.Background=Brushes.Transparent;button.BorderThickness=new(0);button.Margin=new(0,2,0,2);}
        if(id=="return"&&texts.Children.Count>1&&texts.Children[1] is TextBlock targetText){targetText.TextWrapping=TextWrapping.NoWrap;targetText.TextTrimming=TextTrimming.CharacterEllipsis;}
        if(id=="close"&&texts.Children.Count>1)texts.Children.RemoveAt(1);
        if(texts.Children.Count>1&&texts.Children[1] is TextBlock descriptionText)descriptionText.SetResourceReference(TextBlock.ForegroundProperty,ThemeSupport.SecondaryText);if(danger)button.SetResourceReference(ForegroundProperty,ThemeSupport.Critical);
        AutomationProperties.SetName(button,title);AutomationProperties.SetHelpText(button,description);button.ToolTip=title+"\n"+description;
        button.Resources["ABadge"]=badge;button.GotKeyboardFocus+=(_,_)=>{if(navigation.Id!=id){navigation=new(id);SetSelection(false);}};button.Click+=(_,_)=>{ActivatePointerEntry(button);};entries.Add(new(id,button,action,available,description));return button;
    }
    private FrameworkElement ActionIcon(string id,string fallback)
    {
        var key=id.StartsWith("window:",StringComparison.Ordinal)||id.StartsWith("recent:",StringComparison.Ordinal)?"apps":id switch{"session-card" or "previous-window" or "back"=>"back","steam" or "steam-overlay" or "test-steam"=>"steam","gamebar" or "chord" or "guide"=>"gamebar","apps" or "refresh-apps" or "next-apps" or "switch-detail"=>"apps","taskview" or "display" or "minimize"=>"taskview","audio" or "volume" or "volume-down" or "application-mute"=>"speaker","mute"=>audio.Current.Muted?"speaker":"mute","close" or "close-detail" or "exit" or "external-close"=>"close","playnite" or "configure-playnite"=>"playnite","select-session"=>"controller",_=>"settings"};
        var icon=new System.Windows.Shapes.Path{Data=(Geometry)FindResource("Icon."+key),Stretch=Stretch.Uniform,Width=26,Height=26,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center};
        icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,id is "close" or "close-detail"?ThemeSupport.Critical:ThemeSupport.Text);return icon;
    }
    private void SetSelection(bool showReason=true)
    {
        if(navigation.Id!="exit"&&!IsFooterAction(navigation.Id)&&entries.Any(entry=>entry.Id==navigation.Id))lastContentSelection=navigation.Id;
        foreach(var entry in entries){var selected=entry.Id==navigation.Id;var danger=entry.Id is "close" or "close-detail";var primary=entry.Id=="session-card";
            entry.Button.BorderThickness=new(2);entry.Button.SetResourceReference(BorderBrushProperty,selected?ThemeSupport.Success:ThemeSupport.Stroke);
            entry.Button.SetResourceReference(BackgroundProperty,primary?ThemeSupport.SuccessBackground:selected?ThemeSupport.Card:ThemeSupport.Control);if(entry.Id=="close")entry.Button.Background=Brushes.Transparent;
            if(page==0&&subpage==""&&entry.Id=="apps"){entry.Button.Background=selected?(Brush)FindResource(ThemeSupport.Card):Brushes.Transparent;entry.Button.BorderThickness=new(2);}entry.Button.Effect=null;if(entry.Button.Resources["ABadge"] is Border badge&&entry.Button.Content is Grid grid){var showBadge=selected&&entry.Available&&inputMethod=="controller"&&!entry.Id.StartsWith("window:",StringComparison.Ordinal)&&grid.ColumnDefinitions[0].Width.Value>0;badge.Visibility=showBadge?Visibility.Visible:Visibility.Hidden;grid.Children[0].Visibility=showBadge?Visibility.Hidden:grid.ColumnDefinitions[0].Width.Value>0?Visibility.Visible:Visibility.Collapsed;}if(!selected&&(entry.Id=="close"||page==0&&subpage==""&&entry.Id=="apps"))entry.Button.BorderBrush=Brushes.Transparent;
            if(entry.Button.Resources["DescriptionTarget"] is Border host){host.BorderThickness=new(2);host.CornerRadius=new(12);if(selected&&!entry.Available)host.SetResourceReference(Border.BorderBrushProperty,ThemeSupport.Success);else host.BorderBrush=Brushes.Transparent;}
            AutomationProperties.SetItemStatus(entry.Button,selected?(entry.Available?"Selecionado":"Selecionado · indisponível"):entry.Available?"":"Indisponível");
            if(selected){if(showReason)FeedbackText(entry.Available?entry.Reason:"Indisponível · "+entry.Reason);SaveView();if(NavigationAllowed){FocusTarget(entry).Focus();FocusTarget(entry).BringIntoView();}}
        }
    }
    private static string WindowId(WindowRef w)=>$"window:{w.Handle}:{w.Pid}:{w.Started}:{w.Generation}";
    internal string SelectedAction=>navigation.Id;internal string Feedback=>StatusLabel.Text;internal bool Confirming=>closeHold.Target is not null;internal WindowRef? HeldCloseTarget=>closeHold.Target;internal double CloseProgress=>closeHold.Progress;internal IReadOnlyList<string> ActionIds=>entries.Select(e=>e.Id).ToArray();
    internal Color SurfaceColor=>((SolidColorBrush)FindResource(ThemeSupport.Surface)).Color;internal Color SelectionColor=>((SolidColorBrush)FindResource(ThemeSupport.SuccessBackground)).Color;
    private bool IsFooterAction(string id)=>id is "save-pending" or "discard-pending"||(subpage!="operation"&&(id is "operation-details" or "dismiss-operation"));
    internal void Move(NavDirection direction)
    {
        if(!NavigationAllowed||busy||TransitionPending)return;if(Confirming){MoveConfirmation(direction is NavDirection.Up or NavDirection.Left?-1:1);return;}
        var reader=subpage=="operation"?operationText:subpage=="details"?detailsText:subpage=="update-review"?updateNotesText:null;
        if(reader?.IsKeyboardFocusWithin==true){if(direction==NavDirection.Up)reader.LineUp();else if(direction==NavDirection.Down)reader.LineDown();else{navigation=new(subpage=="operation"?direction==NavDirection.Left?"operation-text":"dismiss-operation":subpage=="update-review"?direction==NavDirection.Left?"update-notes":preparedUpdate is null?"prepare-update":"apply-update":direction==NavDirection.Left?"window-information":"switch-detail");SetSelection();}return;}
        if(AdjustGuideLimit(direction))return;if(subpage=="audio"&&navigation.Id=="volume"&&direction is NavDirection.Left or NavDirection.Right){AdjustVolume(direction==NavDirection.Left?-.05f:.05f);return;}
        var before=navigation.Id;var layout=entries.Select((entry,index)=>{var element=FocusTarget(entry);if(element.IsArrangeValid&&element.ActualHeight>0){var point=element.TranslatePoint(new Point(element.ActualWidth/2,element.ActualHeight/2),(UIElement)Content);return(entry.Id,point.X,point.Y);}return(entry.Id,0d,index*60d);}).ToArray();
        navigation=navigation with{Id=Navigation.Spatial(navigation.Id,layout,direction)};SetSelection();if(before!=navigation.Id)RequestCue(FeedbackCue.Navigation);
    }
    internal void Sequential(int step){if(!NavigationAllowed||busy||TransitionPending)return;if(Confirming){MoveConfirmation(step);return;}var before=navigation.Id;navigation=Navigation.Sequential(navigation,entries.Select(e=>e.Id).ToArray(),step);SetSelection();if(before!=navigation.Id)RequestCue(FeedbackCue.Navigation);}
    private void MoveConfirmation(int step){if(closeHold.Target is not {} target)return;closeHold.Open(target);keyboardDown=false;ReleasePointer();navigation=Navigation.Sequential(navigation,entries.Select(e=>e.Id).ToArray(),step);SetSelection(false);RequestCue(FeedbackCue.Navigation);}
    internal void ActivateEntry(){if(!NavigationAllowed||TransitionPending)return;var entry=entries.FirstOrDefault(e=>e.Id==navigation.Id);if(entry is null)return;if(Navigation.CanActivate(busy,Confirming,entry.Available)){RunInterfaceAction(entry.Button,entry.Run,entry.Id=="test-feedback"?null:entry.Id is "back" or "back-header" or "confirm-cancel"?FeedbackCue.Cancellation:FeedbackCue.Confirmation);}else if(!entry.Available){FeedbackText("Indisponível · "+entry.Reason);RequestCue(FeedbackCue.Unavailable);}}
    internal void Details(){if(!NavigationAllowed||busy||TransitionPending||Confirming||view.Current.Route is not (OverlayRoute.Hub or OverlayRoute.Apps or OverlayRoute.Details))return;RequestCue(FeedbackCue.Confirmation);var target=Controller.Apps.FirstOrDefault(w=>WindowId(w)==navigation.Id||"options:"+WindowId(w)==navigation.Id)??Controller.Recent.Items.FirstOrDefault(w=>"recent:"+WindowId(w)==navigation.Id||"options:recent:"+WindowId(w)==navigation.Id)??view.Current.Target??Controller.Session.Anchor;if(target is null){ChooseSession();return;}OpenOptions(target);}
    internal void Back()=>Back(false);
    private void Back(bool trustedDismiss)
    {
        if(CancelUpdateInteraction())return;
        if(interfaceActionDepth==0&&NavigationAllowed)RequestCue(FeedbackCue.Cancellation);
        if(view.Current.Route==OverlayRoute.PlayniteBrowser)CancelPlayniteScan();
        if(TransitionPending){if(handoffDismiss){ReaffirmDismiss(trustedDismiss);return;}CancelHandoff();input.Cancel();if(page==0&&subpage=="")HidePanel(true);else FeedbackText("Transição cancelada.");return;}
        if(Confirming){CancelHold();FeedbackText("Fechamento cancelado antes do envio.");return;}
        if(steamOwner is not null){CancelSteamOwner("Cancelado por Voltar.");return;}if(busy){effectCancellation?.Cancel();pending?.Cancel();RetainOperation("Não confirmado","Observação interrompida; o comando já enviado não pode ser desfeito. Verifique a janela.",dispatched:true);return;}
        if(view.Back()){SyncView();RefreshRows();ContentScroll.ScrollToVerticalOffset(view.Current.ScrollOffset);SetSelection(false);return;}
        if(page!=0){Page(0);return;}HidePanel(true);
    }
    private bool CanRecoverLocalFocus=>!focusConfirmed&&IsVisible&&IsActive&&OwnForeground&&!ownedDialog&&!Confirming&&!TransitionPending&&!busy&&!opening&&!exiting&&!closeRequested;
    private void OnLocalPreviewMouseDown(object sender,MouseButtonEventArgs e)
    {
        inputMethod="mouse";UpdateHints();
        recoveryMouseRelease=null;
        if(fadingOut){fadeInterrupted=true;recoveryMouseRelease=e.ChangedButton;e.Handled=true;return;}
        if(!CanRecoverLocalFocus){if(!NavigationAllowed){recoveryMouseRelease=e.ChangedButton;e.Handled=true;}return;}
        // The activation click confirms the panel, never the button underneath it.
        recoveryMouseRelease=e.ChangedButton;e.Handled=true;OpenPanel();
    }
    private void OnLocalPreviewMouseUp(object sender,MouseButtonEventArgs e)
    {
        if(recoveryMouseRelease!=e.ChangedButton)return;
        recoveryMouseRelease=null;e.Handled=true;
    }
    private static bool LocalRecoveryKey(Key key)=>key is Key.Tab or Key.Up or Key.Down or Key.Left or Key.Right or Key.Enter or Key.Space or Key.PageUp or Key.PageDown or Key.X;
    private void OnKeyDown(object sender,KeyEventArgs e)
    {
        if(fadingOut){fadeInterrupted=true;e.Handled=true;return;}
        inputMethod="keyboard";UpdateHints();
        var recoveryKey=NormalizeKey(e.Key,e.SystemKey,e.ImeProcessedKey,e.DeadCharProcessedKey);
        if(!e.IsRepeat&&LocalRecoveryKey(recoveryKey)&&(KeyboardModifiersReader()&(ModifierKeys.Alt|ModifierKeys.Control|ModifierKeys.Windows))==ModifierKeys.None&&CanRecoverLocalFocus)
        {
            // Keep this key in the release baseline while the explicit request runs.
            // Repeats cannot activate a control until the recovery key is released.
            heldKeys.Add(recoveryKey);e.Handled=true;OpenPanel();return;
        }
        var modifiers=KeyboardModifiersReader();
        var readerFocused=operationText?.IsKeyboardFocusWithin==true||detailsText?.IsKeyboardFocusWithin==true||updateNotesText?.IsKeyboardFocusWithin==true;
        if(readerFocused&&((e.Key is Key.Left or Key.Right)&&modifiers==ModifierKeys.None||e.Key==Key.Tab&&(modifiers&(ModifierKeys.Control|ModifierKeys.Alt|ModifierKeys.Windows))==ModifierKeys.None))
        {
            if(NavigationAllowed&&navigationKeysArmed){if(e.Key==Key.Tab)Sequential(modifiers.HasFlag(ModifierKeys.Shift)?-1:1);else Move(e.Key==Key.Left?NavDirection.Left:NavDirection.Right);}
            e.Handled=true;return;
        }
        if(e.OriginalSource is Slider&&e.Key==Key.Tab&&(modifiers&(ModifierKeys.Control|ModifierKeys.Alt|ModifierKeys.Windows))==ModifierKeys.None)
        {
            if(NavigationAllowed&&navigationKeysArmed&&!busy&&!TransitionPending&&!ownedDialog&&!Confirming){if(modifiers.HasFlag(ModifierKeys.Shift))SetSelection(false);else Sequential(1);}
            e.Handled=true;return;
        }
        if(e.OriginalSource is Slider or TextBox&&e.Key is not Key.Escape){if(!NavigationAllowed||!navigationKeysArmed||busy||TransitionPending||ownedDialog)e.Handled=true;return;}
        if(e.Key==Key.Tab&&navigation.Id=="guide-limit"&&!KeyboardModifiersReader().HasFlag(ModifierKeys.Shift)&&entries.FirstOrDefault(x=>x.Id=="guide-limit")?.Button.Content is Grid limitGrid&&limitGrid.Children[1] is StackPanel limitTexts&&limitTexts.Children.OfType<Slider>().FirstOrDefault() is {} editor){if(NavigationAllowed&&navigationKeysArmed&&!busy&&!TransitionPending&&!ownedDialog&&!Confirming)editor.Focus();e.Handled=true;return;}
        var key=NormalizeKey(e.Key,e.SystemKey,e.ImeProcessedKey,e.DeadCharProcessedKey);var firstPress=key!=Key.None&&heldKeys.Add(key);if(Confirming&&closeSource!=0&&firstPress&&key!=Key.Escape){CancelHold();FeedbackText("Origem da pressão mudou; fechamento cancelado.");e.Handled=true;return;}if(key==Key.None&&TransitionPending){RejectHandoff("Transição cancelada; tecla não identificada.");e.Handled=true;return;}if(TransitionPending){if(e.Key==Key.Escape&&firstPress&&!e.IsRepeat)Back(trustedDismiss:true);e.Handled=true;return;}
        if(ownedDialog){e.Handled=true;return;}if(e.Key is Key.Enter or Key.Space){if(!NavigationAllowed||!navigationKeysArmed){e.Handled=true;return;}if(Confirming&&navigation.Id=="confirm-cancel"){Back();e.Handled=true;return;}keyboardDown=true;if(Confirming&&closeSource!=0){CancelHold();FeedbackText("Origem da pressão mudou; fechamento cancelado.");}else if(!e.IsRepeat&&!Confirming){dispatchSource=0;ActivateEntry();dispatchSource=null;}e.Handled=true;return;}
        if(e.Key==Key.Escape){if(firstPress&&!e.IsRepeat)Back();e.Handled=true;return;}if(!NavigationAllowed||!navigationKeysArmed){e.Handled=true;return;}if(Confirming){if(e.Key==Key.PageUp)ConfirmScroll.PageUp();else if(e.Key==Key.PageDown)ConfirmScroll.PageDown();else if(e.Key==Key.Tab)Sequential(KeyboardModifiersReader().HasFlag(ModifierKeys.Shift)?-1:1);else if(e.Key is Key.Up or Key.Down or Key.Left or Key.Right)Move(e.Key is Key.Up or Key.Left?NavDirection.Up:NavDirection.Down);e.Handled=true;return;}switch(e.Key){case Key.Up:Move(NavDirection.Up);break;case Key.Down:Move(NavDirection.Down);break;case Key.Left:Move(NavDirection.Left);break;case Key.Right:Move(NavDirection.Right);break;case Key.Tab:Sequential(KeyboardModifiersReader().HasFlag(ModifierKeys.Shift)?-1:1);break;case Key.PageUp:Page(page-1);break;case Key.PageDown:Page(page+1);break;case Key.X:Details();break;default:return;}e.Handled=true;
    }
    internal void PollGuideStatus(XInputGuideBackend backend)
    {
        backend.PollForOverlay(IsVisible,focusCycle,Environment.TickCount64,Controller.Settings.GuideOpenMaxHoldMs);if(!IsVisible||(subpage!="integrations"&&page!=2))return;
        UpdateGuideStatus(backend.Status);
    }
    internal void UpdateGuideStatus(string status)=>SetEntryDescription("guide",status);
    private PadFrame[]? reviewFrames;
    internal void ReviewTick(PadFrame[] frames)
    {
        reviewFrames=frames;try{Tick(null,EventArgs.Empty);}finally{reviewFrames=null;}
    }
    private void Tick(object? sender,EventArgs args)
    {
        if(exiting)return;if(guideBackend is not null)PollGuideStatus(guideBackend);var frames=reviewFrames??Enumerable.Range(0,4).Select(i=>Native.ReadPad((uint)i)).ToArray();var now=Environment.TickCount64;if(effectFocusTransferred&&effectCancellation is not null&&(effectFocusTarget is null||FocusForeground()!=effectFocusTarget.Handle||!EffectTargetValid(effectFocusTarget)||!EffectInputsNeutral(frames)))effectCancellation.Cancel();if(fadingOut&&!FadeInputsNeutral(frames))fadeInterrupted=true;if(fadingOut){if(GuidePolling.Consume(ref guideToggleRequested,false))OpenPanel();return;}lastInputSample=new(frames,now,GuideHeld:guideBackend?.Held==true,GuideValid:guideBackend is null||!guideBackend.Active||guideBackend.SnapshotValid,GuideConnections:guideBackend?.Connections??0);UpdatePolling(frames.Any(f=>f.Connected));var current=input.Update(frames,IsVisible,now);UpdateControllerTelemetry(frames,current.Slot,now);if(steamOwner is{} owner&&((owner.InputQualified&&!Neutral(SteamSample(),Environment.TickCount64))||frames.Any(f=>f.Buttons.HasFlag(PadButtons.B))||(PhysicalKeyReader(0x1B)&0x8000)!=0||!SteamAlive(owner)))CancelSteamOwner("Nova entrada ou contexto alterado durante foco.");
        if(TransitionPending){if(GuidePolling.Consume(ref guideToggleRequested,current.Toggle)){Toggle(trustedDismiss:current.Toggle||guideBackend?.SnapshotValid==true);return;}if(current.Events.Pressed.HasFlag(PadButtons.B))Back(trustedDismiss:current.Slot is {} slot&&frames[slot].Connected&&!frames[slot].ReadFailed);else AdvanceHandoff(lastInputSample.Value,now);return;}
        if(GuidePolling.Consume(ref guideToggleRequested,current.Toggle)){Toggle();return;}if(!IsVisible)return;var events=current.Events;if(events.Pressed!=0||events.Direction!=0){inputMethod="controller";UpdateHints();}if(!NavigationAllowed){if(!ownedDialog&&events.Pressed.HasFlag(PadButtons.B))Back();return;}if(!navigationKeysArmed&&heldKeys.Count==0&&PhysicalKeysNeutral())navigationKeysArmed=true;
        if(Confirming&&closeSource==-1){var point=Mouse.GetPosition(HoldCloseButton);var inside=point.X>=0&&point.Y>=0&&point.X<HoldCloseButton.ActualWidth&&point.Y<HoldCloseButton.ActualHeight;var mixed=keyboardDown||heldKeys.Count>0||events.Pressed!=0||events.Direction!=0;AdvancePointerHold(pointerDown,(!pointerDown||pointerCaptured&&HoldCloseButton.IsMouseCaptured)&&!mixed,inside,now);return;}
        if(Confirming&&current.Slot is {} scrollSlot&&scrollSlot>=0&&scrollSlot<frames.Length&&!events.ADown&&events.Pressed==0&&Math.Abs((int)frames[scrollSlot].RY)>Controller.Settings.Deadzone&&now>=nextConfirmationScroll){ConfirmScroll.ScrollToVerticalOffset(ConfirmScroll.VerticalOffset+(frames[scrollSlot].RY<0?48:-48));nextConfirmationScroll=now+150;}
        if(Confirming&&navigation.Id=="confirm-cancel"&&events.Pressed.HasFlag(PadButtons.A)){Back();return;}if(Confirming&&events.Direction!=0&&closeSource!=-1){Move((NavDirection)events.Direction);return;}if(Confirming&&closeHold.Target is {} target){var sourceValid=closeSource==0||current.Slot is {} slot&&closeSource==slot+1&&current.Connected;var down=closeSource==0?keyboardDown:sourceValid&&events.ADown;if((closeSource!=0&&keyboardDown)||(closeSource==0&&events.Pressed.HasFlag(PadButtons.A)))sourceValid=false;var valid=Controller.Platform.Valid(target)?target:null;if(closeHold.Tick(down,events.Pressed.HasFlag(PadButtons.B),IsActive,sourceValid,valid,Environment.TickCount64)){CancelHold();RunClose(target);}else if(!Confirming)CancelHold();HoldProgress.Value=closeHold.Progress;ConfirmHint.Text=closeHold.Progress>0?$"Mantenha pressionado… {closeHold.Progress:P0}":"Solte e pressione novamente por 1,5 s.";return;}
        if(busy){if(events.Pressed.HasFlag(PadButtons.B))Back();return;}dispatchSource=current.Slot+1;if(events.Direction!=0)Move((NavDirection)events.Direction);if(events.Pressed.HasFlag(PadButtons.B))Back();else if(events.Pressed.HasFlag(PadButtons.A)&&events.Direction==0)ActivateEntry();else if(events.Pressed.HasFlag(PadButtons.X)&&events.Direction==0)Details();else if(events.Pressed.HasFlag(PadButtons.LB))Page(page-1);else if(events.Pressed.HasFlag(PadButtons.RB))Page(page+1);dispatchSource=null;
    }
    internal void Confirm(WindowRef target){if(!target.CanClose||!Controller.Platform.Valid(target)){FeedbackText("Janela protegida ou identidade alterada; fechamento bloqueado.");return;}SaveView();view.Open(new(OverlayRoute.Confirmation,target,navigation.Id,ContentScroll.VerticalOffset));SyncView();closeSource=dispatchSource??0;closeHold.Open(target);entries.Clear();RegisterFixed("confirm-hold",HoldCloseButton,()=>{},true,"Segure por 1,5 s após soltar");RegisterFixed("confirm-cancel",CancelCloseButton,Back,true,"Cancelar fechamento");navigation=new("confirm-hold");HeaderArea.Visibility=Visibility.Collapsed;TabArea.Visibility=Visibility.Collapsed;UpdatePersistentState();ConfirmTarget.Text=target.Title;ConfirmTarget.ToolTip=target.Title;AutomationProperties.SetName(ConfirmTarget,target.Title);ConfirmPanel.Visibility=Visibility.Visible;ContentScroll.Visibility=Visibility.Collapsed;CloseArea.Visibility=Visibility.Collapsed;HoldProgress.Value=0;ConfirmHint.Text="Aguardando soltar…";FeedbackText("Nenhum comando de fechamento enviado.");UpdateHints();SetSelection(false);}
    internal bool AdvancePointerHold(bool down,bool captured,bool inside,long now)
    {
        if(!Confirming||closeSource!=-1)return false;var target=closeHold.Target!;var context=InputContext;
        var valid=Controller.Platform.Valid(target)&&target.Same(view.Current.Target??Controller.Session.Anchor)?target:null;
        var completed=closeHold.Tick(down,false,context.Active&&context.OwnForeground&&context.Confirmed,captured&&(!down||inside),valid,now);
        HoldProgress.Value=closeHold.Progress;ConfirmHint.Text=closeHold.Progress>0?$"Mantenha pressionado… {closeHold.Progress:P0}":"Solte e pressione novamente por 1,5 s.";
        if(completed){CancelHold();RunClose(target);}else if(!Confirming)CancelHold();return completed;
    }
    private async void RunClose(WindowRef target){if(busy||exiting)return;busy=true;pending=new();RetainOperation("Pendente","Aguardando confirmação de fechamento…","close",target,true);ActionResult result;try{result=await Controller.Platform.Close(target,pending.Token);}catch(OperationCanceledException){result=new(ResultKind.Unknown,"Observação interrompida após o envio. Verifique a janela e possíveis diálogos de salvar.");}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){result=new(ResultKind.Failed,"Não foi possível solicitar o fechamento.");}finally{pending.Dispose();pending=null;busy=false;}if(exiting)return;RecordResult(result,"close",target);try{await Controller.Refresh();if(!exiting)RefreshRows();}catch(OperationCanceledException){}}
    private void RecordResult(ActionResult result,string action,WindowRef? target=null)
    {
        RetainOperation(result.ToString().Split(" · ")[0],result.Message,action,target,true);FeedbackText(result.ToString());
        if(result.Kind==ResultKind.Completed)RequestCue(FeedbackCue.Completion);else if(result.Kind is ResultKind.Failed or ResultKind.Unknown or ResultKind.Partial or ResultKind.Unavailable)RequestCue(FeedbackCue.Unavailable);
        if(!Controller.Platform.IsPreview)Controller.Diagnostics.Log(action,result.Kind);
        if(!IsVisible&&result.Kind is ResultKind.Failed or ResultKind.Unknown or ResultKind.Partial or ResultKind.Unavailable or ResultKind.Cancelled)RequestResultNotification(result.ToString());
    }
    private void RequestResultNotification(string message){if(exiting)return;if(ResultNotificationRequest is {} request){request(message);return;}if(!diagnostic&&!Controller.Platform.IsPreview)tray?.NotifyResult(message);}
    private void RunEffect(Func<Task<ActionResult>> effect,Func<bool>? valid=null)=>RunEffect((_,_)=>effect(),valid);
    private void RunEffect(Func<CancellationToken,Func<bool>,Task<ActionResult>> effect,Func<bool>? valid=null,WindowRef? focusTarget=null)
    {
        var action=navigation.Id;var target=view.Current.Target??Controller.Session.Anchor;
        QueueHandoff(()=>OperationTask=BeginEffectCore(effect,action,target,focusTarget),valid);
    }
    internal Task OperationTask {get;private set;}=Task.CompletedTask;
    private async Task BeginEffectCore(Func<CancellationToken,Func<bool>,Task<ActionResult>> effect,string action,WindowRef? target,WindowRef? focusTarget)
    {
        if(busy||exiting)return;busy=true;input.Cancel();using var cancellation=new CancellationTokenSource();effectCancellation=cancellation;effectFocusTarget=focusTarget;effectFocusTransferred=false;
        effectPadConnections=0;effectGuideConnections=0;if(lastInputSample is {} baseline){for(var i=0;i<Math.Min(4,baseline.Pads.Count);i++)if(baseline.Pads[i].Connected)effectPadConnections|=1<<i;effectGuideConnections=baseline.GuideConnections;}
        bool Continuing()
        {
            if(exiting||closeRequested||cancellation.IsCancellationRequested)return false;
            var context=InputContext;
            var own=context.Confirmed&&context.Visible&&context.Active&&context.OwnForeground&&!context.OwnedDialog;
            var targetForeground=focusTarget is not null&&FocusForeground()==focusTarget.Handle&&EffectTargetValid(focusTarget);
            if(effectFocusTransferred&&!targetForeground){cancellation.Cancel();return false;}
            if(!own&&!targetForeground){cancellation.Cancel();return false;}
            if(!own&&targetForeground&&!effectFocusTransferred){effectFocusTransferred=true;UpdatePolling();}
            guideBackend?.PollForOverlay(IsVisible,focusCycle,Environment.TickCount64,Controller.Settings.GuideOpenMaxHoldMs);
            if(!EffectInputsNeutral(EffectFramesFixture?.Invoke()??Enumerable.Range(0,4).Select(i=>Native.ReadPad((uint)i)).ToArray())){cancellation.Cancel();return false;}
            return true;
        }
        RetainOperation("Pendente","Solicitação enviada; aguardando o resultado.",action,target,true);
        ActionResult result;
        try{result=await effect(cancellation.Token,Continuing);}
        catch(OperationCanceledException){result=new(ResultKind.Unknown,"Observação interrompida; verifique o aplicativo antes de tentar novamente.");}
        catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){result=new(ResultKind.Failed,"A solicitação não pôde ser concluída.");}
        finally{effectCancellation=null;effectFocusTarget=null;effectFocusTransferred=false;busy=false;}
        if(exiting)return;RecordResult(result,action,target);
        if(!cancellation.IsCancellationRequested&&result.Kind is (ResultKind.Requested or ResultKind.Completed or ResultKind.Simulated)){if(IsVisible)HideNow(false);}UpdatePolling();UpdatePersistentState();
    }
    private void Shortcut(string keys){var target=keys=="Win+Tab"?Controller.ReturnTarget:Controller.Session.Anchor;if(target is null){FeedbackText("Nenhuma janela válida para este atalho.");return;}var steam=keys is "Shift+Tab" or "Ctrl+Tab";if(steam&&!Controller.SteamOverlayEligible(target)){FeedbackText("Overlay Steam não confirmado nesta janela.");return;}if(steam)QueueSteam(target,()=>Controller.Session.Anchor?.Same(target)==true&&Controller.SteamOverlayEligible(target));else RunEffect((c,keep)=>Controller.Platform.Shortcut(target,keys,c,keep),()=>Controller.Platform.Valid(target),target);}
    private void AdjustVolume(float delta)=>AudioAction(audio.Set(audio.Current.Volume+delta));private void AudioAction(ActionResult result)=>RecordResult(result,"audio");
    private void CycleDisplay(){var ids=new string?[]{null}.Concat(Controller.Displays.Select(d=>(string?)d.Id)).ToArray();var index=Array.IndexOf(ids,Controller.Settings.DisplayId);Change(Controller.Settings with{DisplayId=ids[(index+1)%ids.Length]});}
    private string GuideLimitTitle()=>"Guide · limite para abrir "+(Controller.Settings.GuideOpenMaxHoldMs/1000d).ToString("0.0",System.Globalization.CultureInfo.GetCultureInfo("pt-BR"))+" s";
    private bool AdjustGuideLimit(NavDirection direction){if(navigation.Id!="guide-limit"||direction is not (NavDirection.Left or NavDirection.Right))return false;if(entries.FirstOrDefault(e=>e.Id=="guide-limit")?.Button.Content is Grid grid&&grid.Children[1] is StackPanel texts&&texts.Children.OfType<Slider>().FirstOrDefault() is {} slider)slider.Value=Math.Clamp(slider.Value+(direction==NavDirection.Left?-100:100),500,3000);return true;}
    private void ApplySettings()
    {
        var previous=appliedSettings;var settings=Controller.Settings;appliedSettings=settings;
        if(settings.SoundFeedbackEnabled!=previous.SoundFeedbackEnabled||settings.HapticFeedbackEnabled!=previous.HapticFeedbackEnabled)feedbackService.Suspend();
        feedbackService.Configure(settings.SoundFeedbackEnabled,settings.HapticFeedbackEnabled);
        if(!OpenAnimationAllowed)CancelOpenAnimation();
        if(settings.Deadzone!=previous.Deadzone){input=new(settings);input.Cancel();}
        if(settings.GuideXInputEnabled!=previous.GuideXInputEnabled)ConfigureGuide();
        if(IsVisible&&(settings.DisplayId!=previous.DisplayId||settings.UiScalePercent!=previous.UiScalePercent||settings.PanelSide!=previous.PanelSide))Place();
    }
    private void Change(UserSettings settings){var soundJustEnabled=settings.SoundFeedbackEnabled&&!Controller.Settings.SoundFeedbackEnabled;Controller.Change(settings);ApplySettings();RefreshRows();if(soundJustEnabled)RequestCue(FeedbackCue.Confirmation);FeedbackText("Alteração em memória · Salvar ajustes para manter.");}
    private void Save()
    {
        var fromFooter=navigation.Id=="save-pending";var returnTo=lastContentSelection;
        try
        {
            Controller.Save();RetainOperation(Controller.Platform.IsPreview?"Simulado":"Concluído",Controller.Status);
            if(fromFooter&&!Controller.SettingsDirty)navigation=new(entries.Any(entry=>entry.Id==returnTo&&!IsFooterAction(entry.Id))?returnTo:"settings");
            FeedbackText(Controller.Status);
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RetainOperation("Falhou","Falha ao salvar; configuração anterior preservada. Tente salvar novamente ou descarte as alterações.");
            FeedbackText("Falha ao salvar; configuração anterior preservada.");
        }
        RefreshRows();
    }
    private void ConfigurePlaynite(){if(PlayniteDialogFixture is not null){DialogTask=ConfigurePlayniteCore();return;}OpenView(new(OverlayRoute.PlayniteBrowser,SelectedId:"playnite-loading"));}
    private async Task ConfigurePlayniteCore()
    {
        if(Controller.Platform.IsPreview&&PlayniteDialogFixture is null){FeedbackText("Prévia: nenhuma configuração alterada.");return;}
        ownedDialog=true;RevokeFocus("dialog");var cycle=focusCycle;
        try{
            string? path;if(PlayniteDialogFixture is {} fixture)path=await fixture();else{var dialog=new OpenFileDialog{Title="Escolha Playnite.FullscreenApp.exe",Filter="Playnite Fullscreen|Playnite.FullscreenApp.exe",CheckFileExists=true};path=dialog.ShowDialog(this)==true?dialog.FileName:null;}
            if(path is not null&&CurrentCycle(cycle))Change(Controller.Settings with{PlaynitePath=path});
        }catch(OperationCanceledException){}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){if(CurrentCycle(cycle))FeedbackText("Não foi possível escolher o Playnite.");}
        finally{
            ownedDialog=false;if(CurrentCycle(cycle)){RevokeFocus("dialog-return");var resumed=focusCycle;var focus=AcquireFocus(resumed);OpeningTask=focus;if(await focus&&CurrentCycle(resumed)){if(view.Current.Route==OverlayRoute.PlayniteBrowser)RefreshRows();SetSelection(false);}}
        }
    }
    internal void RevealSelection()=>SetSelection(false);
    private FrameworkElement FocusTarget(Entry entry)=>!entry.Available&&entry.Button.Resources["DescriptionTarget"] is FrameworkElement reason?reason:entry.Button;
    private bool AudioFor(WindowRef target)=>audioTarget?.Same(target)==true&&appAudio.Target?.Same(target)==true&&appAudio.Generation==audioGeneration;
    private string AudioBadge()=>appAudio.Availability switch{
        ApplicationAudioAvailability.Partial=>"Áudio parcialmente disponível",
        ApplicationAudioAvailability.Unavailable=>"Áudio indisponível",
        ApplicationAudioAvailability.Unknown=>"Estado não confirmado",
        _=>appAudio.MuteState switch{ApplicationMuteState.Audible=>"Som ativo",ApplicationMuteState.Muted=>"Aplicativo sem som",ApplicationMuteState.Mixed=>"Áudio misto",_=>"Estado não confirmado"}};
    private string MuteActionTitle()=>appAudio.MuteState==ApplicationMuteState.Muted?"Restaurar áudio do aplicativo":appAudio.Availability!=ApplicationAudioAvailability.Available||appAudio.MuteState is ApplicationMuteState.Mixed or ApplicationMuteState.Unknown?"Silenciar áudio disponível":"Silenciar áudio do aplicativo";
    private string AppAudioDescription()=>AudioBadge()+" · "+(appAudio.Availability==ApplicationAudioAvailability.Available?"Afeta também outras janelas deste aplicativo.":appAudio.Availability==ApplicationAudioAvailability.Partial?"Algumas saídas não foram verificadas. Afeta as janelas deste aplicativo.":"Consulte os detalhes ou tente novamente.");
    private void TrackApplicationAudio()
    {
        if(exiting)return;var context=InputContext;if(!context.Visible||!context.Confirmed||!context.Active||!context.OwnForeground){if(audioTarget is not null){applicationAudio.Suspend();audioTarget=null;audioGeneration++;}return;}
        var target=view.Current.Target??Controller.Session.Anchor;if(target is null||!Controller.Platform.Valid(target)){if(audioTarget is not null){applicationAudio.Suspend();audioTarget=null;audioGeneration++;}return;}
        if(audioTarget?.Same(target)==true)return;audioTarget=target;audioGeneration++;audioRevision=-1;appAudio=new(target,audioGeneration,0,ApplicationAudioAvailability.Unknown,ApplicationMuteState.Unknown,0,0,"Consultando sessões do aplicativo…");SessionAudioLabel.Text=AudioBadge();applicationAudio.Track(target,true,audioGeneration);
    }
    private void ApplicationAudioChanged(ApplicationAudioSnapshot snapshot)
    {
        if(exiting||Dispatcher.HasShutdownStarted)return;if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(()=>ApplicationAudioChanged(snapshot));return;}
        if(!InputContext.Visible||snapshot.Generation!=audioGeneration||audioTarget is null||!audioTarget.Same(snapshot.Target)||snapshot.Revision<=audioRevision)return;
        audioRevision=snapshot.Revision;appAudio=snapshot;SessionAudioLabel.Text=AudioBadge();
        var index=entries.FindIndex(e=>e.Id=="application-mute");if(index>=0){var entry=entries[index];var available=snapshot.CanSetMute&&Controller.Platform.Valid(audioTarget);entry.Button.IsEnabled=available;entries[index]=entry with{Available=available,Reason=AppAudioDescription()};SetEntryText("application-mute",MuteActionTitle());SetEntryDescription("application-mute",AppAudioDescription());if(entry.Button.Resources["DescriptionTarget"] is Border reason)AutomationProperties.SetName(reason,MuteActionTitle()+". "+AppAudioDescription());if(entry.Available!=available&&navigation.Id==entry.Id)SetSelection(false);}
    }
    private void FeedbackStatusChanged(FeedbackStatus status)
    {
        if(exiting||Dispatcher.HasShutdownStarted)return;if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(()=>FeedbackStatusChanged(status));return;}SetEntryDescription("test-feedback",FeedbackDescription());
    }
    private void MuteApplication(WindowRef target)
    {
        if(!AudioFor(target)||!appAudio.CanSetMute){FeedbackText("Nenhuma sessão de áudio identificada; nada foi enviado.");return;}
        var desired=appAudio.MuteState!=ApplicationMuteState.Muted;var generation=audioGeneration;
        RunInPlace(target,async token=>{var change=await applicationAudio.SetMutedAsync(target,desired,generation,token);if(!exiting)ApplicationAudioChanged(change.Snapshot);return change.Result;},"application-mute");
    }
    private void RunInPlace(WindowRef target,Func<CancellationToken,Task<ActionResult>> effect,string action)=>QueueHandoff(()=>OperationTask=RunInPlaceCore(target,effect,action),()=>Controller.Platform.Valid(target)&&(view.Current.Target??Controller.Session.Anchor)?.Same(target)==true);
    private async Task RunInPlaceCore(WindowRef target,Func<CancellationToken,Task<ActionResult>> effect,string action)
    {
        if(busy||exiting)return;var generation=++operationGeneration;busy=true;pending=new();RetainOperation("Pendente","Solicitação enviada; aguardando confirmação.",action,target,true);
        ActionResult result;try{result=await effect(pending.Token);}catch(OperationCanceledException){result=new(ResultKind.Unknown,"Observação interrompida; o comando enviado não pode ser desfeito. Verifique o aplicativo.");}catch(Exception error)when(error is InvalidOperationException or System.ComponentModel.Win32Exception){result=new(ResultKind.Failed,"Não foi possível confirmar a operação; não haverá repetição automática.");}finally{pending.Dispose();pending=null;busy=false;}
        if(exiting||generation!=operationGeneration)return;RecordResult(result,action,target);UpdatePersistentState();
    }
    private int controllerMask;
    internal void UpdateControllerTelemetry(IReadOnlyList<PadFrame> frames,int? slot,long now)
    {
        controllerDevices.UpdateConnections(frames.Select(frame=>frame.Connected&&!frame.ReadFailed).ToArray());var mask=0;for(var i=0;i<frames.Count;i++)if(frames[i].Connected&&!frames[i].ReadFailed)mask|=1<<i;
        var connectionsChanged=mask!=controllerMask;if(connectionsChanged){controllerMask=mask;nextBatteryRead=0;activeEpoch=0;feedbackService.Suspend();}connectedControllers=frames.Count(frame=>frame.Connected&&!frame.ReadFailed);var failed=frames.Any(frame=>frame.ReadFailed);
        var owner=slot is {} value&&value>=0&&value<frames.Count&&frames[value].Connected&&!frames[value].ReadFailed?(uint?)value:null;
        var ownerChanged=owner!=activeSlot;if(ownerChanged){feedbackService.Suspend();activeSlot=owner;activeEpoch=0;nextBatteryRead=0;}
        if(mask==0&&failed){activeEpoch=0;nextBatteryRead=0;SetControllerStatus("Leitura do controle indisponível");feedbackService.Suspend();return;}
        if(connectionsChanged||ownerChanged||activeSlot is null)SetControllerStatus(activeSlot is {} selected?"Controle "+(selected+1)+" · Bateria não informada":connectedControllers switch{0=>"Nenhum controle conectado",1=>failed?"1 controle confirmado · outras leituras indisponíveis":"1 controle conectado · aguardando entrada",_=>failed?$"{connectedControllers} controles confirmados · outras leituras indisponíveis":$"{connectedControllers} controles conectados · aguardando entrada"});
        var context=InputContext;var confirmed=context.Visible&&context.Confirmed&&context.Active&&context.OwnForeground&&!context.Exiting&&!context.OwnedDialog;
        if(!confirmed){if(activeSlot is {} hiddenSlot)SetControllerStatus("Controle "+(hiddenSlot+1)+" · Bateria não informada");nextBatteryRead=0;feedbackService.Suspend();return;}
        if(activeSlot is {} active&&now>=nextBatteryRead){nextBatteryRead=now+5000;var status=controllerDevices.Read(active);activeEpoch=status.ConnectionEpoch;SetControllerStatus("Controle "+(active+1)+" · "+BatteryText(status));}
        feedbackService.SetContext(new(context.Visible,confirmed,activeSlot,activeEpoch));
    }
    private void SetControllerStatus(string text){controllerStatus=text;ControllerStatusLabel.Text=text;if(controllerStatusParagraph is not null){controllerStatusParagraph.Text=text;AutomationProperties.SetName(controllerStatusParagraph,text);}}
    private static string BatteryText(ControllerDeviceSnapshot status)=>!status.Connected?"Desconectado":status.BatteryType==ControllerBatteryType.Wired?"Com fio":status.BatteryType is ControllerBatteryType.Alkaline or ControllerBatteryType.NickelMetalHydride?status.BatteryLevel switch{ControllerBatteryLevel.Empty=>"Bateria esgotada",ControllerBatteryLevel.Low=>"Bateria baixa",ControllerBatteryLevel.Medium=>"Bateria média",ControllerBatteryLevel.Full=>"Bateria cheia",_=>"Bateria não informada"}:"Bateria não informada";
    private FeedbackDispatch RequestCue(FeedbackCue cue)
    {
        if(exiting)return new(false,false,"Painel encerrado.");var context=InputContext;feedbackService.SetContext(new(context.Visible,context.Confirmed&&context.Active&&context.OwnForeground&&!context.OwnedDialog&&!context.Exiting,activeSlot,activeEpoch));
        var sourceKind=dispatchSource switch{>0=>FeedbackSourceKind.Controller,-1=>FeedbackSourceKind.Pointer,0=>FeedbackSourceKind.Keyboard,_=>inputMethod=="controller"?FeedbackSourceKind.Controller:inputMethod=="mouse"?FeedbackSourceKind.Pointer:FeedbackSourceKind.Keyboard};
        return feedbackService.Request(cue,new(sourceKind,sourceKind==FeedbackSourceKind.Controller?activeSlot:null,activeEpoch));
    }
    public void SetRenderState(string state){switch(state){case "apps":Page(1);break;case "system":Page(2);break;case "audio":Subpage("audio","volume");break;case "integrations":Subpage("integrations","test-steam");break;case "details":if(Controller.Session.Anchor is {} selected)OpenView(new(OverlayRoute.Details,selected,"switch-detail"));break;case "options":if(Controller.Session.Anchor is {} option)OpenOptions(option);break;case "confirmation":if(Controller.Session.Anchor is {} t)Confirm(t);break;case "long-error":FeedbackText("Não confirmado · A identidade da janela mudou. Selecione novamente em Apps. Nenhum comando adicional foi enviado.");break;case "steam-unavailable":navigation=new("steam-overlay");SetSelection();break;}}
}


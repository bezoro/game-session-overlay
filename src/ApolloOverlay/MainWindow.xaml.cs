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
    private (string Subpage,NavState Navigation,string ParentSelection,WindowRef? Detail,double ScrollOffset)? operationOrigin;
    internal Func<OverlayInputContext>? InputContextReader {private get;set;}
    private OverlayInputContext InputContext=>InputContextReader?.Invoke()??new(focusConfirmed,IsVisible,IsActive,OwnForeground,ownedDialog,exiting);
    internal Func<int,short> PhysicalKeyReader {private get;set;}=Native.GetAsyncKeyState;
    internal Func<nint,uint> DpiReader {private get;set;}=Native.GetDpiForWindow;
    internal Func<nint> FocusForeground {private get;set;}=Native.GetForegroundWindow;
    internal Func<nint,bool> FocusRequest {private get;set;}=Native.SetForegroundWindow;
    internal Func<Task>? RefreshFocusFixture {private get;set;}
    internal Func<Task<string?>>? PlayniteDialogFixture {private get;set;}
    internal Task OpeningTask {get;private set;}=Task.CompletedTask;
    internal Task DialogTask {get;private set;}=Task.CompletedTask;
    private long focusCycle;private bool focusConfirmed,navigationKeysArmed=true;
    private readonly WindowsAudio audio=new();private InputCoordinator input;private readonly CloseHold closeHold=new();private readonly DispatcherTimer timer;
    private bool pointerDown,pointerCaptured,narrow;private string inputMethod="keyboard";
    private readonly bool diagnostic;private HwndSource? source;private TrayHost? tray;private bool busy,exiting,opening,keyboardDown;private int? closeSource,dispatchSource;private CancellationTokenSource? pending;
    private XInputGuideBackend? guideBackend;private GuideConnection? guideConnection;private bool guideToggleRequested;
    private readonly ReleaseGate releaseGate=new();private readonly HashSet<Key> heldKeys=[];private readonly bool[] handoffPadsHeld=new bool[4],handoffPadsKnown=new bool[4];private Action? handoff;private Func<bool>? handoffValid;private bool handoffDismiss,handoffDegraded,degradedExplicit,handoffGuideHeld,handoffGuideKnown,committing;private long? degradedSince;private ReleaseSample? lastInputSample;
    internal bool TransitionPending=>handoff is not null;
    internal static Key NormalizeKey(Key key,Key system,Key ime,Key dead)
    {
        var actual=key switch{Key.System=>system,Key.ImeProcessed=>ime,Key.DeadCharProcessed=>dead,_=>key};
        return actual is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed?Key.None:actual;
    }
    private void QueueHandoff(Action action,Func<bool>? valid=null,bool dismiss=false)
    {
        if(exiting||busy)return;if(handoff is not null){if(!dismiss||handoffDismiss)return;CancelHandoff();}handoff=action;handoffValid=valid;handoffDismiss=dismiss;releaseGate.Reset();if(lastInputSample is {} sample&&Environment.TickCount64-sample.Timestamp<=80){releaseGate.Prime(sample);ObserveHandoffInputs(sample,Environment.TickCount64);}CancelHold();input.Cancel();if(!dismiss)RetainOperation("Pendente","Solte os controles para continuar. B / Esc cancela antes do envio.");FeedbackText(dismiss?"Solte os controles para ocultar o painel.":"Solte os controles para continuar · B / Esc cancela.");
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
    private void ObserveHandoffInputs(ReleaseSample sample,long now)
    {
        Array.Clear(handoffPadsKnown);handoffGuideKnown=false;if(sample.Timestamp>now||now-sample.Timestamp>80)return;
        for(var i=0;i<Math.Min(4,sample.Pads.Count);i++){var pad=sample.Pads[i];if(pad.ReadFailed)continue;handoffPadsKnown[i]=true;handoffPadsHeld[i]=pad.Connected&&(pad.Buttons!=0||Math.Abs((int)pad.X)>=Controller.Settings.Deadzone*2/3||Math.Abs((int)pad.Y)>=Controller.Settings.Deadzone*2/3||Math.Abs((int)pad.RX)>=Controller.Settings.Deadzone*2/3||Math.Abs((int)pad.RY)>=Controller.Settings.Deadzone*2/3||pad.LT>=30||pad.RT>=30);}
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
    internal bool PhysicalKeysNeutral()
    {
        if((PhysicalKeyReader(0x03)&0x8000)!=0)return false;
        for(var key=0x08;key<=0xFE;key++)if((PhysicalKeyReader(key)&0x8000)!=0)return false;
        return heldKeys.All(k=>(PhysicalKeyReader(KeyInterop.VirtualKeyFromKey(k))&0x8000)==0);
    }
    private void UpdatePolling(bool connected=true)=>timer.Interval=TimeSpan.FromMilliseconds(GuidePolling.Interval(guideBackend?.Active==true,IsVisible,connected));
    private void ConfigureGuide()
    {
        if(!GuidePolling.Allowed(Controller.Settings.GuideXInputEnabled,diagnostic,Controller.Platform.IsPreview)){
            guideConnection?.Dispose();guideConnection=null;guideBackend=null;guideToggleRequested=false;
        }else if(guideBackend is null){guideBackend=new();guideConnection=new(guideBackend);guideConnection.Start(()=>guideToggleRequested=true);}
        UpdatePolling();
    }
    private sealed record Entry(string Id,Button Button,Action Run,bool Available,string Reason);
    private readonly List<Entry> entries=[];private NavState navigation=new("return");private int page;private string subpage="";private WindowRef? detail;private string subSelection="return";private readonly string[] tabSelection=["return","refresh-apps","display"];
    public MainWindow(bool pilotRequested=false,bool renderOnly=false,IPlatform? platform=null,LocalDiagnostics? diagnostics=null)
    {
        diagnostic=renderOnly;InitializeComponent();Controller=new(platform??(renderOnly?new PreviewPlatform():null),diagnostics);input=new(Controller.Settings);
        timer=new DispatcherTimer(DispatcherPriority.Input){Interval=TimeSpan.FromMilliseconds(120)};timer.Tick+=Tick;
        HomeTab.Click+=(_,_)=>{if(NavigationAllowed)Page(0);};AppsTab.Click+=(_,_)=>{if(NavigationAllowed)Page(1);};SettingsTab.Click+=(_,_)=>{if(NavigationAllowed)Page(2);};SettingsGear.Click+=(_,_)=>{if(NavigationAllowed)Page(2);};
        BackButton.Click+=(_,_)=>{if(NavigationAllowed)Back();};
        CancelCloseButton.Click+=(_,_)=>{if(NavigationAllowed)Back();};
        SaveSettingsButton.Click+=(_,_)=>{if(NavigationAllowed&&!busy&&!TransitionPending)Save();};
        DiscardSettingsButton.Click+=(_,_)=>{if(NavigationAllowed&&!busy&&!TransitionPending){Controller.Discard();ApplySettings();RefreshRows();FeedbackText("Alterações descartadas.");}};
        DismissOperationButton.Click+=(_,_)=>DismissOperation();
        OperationDetailsButton.Click+=(_,_)=>OpenOperation();
        HoldCloseButton.PreviewMouseLeftButtonDown+=(_,e)=>{if(!NavigationAllowed||!Confirming)return;e.Handled=true;if(closeSource!=-1){CancelHold();FeedbackText("Origem da pressão mudou; fechamento cancelado.");return;}pointerDown=true;pointerCaptured=HoldCloseButton.CaptureMouse();if(!pointerCaptured)CancelHold();};
        HoldCloseButton.PreviewMouseLeftButtonUp+=(_,e)=>{e.Handled=true;pointerDown=false;if(Confirming&&closeSource==-1)AdvancePointerHold(false,true,true,Environment.TickCount64);ReleasePointer();};
        HoldCloseButton.MouseLeave+=(_,_)=>{if(pointerDown){CancelHold();FeedbackText("Pressão cancelada: o ponteiro saiu do botão.");}};
        HoldCloseButton.LostMouseCapture+=(_,_)=>{if(pointerDown){CancelHold();FeedbackText("Pressão cancelada: captura do mouse perdida.");}};
        PreviewMouseDown+=(_,_)=>{inputMethod="mouse";UpdateHints();};
        Rows.SizeChanged+=(_,e)=>{var compact=e.NewSize.Width<390;if(compact!=narrow){narrow=compact;if(!Confirming){var offset=ContentScroll.VerticalOffset;RefreshRows();ContentScroll.ScrollToVerticalOffset(offset);}}};
        PreviewKeyDown+=OnKeyDown;PreviewKeyUp+=(_,e)=>{var key=NormalizeKey(e.Key,e.SystemKey,e.ImeProcessedKey,e.DeadCharProcessedKey);if(key!=Key.None)heldKeys.Remove(key);else if(TransitionPending)RejectHandoff("Transição cancelada; tecla não identificada.");if(!navigationKeysArmed&&heldKeys.Count==0&&PhysicalKeysNeutral())navigationKeysArmed=true;if(e.Key is Key.Enter or Key.Space){keyboardDown=false;e.Handled=true;}};
        Deactivated+=(_,_)=>{if(opening||exiting||(committing&&!busy))return;RevokeFocus("deactivated",!ownedDialog);CancelHandoff();heldKeys.Clear();CancelHold();input.Cancel();if(IsVisible&&!ownedDialog){HideNow(false);}};
        SourceInitialized+=(_,_)=>{source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);source.CompositionTarget.BackgroundColor=Colors.Transparent;source.AddHook(WindowMessage);if(!diagnostic)tray=new(Dispatcher,OpenPanel,ExitFromTray);};
        Closing+=(_,_)=>Cleanup();audio.Changed+=AudioChanged;
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
    private void Toggle(bool trustedDismiss){if(busy)return;if(IsVisible)HidePanel(true,trustedDismiss);else if(!TransitionPending)OpenPanel();}
    internal void OpenPanel(){if(exiting||busy)return;OpeningTask=OpenPanelCore();}
    private async Task OpenPanelCore()
    {
        var wasVisible=IsVisible;RevokeFocus("request");var cycle=focusCycle;CancelHandoff();CancelHold();input.Cancel();if(!wasVisible)Controller.CaptureReturn();
        opening=true;try{if(!wasVisible){Place();PrepareOpenAnimation();Show();}}finally{opening=false;}UpdatePolling();audio.SetVisible(!Controller.Platform.IsPreview);RefreshRows();if(!wasVisible)AnimateOpen();
        if(!await AcquireFocus(cycle))return;
        try{await (RefreshFocusFixture?.Invoke()??Controller.Refresh());if(!CurrentCycle(cycle))return;TraceFocus("refresh",ResultKind.Completed);if(!NavigationAllowed)return;Place();RefreshRows();SetSelection(false);}
        catch(OperationCanceledException){}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){if(CurrentCycle(cycle))FeedbackText("Não foi possível atualizar as janelas. Tente abrir novamente pela bandeja.");}
    }
    private bool CurrentCycle(long cycle)=>cycle==focusCycle&&!exiting&&IsVisible;
    private bool OwnForeground=>FocusForeground()==new WindowInteropHelper(this).Handle;
    private void TraceFocus(string checkpoint,ResultKind kind,string detail=""){if(!Controller.Platform.IsPreview)Controller.Diagnostics.Log($"focus/{focusCycle}/{checkpoint}/foreground={OwnForeground}{detail}",kind);}
    private void RevokeFocus(string reason,bool supersede=true){CancelOpenAnimation();if(supersede)focusCycle++;focusConfirmed=false;navigationKeysArmed=false;input.Cancel();CancelHold();TraceFocus(reason,ResultKind.Cancelled);}
    private bool NavigationAllowed
    {
        get{var context=InputContext;if(!context.Confirmed||!context.Visible||context.OwnedDialog||context.Exiting)return false;if(context.Active&&context.OwnForeground)return true;RevokeFocus("lost");FeedbackText("Foco perdido. Abra novamente com duplo clique na bandeja; B ou Esc dispensa o painel.");return false;}
    }
    private async Task<bool> AcquireFocus(long cycle)
    {
        if(!CurrentCycle(cycle)||ownedDialog)return false;bool activated,requested;opening=true;try{activated=Activate();requested=FocusRequest(new WindowInteropHelper(this).Handle);}finally{opening=false;}
        TraceFocus("request-native",ResultKind.Requested,$"/activate={activated}/request={requested}");
        for(var attempt=0;attempt<=6;attempt++){
            if(!CurrentCycle(cycle)||ownedDialog)return false;
            if(IsActive&&OwnForeground){focusConfirmed=true;navigationKeysArmed=heldKeys.Count==0&&PhysicalKeysNeutral();input.Cancel();TraceFocus("confirmed",ResultKind.Completed);return true;}
            if(!requested||attempt==6){TraceFocus("unconfirmed",requested?ResultKind.Unknown:ResultKind.Failed);FeedbackText("O Windows não confirmou o foco. Tente duplo clique na bandeja; B ou Esc dispensa o painel.");return false;}
            await Task.Delay(50);
        }return false;
    }
    internal void ExitFromTray(){if(!exiting)Close();}
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
        var target=Controller.ReturnTarget??Controller.Session.Anchor;var monitor=target is null?0:Native.MonitorFromWindow(target.Handle,2);var info=new Native.MonitorInfo{Size=System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>(),Device=""};var active=monitor!=0&&Native.GetMonitorInfo(monitor,ref info)?screens.FirstOrDefault(d=>d.Id==info.Device):null;
        var chosen=screens.FirstOrDefault(d=>d.Id==Controller.Settings.DisplayId)??active??screens.FirstOrDefault(d=>d.Primary)??screens[0];var hwnd=new WindowInteropHelper(this).Handle;
        if(hwnd!=0)Native.SetWindowPos(hwnd,0,chosen.X+16,chosen.Y+16,0,0,0x15);
        var dpi=DpiReader(hwnd);if(dpi==0)dpi=96;var scale=dpi/96d;
        var density=SidebarLayout.Density(chosen,dpi);((FrameworkElement)Content).LayoutTransform=new ScaleTransform(density,density);var placement=SidebarLayout.Place(chosen,dpi);Width=placement.Width/scale;Height=placement.Height/scale;
        if(hwnd!=0)Native.SetWindowPos(hwnd,(nint)(-1),placement.X,placement.Y,placement.Width,placement.Height,0x10);
        }finally{placing=false;}
    }
    private void HidePanel(bool restore,bool trustedDismiss=false)
    {
        if(handoffDismiss)ReaffirmDismiss(trustedDismiss);
        var target=Controller.ReturnTarget;QueueHandoff(()=>HideNow(restore,target),()=>target is null?Controller.ReturnTarget is null:Controller.ReturnTarget?.Same(target)==true&&Controller.Platform.Valid(target),dismiss:true);
    }
    private async void HideNow(bool restore,WindowRef? returnTarget=null)
    {
        if(!IsVisible)return;var wasForeground=Native.GetForegroundWindow()==new WindowInteropHelper(this).Handle;RevokeFocus("hide");CancelHandoff();heldKeys.Clear();CancelHold();pending?.Cancel();input.Cancel();Sidebar.BeginAnimation(OpacityProperty,null);Hide();audio.SetVisible(false);UpdatePolling();
        if(restore&&wasForeground&&returnTarget is {} target&&Controller.Platform.Valid(target)){var result=await Controller.Platform.Switch(target);if(result.Kind!=ResultKind.Completed&&result.Kind!=ResultKind.Simulated&&!exiting){if(!Controller.Platform.IsPreview)Controller.Diagnostics.Log("focus-return",result.Kind);FeedbackText(result.Message);}}
    }
    private void Cleanup(){if(exiting)return;exiting=true;RevokeFocus("cleanup");CancelHandoff();heldKeys.Clear();SystemParameters.StaticPropertyChanged-=OnSystemParametersChanged;timer.Stop();timer.Tick-=Tick;guideConnection?.Dispose();guideConnection=null;guideBackend=null;guideToggleRequested=false;pending?.Cancel();ResultNotificationRequest=null;audio.Dispose();source?.RemoveHook(WindowMessage);tray?.Dispose();Controller.Dispose();}
    private void FeedbackText(string text)=>StatusLabel.Text=text;
    private void RetainOperation(string state,string message,string action="",WindowRef? target=null,bool dispatched=false){Controller.SetOperation(state,message,action,target,dispatched);UpdatePersistentState();}
    private void UpdatePersistentState()
    {
        if(Rows.Children.Count>0){
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
        var operation=Controller.Operation;OperationPanel.Visibility=operation is null||Confirming||subpage=="operation"?Visibility.Collapsed:Visibility.Visible;
        var text=operation is null?"":operation.State+" · "+operation.Message;
        if(OperationLabel.Text!=text){OperationLabel.Text=text;System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(OperationLabel)?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);}
        OperationDetailsButton.IsEnabled=!busy&&!TransitionPending;OperationLabel.ToolTip=text;SaveSettingsButton.IsEnabled=!busy&&!TransitionPending;DiscardSettingsButton.IsEnabled=!busy&&!TransitionPending;DismissOperationButton.IsEnabled=!busy&&!TransitionPending;
    }
    private void OpenOperation(){if(!NavigationAllowed||busy||TransitionPending||Confirming||Controller.Operation is null)return;operationOrigin=(subpage,navigation,subSelection,detail,ContentScroll.VerticalOffset);Subpage("operation","operation-text");}
    private void DismissOperation(){if(!NavigationAllowed||busy||TransitionPending||Confirming)return;Controller.DismissOperation();if(subpage=="operation")Back();else{UpdatePersistentState();SetSelection(false);}}
    private void UpdateHints()
    {
        if(Confirming){ModeLabel.Text=inputMethod=="controller"?"A segurar · B cancelar":inputMethod=="mouse"?"Segure para confirmar · Cancelar interrompe":"Enter / Espaço segurar · Esc cancelar";return;}
        var details=page==1||subpage=="details";
        ModeLabel.Text=inputMethod switch{
            "mouse"=>"Clique para selecionar · bandeja: duplo clique para abrir",
            "controller"=>"A selecionar · B voltar"+(details?" · X detalhes":"")+"\nLB/RB abas · direcional mover"+(Controller.Settings.GuideXInputEnabled&&guideBackend?.Active==true?" · Guide abrir":"")+" · bandeja: abrir",
            _=>"Enter selecionar · Esc voltar"+(details?" · X detalhes":"")+"\nPgUp/PgDn abas · Setas / Tab mover · bandeja: abrir"};
    }
    private void ReleasePointer(){pointerDown=false;pointerCaptured=false;if(HoldCloseButton.IsMouseCaptured)HoldCloseButton.ReleaseMouseCapture();}
    private void CancelHold(){ReleasePointer();closeHold.Cancel();HeaderArea.Visibility=Visibility.Visible;TabArea.Visibility=Visibility.Visible;closeSource=null;keyboardDown=false;ConfirmPanel.Visibility=Visibility.Collapsed;ContentScroll.Visibility=Visibility.Visible;CloseArea.Visibility=Visibility.Visible;UpdatePersistentState();UpdateHints();}
    internal void Page(int index){if(TransitionPending)return;index=(index+3)%3;if(subpage==""&&index==page)tabSelection[index]=index switch{0=>"return",1=>Controller.Apps.FirstOrDefault() is {} app?WindowId(app):"refresh-apps",_=>"display"};else tabSelection[page]=navigation.Id;page=index;subpage="";detail=null;operationOrigin=null;navigation=new(tabSelection[page]);CancelHold();RefreshRows();}
    private void Subpage(string name,string first){subSelection=navigation.Id;subpage=name;navigation=new(first);RefreshRows();}
    internal void RefreshRows()
    {
        var previous=entries.Select(e=>e.Id).ToArray();Rows.Children.Clear();Rows.RowDefinitions.Clear();Rows.ColumnDefinitions.Clear();Rows.ColumnDefinitions.Add(new());Rows.ColumnDefinitions.Add(new());CloseArea.Children.Clear();entries.Clear();operationText=null;
        entries.Add(new("settings",SettingsGear,()=>Page(2),true,"Abrir Sistema"));ContextCard.Visibility=page==0&&subpage==""?Visibility.Visible:Visibility.Collapsed;
        var context=Controller.Session.Anchor is {} anchor&&Controller.Platform.Valid(anchor)?anchor:null;
        TargetLabel.Text=context?.Title??"Escolha uma janela";TargetLabel.ToolTip=context?.Title;
        AutomationProperties.SetName(ContextCard,context?.Title??"Nenhuma janela selecionada");
        SessionEvidence.Text=context?.ProcessName??"Aplicativos";ContextStatus.Text=context is null?"Sem seleção":"Disponível";ContextStatusDot.Opacity=context is null?.3:1;
        ContextIcon.Source=context is null?null:Controller.Platform.Icon(context);ContextIcon.Visibility=ContextIcon.Source is null?Visibility.Collapsed:Visibility.Visible;ContextGlyph.Visibility=ContextIcon.Source is null?Visibility.Visible:Visibility.Collapsed;
        BackButton.Visibility=subpage!=""?Visibility.Visible:Visibility.Collapsed;UpdateHints();UpdatePersistentState();
        foreach(var pair in new[]{(HomeTab,0),(AppsTab,1),(SettingsTab,2)}){var selected=pair.Item2==page;pair.Item1.BorderThickness=new(0,0,0,selected?2:0);pair.Item1.SetResourceReference(BorderBrushProperty,ThemeSupport.Success);pair.Item1.SetResourceReference(BackgroundProperty,selected?ThemeSupport.Control:ThemeSupport.Card);pair.Item1.SetResourceReference(ForegroundProperty,selected?ThemeSupport.Text:ThemeSupport.SecondaryText);AutomationProperties.SetName(pair.Item1,(pair.Item2 switch{0=>"Sessão",1=>"Aplicativos",_=>"Sistema"})+(selected?" · selecionada":""));AutomationProperties.SetItemStatus(pair.Item1,selected?"Selecionada":"Não selecionada");}
        PageTitle.Text=subpage switch{"audio"=>"Áudio do Windows","details"=>"Detalhes da janela","integrations"=>"Integrações","operation"=>"Resultado da operação",_=>page switch{0=>"Controle da sessão",1=>"Aplicativos",_=>"Sistema"}};
        void Section(string title){var label=new TextBlock{Text=title,FontSize=11,FontWeight=FontWeights.SemiBold,Margin=new(2,18,0,8)};label.SetResourceReference(TextBlock.ForegroundProperty,ThemeSupport.SecondaryText);var r=Rows.RowDefinitions.Count;Rows.RowDefinitions.Add(new(){Height=GridLength.Auto});Grid.SetRow(label,r);Grid.SetColumnSpan(label,2);Rows.Children.Add(label);}
        void Add(string id,string title,string description,Action run,bool available=true,string icon="\uE8A7",bool danger=false,int? gridRow=null,int column=0){if(narrow&&gridRow is not null){gridRow=null;column=0;}var row=gridRow??Rows.RowDefinitions.Count;if(row>=Rows.RowDefinitions.Count)Rows.RowDefinitions.Add(new(){Height=GridLength.Auto});var button=MakeButton(id,title,description,run,available,icon,danger);Grid.SetRow(button,row);Grid.SetColumn(button,column);Grid.SetColumnSpan(button,gridRow is null?2:1);if(gridRow is not null)button.Margin=new(column==0?0:4,4,column==0?4:0,4);if(available)Rows.Children.Add(button);else{var descriptionHost=new Border{Focusable=true,Tag="description:"+id,Margin=new(-2)};AutomationProperties.SetName(descriptionHost,title+". "+description);AutomationProperties.SetHelpText(descriptionHost,entries[^1].Reason);button.Resources["DescriptionTarget"]=descriptionHost;Grid.SetRow(descriptionHost,row);Grid.SetColumn(descriptionHost,column);Grid.SetColumnSpan(descriptionHost,gridRow is null?2:1);Rows.Children.Add(descriptionHost);Rows.Children.Add(button);}
        }
        if(subpage=="operation"&&Controller.Operation is {} operation){
            Add("operation-text","Ler mensagem completa","Setas para rolar · B / Esc para voltar",()=>operationText?.Focus());
            operationText=new TextBox{Text=operation.Message,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MinHeight=120,MaxHeight=160,FontSize=16,Padding=new(12),Margin=new(0,8,0,8)};operationText.SetResourceReference(BackgroundProperty,ThemeSupport.Card);operationText.SetResourceReference(ForegroundProperty,ThemeSupport.Text);AutomationProperties.SetName(operationText,"Resultado completo · "+operation.State);AutomationProperties.SetHelpText(operationText,"Use as setas para ler; Esc ou B volta ao painel.");var r=Rows.RowDefinitions.Count;Rows.RowDefinitions.Add(new(){Height=GridLength.Auto});Grid.SetRow(operationText,r);Grid.SetColumnSpan(operationText,2);Rows.Children.Add(operationText);
            Add("dismiss-operation","Dispensar resultado","Remove apenas esta mensagem",DismissOperation);Add("back","Voltar ao painel","Mantém o resultado disponível",Back,true,"\uE72B");
        }else if(subpage=="audio"){
            var state=audio.Current;Add("volume-down","Diminuir volume","− 5% · saída padrão do Windows",()=>AdjustVolume(-.05f),state.Available,"\uE767");Add("volume","Volume · "+(state.Available?state.Volume.ToString("P0"):"indisponível"),"Aumentar + 5% · ← / → ajusta · saída padrão do Windows",()=>AdjustVolume(.05f),state.Available,"\uE767");Add("mute",state.Muted?"Ativar som":"Silenciar","Afeta a saída de áudio do Windows",()=>AudioAction(audio.Set(muted:!audio.Current.Muted)),state.Available,"\uE74F");Add("back","Voltar",state.Status,Back,true,"\uE72B");
        }else if(subpage=="details"&&detail is {} window){
            var identity=new StackPanel{Margin=new(2,4,2,16)};identity.Children.Add(new TextBlock{Text=window.Title,FontSize=22,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});identity.Children.Add(new TextBlock{Text=$"{window.ProcessName} · PID {window.Pid}",FontSize=15,Margin=new(0,8,0,0)});AutomationProperties.SetName(identity,window.Title);Rows.RowDefinitions.Add(new(){Height=GridLength.Auto});Grid.SetColumnSpan(identity,2);Rows.Children.Add(identity);
            Add("switch-detail","Alternar para esta janela",$"{window.ProcessName} · PID {window.Pid}",()=>RunEffect(()=>Controller.Platform.Switch(window),()=>Controller.Platform.Valid(window)),true,"\uE8A7");Add("select-session","Usar nesta sessão","Mantém esta janela como referência para retomar e acessar atalhos",()=>{Controller.Select(window);subpage="";Page(0);},!window.Launcher&&!window.Protected,"\uE73E");Add("close-detail","Fechar esta janela…",window.CanClose?"Nova pressão longa · 1,5 s":"Janela protegida contra fechamento",()=>Confirm(window),window.CanClose,"\uE74D",true);Add("back","Voltar à lista","B / Esc também retorna",Back,true,"\uE72B");
        }else if(subpage=="integrations"){
            Add("test-steam","Solicitar overlay Steam",SteamPermission.Reason(Controller.SteamEvidence),()=>Shortcut(Controller.Settings.SteamShortcut),Controller.Session.Anchor is not null&&Controller.SteamEvidence==ModuleEvidence.Loaded,"\uE7FC");Add("guide","Botão Xbox / Guide · "+(Controller.Settings.GuideXInputEnabled?"ligado":"desligado"),GuideDescription(),()=>Change(Controller.Settings with{GuideXInputEnabled=!Controller.Settings.GuideXInputEnabled}),!diagnostic&&!Controller.Platform.IsPreview,"\uE7FC");Add("external-close","Fechar overlays externos","Indisponível: visibilidade e navegação externas não verificadas",()=>{},false,"\uE711");Add("back","Voltar","Duplo clique na bandeja também abre o painel",Back,true,"\uE72B");
        }else if(page==0){
            var game=context;Add("return",game is null?"Escolher janela":"Retomar janela",game?.Title??"Escolher janela em Aplicativos",()=>{if(game is not null)RunEffect(()=>Controller.Platform.Switch(game),()=>Controller.Platform.Valid(game));else Page(1);},true,"\uE72B");
            Add("playnite","Playnite",Controller.Settings.PlaynitePath is null?"Configure o caminho em Sistema":"Biblioteca em tela cheia",()=>RunEffect(()=>Controller.Launch(true)),Controller.Settings.PlaynitePath is not null||Controller.Apps.Any(a=>a.ProcessName.Equals("Playnite.FullscreenApp",StringComparison.OrdinalIgnoreCase)),"\uE7FC");
            Add("steam","Steam Big Picture",Controller.SteamAvailable?"Sua biblioteca em modo de controle":"Protocolo Steam indisponível",()=>RunEffect(()=>Controller.Launch(false)),Controller.SteamAvailable,"\uE7FC");
            Add("apps","Aplicativos abertos",Controller.Apps.Count==1?"1 janela disponível":$"{Controller.Apps.Count} janelas disponíveis",()=>Page(1),true,"\uE737");
            var label=new TextBlock{Text="ACESSO RÁPIDO",FontSize=13,Margin=new(2,12,0,5)};label.SetResourceReference(TextBlock.ForegroundProperty,ThemeSupport.SecondaryText);var row=Rows.RowDefinitions.Count;Rows.RowDefinitions.Add(new(){Height=GridLength.Auto});Grid.SetRow(label,row);Grid.SetColumnSpan(label,2);Rows.Children.Add(label);
            var steamContext=Controller.ReturnTarget;var menu=Controller.Platform.CanSteamMenu(steamContext);var steamGame=steamContext is not null&&!steamContext.Launcher&&!steamContext.Protected&&Controller.Platform.SteamEvidence(steamContext)==ModuleEvidence.Loaded;
            Add("gamebar","Game Bar",game is null?"Escolha uma janela em Aplicativos":"Xbox · janela da sessão",()=>Shortcut("Win+G"),Controller.Session.Anchor is not null,"\uE7FC",gridRow:row+1);Add("steam-overlay",menu?"Menu do Steam Big Picture":"Overlay Steam",menu?"Menu · "+steamContext!.Title:steamGame?"Overlay · "+steamContext!.Title:"Sem contexto Steam confirmado",()=>{if(steamContext is null)return;if(menu)RunEffect(()=>Controller.Platform.SteamMenu(steamContext),()=>Controller.Platform.CanSteamMenu(steamContext));else RunEffect(()=>Controller.Platform.Shortcut(steamContext,Controller.Settings.SteamShortcut),()=>Controller.Platform.Valid(steamContext)&&Controller.Platform.SteamEvidence(steamContext)==ModuleEvidence.Loaded);},menu||steamGame,"\uE7FC",gridRow:row+1,column:1);
            Add("taskview","Visão de tarefas","Janelas do Windows",()=>RunEffect(Controller.Platform.TaskView),true,"\uE7C4",gridRow:row+2);Add("audio","Áudio",audio.Current.Available?(audio.Current.Muted?"Sem som":audio.Current.Volume.ToString("P0")):"Saída indisponível",()=>Subpage("audio","volume"),true,"\uE767",gridRow:row+2,column:1);
            var close=MakeButton("close","Fechar janela…",Controller.Session.Anchor?.CanClose==true?"Confirmação por pressão longa":"Selecione uma janela elegível em Aplicativos",()=>{if(Controller.Session.Anchor is {} t)Confirm(t);},Controller.Session.Anchor?.CanClose==true,"\uE74D",true);close.MinHeight=44;close.Padding=new(12,8,12,8);close.Background=Brushes.Transparent;close.BorderThickness=new(0);CloseArea.Children.Add(close);
        }else if(page==1){
            foreach(var app in Controller.Apps){var captured=app;var id=WindowId(app);Add(id,app.Title,app.ProcessName+" · Alternar janela",()=>RunEffect(()=>Controller.Platform.Switch(captured),()=>Controller.Platform.Valid(captured)),true,"\uE737");var appButton=entries[^1].Button;var content=(Grid)appButton.Content;((StackPanel)content.Children[1]).Margin=new(0,0,58,0);if(Controller.Platform.Icon(app) is {} appIcon){content.Children.RemoveAt(0);content.Children.Insert(0,new Image{Source=appIcon,Width=28,Height=28,HorizontalAlignment=HorizontalAlignment.Left});}
                var more=new Button{Content="···",Width=44,MinHeight=44,Padding=new(4),Margin=new(0,8,8,8),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center,Tag="details:"+id};AutomationProperties.SetName(more,"Detalhes de "+app.Title);AutomationProperties.SetHelpText(more,"Ver identidade, usar nesta sessão ou fechar com confirmação");more.Click+=(_,_)=>{if(NavigationAllowed&&!busy&&!TransitionPending&&!Confirming){navigation=new(id);detail=captured;Subpage("details","switch-detail");}};Grid.SetRow(more,Rows.RowDefinitions.Count-1);Grid.SetColumn(more,1);Rows.Children.Add(new Border{Child=more,HorizontalAlignment=HorizontalAlignment.Right,Width=60});Grid.SetRow(Rows.Children[^1],Rows.RowDefinitions.Count-1);Grid.SetColumn(Rows.Children[^1],1);
            }
            if(Controller.Apps.Count==0){var text=new TextBlock{Text="Nenhuma janela disponível. As bibliotecas, áudio e ajustes continuam acessíveis.",TextWrapping=TextWrapping.Wrap,FontSize=21,Margin=new(6,20,6,24)};Rows.RowDefinitions.Add(new(){Height=GridLength.Auto});Grid.SetColumnSpan(text,2);Rows.Children.Add(text);}
            Add("refresh-apps","Atualizar janelas","Obtém apenas janelas relevantes e visíveis",async()=>{try{await Controller.Refresh();RefreshRows();}catch(OperationCanceledException){}},true,"\uE72C");
        }else{
            Section("EXIBIÇÃO E APARÊNCIA");Add("display","Monitor · "+(Controller.Settings.DisplayId??"janela ativa"),"A alterna entre automático e as telas disponíveis",CycleDisplay,true,"\uE7F4");
            Add("motion",Controller.Settings.ReducedMotion?"Animações reduzidas":"Animações suaves","A alterna · não há animação contínua",()=>Change(Controller.Settings with{ReducedMotion=!Controller.Settings.ReducedMotion}),true,"\uE713");
            Section("ENTRADA E CONTROLE");Add("deadzone","Deadzone · "+Controller.Settings.Deadzone,"A alterna sensibilidade do analógico",()=>Change(Controller.Settings with{Deadzone=Controller.Settings.Deadzone<12000?12000:Controller.Settings.Deadzone<18000?18000:Controller.Settings.Deadzone<24000?24000:12000}),true,"\uE713");Add("guide","Botão Xbox / Guide · "+(Controller.Settings.GuideXInputEnabled?"ligado":"desligado"),GuideDescription(),()=>Change(Controller.Settings with{GuideXInputEnabled=!Controller.Settings.GuideXInputEnabled}),!diagnostic&&!Controller.Platform.IsPreview,"\uE713");
            Add("guide-limit",GuideLimitTitle(),"← / → ajusta · 0,5–3,0 s · Salvar para manter",()=>{},true,"\uE713");
            var limitButton=entries[^1].Button;limitButton.MinHeight=112;
            var limitTexts=(StackPanel)((Grid)limitButton.Content).Children[1];
            var limitSlider=new Slider{Minimum=500,Maximum=3000,TickFrequency=100,SmallChange=100,LargeChange=100,IsSnapToTickEnabled=true,Focusable=true,Value=Controller.Settings.GuideOpenMaxHoldMs,Margin=new(0,9,0,0)};
            AutomationProperties.SetName(limitSlider,"Limite de pressão do Guide para abrir, em milissegundos");AutomationProperties.SetHelpText(limitSlider,"De 500 a 3000 ms. Setas ajustam 100 ms; Home e End definem os limites.");
            limitSlider.PreviewKeyDown+=(_,e)=>{var delta=e.Key switch{Key.Left or Key.Down=>-100,Key.Right or Key.Up=>100,Key.PageDown=>-500,Key.PageUp=>500,_=>0};if(delta!=0){limitSlider.Value=Math.Clamp(limitSlider.Value+delta,500,3000);e.Handled=true;}else if(e.Key is Key.Home or Key.End){limitSlider.Value=e.Key==Key.Home?500:3000;e.Handled=true;}};
            limitSlider.ValueChanged+=(_,_)=>{var value=(int)Math.Round(limitSlider.Value/100)*100;Controller.Change(Controller.Settings with{GuideOpenMaxHoldMs=value});SetEntryText("guide-limit",GuideLimitTitle());UpdatePersistentState();FeedbackText("Alteração em memória · Salvar ajustes para manter.");};limitTexts.Children.Add(limitSlider);
            Section("INTEGRAÇÕES");Add("configure-playnite","Caminho do Playnite",Controller.Settings.PlaynitePath is null?"Escolher executável…":"Instalação configurada",ConfigurePlaynite,true,"\uE8B7");
            Add("shortcut","Atalho Steam · "+Controller.Settings.SteamShortcut,"A alterna Shift+Tab / Ctrl+Tab",()=>Change(Controller.Settings with{SteamShortcut=Controller.Settings.SteamShortcut=="Shift+Tab"?"Ctrl+Tab":"Shift+Tab"}),true,"\uE765");
            Add("integrations","Integrações e limites","Guide, Steam e funções externas",()=>Subpage("integrations","test-steam"),true,"\uE713");Add("save","Salvar ajustes","AppData · cópia anterior preservada",Save,true,"\uE74E");Add("exit","Encerrar overlay",Controller.SettingsDirty?"Alterações não salvas serão perdidas":"Encerra observadores e remove o ícone da bandeja",()=>QueueHandoff(Close),true,"\uE7E8");
        }
        UpdatePersistentState();
        navigation=navigation with{Id=Navigation.Preserve(navigation.Id,previous,entries.Select(e=>e.Id).ToArray())};SetSelection(false);
    }
    private string GuideDescription()=>Controller.Platform.IsPreview?"Prévia · conexão Guide não é consultada":!Controller.Settings.GuideXInputEnabled?"Desativado · abra pelo duplo clique na bandeja":guideBackend?.Active==true?guideBackend.Status:guideConnection?.Status??"Controle ou leitura Guide indisponível";
    private Button MakeButton(string id,string title,string description,Action action,bool available,string icon,bool danger)
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(42)});grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});var glyph=ActionIcon(id,icon);if(page==2&&subpage==""&&id is "deadzone" or "motion" or "guide-limit" or "shortcut" or "integrations" or "save"){glyph.Visibility=Visibility.Collapsed;grid.ColumnDefinitions[0].Width=new(0);}grid.Children.Add(glyph);var texts=new StackPanel{VerticalAlignment=VerticalAlignment.Center};texts.Children.Add(new TextBlock{Text=title,FontSize=id=="return"?22:18,TextTrimming=TextTrimming.CharacterEllipsis,FontWeight=FontWeights.SemiBold});if(description.Length>0)texts.Children.Add(new TextBlock{Text=description,FontSize=15,TextWrapping=TextWrapping.Wrap,Margin=new(0,4,0,0)});Grid.SetColumn(texts,1);grid.Children.Add(texts);
        var badge=new Border{CornerRadius=new(18),Width=30,Height=30,Margin=new(8,0,0,0),Visibility=Visibility.Collapsed,Child=new TextBlock{Text="A",FontSize=18,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Foreground=new SolidColorBrush(Color.FromRgb(15,38,20))}};badge.SetResourceReference(BackgroundProperty,ThemeSupport.Success);Grid.SetColumn(badge,2);grid.Children.Add(badge);
        if(id=="apps"){var count=new Border{CornerRadius=new(15),Padding=new(9,4,9,4),Margin=new(8,0,0,0),VerticalAlignment=VerticalAlignment.Center,Child=new TextBlock{Text=Controller.Apps.Count.ToString(),FontSize=17}};count.SetResourceReference(BackgroundProperty,ThemeSupport.Card);Grid.SetColumn(count,2);grid.Children.Add(count);grid.Resources["CountBadge"]=count;}
        ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty,SystemParameters.HighContrast?SystemColors.HighlightTextBrushKey:ThemeSupport.Surface);
        var button=new Button{Content=grid,Tag=id,MinHeight=id=="return"?76:56,IsEnabled=available,Padding=new(12,8,12,8)};
        if(page==0&&subpage==""&&id is "playnite" or "steam" or "apps"){button.Background=Brushes.Transparent;button.BorderThickness=new(0);button.Margin=new(0,2,0,2);}
        if(id=="return"&&texts.Children.Count>1&&texts.Children[1] is TextBlock targetText){targetText.TextWrapping=TextWrapping.NoWrap;targetText.TextTrimming=TextTrimming.CharacterEllipsis;}
        if(id=="close"&&texts.Children.Count>1)texts.Children.RemoveAt(1);
        if(texts.Children.Count>1&&texts.Children[1] is TextBlock descriptionText)descriptionText.SetResourceReference(TextBlock.ForegroundProperty,ThemeSupport.SecondaryText);if(danger)button.SetResourceReference(ForegroundProperty,ThemeSupport.Critical);
        AutomationProperties.SetName(button,title);AutomationProperties.SetHelpText(button,description);button.ToolTip=title+"\n"+description;
        button.Resources["ABadge"]=badge;button.GotKeyboardFocus+=(_,_)=>{if(navigation.Id!=id){navigation=new(id);SetSelection(false);}};button.Click+=(_,_)=>{navigation=new(id);SetSelection();dispatchSource=-1;ActivateEntry();dispatchSource=null;};entries.Add(new(id,button,action,available,description));return button;
    }
    private FrameworkElement ActionIcon(string id,string fallback)
    {
        var key=id.StartsWith("window:",StringComparison.Ordinal)?"apps":id switch{"return" or "back"=>"back","steam" or "steam-overlay" or "test-steam"=>"steam","gamebar" or "chord" or "guide"=>"gamebar","apps" or "refresh-apps" or "next-apps" or "switch-detail"=>"apps","taskview" or "display"=>"taskview","audio" or "volume" or "volume-down"=>"speaker","mute"=>audio.Current.Muted?"speaker":"mute","close" or "close-detail" or "exit" or "external-close"=>"close","playnite" or "configure-playnite"=>"playnite","select-session"=>"controller",_=>"settings"};
        var icon=new System.Windows.Shapes.Path{Data=(Geometry)FindResource("Icon."+key),Stretch=Stretch.Uniform,Width=26,Height=26,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center};
        icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,id is "close" or "close-detail"?ThemeSupport.Critical:ThemeSupport.Text);return icon;
    }
    private void SetSelection(bool showReason=true)
    {
        foreach(var entry in entries){var selected=entry.Id==navigation.Id;var danger=entry.Id is "close" or "close-detail";var primary=entry.Id=="return";
            entry.Button.BorderThickness=new(selected?2:entry.Id=="close"?0:1);entry.Button.SetResourceReference(BorderBrushProperty,selected?ThemeSupport.Success:ThemeSupport.Stroke);
            entry.Button.SetResourceReference(BackgroundProperty,primary?ThemeSupport.SuccessBackground:selected?ThemeSupport.Card:ThemeSupport.Control);if(entry.Id=="close")entry.Button.Background=Brushes.Transparent;
            if(page==0&&subpage==""&&entry.Id is "playnite" or "steam" or "apps"){entry.Button.Background=selected?(Brush)FindResource(ThemeSupport.Card):Brushes.Transparent;entry.Button.BorderThickness=new(selected?2:0);}entry.Button.Effect=null;if(entry.Button.Resources["ABadge"] is Border badge)badge.Visibility=selected&&entry.Available&&inputMethod=="controller"&&!entry.Id.StartsWith("window:",StringComparison.Ordinal)?Visibility.Visible:Visibility.Collapsed;
            if(entry.Button.Resources["DescriptionTarget"] is Border host){host.BorderThickness=new(selected?2:0);host.CornerRadius=new(12);host.SetResourceReference(Border.BorderBrushProperty,ThemeSupport.Success);}
            if(selected){if(showReason)FeedbackText(entry.Available?entry.Reason:"Indisponível · "+entry.Reason);if(NavigationAllowed){if(entry.Available)entry.Button.Focus();else if(entry.Button.Resources["DescriptionTarget"] is Border description)description.Focus();entry.Button.BringIntoView();}}
        }
    }
    private static string WindowId(WindowRef w)=>$"window:{w.Handle}:{w.Pid}:{w.Started}:{w.Generation}";
    internal string SelectedAction=>navigation.Id;internal string Feedback=>StatusLabel.Text;internal bool Confirming=>closeHold.Target is not null;internal WindowRef? HeldCloseTarget=>closeHold.Target;internal double CloseProgress=>closeHold.Progress;internal IReadOnlyList<string> ActionIds=>entries.Select(e=>e.Id).ToArray();
    internal Color SurfaceColor=>((SolidColorBrush)FindResource(ThemeSupport.Surface)).Color;internal Color SelectionColor=>((SolidColorBrush)FindResource(ThemeSupport.SuccessBackground)).Color;
    private bool IsFooterAction(string id)=>id is "save-pending" or "discard-pending"||(subpage!="operation"&&(id is "operation-details" or "dismiss-operation"));
    internal void Move(NavDirection direction){if(!NavigationAllowed||busy||Confirming||TransitionPending)return;if(subpage=="operation"&&operationText?.IsKeyboardFocusWithin==true){if(direction==NavDirection.Up)operationText.LineUp();else if(direction==NavDirection.Down)operationText.LineDown();else{navigation=new(direction==NavDirection.Left?"operation-text":"dismiss-operation");SetSelection();}return;}
        var hasResult=Controller.Operation is not null&&subpage!="operation";
        var lastContent=entries.Last(e=>!IsFooterAction(e.Id)).Id;
        if(navigation.Id==lastContent&&direction==NavDirection.Down&&(hasResult||Controller.SettingsDirty)){navigation=new(hasResult?"operation-details":"save-pending");SetSelection();return;}
        if(navigation.Id is "operation-details" or "dismiss-operation"&&subpage!="operation"){
            navigation=direction switch{NavDirection.Left=>new("operation-details"),NavDirection.Right=>new("dismiss-operation"),NavDirection.Down when Controller.SettingsDirty=>new("save-pending"),NavDirection.Up=>new(lastContent),_=>navigation};SetSelection();return;
        }
        if(navigation.Id is "save-pending" or "discard-pending"){
            navigation=direction switch{NavDirection.Left=>new("save-pending"),NavDirection.Right=>new("discard-pending"),NavDirection.Up when hasResult=>new("operation-details"),NavDirection.Up=>new(lastContent),_=>Navigation.Linear(navigation,entries.Select(e=>e.Id).ToArray(),direction)};SetSelection();return;
        }if(AdjustGuideLimit(direction))return;if(subpage=="audio"&&navigation.Id=="volume"&&direction is NavDirection.Left or NavDirection.Right){AdjustVolume(direction==NavDirection.Left?-.05f:.05f);return;}navigation=page==0&&subpage==""?narrow?Navigation.Linear(navigation,entries.Select(e=>e.Id).ToArray(),direction):Navigation.Home(navigation,direction):Navigation.Linear(navigation,entries.Select(e=>e.Id).ToArray(),direction);SetSelection();}
    internal void Sequential(int step){if(!NavigationAllowed||Confirming)return;navigation=Navigation.Sequential(navigation,entries.Select(e=>e.Id).ToArray(),step);SetSelection();}
    internal void ActivateEntry(){if(!NavigationAllowed||TransitionPending)return;var entry=entries.FirstOrDefault(e=>e.Id==navigation.Id);if(entry is null)return;if(Navigation.CanActivate(busy,Confirming,entry.Available))entry.Run();else if(!entry.Available)FeedbackText("Indisponível · "+entry.Reason);}
    internal void Details(){if(!NavigationAllowed)return;var target=Controller.Apps.FirstOrDefault(w=>WindowId(w)==navigation.Id)??Controller.Session.Anchor;if(target is null){FeedbackText("Escolha uma janela em Apps.");return;}detail=target;Subpage("details","switch-detail");}
    internal void Back()=>Back(false);
    private void Back(bool trustedDismiss){if(TransitionPending){if(handoffDismiss){ReaffirmDismiss(trustedDismiss);return;}CancelHandoff();input.Cancel();if(page==0&&subpage=="")HidePanel(true);else FeedbackText("Transição cancelada.");return;}if(Confirming){CancelHold();FeedbackText("Fechamento cancelado antes do envio.");return;}if(busy){pending?.Cancel();RetainOperation("Não confirmado","Observação interrompida; o comando já enviado não pode ser desfeito. Verifique a janela.",dispatched:true);return;}if(subpage=="operation"&&operationOrigin is {} origin){subpage=origin.Subpage;navigation=origin.Navigation;subSelection=origin.ParentSelection;detail=origin.Detail;operationOrigin=null;RefreshRows();ContentScroll.ScrollToVerticalOffset(origin.ScrollOffset);return;}if(subpage!=""){subpage="";detail=null;navigation=new(subSelection);RefreshRows();return;}if(page!=0){Page(0);return;}HidePanel(true);}
    private void OnKeyDown(object sender,KeyEventArgs e)
    {
        inputMethod="keyboard";UpdateHints();if(e.OriginalSource is Slider or TextBox&&e.Key is not Key.Escape)return;
        if(e.Key==Key.Tab&&navigation.Id=="guide-limit"&&!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)&&entries.FirstOrDefault(x=>x.Id=="guide-limit")?.Button.Content is Grid limitGrid&&limitGrid.Children[1] is StackPanel limitTexts&&limitTexts.Children.OfType<Slider>().FirstOrDefault() is {} editor){editor.Focus();e.Handled=true;return;}
        var key=NormalizeKey(e.Key,e.SystemKey,e.ImeProcessedKey,e.DeadCharProcessedKey);var firstPress=key!=Key.None&&heldKeys.Add(key);if(Confirming&&closeSource!=0&&firstPress&&key!=Key.Escape){CancelHold();FeedbackText("Origem da pressão mudou; fechamento cancelado.");e.Handled=true;return;}if(key==Key.None&&TransitionPending){RejectHandoff("Transição cancelada; tecla não identificada.");e.Handled=true;return;}if(TransitionPending){if(e.Key==Key.Escape&&firstPress&&!e.IsRepeat)Back(trustedDismiss:true);e.Handled=true;return;}
        if(ownedDialog){e.Handled=true;return;}if(e.Key is Key.Enter or Key.Space){if(!NavigationAllowed||!navigationKeysArmed){e.Handled=true;return;}keyboardDown=true;if(Confirming&&closeSource!=0){CancelHold();FeedbackText("Origem da pressão mudou; fechamento cancelado.");}else if(!e.IsRepeat&&!Confirming){dispatchSource=0;ActivateEntry();dispatchSource=null;}e.Handled=true;return;}
        if(e.Key==Key.Escape){Back();e.Handled=true;return;}if(!NavigationAllowed||!navigationKeysArmed){e.Handled=true;return;}if(Confirming){e.Handled=true;return;}switch(e.Key){case Key.Up:Move(NavDirection.Up);break;case Key.Down:Move(NavDirection.Down);break;case Key.Left:Move(NavDirection.Left);break;case Key.Right:Move(NavDirection.Right);break;case Key.Tab:Sequential(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)?-1:1);break;case Key.PageUp:Page(page-1);break;case Key.PageDown:Page(page+1);break;case Key.X:Details();break;default:return;}e.Handled=true;
    }
    internal void PollGuideStatus(XInputGuideBackend backend)
    {
        backend.PollForOverlay(IsVisible,focusCycle,Environment.TickCount64,Controller.Settings.GuideOpenMaxHoldMs);if(!IsVisible||(subpage!="integrations"&&page!=2))return;
        UpdateGuideStatus(backend.Status);
    }
    internal void UpdateGuideStatus(string status)=>SetEntryDescription("guide",status);
    private void Tick(object? sender,EventArgs args)
    {
        if(exiting)return;if(guideBackend is not null)PollGuideStatus(guideBackend);var frames=Enumerable.Range(0,4).Select(i=>Native.ReadPad((uint)i)).ToArray();var now=Environment.TickCount64;lastInputSample=new(frames,now,GuideHeld:guideBackend?.Held==true,GuideValid:guideBackend is null||!guideBackend.Active||guideBackend.SnapshotValid,GuideConnections:guideBackend?.Connections??0);UpdatePolling(frames.Any(f=>f.Connected));var current=input.Update(frames,IsVisible,now);
        if(TransitionPending){if(GuidePolling.Consume(ref guideToggleRequested,current.Toggle)){Toggle(trustedDismiss:current.Toggle||guideBackend?.SnapshotValid==true);return;}if(current.Events.Pressed.HasFlag(PadButtons.B))Back(trustedDismiss:current.Slot is {} slot&&frames[slot].Connected&&!frames[slot].ReadFailed);else AdvanceHandoff(lastInputSample.Value,now);return;}
        if(GuidePolling.Consume(ref guideToggleRequested,current.Toggle)){Toggle();return;}if(!IsVisible)return;var events=current.Events;if(events.Pressed!=0||events.Direction!=0){inputMethod="controller";UpdateHints();}if(!NavigationAllowed){if(!ownedDialog&&events.Pressed.HasFlag(PadButtons.B))Back();return;}if(!navigationKeysArmed&&heldKeys.Count==0&&PhysicalKeysNeutral())navigationKeysArmed=true;
        if(Confirming&&closeSource==-1){var point=Mouse.GetPosition(HoldCloseButton);var inside=point.X>=0&&point.Y>=0&&point.X<HoldCloseButton.ActualWidth&&point.Y<HoldCloseButton.ActualHeight;var mixed=keyboardDown||heldKeys.Count>0||events.Pressed!=0||events.Direction!=0;AdvancePointerHold(pointerDown,(!pointerDown||pointerCaptured&&HoldCloseButton.IsMouseCaptured)&&!mixed,inside,now);return;}
        if(Confirming&&closeHold.Target is {} target){var sourceValid=closeSource==0||current.Slot is {} slot&&closeSource==slot+1&&current.Connected;var down=closeSource==0?keyboardDown:sourceValid&&events.ADown;if((closeSource!=0&&keyboardDown)||(closeSource==0&&events.Pressed.HasFlag(PadButtons.A)))sourceValid=false;var valid=Controller.Platform.Valid(target)?target:null;if(closeHold.Tick(down,events.Pressed.HasFlag(PadButtons.B),IsActive,sourceValid,valid,Environment.TickCount64)){CancelHold();RunClose(target);}else if(!Confirming)CancelHold();HoldProgress.Value=closeHold.Progress;ConfirmHint.Text=closeHold.Progress>0?$"Mantenha pressionado… {closeHold.Progress:P0}":"Solte e pressione novamente por 1,5 s.";return;}
        if(busy)return;if(events.Direction!=0)Move((NavDirection)events.Direction);dispatchSource=current.Slot+1;if(events.Pressed.HasFlag(PadButtons.B))Back();else if(events.Pressed.HasFlag(PadButtons.A))ActivateEntry();else if(events.Pressed.HasFlag(PadButtons.X))Details();else if(events.Pressed.HasFlag(PadButtons.LB))Page(page-1);else if(events.Pressed.HasFlag(PadButtons.RB))Page(page+1);dispatchSource=null;
    }
    internal void Confirm(WindowRef target){if(!target.CanClose||!Controller.Platform.Valid(target)){FeedbackText("Janela protegida ou identidade alterada; fechamento bloqueado.");return;}closeSource=dispatchSource??0;closeHold.Open(target);HeaderArea.Visibility=Visibility.Collapsed;TabArea.Visibility=Visibility.Collapsed;UpdatePersistentState();ConfirmTarget.Text=target.Title;ConfirmTarget.ToolTip=target.Title;AutomationProperties.SetName(ConfirmTarget,target.Title);ConfirmPanel.Visibility=Visibility.Visible;ContentScroll.Visibility=Visibility.Collapsed;CloseArea.Visibility=Visibility.Collapsed;HoldProgress.Value=0;ConfirmHint.Text="Aguardando soltar…";FeedbackText("Nenhum comando de fechamento enviado.");UpdateHints();}
    internal bool AdvancePointerHold(bool down,bool captured,bool inside,long now)
    {
        if(!Confirming||closeSource!=-1)return false;var target=closeHold.Target!;var context=InputContext;
        var valid=Controller.Platform.Valid(target)&&target.Same(subpage=="details"?detail:Controller.Session.Anchor)?target:null;
        var completed=closeHold.Tick(down,false,context.Active&&context.OwnForeground&&context.Confirmed,captured&&(!down||inside),valid,now);
        HoldProgress.Value=closeHold.Progress;ConfirmHint.Text=closeHold.Progress>0?$"Mantenha pressionado… {closeHold.Progress:P0}":"Solte e pressione novamente por 1,5 s.";
        if(completed){CancelHold();RunClose(target);}else if(!Confirming)CancelHold();return completed;
    }
    private async void RunClose(WindowRef target){if(busy||exiting)return;busy=true;pending=new();RetainOperation("Pendente","Aguardando confirmação de fechamento…","close",target,true);ActionResult result;try{result=await Controller.Platform.Close(target,pending.Token);}catch(OperationCanceledException){result=new(ResultKind.Unknown,"Observação interrompida após o envio. Verifique a janela e possíveis diálogos de salvar.");}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){result=new(ResultKind.Failed,"Não foi possível solicitar o fechamento.");}finally{pending.Dispose();pending=null;busy=false;}if(exiting)return;RecordResult(result,"close",target);try{await Controller.Refresh();if(!exiting)RefreshRows();}catch(OperationCanceledException){}}
    private void RecordResult(ActionResult result,string action,WindowRef? target=null)
    {
        RetainOperation(result.ToString().Split(" · ")[0],result.Message,action,target,true);FeedbackText(result.ToString());
        if(!Controller.Platform.IsPreview)Controller.Diagnostics.Log(action,result.Kind);
        if(!IsVisible&&result.Kind is ResultKind.Failed or ResultKind.Unknown or ResultKind.Partial or ResultKind.Unavailable or ResultKind.Cancelled)RequestResultNotification(result.ToString());
    }
    private void RequestResultNotification(string message){if(exiting)return;if(ResultNotificationRequest is {} request){request(message);return;}if(!diagnostic&&!Controller.Platform.IsPreview)tray?.NotifyResult(message);}
    private void RunEffect(Func<Task<ActionResult>> effect,Func<bool>? valid=null){var action=navigation.Id;var target=subpage=="details"?detail:Controller.Session.Anchor;QueueHandoff(()=>BeginEffect(effect,action,target),valid);}
    internal Task OperationTask {get;private set;}=Task.CompletedTask;
    private void BeginEffect(Func<Task<ActionResult>> effect,string action,WindowRef? target)=>OperationTask=BeginEffectCore(effect,action,target);
    private async Task BeginEffectCore(Func<Task<ActionResult>> effect,string action,WindowRef? target)
    {
        if(busy||exiting)return;busy=true;input.Cancel();RetainOperation("Pendente","Solicitação enviada; aguardando o resultado.",action,target,true);
        ActionResult result;try{result=await effect();}catch(OperationCanceledException){result=new(ResultKind.Unknown,"Observação interrompida; verifique o aplicativo antes de tentar novamente.");}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){result=new(ResultKind.Failed,"A solicitação não pôde ser concluída.");}
        busy=false;if(exiting)return;RecordResult(result,action,target);
        if(result.Kind is ResultKind.Requested or ResultKind.Completed or ResultKind.Simulated){if(IsVisible)HideNow(false);}UpdatePolling();UpdatePersistentState();
    }
    private void Shortcut(string keys){var target=keys=="Win+Tab"?Controller.ReturnTarget:Controller.Session.Anchor;if(target is null){FeedbackText("Nenhuma janela válida para este atalho.");return;}if(keys is "Shift+Tab" or "Ctrl+Tab"&&Controller.Platform.SteamEvidence(target)!=ModuleEvidence.Loaded){FeedbackText("Overlay Steam não confirmado nesta janela.");return;}RunEffect(()=>Controller.Platform.Shortcut(target,keys));}
    private void AdjustVolume(float delta)=>AudioAction(audio.Set(audio.Current.Volume+delta));private void AudioAction(ActionResult result)=>RecordResult(result,"audio");
    private void CycleDisplay(){var ids=new string?[]{null}.Concat(Controller.Displays.Select(d=>(string?)d.Id)).ToArray();var index=Array.IndexOf(ids,Controller.Settings.DisplayId);Change(Controller.Settings with{DisplayId=ids[(index+1)%ids.Length]});Place();}
    private string GuideLimitTitle()=>"Guide · limite para abrir "+(Controller.Settings.GuideOpenMaxHoldMs/1000d).ToString("0.0",System.Globalization.CultureInfo.GetCultureInfo("pt-BR"))+" s";
    private bool AdjustGuideLimit(NavDirection direction){if(navigation.Id!="guide-limit"||direction is not (NavDirection.Left or NavDirection.Right))return false;if(entries.FirstOrDefault(e=>e.Id=="guide-limit")?.Button.Content is Grid grid&&grid.Children[1] is StackPanel texts&&texts.Children.OfType<Slider>().FirstOrDefault() is {} slider)slider.Value=Math.Clamp(slider.Value+(direction==NavDirection.Left?-100:100),500,3000);return true;}
    private void ApplySettings(){if(!OpenAnimationAllowed)CancelOpenAnimation();input=new(Controller.Settings);input.Cancel();ConfigureGuide();if(IsVisible)Place();}
    private void Change(UserSettings settings){Controller.Change(settings);ApplySettings();RefreshRows();FeedbackText("Alteração em memória · Salvar ajustes para manter.");}
    private void Save(){try{Controller.Save();RetainOperation(Controller.Platform.IsPreview?"Simulado":"Concluído",Controller.Status);FeedbackText(Controller.Status);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException){RetainOperation("Falhou","Falha ao salvar; configuração anterior preservada. Tente salvar novamente ou descarte as alterações.");FeedbackText("Falha ao salvar; configuração anterior preservada.");}UpdatePersistentState();}
    private void ConfigurePlaynite()=>DialogTask=ConfigurePlayniteCore();
    private async Task ConfigurePlayniteCore()
    {
        if(Controller.Platform.IsPreview&&PlayniteDialogFixture is null){FeedbackText("Prévia: nenhuma configuração alterada.");return;}
        ownedDialog=true;RevokeFocus("dialog");var cycle=focusCycle;
        try{
            string? path;if(PlayniteDialogFixture is {} fixture)path=await fixture();else{var dialog=new OpenFileDialog{Title="Escolha Playnite.FullscreenApp.exe",Filter="Playnite Fullscreen|Playnite.FullscreenApp.exe",CheckFileExists=true};path=dialog.ShowDialog(this)==true?dialog.FileName:null;}
            if(path is not null&&CurrentCycle(cycle))Change(Controller.Settings with{PlaynitePath=path});
        }catch(OperationCanceledException){}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){if(CurrentCycle(cycle))FeedbackText("Não foi possível escolher o Playnite.");}
        finally{
            ownedDialog=false;if(CurrentCycle(cycle)){RevokeFocus("dialog-return");var resumed=focusCycle;var focus=AcquireFocus(resumed);OpeningTask=focus;if(await focus&&CurrentCycle(resumed))SetSelection(false);}
        }
    }
    public void SetRenderState(string state){switch(state){case "apps":Page(1);break;case "system":Page(2);break;case "audio":Subpage("audio","volume");break;case "integrations":Subpage("integrations","test-steam");break;case "details":Details();break;case "confirmation":if(Controller.Session.Anchor is {} t)Confirm(t);break;case "long-error":FeedbackText("Não confirmado · A identidade da janela mudou. Selecione novamente em Apps. Nenhum comando adicional foi enviado.");break;case "steam-unavailable":navigation=new("steam-overlay");SetSelection();break;}}
}

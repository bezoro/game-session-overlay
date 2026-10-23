using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ApolloOverlay;

public partial class MainWindow : Window
{
    internal readonly OverlayController Controller;
    internal Func<nint,uint> DpiReader {private get;set;}=Native.GetDpiForWindow;
    private readonly WindowsAudio audio=new();private InputCoordinator input;private readonly CloseHold closeHold=new();private readonly DispatcherTimer timer;
    private readonly bool diagnostic;private HwndSource? source;private NativeTray? tray;private bool hotkey,busy,exiting,opening,keyboardDown;private int? closeSource,dispatchSource;private CancellationTokenSource? pending;
    private XInputGuideBackend? guideBackend;private GuideConnection? guideConnection;private bool guideToggleRequested;
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
        HomeTab.Click+=(_,_)=>Page(0);AppsTab.Click+=(_,_)=>Page(1);SettingsTab.Click+=(_,_)=>Page(2);SettingsGear.Click+=(_,_)=>Page(2);
        PreviewKeyDown+=OnKeyDown;PreviewKeyUp+=(_,e)=>{if(e.Key is Key.Enter or Key.Space){keyboardDown=false;e.Handled=true;}};
        Deactivated+=(_,_)=>{if(opening||exiting)return;CancelHold();input.Cancel();if(IsVisible&&!ownedDialog){HidePanel(false);}};
        SourceInitialized+=(_,_)=>{source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);source.CompositionTarget.BackgroundColor=Colors.Transparent;source.AddHook(WindowMessage);if(!diagnostic){ApplyHotkey();tray=new(source.Handle);}};
        Closing+=(_,_)=>Cleanup();audio.Changed+=AudioChanged;
        SystemParameters.StaticPropertyChanged+=OnSystemParametersChanged;UpdateAppearance();
        new WindowInteropHelper(this).EnsureHandle();ConfigureGuide();if(!diagnostic)timer.Start();RefreshRows();FeedbackText(Controller.Status);
    }
    private bool ownedDialog,placing;
    private void AudioChanged()
    {
        if(!IsVisible)return;var state=audio.Current;
        if(subpage=="audio"){var volume=entries.FirstOrDefault(e=>e.Id=="volume");if(volume is not null&&volume.Available!=state.Available){RefreshRows();return;}SetEntryText("volume","Volume · "+(state.Available?state.Volume.ToString("P0"):"indisponível"));SetEntryText("mute",state.Muted?"Ativar som":"Silenciar");}
        else if(page==0){var entry=entries.FirstOrDefault(e=>e.Id=="audio");if(entry?.Button.Content is Grid grid&&grid.Children[1] is StackPanel texts&&texts.Children.Count>1)((TextBlock)texts.Children[1]).Text=state.Available?(state.Muted?"Sem som":state.Volume.ToString("P0")):"Saída indisponível";}
    }
    private void SetEntryText(string id,string title){if(entries.FirstOrDefault(e=>e.Id==id)?.Button.Content is Grid grid&&grid.Children[1] is StackPanel texts)((TextBlock)texts.Children[0]).Text=title;}
    private void ApplyHotkey()
    {
        if(source is null)return;if(hotkey)Native.UnregisterHotKey(source.Handle,1);var config=OpeningConfig.Parse(Controller.Settings.Hotkey,Controller.Settings.Chord,Controller.Settings.ChordHoldMs);hotkey=Native.RegisterHotKey(source.Handle,1,config.Modifiers,config.Key);
        if(!hotkey)FeedbackText("Atalho ocupado. Abra pelo ícone da bandeja ou pela combinação do controle e escolha outro em Sistema.");
        if(!hotkey&&!Enumerable.Range(0,4).Any(i=>Native.ReadPad((uint)i).Connected))Dispatcher.BeginInvoke(()=>{if(exiting)return;OpenPanel();Page(2);navigation=new("hotkey");SetSelection();});
    }
    private nint WindowMessage(nint hwnd,int message,nint w,nint l,ref bool handled)
    {
        if(message==0x312&&w==1){Toggle();handled=true;}
        if(message==NativeTray.Callback){var action=(int)l;if(action is 0x202 or 0x203){OpenPanel();handled=true;}else if(action==0x205){var item=tray?.Menu();if(item==1)OpenPanel();if(item==2){OpenPanel();Page(2);}if(item==3)Close();handled=true;}}
        if(message==0x8002){OpenPanel();handled=true;}
        if(message is 0x7E or 0x2E0 && IsVisible&&!placing)Dispatcher.BeginInvoke(()=>Place());return 0;
    }
    internal void Toggle(){if(busy)return;if(IsVisible)HidePanel(true);else OpenPanel();}
    internal async void OpenPanel()
    {
        if(exiting||busy)return;if(IsVisible){Activate();return;}Controller.CaptureReturn();opening=true;input.Cancel();CancelHold();
        Place();Show();Activate();Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);opening=false;UpdatePolling();audio.SetVisible(!Controller.Platform.IsPreview);RefreshRows();AnimateOpen();
        try{await Controller.Refresh();if(exiting||!IsVisible)return;Place();RefreshRows();}catch(OperationCanceledException){}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){FeedbackText("Não foi possível atualizar as janelas. Áudio e Sistema continuam disponíveis.");}
        SetSelection(false);
    }
    internal void ShowDiagnostic(){opening=true;Show();Activate();Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);opening=false;SetSelection(false);}
    internal void ValidationHost(bool visible){if(visible){ShowDiagnostic();audio.SetVisible(true);}else{Hide();audio.SetVisible(false);}UpdatePolling();timer.Start();}
    internal void AnimateOpen(){if(Controller.Settings.ReducedMotion||SystemParameters.HighContrast||!SystemParameters.ClientAreaAnimation)return;Sidebar.BeginAnimation(OpacityProperty,new DoubleAnimation(.35,1,TimeSpan.FromMilliseconds(140)));}
    private void OnSystemParametersChanged(object? sender,PropertyChangedEventArgs args){if(exiting||Dispatcher.HasShutdownStarted)return;if(Dispatcher.CheckAccess())UpdateAppearance();else Dispatcher.BeginInvoke(UpdateAppearance);}
    private void UpdateAppearance(){GlassSurface.Opacity=SystemParameters.HighContrast?1:.93;GlassSurface.Effect=SystemParameters.HighContrast?null:new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=16,ShadowDepth=4,Opacity=.5};}
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
    private async void HidePanel(bool restore)
    {
        if(!IsVisible)return;var wasForeground=Native.GetForegroundWindow()==new WindowInteropHelper(this).Handle;CancelHold();pending?.Cancel();input.Cancel();Sidebar.BeginAnimation(OpacityProperty,null);Hide();audio.SetVisible(false);UpdatePolling();
        if(restore&&wasForeground&&Controller.ReturnTarget is {} target){var result=await Controller.Platform.Switch(target);if(result.Kind!=ResultKind.Completed&&result.Kind!=ResultKind.Simulated&&!exiting){if(!Controller.Platform.IsPreview)Controller.Diagnostics.Log("focus-return",result.Kind);FeedbackText(result.Message);}}
    }
    private void Cleanup(){if(exiting)return;exiting=true;SystemParameters.StaticPropertyChanged-=OnSystemParametersChanged;timer.Stop();timer.Tick-=Tick;guideConnection?.Dispose();guideConnection=null;guideBackend=null;guideToggleRequested=false;pending?.Cancel();audio.Dispose();if(hotkey&&source is not null)Native.UnregisterHotKey(source.Handle,1);source?.RemoveHook(WindowMessage);tray?.Dispose();Controller.Dispose();}
    private void FeedbackText(string text)=>StatusLabel.Text=text;
    private void CancelHold(){closeHold.Cancel();closeSource=null;keyboardDown=false;ConfirmPanel.Visibility=Visibility.Collapsed;ContentScroll.Visibility=Visibility.Visible;CloseArea.Visibility=Visibility.Visible;}
    internal void Page(int index){index=(index+3)%3;if(subpage==""&&index==page)tabSelection[index]=index switch{0=>"return",1=>Controller.Apps.FirstOrDefault() is {} app?WindowId(app):"refresh-apps",_=>"display"};else tabSelection[page]=navigation.Id;page=index;subpage="";detail=null;navigation=new(tabSelection[page]);CancelHold();RefreshRows();}
    private void Subpage(string name,string first){subSelection=navigation.Id;subpage=name;navigation=new(first);RefreshRows();}
    internal void RefreshRows()
    {
        var previous=entries.Select(e=>e.Id).ToArray();Rows.Children.Clear();Rows.RowDefinitions.Clear();Rows.ColumnDefinitions.Clear();Rows.ColumnDefinitions.Add(new());Rows.ColumnDefinitions.Add(new());CloseArea.Children.Clear();entries.Clear();
        entries.Add(new("settings",SettingsGear,()=>Page(2),true,"Abrir Sistema"));ContextCard.Visibility=page==0&&subpage==""?Visibility.Visible:Visibility.Collapsed;
        TargetLabel.Text=(Controller.Session.Anchor??Controller.ReturnTarget)?.Title??"Nenhuma janela selecionada";SessionEvidence.Text=Controller.Platform.IsPreview?"Janela própria":(Controller.Session.Anchor??Controller.ReturnTarget)?.ProcessName.Equals("steam",StringComparison.OrdinalIgnoreCase)==true?"Steam":"Windows";ContextStatus.Text=Controller.Session.Anchor is not null||Controller.ReturnTarget is not null?"Disponível":"Sem sessão";ContextStatusDot.Opacity=Controller.Session.Anchor is not null||Controller.ReturnTarget is not null?1:.3;
        var context=Controller.Session.Anchor??Controller.ReturnTarget;ContextIcon.Source=context is null?null:Controller.Platform.Icon(context);ContextIcon.Visibility=ContextIcon.Source is null?Visibility.Collapsed:Visibility.Visible;ContextGlyph.Visibility=ContextIcon.Source is null?Visibility.Visible:Visibility.Collapsed;
        ModeLabel.Text=diagnostic?"Diagnóstico · ações de janela simuladas":Controller.Settings.Hotkey+" · "+Controller.Settings.Chord;
        foreach(var pair in new[]{(HomeTab,0),(AppsTab,1),(SettingsTab,2)}){pair.Item1.BorderThickness=new(0,0,0,pair.Item2==page?2:0);pair.Item1.SetResourceReference(BorderBrushProperty,ThemeSupport.Success);pair.Item1.SetResourceReference(ForegroundProperty,pair.Item2==page?ThemeSupport.Text:ThemeSupport.SecondaryText);}
        PageTitle.Text=subpage switch{"audio"=>"Áudio do Windows","details"=>"Detalhes da janela","integrations"=>"Integrações",_=>page switch{0=>"Controle da sessão",1=>"Aplicativos",_=>"Sistema"}};
        void Add(string id,string title,string description,Action run,bool available=true,string icon="\uE8A7",bool danger=false,int? gridRow=null,int column=0){var row=gridRow??Rows.RowDefinitions.Count;if(row>=Rows.RowDefinitions.Count)Rows.RowDefinitions.Add(new(){Height=GridLength.Auto});var button=MakeButton(id,title,description,run,available,icon,danger);Grid.SetRow(button,row);Grid.SetColumn(button,column);Grid.SetColumnSpan(button,gridRow is null?2:1);if(gridRow is not null)button.Margin=new(column==0?0:4,4,column==0?4:0,4);Rows.Children.Add(button);}
        if(subpage=="audio"){
            var state=audio.Current;Add("volume","Volume · "+(state.Available?state.Volume.ToString("P0"):"indisponível"),"← / → ajusta 5% · A aumenta · saída padrão do Windows",()=>AdjustVolume(.05f),state.Available,"\uE767");Add("mute",state.Muted?"Ativar som":"Silenciar","Afeta a saída de áudio do Windows",()=>AudioAction(audio.Set(muted:!audio.Current.Muted)),state.Available,"\uE74F");Add("back","Voltar",state.Status,Back,true,"\uE72B");
        }else if(subpage=="details"&&detail is {} window){
            Add("switch-detail",window.Title,$"{window.ProcessName} · PID {window.Pid}",()=>RunEffect(()=>Controller.Platform.Switch(window)),true,"\uE8A7");Add("select-session","Usar nesta sessão","Seleciona esta janela, sem inferir que é um jogo",()=>{Controller.Select(window);subpage="";Page(0);},!window.Launcher&&!window.Protected,"\uE73E");Add("close-detail","Fechar esta janela…",window.CanClose?"Nova pressão longa · 1,5 s":"Janela protegida contra fechamento",()=>Confirm(window),window.CanClose,"\uE74D",true);Add("back","Voltar à lista","B / Esc também retorna",Back,true,"\uE72B");
        }else if(subpage=="integrations"){
            Add("test-steam","Solicitar overlay Steam",SteamPermission.Reason(Controller.SteamEvidence),()=>Shortcut(Controller.Settings.SteamShortcut),Controller.Session.Anchor is not null&&Controller.SteamEvidence==ModuleEvidence.Loaded,"\uE7FC");Add("guide","Botão Xbox / Guide · "+(Controller.Settings.GuideXInputEnabled?"ligado":"desligado"),guideBackend is null?"Compatibilidade legada experimental · toque abre ou fecha · não bloqueia a Steam":guideBackend.Active?guideBackend.Status:guideConnection?.Status??guideBackend.Status,()=>Change(Controller.Settings with{GuideXInputEnabled=!Controller.Settings.GuideXInputEnabled}),!diagnostic&&!Controller.Platform.IsPreview,"\uE7FC");Add("external-close","Fechar overlays externos","Indisponível: visibilidade e navegação externas não verificadas",()=>{},false,"\uE711");Add("back","Voltar","Atalho e combinação continuam disponíveis",Back,true,"\uE72B");
        }else if(page==0){
            Add("return","Voltar à janela",Controller.ReturnTarget?.Title??"Abra Apps para escolher uma janela",()=>HidePanel(true),Controller.ReturnTarget is not null,"\uE72B");
            Add("playnite","Playnite","Abrir biblioteca em tela cheia",()=>RunEffect(()=>Controller.Launch(true)),Controller.Settings.PlaynitePath is not null||Controller.Apps.Any(a=>a.ProcessName.Equals("Playnite.FullscreenApp",StringComparison.OrdinalIgnoreCase)),"\uE7FC");
            Add("steam","Steam Big Picture","Abrir biblioteca em modo de controle",()=>RunEffect(()=>Controller.Launch(false)),Controller.SteamAvailable,"\uE7FC");
            Add("apps","Aplicativos abertos",Controller.Apps.Count==1?"1 janela disponível":$"{Controller.Apps.Count} janelas disponíveis",()=>Page(1),true,"\uE737");
            var label=new TextBlock{Text="ACESSO RÁPIDO",FontSize=13,Margin=new(2,12,0,5)};label.SetResourceReference(TextBlock.ForegroundProperty,ThemeSupport.SecondaryText);var row=Rows.RowDefinitions.Count;Rows.RowDefinitions.Add(new(){Height=GridLength.Auto});Grid.SetRow(label,row);Grid.SetColumnSpan(label,2);Rows.Children.Add(label);
            Add("gamebar","Game Bar","Recursos Xbox",()=>Shortcut("Win+G"),Controller.Session.Anchor is not null,"\uE7FC",gridRow:row+1);Add("steam-overlay","Overlay Steam","Solicitar nesta janela",()=>Shortcut(Controller.Settings.SteamShortcut),Controller.Session.Anchor is not null&&Controller.SteamEvidence==ModuleEvidence.Loaded,"\uE7FC",gridRow:row+1,column:1);
            Add("taskview","Visão de tarefas","Janelas do Windows",()=>Shortcut("Win+Tab"),Controller.ReturnTarget is not null,"\uE7C4",gridRow:row+2);Add("audio","Áudio",audio.Current.Available?(audio.Current.Muted?"Sem som":audio.Current.Volume.ToString("P0")):"Saída indisponível",()=>Subpage("audio","volume"),true,"\uE767",gridRow:row+2,column:1);
            var close=MakeButton("close","Fechar janela…",Controller.Session.Anchor?.CanClose==true?"Manter A / Enter por 1,5 s":"Selecione uma janela elegível em Apps",()=>{if(Controller.Session.Anchor is {} t)Confirm(t);},Controller.Session.Anchor?.CanClose==true,"\uE74D",true);CloseArea.Children.Add(close);
        }else if(page==1){
            foreach(var app in Controller.Apps){var captured=app;Add(WindowId(app),app.Title,app.ProcessName+" · A alternar · X detalhes",()=>RunEffect(()=>Controller.Platform.Switch(captured)),true,"\uE737");}
            if(Controller.Apps.Count==0){var text=new TextBlock{Text="Nenhuma janela disponível. As bibliotecas, áudio e ajustes continuam acessíveis.",TextWrapping=TextWrapping.Wrap,FontSize=21,Margin=new(6,20,6,24)};Rows.RowDefinitions.Add(new(){Height=GridLength.Auto});Grid.SetColumnSpan(text,2);Rows.Children.Add(text);}
            Add("refresh-apps","Atualizar janelas","Obtém apenas janelas relevantes e visíveis",async()=>{try{await Controller.Refresh();RefreshRows();}catch(OperationCanceledException){}},true,"\uE72C");
        }else{
            Add("display","Monitor · "+(Controller.Settings.DisplayId??"janela ativa"),"A alterna entre automático e as telas disponíveis",CycleDisplay,true,"\uE7F4");
            Add("hotkey","Atalho · "+Controller.Settings.Hotkey,hotkey||diagnostic?"A alterna os atalhos predefinidos":"Ocupado · escolha outro atalho",()=>{Change(Controller.Settings with{Hotkey=Controller.Settings.Hotkey=="Ctrl+Alt+Space"?"Ctrl+Shift+O":"Ctrl+Alt+Space"});ApplyHotkey();RefreshRows();},true,"\uE765");
            Add("chord","Combinação · "+Controller.Settings.Chord,$"Manter {Controller.Settings.ChordHoldMs} ms · rearmar ao soltar",()=>Change(Controller.Settings with{Chord=Controller.Settings.Chord=="LB+RB+Start"?"Back+Start":"LB+RB+Start"}),true,"\uE7FC");
            Add("deadzone","Deadzone · "+Controller.Settings.Deadzone,"A alterna sensibilidade do analógico",()=>Change(Controller.Settings with{Deadzone=Controller.Settings.Deadzone<12000?12000:Controller.Settings.Deadzone<18000?18000:Controller.Settings.Deadzone<24000?24000:12000}),true,"\uE713");
            Add("motion",Controller.Settings.ReducedMotion?"Animações reduzidas":"Animações suaves","A alterna · não há animação contínua",()=>Change(Controller.Settings with{ReducedMotion=!Controller.Settings.ReducedMotion}),true,"\uE713");
            Add("configure-playnite","Caminho do Playnite",Controller.Settings.PlaynitePath is null?"Escolher executável…":"Instalação configurada",ConfigurePlaynite,true,"\uE8B7");
            Add("shortcut","Atalho Steam · "+Controller.Settings.SteamShortcut,"A alterna Shift+Tab / Ctrl+Tab",()=>Change(Controller.Settings with{SteamShortcut=Controller.Settings.SteamShortcut=="Shift+Tab"?"Ctrl+Tab":"Shift+Tab"}),true,"\uE765");
            Add("integrations","Integrações e limites","Guide, Steam e funções externas",()=>Subpage("integrations","test-steam"),true,"\uE713");Add("save","Salvar ajustes","AppData · cópia anterior preservada",Save,true,"\uE74E");Add("exit","Encerrar overlay","Remove atalho, observadores e ícone da bandeja",Close,true,"\uE7E8");
        }
        navigation=navigation with{Id=Navigation.Preserve(navigation.Id,previous,entries.Select(e=>e.Id).ToArray())};SetSelection(false);
    }
    private Button MakeButton(string id,string title,string description,Action action,bool available,string icon,bool danger)
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(42)});grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});var glyph=ActionIcon(id,icon);grid.Children.Add(glyph);var texts=new StackPanel{VerticalAlignment=VerticalAlignment.Center};texts.Children.Add(new TextBlock{Text=title,FontSize=id is "gamebar" or "steam-overlay" or "taskview" or "audio"?18:22,TextTrimming=TextTrimming.CharacterEllipsis,FontWeight=FontWeights.Medium});if(description.Length>0&&(page!=0||subpage!=""||id=="playnite"))texts.Children.Add(new TextBlock{Text=description,FontSize=13,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new(0,4,0,0),Opacity=.8});Grid.SetColumn(texts,1);grid.Children.Add(texts);
        var badge=new Border{CornerRadius=new(18),Width=30,Height=30,Margin=new(8,0,0,0),Visibility=Visibility.Collapsed,Child=new TextBlock{Text="A",FontSize=18,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Foreground=new SolidColorBrush(Color.FromRgb(15,38,20))}};badge.SetResourceReference(BackgroundProperty,ThemeSupport.Success);Grid.SetColumn(badge,2);grid.Children.Add(badge);
        if(id=="apps"){var count=new Border{CornerRadius=new(15),Padding=new(9,4,9,4),Margin=new(8,0,0,0),VerticalAlignment=VerticalAlignment.Center,Child=new TextBlock{Text=Controller.Apps.Count.ToString(),FontSize=17}};count.SetResourceReference(BackgroundProperty,ThemeSupport.Card);Grid.SetColumn(count,2);grid.Children.Add(count);grid.Resources["CountBadge"]=count;}
        ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty,SystemParameters.HighContrast?SystemColors.HighlightTextBrushKey:ThemeSupport.Surface);
        var button=new Button{Content=grid,Tag=id,MinHeight=64,Opacity=available?1:.6};if(danger)button.SetResourceReference(ForegroundProperty,ThemeSupport.Critical);
        button.Resources["ABadge"]=badge;button.Click+=(_,_)=>{navigation=new(id);SetSelection();dispatchSource=0;ActivateEntry();dispatchSource=null;};entries.Add(new(id,button,action,available,description));return button;
    }
    private FrameworkElement ActionIcon(string id,string fallback)
    {
        var key=id switch{"return" or "back"=>"back","steam" or "steam-overlay" or "test-steam"=>"steam","gamebar" or "chord" or "guide"=>"gamebar","apps" or "refresh-apps" or "next-apps" or "switch-detail"=>"apps","taskview" or "display"=>"taskview","audio" or "volume"=>"speaker","mute"=>audio.Current.Muted?"speaker":"mute","close" or "close-detail" or "exit" or "external-close"=>"close","playnite" or "configure-playnite"=>"play","select-session"=>"controller",_=>"settings"};
        var icon=new System.Windows.Shapes.Path{Data=(Geometry)FindResource("Icon."+key),Stretch=Stretch.Uniform,Width=26,Height=26,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center};
        icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,id is "close" or "close-detail"?ThemeSupport.Critical:ThemeSupport.Text);return icon;
    }
    private void SetSelection(bool showReason=true){foreach(var entry in entries){var selected=entry.Id==navigation.Id;var danger=entry.Id is "close" or "close-detail";entry.Button.BorderThickness=new(selected?2:1);entry.Button.SetResourceReference(BorderBrushProperty,selected?ThemeSupport.Success:danger?ThemeSupport.Critical:ThemeSupport.Stroke);entry.Button.SetResourceReference(BackgroundProperty,selected?ThemeSupport.SuccessBackground:danger?ThemeSupport.CriticalBackground:ThemeSupport.Control);entry.Button.Effect=selected&&entry.Available&&entry.Id!="settings"&&!SystemParameters.HighContrast&&TryFindResource(ThemeSupport.Success) is SolidColorBrush success?new System.Windows.Media.Effects.DropShadowEffect{Color=success.Color,BlurRadius=10,ShadowDepth=0,Opacity=.25}:null;if(entry.Button.Resources["ABadge"] is Border badge)badge.Visibility=selected&&entry.Available?Visibility.Visible:Visibility.Collapsed;if(entry.Button.Content is Grid grid&&grid.Resources["CountBadge"] is Border count)count.Visibility=selected?Visibility.Collapsed:Visibility.Visible;if(selected){if(showReason)FeedbackText(entry.Available?entry.Reason:"Indisponível · "+entry.Reason);if(IsVisible&&IsActive){entry.Button.Focus();entry.Button.BringIntoView();}}}}
    private static string WindowId(WindowRef w)=>$"window:{w.Handle}:{w.Pid}:{w.Started}:{w.Generation}";
    internal string SelectedAction=>navigation.Id;internal string Feedback=>StatusLabel.Text;internal bool Confirming=>closeHold.Target is not null;internal WindowRef? HeldCloseTarget=>closeHold.Target;internal double CloseProgress=>closeHold.Progress;internal IReadOnlyList<string> ActionIds=>entries.Select(e=>e.Id).ToArray();
    internal Color SurfaceColor=>((SolidColorBrush)FindResource(ThemeSupport.Surface)).Color;internal Color SelectionColor=>((SolidColorBrush)FindResource(ThemeSupport.SuccessBackground)).Color;
    internal void Move(NavDirection direction){if(busy||Confirming)return;if(subpage=="audio"&&navigation.Id=="volume"&&direction is NavDirection.Left or NavDirection.Right){AdjustVolume(direction==NavDirection.Left?-.05f:.05f);return;}navigation=page==0&&subpage==""?Navigation.Home(navigation,direction):Navigation.Linear(navigation,entries.Select(e=>e.Id).ToArray(),direction);SetSelection();}
    internal void Sequential(int step){if(Confirming)return;navigation=Navigation.Sequential(navigation,entries.Select(e=>e.Id).ToArray(),step);SetSelection();}
    internal void ActivateEntry(){var entry=entries.FirstOrDefault(e=>e.Id==navigation.Id);if(entry is null)return;if(Navigation.CanActivate(busy,Confirming,entry.Available))entry.Run();else if(!entry.Available)FeedbackText("Indisponível · "+entry.Reason);}
    internal void Details(){var target=Controller.Apps.FirstOrDefault(w=>WindowId(w)==navigation.Id)??Controller.Session.Anchor;if(target is null){FeedbackText("Escolha uma janela em Apps.");return;}detail=target;Subpage("details","switch-detail");}
    internal void Back(){if(Confirming){CancelHold();FeedbackText("Fechamento cancelado antes do envio.");return;}if(busy){pending?.Cancel();return;}if(subpage!=""){subpage="";detail=null;navigation=new(subSelection);RefreshRows();return;}if(page!=0){Page(0);return;}HidePanel(true);}
    private void OnKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key is Key.Enter or Key.Space){keyboardDown=true;if(Confirming&&closeSource!=0){CancelHold();FeedbackText("Origem da pressão mudou; fechamento cancelado.");}else if(!e.IsRepeat&&!Confirming){dispatchSource=0;ActivateEntry();dispatchSource=null;}e.Handled=true;return;}
        if(e.Key==Key.Escape){Back();e.Handled=true;return;}if(Confirming){e.Handled=true;return;}switch(e.Key){case Key.Up:Move(NavDirection.Up);break;case Key.Down:Move(NavDirection.Down);break;case Key.Left:Move(NavDirection.Left);break;case Key.Right:Move(NavDirection.Right);break;case Key.Tab:Sequential(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)?-1:1);break;case Key.PageUp:Page(page-1);break;case Key.PageDown:Page(page+1);break;case Key.X:Details();break;default:return;}e.Handled=true;
    }
    internal void PollGuideStatus(XInputGuideBackend backend)
    {
        backend.Poll();if(!IsVisible||subpage!="integrations")return;
        if(entries.FirstOrDefault(e=>e.Id=="guide")?.Button.Content is Grid grid&&grid.Children[1] is StackPanel texts&&texts.Children.Count>1&&texts.Children[1] is TextBlock subtitle&&subtitle.Text!=backend.Status)subtitle.Text=backend.Status;
    }
    private void Tick(object? sender,EventArgs args)
    {
        if(exiting)return;if(guideBackend is not null)PollGuideStatus(guideBackend);var frames=Enumerable.Range(0,4).Select(i=>Native.ReadPad((uint)i)).ToArray();UpdatePolling(frames.Any(f=>f.Connected));var current=input.Update(frames,IsVisible&&IsActive,Environment.TickCount64);if(GuidePolling.Consume(ref guideToggleRequested,current.Toggle)){Toggle();return;}if(!IsVisible||!IsActive)return;var events=current.Events;
        if(Confirming&&closeHold.Target is {} target){var sourceValid=closeSource==0||current.Slot is {} slot&&closeSource==slot+1&&current.Connected;var down=closeSource==0?keyboardDown:sourceValid&&events.ADown;if((closeSource!=0&&keyboardDown)||(closeSource==0&&events.Pressed.HasFlag(PadButtons.A)))sourceValid=false;var valid=Controller.Platform.Valid(target)?target:null;if(closeHold.Tick(down,events.Pressed.HasFlag(PadButtons.B),IsActive,sourceValid,valid,Environment.TickCount64)){CancelHold();RunClose(target);}else if(!Confirming)CancelHold();HoldProgress.Value=closeHold.Progress;ConfirmHint.Text=closeHold.Progress>0?$"Mantenha pressionado… {closeHold.Progress:P0}":"Solte e pressione novamente por 1,5 s.";return;}
        if(busy)return;if(events.Direction!=0)Move((NavDirection)events.Direction);dispatchSource=current.Slot+1;if(events.Pressed.HasFlag(PadButtons.B))Back();else if(events.Pressed.HasFlag(PadButtons.A))ActivateEntry();else if(events.Pressed.HasFlag(PadButtons.X))Details();else if(events.Pressed.HasFlag(PadButtons.LB))Page(page-1);else if(events.Pressed.HasFlag(PadButtons.RB))Page(page+1);dispatchSource=null;
    }
    private void Confirm(WindowRef target){if(!target.CanClose||!Controller.Platform.Valid(target)){FeedbackText("Janela protegida ou identidade alterada; fechamento bloqueado.");return;}closeSource=dispatchSource??0;closeHold.Open(target);ConfirmTarget.Text=target.Title;ConfirmPanel.Visibility=Visibility.Visible;ContentScroll.Visibility=Visibility.Collapsed;CloseArea.Visibility=Visibility.Collapsed;HoldProgress.Value=0;ConfirmHint.Text="Aguardando soltar…";}
    private async void RunClose(WindowRef target){if(busy)return;busy=true;pending=new();var result=await Controller.Platform.Close(target,pending.Token);pending.Dispose();pending=null;busy=false;FeedbackText(result.ToString());Controller.Diagnostics.Log("close",result.Kind);try{await Controller.Refresh();RefreshRows();}catch(OperationCanceledException){}}
    private async void RunEffect(Func<Task<ActionResult>> effect){if(busy)return;busy=true;opening=true;Sidebar.BeginAnimation(OpacityProperty,null);Hide();audio.SetVisible(false);UpdatePolling();input.Cancel();ActionResult result;try{result=await effect();}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){result=new(ResultKind.Failed,"A solicitação não pôde ser concluída.");}opening=false;busy=false;if(exiting)return;Controller.Diagnostics.Log("action",result.Kind);FeedbackText(result.ToString());if(result.Kind is ResultKind.Failed or ResultKind.Unknown or ResultKind.Partial or ResultKind.Unavailable or ResultKind.Simulated){opening=true;Show();Activate();opening=false;audio.SetVisible(!Controller.Platform.IsPreview);UpdatePolling();SetSelection(false);}else UpdatePolling();}
    private void Shortcut(string keys){var target=keys=="Win+Tab"?Controller.ReturnTarget:Controller.Session.Anchor;if(target is null){FeedbackText("Nenhuma janela válida para este atalho.");return;}if(keys is "Shift+Tab" or "Ctrl+Tab"&&Controller.Platform.SteamEvidence(target)!=ModuleEvidence.Loaded){FeedbackText("Overlay Steam não confirmado nesta janela.");return;}RunEffect(()=>Controller.Platform.Shortcut(target,keys));}
    private void AdjustVolume(float delta)=>AudioAction(audio.Set(audio.Current.Volume+delta));private void AudioAction(ActionResult result)=>FeedbackText(result.ToString());
    private void CycleDisplay(){var ids=new string?[]{null}.Concat(Controller.Displays.Select(d=>(string?)d.Id)).ToArray();var index=Array.IndexOf(ids,Controller.Settings.DisplayId);Change(Controller.Settings with{DisplayId=ids[(index+1)%ids.Length]});Place();}
    private void Change(UserSettings settings){Controller.Change(settings);input=new(settings);input.Cancel();ConfigureGuide();RefreshRows();FeedbackText("Alteração em memória · Salvar ajustes para manter.");}
    private void Save(){try{Controller.Save();FeedbackText(Controller.Status);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException){FeedbackText("Falha ao salvar; configuração anterior preservada.");}}
    private void ConfigurePlaynite(){if(Controller.Platform.IsPreview){FeedbackText("Prévia: nenhuma configuração alterada.");return;}ownedDialog=true;try{var dialog=new OpenFileDialog{Title="Escolha Playnite.FullscreenApp.exe",Filter="Playnite Fullscreen|Playnite.FullscreenApp.exe",CheckFileExists=true};if(dialog.ShowDialog(this)==true)Change(Controller.Settings with{PlaynitePath=dialog.FileName});}finally{ownedDialog=false;Activate();input.Cancel();}}
    public void SetRenderState(string state){switch(state){case "apps":Page(1);break;case "system":Page(2);break;case "audio":Subpage("audio","volume");break;case "integrations":Subpage("integrations","test-steam");break;case "details":Details();break;case "confirmation":if(Controller.Session.Anchor is {} t)Confirm(t);break;case "long-error":FeedbackText("Não confirmado · A identidade da janela mudou. Selecione novamente em Apps. Nenhum comando adicional foi enviado.");break;case "steam-unavailable":navigation=new("steam-overlay");SetSelection();break;}}
}

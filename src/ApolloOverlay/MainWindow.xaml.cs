using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ApolloOverlay;

public partial class MainWindow : Window
{
    private IPlatform platform=new PreviewPlatform();
    private readonly SessionTracker session=new();
    private readonly SteamPermission steam=new();
    private readonly CloseHold closeHold=new();
    private readonly PadInterpreter pad=new();
    private readonly DispatcherTimer timer;
    private readonly bool pilotRequested,renderOnly;
    private sealed record Entry(string Id,Button Button,Action Run,bool Available,string Reason);
    private readonly List<Entry> entries=[];
    private NavState navigation=new("return");
    private readonly NavState[] tabSelection=[new("return"),new("app-first"),new("display")];
    private string returnSelection="return";
    private IReadOnlyList<WindowRef> windows=[];
    private IReadOnlyList<DisplayRef> displays=[];
    private UserSettings settings=new();
    private readonly string settingsPath=Path.Combine(AppContext.BaseDirectory,"data","settings.json");
    private int page,appOffset;
    private uint? padSlot;
    private bool started,busy,keyboardA,externalHandoff,settingsDirty,integrationDetails;
    private long lastObservation;
    private WindowRef? detailTarget;
    private GuideConnection? guide;
    private HwndSource? source;
    private bool hotkey;
    private ModuleEvidence steamEvidence;
    private WindowRef? steamTestTarget;
    private string? steamTestShortcut;
    private CancellationTokenSource? pending;
    public MainWindow(bool pilotRequested=false,bool renderOnly=false)
    {
        InitializeComponent(); this.pilotRequested=pilotRequested; this.renderOnly=renderOnly;
        session.Observe(platform.Foreground()); displays=platform.Displays();
        timer=new DispatcherTimer(TimeSpan.FromMilliseconds(30),DispatcherPriority.Input,(_,_)=>Tick(),Dispatcher);
        HomeTab.Click+=(_,_)=>Page(0); AppsTab.Click+=(_,_)=>Page(1); SettingsTab.Click+=(_,_)=>Page(2);
        SettingsGear.Click+=(_,_)=>Page(2);
        PreviewKeyDown+=OnKeyDown; PreviewKeyUp+=(_,e)=>{ if (e.Key==Key.Enter || e.Key==Key.Space) keyboardA=false; };
        Deactivated+=(_,_)=>{ closeHold.Cancel(); keyboardA=false; pad.Reset(); ConfirmPanel.Visibility=Visibility.Collapsed; };
        SourceInitialized+=(_,_)=>{ source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); source?.AddHook(WindowMessage); };
        Closing+=OnClosing;
        if (renderOnly) timer.Stop(); else timer.Start();
        if (pilotRequested) { ModeLabel.Text="Piloto experimental · aguarda sua confirmação"; StatusLabel.Text="O jogo pode continuar recebendo o controle. Use um ambiente seguro e mantenha um teclado para recuperação."; }
        else StatusLabel.Text="Prévia · ações simuladas";
        RefreshRows();
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        started=false; timer.Stop(); pending?.Cancel(); guide?.Dispose();
        if (hotkey && source is not null) Native.UnregisterHotKey(source.Handle,1);
        source?.RemoveHook(WindowMessage); platform.Dispose();
    }
    private nint WindowMessage(nint hwnd,int message,nint w,nint l,ref bool handled)
    {
        if (message==0x312 && w==1 && started) { Toggle(); handled=true; }
        if ((message==0x7E || message==0x2E0) && started) Dispatcher.BeginInvoke(()=>RefreshDisplays(true));
        return 0;
    }
    private async void StartPilot()
    {
        if (started || busy) return;
        busy=true; Hide(); await Task.Delay(180); // Capture the revealed external foreground before our panel regains focus.
        platform.Dispose(); platform=new WindowsPlatform(); started=true;
        session.Observe(platform.Foreground());
        settings=UserSettings.Load(settingsPath,out var status);
        if (settings.PlaynitePath is null) {
            var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Playnite","Playnite.FullscreenApp.exe");
            if (File.Exists(path)) settings=settings with { PlaynitePath=path };
        }
        hotkey=source is not null && Native.RegisterHotKey(source.Handle,1,0x4003,0x20);
        guide=new(new GameInputBackend());
        guide.Start(()=>Dispatcher.BeginInvoke(()=>{ if (started && !busy) Toggle(); }));
        RefreshDisplays(false); Observe(); Show(); Activate(); busy=false;
        StatusLabel.Text=hotkey ? "Teste iniciado. Ctrl+Alt+Espaço reabre o painel. O jogo ainda pode receber o controle. Veja Guide e configurações em Sistema." : "Atalho global ocupado. Mantenha o teclado e esta janela acessíveis; consulte Guide em Sistema → Integrações.";
        pad.Reset(); RefreshRows();
    }
    private void Toggle()
    {
        if (busy) return;
        closeHold.Cancel(); ConfirmPanel.Visibility=Visibility.Collapsed; keyboardA=false; pad.Reset();
        if (IsVisible) { Hide(); externalHandoff=true; return; }
        Observe(); externalHandoff=false; Show(); Activate(); RefreshDisplays(false); RefreshRows();
        if (guide?.Observed==true) ModeLabel.Text="LB/RB Abas · Guide recebido no piloto";
    }
    private void Observe()
    {
        var previous=session.Anchor;
        session.ClearIfStale(platform.Valid); session.Observe(platform.Foreground());
        if(previous is not null && !previous.Same(session.Anchor)) {steam.Invalidate();steamTestTarget=null;}
        windows=platform.Windows(); steamEvidence=platform.SteamEvidence(session.Anchor);
        if (steamEvidence!=ModuleEvidence.Loaded) {steam.Invalidate();steamTestTarget=null;}
    }
    private void RefreshDisplays(bool topologyChanged)
    {
        var current=platform.Displays(); if (current.Count==0) { StatusLabel.Text="Nenhuma tela enumerada; mantendo posição atual."; return; }
        displays=current;
        var selected=DisplayRef.Choose(displays,settings.DisplayId);
        if (topologyChanged && settings.DisplayId is not null && selected.Id!=settings.DisplayId) StatusLabel.Text="A tela escolhida desapareceu; usando a principal atual. O alvo de captura do Apollo não é conhecido.";
        settings=settings with { DisplayId=selected.Id };
        if (started && source is not null) { UseCompactLayout();Topmost=true;var bounds=SidebarLayout.Place(selected);Native.SetWindowPos(source.Handle,new nint(-1),bounds.X,bounds.Y,bounds.Width,bounds.Height,0x10); }
        RefreshLabels();
    }
    private void RefreshLabels()
    {
        TargetLabel.Text=session.Anchor?.Title ?? "Nenhuma janela da sessão";
        SessionEvidence.Text=platform.IsPreview ? "Sessão ilustrativa · Steam não confirmado" : steamEvidence==ModuleEvidence.Loaded ? "Janela da sessão · overlay Steam detectado, não verificado" : "Janela da sessão · Steam não confirmado";
    }
    internal void Page(int value)
    {
        if (busy || closeHold.Target is not null || (pilotRequested && !started)) return;
        tabSelection[page]=integrationDetails ? new("integrations") : detailTarget is not null ? new(returnSelection) : navigation;page=(value+3)%3;navigation=tabSelection[page];detailTarget=null;integrationDetails=false;RefreshRows();
    }
    private void UseCompactLayout()
    {
        PreviewBackdrop.Visibility=Visibility.Collapsed;CanvasRoot.Width=440;CanvasRoot.Height=684;Sidebar.Margin=new Thickness(0);
        ModeLabel.Text="LB/RB Abas · Guide/atalho reabre o piloto";
    }
    private static string IconGeometry(string id)=>id switch {
        "return"=>"M 7,3 L 21,12 7,21 Z", "playnite"=>"M 5,7 Q 12,3 19,7 L 23,18 Q 20,22 16,16 L 8,16 Q 4,22 1,18 Z M 5,10 L 5,15 M 3,12 L 8,12 M 17,10 L 18,10 M 20,13 L 21,13",
        "steam" or "steam-overlay"=>"M 3,17 L 10,20 18,10 M 18,4 A 5,5 0 1 1 17.9,4 M 2,14 A 4,4 0 1 1 1.9,14",
        "apps"=>"M 2,2 L 9,2 9,9 2,9 Z M 15,2 L 22,2 22,9 15,9 Z M 2,15 L 9,15 9,22 2,22 Z M 15,15 L 22,15 22,22 15,22 Z",
        "gamebar"=>"M 2,15 L 6,15 6,22 2,22 Z M 10,9 L 14,9 14,22 10,22 Z M 18,2 L 22,2 22,22 18,22 Z",
        "taskview"=>"M 2,3 L 22,3 22,21 2,21 Z M 7,3 L 7,21 M 7,9 L 22,9", "audio"=>"M 2,9 L 7,9 13,4 13,20 7,15 2,15 Z M 17,7 Q 22,12 17,17 M 20,3 Q 29,12 20,21",
        "close"=>"M 5,5 L 19,19 M 19,5 L 5,19", _=>"M 5,4 L 19,4 19,20 5,20 Z M 9,9 L 15,9 M 9,14 L 15,14"};
    private void ConfigureRows(params double[] heights)
    {
        Rows.RowDefinitions.Clear();Rows.ColumnDefinitions.Clear();Rows.ColumnDefinitions.Add(new ColumnDefinition());Rows.ColumnDefinitions.Add(new ColumnDefinition());
        foreach(var height in heights)Rows.RowDefinitions.Add(new RowDefinition {Height=new GridLength(height)});
    }
    private void Add(string id,string title,string reason,Action run,bool available=true,int row=0,int column=0,int span=2,bool tool=false,bool danger=false,Grid? parent=null)
    {
        var content=new Grid();content.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(tool ? 26 : 34)});content.ColumnDefinitions.Add(new ColumnDefinition());
        if(!tool)content.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(22)});
        var icon=new System.Windows.Shapes.Path {Data=Geometry.Parse(IconGeometry(id)),StrokeThickness=1.8,Stretch=Stretch.Uniform,Width=tool ? 19 : 22,Height=22,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center};icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,!available ? ThemeSupport.DisabledText : danger ? ThemeSupport.Critical : ThemeSupport.Text);content.Children.Add(icon);
        var label=new TextBlock {Text=title,FontSize=tool ? 14 : 17,FontWeight=id=="return" ? FontWeights.SemiBold : FontWeights.Normal,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};label.SetResourceReference(TextBlock.ForegroundProperty,!available ? ThemeSupport.DisabledText : danger ? ThemeSupport.Critical : ThemeSupport.Text);Grid.SetColumn(label,1);content.Children.Add(label);
        if(!tool){var badge=new TextBlock {Text="A",FontSize=12,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};badge.SetResourceReference(TextBlock.ForegroundProperty,ThemeSupport.SecondaryText);Grid.SetColumn(badge,2);content.Children.Add(badge);}
        var button=new Button {Content=content,Margin=new Thickness(column==1 ? 3 : 0,2,span==1 && column==0 ? 3 : 0,3),Padding=new Thickness(tool ? 10 : 12,5,10,5),ToolTip=reason};
        if(danger){button.SetResourceReference(BackgroundProperty,ThemeSupport.CriticalBackground);button.SetResourceReference(BorderBrushProperty,ThemeSupport.Critical);}
        button.Click+=(_,_)=>{navigation=navigation with {Id=id};SetSelection(false);ActivateEntry();};
        entries.Add(new(id,button,run,available,reason));Grid.SetRow(button,row);Grid.SetColumn(button,column);Grid.SetColumnSpan(button,span);(parent ?? Rows).Children.Add(button);
    }
    internal void RefreshRows()
    {
        var previousIds=entries.Select(e=>e.Id).ToArray();Rows.Children.Clear();CloseArea.Children.Clear();entries.Clear();RefreshLabels();
        entries.Add(new("settings",SettingsGear,()=>Page(2),true,"Abrir Sistema: tela, integrações, ajustes e saída."));
        PageTitle.Text="Controle da sessão";
        HomeTab.BorderThickness=new Thickness(0,0,0,page==0 ? 2 : 0);AppsTab.BorderThickness=new Thickness(0,0,0,page==1 ? 2 : 0);SettingsTab.BorderThickness=new Thickness(0,0,0,page==2 ? 2 : 0);
        HomeTab.SetResourceReference(BorderBrushProperty,ThemeSupport.Success);AppsTab.SetResourceReference(BorderBrushProperty,ThemeSupport.Success);SettingsTab.SetResourceReference(BorderBrushProperty,ThemeSupport.Success);
        if(pilotRequested && !started){
            PageTitle.Text="Teste controlado";ConfigureRows(76,76,60);
            Add("start-pilot","Iniciar teste controlado","Ativa ações reais. O jogo pode continuar recebendo o controle; mantenha teclado para recuperação.",StartPilot,row:0);
            Add("safe-preview","Continuar na prévia","Simular a interface sem operar aplicativos.",()=>{new MainWindow().Show();Close();},row:1);
            Add("exit","Sair","Encerra apenas este painel.",Close,row:2);navigation=navigation with {Id=navigation.Id=="return" ? "start-pilot" : navigation.Id};
        } else if(integrationDetails){
            PageTitle.Text="Integrações";ConfigureRows(52,52,52,52,52,52);
            Add("test-steam","Testar atalho Steam",SteamPermission.Reason(steamEvidence),()=>RequestShortcut(settings.SteamShortcut),steamEvidence==ModuleEvidence.Loaded,row:0);
            var canVerify=session.Anchor is {} anchor && anchor.Same(steamTestTarget) && steamTestShortcut==settings.SteamShortcut && steamEvidence==ModuleEvidence.Loaded;
            Add("verify-steam","Confirmar: vi o overlay","Confirme somente após observar a resposta no alvo e reabrir o painel.",()=>{if(session.Anchor is {} target){steam.Verify(target,settings.SteamShortcut);StatusLabel.Text="Teste confirmado só nesta sessão. A visibilidade atual continua desconhecida.";RefreshRows();}},canVerify,row:1);
            Add("close-steam","Fechar overlay Steam","Indisponível: navegação, visibilidade e fechamento externos não verificados.",()=>{},false,row:2);
            Add("close-gamebar","Fechar Game Bar","Indisponível: navegação, visibilidade e fechamento externos não verificados.",()=>{},false,row:3);
            Add("guide","Guide / Xbox",guide?.Status ?? "Desligado na prévia; nenhum callback global registrado.",()=>{},false,row:4);
            Add("back","Voltar ao Sistema","B / Esc também retorna.",()=>{integrationDetails=false;navigation=new("integrations");RefreshRows();},row:5);
        } else if(detailTarget is not null){
            PageTitle.Text="Detalhes da janela";ConfigureRows(104,52,52,52,52);
            var title=new TextBlock {Text=detailTarget.Title,FontSize=18,TextWrapping=TextWrapping.Wrap,MaxHeight=96,Margin=new Thickness(8)};title.SetResourceReference(TextBlock.ForegroundProperty,ThemeSupport.Text);Grid.SetColumnSpan(title,2);Rows.Children.Add(title);
            Add("switch-detail","Alternar para esta janela",$"PID {detailTarget.Pid}. Identidade revalidada antes de cada ação.",()=>RunSwitch(detailTarget),row:1);
            Add("select-session","Usar como janela da sessão","Escolha explícita. Um launcher não é tratado como jogo.",()=>{session.Select(detailTarget);steam.Invalidate();steamTestTarget=null;detailTarget=null;Observe();RefreshRows();},!detailTarget.Launcher,row:2);
            Add("close-detail","Fechar esta janela…","Solte A/Enter e mantenha uma nova pressão por 1,5 s. B cancela.",()=>Confirm(detailTarget),detailTarget.CanClose,row:3,danger:true);
            Add("back","Voltar à lista","B / Esc também retorna.",()=>{detailTarget=null;navigation=new(returnSelection);RefreshRows();},row:4);
        } else if(page==0){
            ConfigureRows(52,52,52,52,24,52,52);
            Add("return","Voltar ao jogo",session.Anchor?.Title ?? "Escolha uma janela em Aplicativos.",()=>{if(session.Anchor is {} target)RunSwitch(target);},session.Anchor is not null,row:0);
            Add("playnite","Playnite",platform.IsPreview ? "Simulado: abrir a biblioteca em tela cheia." : settings.PlaynitePath is null ? "Configure Playnite.FullscreenApp.exe no Sistema." : "Solicita abrir a biblioteca em tela cheia; foco não confirmado.",()=>Launch(true),platform.IsPreview || settings.PlaynitePath is not null,row:1);
            Add("steam","Steam Big Picture","Abrir a biblioteca da Steam em modo de controle.",()=>Launch(false),row:2);
            Add("apps","Aplicativos abertos","Escolher, alternar e ver detalhes de uma janela.",()=>Page(1),row:3);
            var heading=new TextBlock {Text="Acesso rápido",FontSize=13.5,VerticalAlignment=VerticalAlignment.Center};heading.SetResourceReference(TextBlock.ForegroundProperty,ThemeSupport.SecondaryText);Grid.SetRow(heading,4);Grid.SetColumnSpan(heading,2);Rows.Children.Add(heading);
            Add("gamebar","Game Bar","Solicitar recursos Xbox. Navegação e fechamento externos ainda não verificados.",()=>RequestShortcut("Win+G"),session.Anchor is not null && !SystemParameters.IsRemoteSession,row:5,column:0,span:1,tool:true);
            var allowed=steam.ToggleAllowed(session.Anchor,steamEvidence,settings.SteamShortcut);
            Add("steam-overlay","Overlay Steam",steamEvidence==ModuleEvidence.Loaded ? allowed ? "Alternar overlay desta sessão; visibilidade atual desconhecida." : "Teste e confirme a resposta no Sistema → Integrações." : "Overlay não confirmado nesta sessão. Veja Sistema → Integrações.",()=>RequestShortcut(settings.SteamShortcut),session.Anchor is not null && steamEvidence==ModuleEvidence.Loaded,row:5,column:1,span:1,tool:true);
            Add("taskview","Visão de tarefas","Ver janelas do Windows. Controle externo ainda não verificado.",()=>RequestShortcut("Win+Tab"),session.Anchor is not null,row:6,column:0,span:1,tool:true);
            Add("audio","Áudio","Controle de áudio indisponível nesta versão. Nenhuma integração de áudio foi adicionada.",()=>{},false,row:6,column:1,span:1,tool:true);
            Add("close","Fechar jogo…","Fechar somente a janela da sessão após uma nova pressão de A por 1,5 s.",()=>{if(session.Anchor is {} target)Confirm(target);},session.Anchor?.CanClose==true,danger:true,parent:CloseArea);
        } else if(page==1){
            PageTitle.Text="Aplicativos";ConfigureRows(52,52,52,52,52,52);windows=platform.Windows();if(appOffset>=windows.Count)appOffset=0;
            if(navigation.Id=="app-first")navigation=new(windows.Skip(appOffset).FirstOrDefault() is {} first ? WindowId(first) : "apps-more");
            var row=0;foreach(var window in windows.Skip(appOffset).Take(4)){var id=WindowId(window);Add(id,window.Title,$"PID {window.Pid}. A alterna; X abre detalhes.",()=>RunSwitch(window),row:row++);}
            if(windows.Count==0){var empty=new TextBlock {Text="Nenhuma janela disponível. Atualize a lista ou volte à sessão.",FontSize=18,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(10)};Grid.SetColumnSpan(empty,2);Rows.Children.Add(empty);row=2;}
            Add("apps-more",windows.Count>4 ? "Próximas janelas" : "Atualizar janelas",$"{windows.Count} janelas. Janelas protegidas não são listadas.",()=>{appOffset=Navigation.NextAppPage(appOffset,windows.Count);Observe();RefreshRows();},row:row);
            Add("session-details","Detalhes da sessão","Acessar o alvo exato e o fechamento com confirmação.",()=>{if(session.Anchor is {} target)OpenDetails(target);},session.Anchor is not null,row:row+1);
        } else {
            PageTitle.Text="Sistema";ConfigureRows(52,52,52,52,52,52);
            var display=DisplayRef.Choose(displays,settings.DisplayId);
            Add("display","Tela da sessão",$"{display.Id} · {display.Width} × {display.Height}. A troca a tela; captura Apollo não identificada.",()=>{var next=(displays.ToList().IndexOf(display)+1)%displays.Count;settings=settings with {DisplayId=displays[next].Id};settingsDirty=true;RefreshDisplays(false);RefreshRows();},row:0);
            Add("configure-playnite","Configurar Playnite…",settings.PlaynitePath is null ? "Escolha o executável com a caixa de arquivo (teclado)." : Path.GetFileName(settings.PlaynitePath),ConfigurePlaynite,row:1);
            Add("shortcut","Atalho Steam · "+settings.SteamShortcut,"A alterna o atalho. Teste e confirmação anteriores serão invalidados.",()=>{settings=settings with {SteamShortcut=settings.SteamShortcut=="Shift+Tab" ? "Ctrl+Tab" : "Shift+Tab"};settingsDirty=true;steam.Invalidate();steamTestTarget=null;RefreshRows();},row:2);
            Add("integrations","Integrações e Guide","Guide, teste Steam e limites dos overlays externos.",()=>{integrationDetails=true;navigation=new("test-steam");RefreshRows();},row:3);
            Add("save","Salvar ajustes",platform.IsPreview ? "Simulado na prévia: nenhum arquivo será criado." : settingsDirty ? "Gravar alterações explicitamente; preservar cópia anterior." : "Nenhuma alteração pendente.",SaveSettings,row:4);
            Add("exit","Encerrar painel","Encerra somente o painel e remove seus atalhos/callbacks.",Close,row:5);
        }
        navigation=navigation with {Id=Navigation.Preserve(navigation.Id,previousIds,entries.Select(e=>e.Id).ToArray())};SetSelection(false);
    }
    private static string WindowId(WindowRef window)=>$"window:{window.Handle}:{window.Pid}:{window.Started}:{window.Generation}";
    private bool HomeTopology=>page==0 && detailTarget is null && !integrationDetails && !(pilotRequested && !started);
    private void SetSelection(bool showReason=true)
    {
        foreach(var entry in entries){var selected=entry.Id==navigation.Id;entry.Button.SetResourceReference(BorderBrushProperty,selected ? ThemeSupport.Success : entry.Id is "close" or "close-detail" ? ThemeSupport.Critical : ThemeSupport.Stroke);entry.Button.BorderThickness=new Thickness(selected ? 2 : 1);entry.Button.SetResourceReference(BackgroundProperty,selected ? ThemeSupport.SuccessBackground : entry.Id is "close" or "close-detail" ? ThemeSupport.CriticalBackground : entry.Id=="settings" ? ThemeSupport.Surface : ThemeSupport.Control);if(selected){if(showReason)StatusLabel.Text=entry.Reason;if(!renderOnly)entry.Button.Focus();}}
    }
    internal string SelectedAction=>navigation.Id;
    internal string Feedback=>StatusLabel.Text;
    internal bool Confirming=>closeHold.Target is not null;
    internal WindowRef? HeldCloseTarget=>closeHold.Target;
    internal double CloseProgress=>closeHold.Progress;
    internal Color SurfaceColor=>((SolidColorBrush)Sidebar.Background).Color;
    internal Color SelectionColor=>((SolidColorBrush)entries.First(e=>e.Id==navigation.Id).Button.Background).Color;
    internal IReadOnlyList<string> ActionIds=>entries.Select(e=>e.Id).ToArray();
    internal void Move(NavDirection direction){if(busy || closeHold.Target is not null)return;navigation=HomeTopology ? Navigation.Home(navigation,direction) : Navigation.Linear(navigation,entries.Select(e=>e.Id).ToArray(),direction);SetSelection();}
    internal void Sequential(int step){if(busy || closeHold.Target is not null)return;navigation=Navigation.Sequential(navigation,entries.Select(e=>e.Id).ToArray(),step);SetSelection();}
    internal void ActivateEntry(){var entry=entries.FirstOrDefault(e=>e.Id==navigation.Id);if(entry is null)return;if(Navigation.CanActivate(busy,closeHold.Target is not null,entry.Available))entry.Run();else if(!entry.Available)StatusLabel.Text=entry.Reason;}
    private void OpenDetails(WindowRef target){returnSelection=navigation.Id;detailTarget=target;navigation=new("switch-detail");RefreshRows();}
    internal void Back()
    {
        if(closeHold.Target is not null){closeHold.Cancel();ConfirmPanel.Visibility=Visibility.Collapsed;StatusLabel.Text="Fechamento cancelado antes do envio.";return;}
        if(busy){pending?.Cancel();return;}if(integrationDetails){integrationDetails=false;navigation=new("integrations");RefreshRows();return;}
        if(detailTarget is not null){detailTarget=null;navigation=new(returnSelection);RefreshRows();return;}if(page!=0){Page(0);return;}if(session.Anchor is {} target)RunSwitch(target);
    }
    internal void Details(){if(page==1){var window=windows.FirstOrDefault(w=>WindowId(w)==navigation.Id);if(window is not null){OpenDetails(window);return;}}if(session.Anchor is {} target)OpenDetails(target);}
    private void OnKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key is Key.Enter or Key.Space){keyboardA=true;if(!e.IsRepeat && closeHold.Target is null)ActivateEntry();e.Handled=true;return;}
        if(e.Key==Key.Escape){Back();e.Handled=true;return;}if(closeHold.Target is not null){e.Handled=true;return;}
        switch(e.Key){case Key.Up:Move(NavDirection.Up);break;case Key.Down:Move(NavDirection.Down);break;case Key.Left:Move(NavDirection.Left);break;case Key.Right:Move(NavDirection.Right);break;case Key.Tab:Sequential(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)?-1:1);break;case Key.PageUp:Page(page-1);break;case Key.PageDown:Page(page+1);break;case Key.X:Details();break;default:return;}e.Handled=true;
    }
    private void Tick()
    {
        var now=Environment.TickCount64;
        if(started && now-lastObservation>=650 && !busy){lastObservation=now;var previous=session.Anchor;Observe();if(!Equals(previous,session.Anchor)){closeHold.Cancel();ConfirmPanel.Visibility=Visibility.Collapsed;RefreshRows();}}
        if(!IsVisible || !IsActive || externalHandoff)return;
        PadFrame frame=default;
        if(padSlot is {} slot){frame=Native.ReadPad(slot);if(!frame.Connected){padSlot=null;pad.Reset();closeHold.Cancel();ConfirmPanel.Visibility=Visibility.Collapsed;}}
        else for(uint i=0;i<4;i++){var candidate=Native.ReadPad(i);if(candidate.Connected && (candidate.Buttons!=0 || Math.Abs((int)candidate.X)>18000 || Math.Abs((int)candidate.Y)>18000)){padSlot=i;frame=candidate;pad.Reset();break;}}
        var events=pad.Update(frame,now);
        if(closeHold.Target is {} closing){var valid=platform.Valid(closing)?closing:null;if(closeHold.Tick(events.ADown||keyboardA,events.Pressed.HasFlag(PadButtons.B),IsActive,keyboardA||padSlot is null||frame.Connected,valid,now)){ConfirmPanel.Visibility=Visibility.Collapsed;RunClose(closing);}else if(closeHold.Target is null)ConfirmPanel.Visibility=Visibility.Collapsed;HoldProgress.Value=closeHold.Progress;ConfirmHint.Text=closeHold.Progress>0?$"Mantenha pressionado… {closeHold.Progress:P0}":"Solte e mantenha A / Enter por 1,5 s. B / Esc cancela.";return;}
        if(busy)return;if(events.Direction!=0)Move((NavDirection)events.Direction);if(events.Pressed.HasFlag(PadButtons.A))ActivateEntry();if(events.Pressed.HasFlag(PadButtons.B))Back();if(events.Pressed.HasFlag(PadButtons.LB))Page(page-1);if(events.Pressed.HasFlag(PadButtons.RB))Page(page+1);if(events.Pressed.HasFlag(PadButtons.X))Details();
    }
    private void Confirm(WindowRef target){if(!platform.Valid(target)){StatusLabel.Text="A janela mudou; atualize a lista.";return;}closeHold.Open(target);ConfirmTarget.Text=target.Title;ConfirmPanel.Visibility=Visibility.Visible;HoldProgress.Value=0;}
    private async void RunSwitch(WindowRef target)
    {
        if(busy)return;busy=true;
        if(!platform.IsPreview) {Hide();externalHandoff=true;pad.Reset();}
        var result=await platform.Switch(target);busy=false;StatusLabel.Text=result.ToString();
        if(result.Kind is ResultKind.Failed or ResultKind.Unknown) {externalHandoff=false;Show();Activate();}
    }
    private async void RunClose(WindowRef target)
    {
        if(busy)return;busy=true;pending=new();StatusLabel.Text="Solicitando fechamento da janela confirmada…";
        var result=await platform.Close(target,pending.Token);pending.Dispose();pending=null;busy=false;StatusLabel.Text=result.ToString();Observe();RefreshRows();
    }
    private async void RequestShortcut(string keys)
    {
        if(busy || session.Anchor is not {} target)return;
        if(keys is "Shift+Tab" or "Ctrl+Tab" && platform.SteamEvidence(target)!=ModuleEvidence.Loaded) {StatusLabel.Text="Evidência Steam mudou; solicitação bloqueada.";steam.Invalidate();RefreshRows();return;}
        busy=true;if(!platform.IsPreview){Hide();externalHandoff=true;pad.Reset();}
        var result=await platform.Shortcut(target,keys);busy=false;StatusLabel.Text=result.ToString();
        if(keys is "Shift+Tab" or "Ctrl+Tab" && result.Kind==ResultKind.Requested) {steamTestTarget=target;steamTestShortcut=keys;}
        if(result.Kind is ResultKind.Failed or ResultKind.Unknown or ResultKind.Partial) {externalHandoff=false;Show();Activate();}
    }
    private void Launch(bool playnite) { if(busy)return;var result=platform.Launch(playnite,settings.PlaynitePath);StatusLabel.Text=result.ToString();if(result.Kind==ResultKind.Requested && !platform.IsPreview){Hide();externalHandoff=true;pad.Reset();} }
    private void ConfigurePlaynite()
    {
        if(platform.IsPreview) {StatusLabel.Text="Simulado · Na sessão real você escolherá Playnite.FullscreenApp.exe.";return;}
        var dialog=new OpenFileDialog {Title="Escolha Playnite.FullscreenApp.exe",Filter="Playnite Fullscreen|Playnite.FullscreenApp.exe",CheckFileExists=true};
        if(dialog.ShowDialog(this)==true) {settings=settings with {PlaynitePath=dialog.FileName};settingsDirty=true;RefreshRows();}
    }
    private void SaveSettings()
    {
        if(platform.IsPreview) {StatusLabel.Text="Simulado · Nenhum arquivo de ajustes foi criado.";return;}
        try {UserSettings.Save(settingsPath,settings);settingsDirty=false;StatusLabel.Text="Ajustes salvos. Nenhuma ação externa será repetida ao reiniciar.";RefreshRows();}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException) {StatusLabel.Text="Falha ao salvar; a configuração anterior foi preservada.";}
    }
    public void SetRenderState(string state)
    {
        switch(state){
            case "apps":Page(1);break;
            case "system":Page(2);break;
            case "details":OpenDetails(session.Anchor!);break;
            case "confirmation":Confirm(session.Anchor!);closeHold.Tick(false,false,true,true,session.Anchor,0);closeHold.Tick(true,false,true,true,session.Anchor,1);closeHold.Tick(true,false,true,true,session.Anchor,826);HoldProgress.Value=closeHold.Progress;ConfirmHint.Text="Mantenha pressionado… 55%";break;
            case "long-error":session.Select(new(101,41,100,"Uma aventura extraordinariamente longa pela galáxia — edição especial com um título maior que o cartão da sessão","sample"));RefreshRows();StatusLabel.Text="Falhou · A janela mudou. Selecione novamente em Aplicativos. Nenhum atalho foi enviado; o painel continua disponível.";break;
            case "steam-unavailable":navigation=new("steam-overlay");SetSelection();break;
            case "audio-unavailable":navigation=new("audio",1);SetSelection();break;
            case "integrations":Page(2);integrationDetails=true;navigation=new("close-steam");RefreshRows();SetSelection();break;
            case "empty-apps":foreach(var window in platform.Windows().ToArray())platform.Close(window,CancellationToken.None).GetAwaiter().GetResult();Page(1);break;
            case "pilot-layout":UseCompactLayout();ModeLabel.Text="Layout do piloto simulado · Guide desligado";break;
        }
    }
}

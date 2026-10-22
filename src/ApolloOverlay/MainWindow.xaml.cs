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
    private readonly List<(Button Button,Action Run)> entries=[];
    private IReadOnlyList<WindowRef> windows=[];
    private IReadOnlyList<DisplayRef> displays=[];
    private UserSettings settings=new();
    private readonly string settingsPath=Path.Combine(AppContext.BaseDirectory,"data","settings.json");
    private int page,index,appOffset;
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
        PreviewKeyDown+=OnKeyDown; PreviewKeyUp+=(_,e)=>{ if (e.Key==Key.Enter || e.Key==Key.Space) keyboardA=false; };
        Deactivated+=(_,_)=>{ closeHold.Cancel(); keyboardA=false; pad.Reset(); ConfirmPanel.Visibility=Visibility.Collapsed; };
        SourceInitialized+=(_,_)=>{ source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); source?.AddHook(WindowMessage); };
        Closing+=OnClosing;
        if (renderOnly) timer.Stop(); else timer.Start();
        if (pilotRequested) { ModeLabel.Text="TESTE CONTROLADO"; StatusLabel.Text="O jogo pode continuar recebendo o controle. Use somente em ambiente seguro, com teclado para recuperação."; }
        else StatusLabel.Text="Tudo aqui é simulado. Nenhum aplicativo externo recebe comandos. Guide e atalhos globais ficam desligados.";
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
        StatusLabel.Text=status+" "+(hotkey ? "Ctrl+Alt+Espaço registrado. " : "Atalho global ocupado; mantenha esta janela acessível. ")+guide.Status;
        pad.Reset(); RefreshRows();
    }
    private void Toggle()
    {
        if (busy) return;
        closeHold.Cancel(); ConfirmPanel.Visibility=Visibility.Collapsed; keyboardA=false; pad.Reset();
        if (IsVisible) { Hide(); externalHandoff=true; return; }
        Observe(); externalHandoff=false; Show(); Activate(); RefreshDisplays(false); RefreshRows();
        if (guide?.Observed==true) ModeLabel.Text="PILOTO · GUIDE RECEBIDO";
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
        if (started && source is not null) { WindowStyle=WindowStyle.None; ResizeMode=ResizeMode.NoResize; Topmost=true; Native.SetWindowPos(source.Handle,new nint(-1),selected.X,selected.Y,selected.Width,selected.Height,0x10); }
        RefreshLabels();
    }
    private void RefreshLabels()
    {
        TargetLabel.Text="Janela da sessão · "+(session.Anchor?.Title ?? "nenhuma — selecione em Aplicativos");
        var d=displays.Count>0 ? DisplayRef.Choose(displays,settings.DisplayId) : null;
        DisplayLabel.Text=d is null ? "Tela indisponível" : $"Tela · {d.Id}  ·  {d.Width} × {d.Height}  ·  captura Apollo não identificada";
    }
    private void Page(int value)
    {
        if (busy || closeHold.Target is not null || (pilotRequested && !started)) return;
        page=(value+3)%3; index=0; detailTarget=null; integrationDetails=false; RefreshRows();
    }
    private void Add(string title,string reason,Action run,bool enabled=true)
    {
        var text=new StackPanel();
        text.Children.Add(new TextBlock { Text=title,FontSize=26,LineHeight=30,LineStackingStrategy=LineStackingStrategy.BlockLineHeight,TextTrimming=TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text=reason,FontSize=18,LineHeight=22,LineStackingStrategy=LineStackingStrategy.BlockLineHeight,Foreground=new SolidColorBrush(Color.FromRgb(175,196,209)),TextTrimming=TextTrimming.CharacterEllipsis });
        var button=new Button { Content=text,Height=68,Margin=new Thickness(0,0,0,4),IsEnabled=enabled,Focusable=enabled };
        button.Click+=(_,_)=>{ if (!busy && closeHold.Target is null) run(); };
        entries.Add((button,run)); Rows.Children.Add(button);
    }
    private void RefreshRows()
    {
        Rows.Children.Clear(); entries.Clear(); RefreshLabels();
        if (pilotRequested && !started) {
            PageTitle.Text="Prévia experimental";
            Add("Iniciar teste controlado","O jogo pode continuar recebendo o controle. Guide e ações reais serão habilitados.",StartPilot);
            Add("Continuar na prévia segura","Simular a interface sem operar outros aplicativos.",()=>{ new MainWindow().Show(); Close(); });
            Add("Sair","Encerra apenas este painel.",Close); SetSelection(); return;
        }
        HomeTab.Background=Brush(page==0 ? "#315647" : "#1A2932"); AppsTab.Background=Brush(page==1 ? "#315647" : "#1A2932"); SettingsTab.Background=Brush(page==2 ? "#315647" : "#1A2932");
        if (integrationDetails) {
            PageTitle.Text="Integrações experimentais";
            Add("Testar atalho Steam · "+settings.SteamShortcut,SteamPermission.Reason(steamEvidence),()=>RequestShortcut(settings.SteamShortcut),steamEvidence==ModuleEvidence.Loaded);
            var canVerify=session.Anchor is {} anchor && anchor.Same(steamTestTarget) && steamTestShortcut==settings.SteamShortcut && steamEvidence==ModuleEvidence.Loaded;
            Add("Confirmar: vi o overlay Steam neste alvo","Somente após testar, observar o jogo e reabrir este painel.",()=>{if(session.Anchor is {} target) {steam.Verify(target,settings.SteamShortcut);StatusLabel.Text="Teste confirmado manualmente nesta sessão; visibilidade atual permanece desconhecida.";RefreshRows();}},canVerify);
            Add("Fechar overlay Steam","Indisponível: estado, navegação e fechamento externos não verificados.",()=>{},false);
            Add("Fechar Game Bar","Indisponível: estado, navegação e fechamento externos não verificados.",()=>{},false);
            Add("Guide / Xbox",guide?.Status ?? "Desligado na prévia; nenhum callback global é registrado.",()=>{},false);
            Add("Voltar aos ajustes","B / Esc também retorna.",()=>{integrationDetails=false;RefreshRows();});
        } else if (detailTarget is not null) {
            PageTitle.Text="Detalhes da janela";
            Add(detailTarget.Title,$"PID {detailTarget.Pid} · identidade validada antes de cada ação",()=>RunSwitch(detailTarget));
            Add("Usar como janela da sessão","Escolha explícita; launcher não é tratado como jogo.",()=>{ session.Select(detailTarget); steam.Invalidate(); detailTarget=null; Observe(); RefreshRows(); },!detailTarget.Launcher);
            Add("Fechar esta janela…","Confirmação com A / Enter por 1,5 s. Sem encerramento forçado.",()=>Confirm(detailTarget),detailTarget.CanClose);
            Add("Voltar à lista","B / Esc também retorna.",()=>{ detailTarget=null; RefreshRows(); });
        } else if (page==0) {
            PageTitle.Text="Controle da sessão";
            Add("Voltar para a janela da sessão",session.Anchor?.Title ?? "Escolha uma janela em Aplicativos.",()=>{ if(session.Anchor is {} target) RunSwitch(target); },session.Anchor is not null);
            Add("Playnite em tela cheia",platform.IsPreview ? "Exemplo seguro de inicialização." : settings.PlaynitePath is null ? "Configure Playnite.FullscreenApp.exe em Ajustes." : "Solicita inicialização; modo e foco não são garantidos.",()=>Launch(true),platform.IsPreview || settings.PlaynitePath is not null);
            Add("Steam Big Picture","Abrir a biblioteca da Steam em modo de controle.",()=>Launch(false));
            Add("Visão de tarefas","Ver as janelas abertas do Windows; controle externo ainda não verificado.",()=>RequestShortcut("Win+Tab"),session.Anchor is not null);
            Add("Solicitar Game Bar","Abrir os recursos Xbox sobre a sessão; use Guide/atalho para reabrir.",()=>RequestShortcut("Win+G"),session.Anchor is not null && !System.Windows.SystemParameters.IsRemoteSession);
            var allowed=steam.ToggleAllowed(session.Anchor,steamEvidence,settings.SteamShortcut);
            Add(allowed ? "Alternar overlay Steam" : "Testar atalho do overlay Steam",steamEvidence==ModuleEvidence.Loaded ? "Teste e confirme a resposta nesta sessão em Ajustes → Integrações." : "Overlay não confirmada para esta sessão. Consulte Ajustes → Integrações.",()=>RequestShortcut(settings.SteamShortcut),session.Anchor is not null && steamEvidence==ModuleEvidence.Loaded);
        } else if (page==1) {
            PageTitle.Text="Aplicativos";
            windows=platform.Windows();
            if (appOffset>=windows.Count) appOffset=0;
            foreach(var window in windows.Skip(appOffset).Take(4)) Add(window.Title,$"PID {window.Pid} · A alterna  /  X abre detalhes",()=>RunSwitch(window));
            Add("Atualizar / mais janelas",windows.Count>4 ? $"{windows.Count} janelas · próximo grupo" : "Lista apenas janelas comuns e visíveis.",()=>{ appOffset=windows.Count>4 ? (appOffset+4)%windows.Count : 0; Observe(); RefreshRows(); });
            Add("Fechar a janela da sessão…",session.Anchor?.Title ?? "Nenhum alvo válido.",()=>{ if(session.Anchor is {} target) Confirm(target); },session.Anchor?.CanClose==true);
        } else {
            PageTitle.Text="Ajustes e diagnóstico";
            Add("Escolher tela","A percorre as telas. O monitor principal é apenas o ponto de partida.",()=>{ var selected=DisplayRef.Choose(displays,settings.DisplayId); var next=(displays.ToList().IndexOf(selected)+1)%displays.Count; settings=settings with { DisplayId=displays[next].Id }; settingsDirty=true; RefreshDisplays(false); RefreshRows(); });
            Add("Configurar Playnite…",settings.PlaynitePath is null ? "Escolha o executável pela caixa de arquivo (teclado disponível)." : Path.GetFileName(settings.PlaynitePath),ConfigurePlaynite);
            Add("Atalho Steam · "+settings.SteamShortcut,"A alterna Shift+Tab / Ctrl+Tab; a confirmação da sessão é invalidada.",()=>{settings=settings with {SteamShortcut=settings.SteamShortcut=="Shift+Tab" ? "Ctrl+Tab" : "Shift+Tab"}; settingsDirty=true;steam.Invalidate();steamTestTarget=null;RefreshRows();});
            Add("Integrações e confirmação Steam","Guide, teste Steam e limites de fechamento externo.",()=>{integrationDetails=true;index=0;RefreshRows();});
            Add("Salvar ajustes",platform.IsPreview ? "Na prévia, o salvamento também é simulado." : settingsDirty ? "Grava somente por esta ação explícita; mantém versão anterior." : "Nenhuma alteração pendente.",SaveSettings);
            Add("Encerrar painel",started ? (guide?.Status ?? "Guide indisponível.") : "Guide/atalho global desligados. Fechar overlays externos: não verificado.",Close);
        }
        SetSelection();
    }
    private static SolidColorBrush Brush(string value)=>(SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    private void SetSelection()
    {
        if (entries.Count==0) return; index=Math.Clamp(index,0,entries.Count-1);
        for(var i=0;i<entries.Count;i++) { entries[i].Button.BorderBrush=Brush(i==index ? "#77E3B0" : "#39505E"); entries[i].Button.Background=Brush(i==index ? "#25463B" : "#1A2932"); }
        if (!renderOnly && entries[index].Button.IsEnabled) entries[index].Button.Focus();
    }
    private void Move(int amount) { index=(index+amount+entries.Count)%entries.Count; SetSelection(); }
    private void ActivateEntry() { if (!busy && entries.Count>0 && entries[index].Button.IsEnabled) entries[index].Run(); }
    private void Back()
    {
        if (closeHold.Target is not null) { closeHold.Cancel();ConfirmPanel.Visibility=Visibility.Collapsed;StatusLabel.Text="Fechamento cancelado antes do envio.";return; }
        if (busy) { pending?.Cancel(); return; }
        if(integrationDetails) {integrationDetails=false;RefreshRows();return;}
        if(detailTarget is not null) {detailTarget=null;RefreshRows();return;}
        if(page!=0) {Page(0);return;}
        if(session.Anchor is {} target) RunSwitch(target);
    }
    private void Details() { if(page==1 && index<Math.Min(4,windows.Count-appOffset)) {detailTarget=windows[appOffset+index];index=0;RefreshRows();} }
    private void OnKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key is Key.Enter or Key.Space) { keyboardA=true; if(!e.IsRepeat && closeHold.Target is null) ActivateEntry(); e.Handled=true;return; }
        if (e.Key==Key.Escape) { Back();e.Handled=true;return; }
        if(closeHold.Target is not null) {e.Handled=true;return;}
        switch(e.Key) { case Key.Up: Move(-1);break;case Key.Down:case Key.Tab:Move(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);break;case Key.Left:case Key.PageUp:Page(page-1);break;case Key.Right:case Key.PageDown:Page(page+1);break;case Key.X:Details();break;default:return;} e.Handled=true;
    }
    private void Tick()
    {
        var now=Environment.TickCount64;
        if(started && now-lastObservation>=650 && !busy) { lastObservation=now;var previous=session.Anchor;Observe();if(!Equals(previous,session.Anchor)) { closeHold.Cancel();ConfirmPanel.Visibility=Visibility.Collapsed;RefreshRows(); } }
        if(!IsVisible || !IsActive || externalHandoff) return;
        PadFrame frame=default;
        if(padSlot is {} slot) {frame=Native.ReadPad(slot);if(!frame.Connected){padSlot=null;pad.Reset();closeHold.Cancel();ConfirmPanel.Visibility=Visibility.Collapsed;}}
        else for(uint i=0;i<4;i++) {var candidate=Native.ReadPad(i);if(candidate.Connected && (candidate.Buttons!=0 || Math.Abs((int)candidate.X)>18000 || Math.Abs((int)candidate.Y)>18000)){padSlot=i;frame=candidate;pad.Reset();break;}}
        var events=pad.Update(frame,now);
        if(closeHold.Target is {} closing) {
            var valid=platform.Valid(closing) ? closing : null;
            if(closeHold.Tick(events.ADown || keyboardA,events.Pressed.HasFlag(PadButtons.B),IsActive,keyboardA || padSlot is null || frame.Connected,valid,now)) {ConfirmPanel.Visibility=Visibility.Collapsed;RunClose(closing);}
            else if(closeHold.Target is null) ConfirmPanel.Visibility=Visibility.Collapsed;
            HoldProgress.Value=closeHold.Progress; ConfirmHint.Text=closeHold.Progress>0 ? $"Mantenha pressionado… {closeHold.Progress:P0}" : "Solte e mantenha A / Enter por 1,5 s. B / Esc cancela.";return;
        }
        if(busy) return;
        if(events.Direction!=0) Move(events.Direction);
        if(events.Pressed.HasFlag(PadButtons.A)) ActivateEntry();
        if(events.Pressed.HasFlag(PadButtons.B)) Back();
        if(events.Pressed.HasFlag(PadButtons.LB)) Page(page-1);
        if(events.Pressed.HasFlag(PadButtons.RB)) Page(page+1);
        if(events.Pressed.HasFlag(PadButtons.X)) Details();
    }
    private void Confirm(WindowRef target) { if(!platform.Valid(target)) {StatusLabel.Text="A janela mudou; atualize a lista.";return;}closeHold.Open(target);ConfirmTarget.Text=target.Title;ConfirmPanel.Visibility=Visibility.Visible;HoldProgress.Value=0; }
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
    public void SetRenderState(bool longTitle)
    {
        if(!longTitle)return;
        session.Select(new(101,41,100,"Uma aventura extraordinariamente longa pela galáxia — edição especial com um título maior que a largura da televisão e uma sessão que precisa continuar identificável","sample"));
        RefreshRows();StatusLabel.Text="Falhou · A identidade da janela mudou durante a solicitação. Selecione a janela novamente em Aplicativos; nenhum atalho foi enviado.";
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ApolloOverlay;

public partial class MainWindow
{
    private TextBox? detailsText;
    private TextBlock? controllerStatusParagraph;
    private void Section(string title)
    {
        var text=new TextBlock{Text=title,FontSize=14,FontWeight=FontWeights.SemiBold,Margin=new(2,18,0,8)};text.SetResourceReference(TextBlock.ForegroundProperty,ThemeSupport.SecondaryText);AddVisual(text);
    }
    private TextBlock Paragraph(string text,int size=16)
    {
        var block=new TextBlock{Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new(2,6,2,14)};block.SetResourceReference(TextBlock.ForegroundProperty,ThemeSupport.SecondaryText);AutomationProperties.SetName(block,text);AddVisual(block);return block;
    }
    private void AddVisual(FrameworkElement control)
    {
        var row=Rows.RowDefinitions.Count;Rows.RowDefinitions.Add(new(){Height=GridLength.Auto});Grid.SetRow(control,row);Grid.SetColumnSpan(control,2);Rows.Children.Add(control);
    }
    private Button AddRow(string id,string title,string description,Action action,bool available=true,bool danger=false,int? row=null,int column=0)
    {
        if(narrow)row=null;var index=row??Rows.RowDefinitions.Count;while(index>=Rows.RowDefinitions.Count)Rows.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var button=MakeButton(id,title,description,action,available,"",danger);Grid.SetRow(button,index);Grid.SetColumn(button,row is null?0:column);Grid.SetColumnSpan(button,row is null?2:1);if(row is not null)button.Margin=new(column==0?0:4,4,column==0?4:0,4);
        var reason=new Border{Focusable=true,Tag="description:"+id,Margin=new(-2)};AutomationProperties.SetName(reason,title+". "+description);AutomationProperties.SetHelpText(reason,description);button.Resources["DescriptionTarget"]=reason;Grid.SetRow(reason,index);Grid.SetColumn(reason,row is null?0:column);Grid.SetColumnSpan(reason,row is null?2:1);Rows.Children.Add(reason);Rows.Children.Add(button);return button;
    }
    private void BuildCurrentView()
    {
        controllerStatusParagraph=null;
        switch(view.Current.Route){
            case OverlayRoute.OperationResult:BuildResult();break;
            case OverlayRoute.WindowsAudio:BuildWindowsAudio();break;
            case OverlayRoute.TargetOptions:BuildOptions();break;
            case OverlayRoute.Details:BuildDetails();break;
            case OverlayRoute.Integrations:BuildIntegrations();break;
            case OverlayRoute.Customize:BuildCustomize();break;
            case OverlayRoute.Setup:BuildSetup();break;
            case OverlayRoute.PlayniteBrowser:BuildPlayniteBrowser();break;
            case OverlayRoute.Apps:BuildApps();break;
            case OverlayRoute.System:BuildSystem();break;
            default:BuildHub();break;
        }
    }
    private void BuildHub()
    {
        var target=Controller.Session.Anchor;var valid=target is not null&&Controller.Platform.Valid(target);
        RegisterFixed("session-card",SessionCardButton,()=>{if(target is null||!valid)ChooseSession();else RunEffect(()=>Controller.Switch(target),()=>Controller.Platform.Valid(target));},true,valid?target!.Title:"Escolher uma janela em Aplicativos");
        RegisterFixed("session-options",SessionOptionsButton,()=>{if(target is not null)OpenOptions(target);else ChooseSession();},true,"Opções da janela da sessão");
        AutomationProperties.SetName(SessionCardButton,valid?target!.Title+" · Retomar janela":"Escolher janela");AutomationProperties.SetHelpText(SessionCardButton,valid?"A / Enter retoma esta janela. X abre opções.":"Escolher uma janela em Aplicativos");
        var windowCount=Controller.Apps.Count;
        var windowSummary=windowCount switch{0=>"Nenhuma janela encontrada",1=>"1 janela disponível",_=>$"{windowCount} janelas disponíveis"};
        AddRow("apps","Aplicativos abertos",windowSummary,()=>Page(1));
        var favorites=SessionHubSettings.Favorites(Controller.Settings);
        if(favorites.Length>0){Section("Acesso rápido");var first=Rows.RowDefinitions.Count;for(var i=0;i<favorites.Length;i++)BuildFavorite(favorites[i],narrow?null:first+i/2,i%2);}
        var previous=Controller.Recent.Previous(Controller.Platform.Valid);
        var recents=Controller.Recent.Items.Where(w=>!w.Same(Controller.Recent.OpeningForeground)&&!w.Same(previous)).Take(4).ToArray();
        if(previous is not null||recents.Length>0)Section("Recentes");
        if(previous is not null)AddRow("previous-window","Janela anterior",previous.Title,()=>RunEffect(()=>Controller.Switch(previous),()=>Controller.Platform.Valid(previous)));
        foreach(var recent in recents)AddWindowRow(recent,"recent:",false);
        if(!Controller.Settings.SetupSeen)AddRow("setup-invitation","Personalizar painel","Configuração rápida em três passos",()=>OpenView(new(OverlayRoute.Setup,SelectedId:"setup-next")));
    }
    private void BuildFavorite(string id,int? row,int column)
    {
        var target=Controller.Session.Anchor;var steamContext=Controller.ReturnTarget;var menu=Controller.Platform.CanSteamMenu(steamContext);var steamGame=Controller.SteamOverlayEligible(steamContext);
        switch(id){
            case "playnite":AddRow(id,"Playnite",Controller.Settings.PlaynitePath is null?"Configure em Sistema":"Abrir biblioteca",()=>RunEffect((token,keep)=>Controller.Launch(true,token,keep),focusTarget:Controller.LaunchTarget(true)),Controller.Settings.PlaynitePath is not null||Controller.Apps.Any(a=>a.ProcessName.Equals("Playnite.FullscreenApp",StringComparison.OrdinalIgnoreCase)),row:row,column:column);break;
            case "steam":AddRow(id,"Steam Big Picture",Controller.SteamAvailable?"Abrir biblioteca":"Steam não encontrada",()=>RunEffect((token,keep)=>Controller.Launch(false,token,keep),focusTarget:Controller.LaunchTarget(false)),Controller.SteamAvailable,row:row,column:column);break;
            case "gamebar":AddRow(id,"Game Bar",target is null?"Escolha uma janela":"Xbox · janela da sessão",()=>Shortcut("Win+G"),target is not null,row:row,column:column);break;
            case "steam-overlay":AddRow(id,menu?"Menu do Steam Big Picture":"Overlay Steam",menu?"Big Picture":steamGame?Controller.HasSteamLaunchObservation(steamContext)?"Iniciado pela Steam · "+Controller.Settings.SteamShortcut:"Solicitar "+Controller.Settings.SteamShortcut:"Não disponível nesta janela",()=>{if(steamContext is null)return;if(menu)RunEffect((token,keep)=>Controller.Platform.SteamMenu(steamContext,token,keep),()=>Controller.Platform.CanSteamMenu(steamContext),steamContext);else QueueSteam(steamContext,()=>Controller.ReturnTarget?.Same(steamContext)==true&&Controller.SteamOverlayEligible(steamContext));},menu||steamGame,row:row,column:column);break;
            case "taskview":AddRow(id,"Visão de tarefas","Janelas do Windows",()=>RunEffect(Controller.Platform.TaskView),row:row,column:column);break;
            case "audio":AddRow(id,"Áudio do Windows",audio.Current.Available?audio.Current.Muted?"Saída sem som":audio.Current.Volume.ToString("P0"):"Saída indisponível",()=>Subpage("audio","volume"),row:row,column:column);break;
        }
    }
    private void AddWindowRow(WindowRef target,string prefix,bool chooser)
    {
        var id=prefix+WindowId(target);var eligible=!chooser||!target.Protected&&!target.Launcher;var button=AddRow(id,target.Title,eligible?target.ProcessName+(chooser?" · Usar nesta sessão":" · Alternar janela"):"Esta janela não pode ser a referência da sessão",()=>{if(chooser){if(!Controller.Platform.Valid(target)){FeedbackText("A janela mudou; atualize a lista.");return;}Controller.Select(target);Back();RefreshRows();}else RunEffect(()=>Controller.Switch(target),()=>Controller.Platform.Valid(target));},eligible);
        var grid=(Grid)button.Content;var texts=(StackPanel)grid.Children[1];texts.Margin=new(0,0,48,0);if(Controller.Platform.Icon(target) is {} icon){grid.Children.RemoveAt(0);grid.Children.Insert(0,new Image{Source=icon,Width=26,Height=26,HorizontalAlignment=HorizontalAlignment.Left});}
        var more=new Button{Content="···",Width=44,MinHeight=44,Padding=new(4),Margin=new(0,8,8,8),HorizontalAlignment=HorizontalAlignment.Right,Tag="options:"+id};AutomationProperties.SetName(more,"Opções de "+target.Title);AutomationProperties.SetHelpText(more,"Áudio do aplicativo, minimizar, detalhes e fechamento com confirmação");more.Click+=(_,_)=>ActivatePointerEntry(more);
        Grid.SetRow(more,Grid.GetRow(button));Grid.SetColumn(more,1);Rows.Children.Add(more);RegisterFixed("options:"+id,more,()=>OpenOptions(target),true,"Opções de "+target.Title);
    }
    private void BuildApps()
    {
        if(view.Current.ChooseSession)Paragraph("Escolha a janela que deseja retomar pelo painel.");
        foreach(var app in Controller.Apps)AddWindowRow(app,"",view.Current.ChooseSession);
        if(Controller.Apps.Count==0)Paragraph("Nenhuma janela encontrada. Atualize a lista para procurar novamente.",20);
        AddRow("refresh-apps","Atualizar janelas","Procurar novamente",async()=>{try{await Controller.Refresh();RefreshRows();}catch(OperationCanceledException){}});
        if(view.Current.ChooseSession){AddRow("clear-session","Limpar janela da sessão","Deixa o cartão sem uma janela selecionada",()=>{Controller.ClearSelection();Page(0);navigation=new("session-card");SetSelection(false);});AddRow("back","Voltar","Mantém a seleção anterior",Back);}
    }
    private void BuildOptions()
    {
        var target=view.Current.Target;if(target is null){AddRow("choose-session","Escolher janela","Abrir Aplicativos",ChooseSession);return;}
        var valid=Controller.Platform.Valid(target);Paragraph(target.Title,20);if(!valid)Paragraph("Esta janela não está mais disponível. Escolha outra janela.");
        AddRow("application-mute",MuteActionTitle(),valid?AppAudioDescription():"Esta janela não está mais disponível.",()=>MuteApplication(target),valid&&AudioFor(target)&&appAudio.CanSetMute);
        AddRow("minimize","Minimizar janela",valid&&target.CanClose?"O painel permanece aberto":"Janela protegida ou indisponível",()=>RunInPlace(target,token=>Controller.Platform.Minimize(target,token),"minimize"),valid&&target.CanClose);
        AddRow("choose-session","Escolher outra janela","Escolha o que deseja retomar",ChooseSession);
        AddRow("details","Detalhes da janela","Ver informações completas",()=>OpenView(new(OverlayRoute.Details,target,"switch-detail")));
        AddRow("close","Fechar janela…",target.CanClose&&valid?"Confirmar com nova pressão de 1,5 s":"Janela protegida ou indisponível",()=>Confirm(target),valid&&target.CanClose,true);
        AddRow("back","Voltar","Voltar",Back);
    }
    private void BuildDetails()
    {
        var target=view.Current.Target;if(target is null){AddRow("back","Voltar","Nenhuma janela selecionada",Back);return;}
        Paragraph(target.Title,22);AddRow("window-information","Título e informações completas","Enter / A para ler · setas rolam · ← / → volta às ações",()=>detailsText?.Focus());
        var information=$"{target.Title}\n\n{target.ProcessName} · PID {target.Pid}\nIdentidade de criação {target.Started} · geração {target.Generation}";
        if(AudioFor(target))information+=$"\n\nÁudio do aplicativo · {AudioBadge()}\n{appAudio.IdentifiedSessions} sessões identificadas · {appAudio.SkippedOrFailedSessions} não verificadas\n{appAudio.Reason}";
        detailsText=new TextBox{Text=information,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MinHeight=100,MaxHeight=140,FontSize=16,Padding=new(12),Margin=new(0,8,0,12)};detailsText.SetResourceReference(BackgroundProperty,ThemeSupport.Card);detailsText.SetResourceReference(ForegroundProperty,ThemeSupport.Text);AutomationProperties.SetName(detailsText,"Título completo e identidade da janela");AutomationProperties.SetHelpText(detailsText,"Setas rolam. Direita retorna às ações; Esc volta às opções.");AddVisual(detailsText);
        AddRow("switch-detail","Alternar para esta janela","Retoma apenas esta identidade",()=>RunEffect(()=>Controller.Switch(target),()=>Controller.Platform.Valid(target)),Controller.Platform.Valid(target));
        AddRow("select-session","Usar nesta sessão","Atualiza o cartão sem alternar o Windows",()=>{Controller.Select(target);Back();RefreshRows();},!target.Launcher&&!target.Protected&&Controller.Platform.Valid(target));
        AddRow("close-detail","Fechar esta janela…","Nova pressão longa · 1,5 s",()=>Confirm(target),target.CanClose&&Controller.Platform.Valid(target),true);
        AddRow("back","Voltar","Retorna às opções anteriores",Back);
    }
    private void BuildWindowsAudio()
    {
        var state=audio.Current;Paragraph("Ajuste a saída de áudio do Windows. Para o som de um aplicativo, abra as opções da janela.");
        AddRow("volume-down","Diminuir volume","− 5% · saída padrão do Windows",()=>AdjustVolume(-.05f),state.Available);
        AddRow("volume","Volume · "+(state.Available?state.Volume.ToString("P0"):"indisponível"),"+ 5% · ← / → ajusta",()=>AdjustVolume(.05f),state.Available);
        AddRow("mute",state.Muted?"Ativar som":"Silenciar","Silencia ou ativa a saída do Windows",()=>AudioAction(audio.Set(muted:!audio.Current.Muted)),state.Available);AddRow("back","Voltar",state.Status,Back);
    }
    private void BuildResult()
    {
        if(Controller.Operation is not {} result){AddRow("back","Voltar","Nenhum resultado retido",Back);return;}
        AddRow("operation-text","Ler mensagem completa","Setas para rolar · B / Esc para voltar",()=>operationText?.Focus());
        operationText=new TextBox{Text=result.Message,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MinHeight=120,MaxHeight=160,FontSize=16,Padding=new(12),Margin=new(0,8,0,8)};operationText.SetResourceReference(BackgroundProperty,ThemeSupport.Card);operationText.SetResourceReference(ForegroundProperty,ThemeSupport.Text);AutomationProperties.SetName(operationText,"Resultado completo · "+result.State);AddVisual(operationText);
        AddRow("dismiss-operation","Dispensar resultado","Remove apenas esta mensagem",DismissOperation);AddRow("back","Voltar ao painel","Mantém o resultado disponível",Back);
    }
    private void BuildIntegrations()
    {
        AddRow("test-steam","Solicitar overlay Steam",(Controller.HasSteamLaunchObservation(Controller.Session.Anchor)&&Controller.SteamEvidence!=ModuleEvidence.Loaded?"Iniciado pela Steam · ":"")+SteamPermission.Reason(Controller.SteamEvidence),()=>Shortcut(Controller.Settings.SteamShortcut),Controller.SteamOverlayEligible(Controller.Session.Anchor));
        AddGuideToggle();AddRow("external-close","Fechar overlays externos","Indisponível: visibilidade externa não verificada",()=>{},false);AddRow("back","Voltar","Duplo clique na bandeja abre o painel",Back);
    }
    private void BuildSystem()
    {
        Section("Exibição e aparência");AddDisplay();AddRow("side","Lado · "+(Controller.Settings.PanelSide=="Right"?"Direito":"Esquerdo"),"Escolha o lado do painel",()=>Change(Controller.Settings with{PanelSide=Controller.Settings.PanelSide=="Left"?"Right":"Left"}));AddScale();
        AddRow("motion",Controller.Settings.ReducedMotion?"Animações reduzidas":"Animações suaves","Ajustar movimento do painel",()=>Change(Controller.Settings with{ReducedMotion=!Controller.Settings.ReducedMotion}));
        Section("Controle");controllerStatusParagraph=Paragraph(controllerStatus);AddRow("deadzone","Deadzone · "+Controller.Settings.Deadzone,"Ajustar sensibilidade do analógico",()=>Change(Controller.Settings with{Deadzone=Controller.Settings.Deadzone<12000?12000:Controller.Settings.Deadzone<18000?18000:Controller.Settings.Deadzone<24000?24000:12000}));AddGuideToggle();AddGuideLimit();
        Section("Do seu jeito");AddRow("customize","Personalizar acesso rápido",$"{SessionHubSettings.Favorites(Controller.Settings).Length} ações visíveis · ordem e visibilidade",()=>OpenView(new(OverlayRoute.Customize,SelectedId:"favorite:playnite")));AddFeedback();AddRow("setup","Guia de configuração","Três passos opcionais · pode sair a qualquer momento",()=>OpenView(new(OverlayRoute.Setup,SelectedId:"setup-next")));
        Section("Integrações");AddRow("configure-playnite","Caminho do Playnite",Controller.Settings.PlaynitePath is null?"Escolher executável…":"Instalação configurada",ConfigurePlaynite);AddRow("shortcut","Atalho Steam · "+Controller.Settings.SteamShortcut,"A alterna Shift+Tab / Ctrl+Tab",()=>Change(Controller.Settings with{SteamShortcut=Controller.Settings.SteamShortcut=="Shift+Tab"?"Ctrl+Tab":"Shift+Tab"}));AddRow("integrations","Integrações e limites","Guide, Steam e funções externas",()=>Subpage("integrations","test-steam"));AddRow("save","Salvar ajustes","Salvar neste computador",Save);AddRow("exit","Encerrar overlay",Controller.SettingsDirty?"Alterações não salvas serão perdidas":"Encerra observadores e remove o ícone da bandeja",()=>QueueHandoff(Close));
    }
    private void AddDisplay()=>AddRow("display","Monitor · "+(Controller.Settings.DisplayId??"janela ativa"),"Automático ou escolha uma tela",CycleDisplay);
    private void AddScale()=>AddRow("ui-scale","Escala da interface · "+Controller.Settings.UiScalePercent+"%","100 / 115 / 130 / 150% · texto maior, conteúdo com rolagem",()=>{var scales=new[]{100,115,130,150};Change(Controller.Settings with{UiScalePercent=scales[(Array.IndexOf(scales,Controller.Settings.UiScalePercent)+1)%scales.Length]});});
    private void AddGuideToggle()=>AddRow("guide","Botão Xbox / Guide · "+(Controller.Settings.GuideXInputEnabled?"ligado":"desligado"),GuideDescription(),()=>Change(Controller.Settings with{GuideXInputEnabled=!Controller.Settings.GuideXInputEnabled}),!diagnostic&&!Controller.Platform.IsPreview);
    private void AddGuideLimit()
    {
        var button=AddRow("guide-limit",GuideLimitTitle(),"← / → ajusta · 0,5–3,0 s · Salvar para manter",()=>{});button.MinHeight=112;var texts=(StackPanel)((Grid)button.Content).Children[1];
        var slider=new Slider{Minimum=500,Maximum=3000,TickFrequency=100,SmallChange=100,LargeChange=100,IsSnapToTickEnabled=true,Focusable=true,Value=Controller.Settings.GuideOpenMaxHoldMs,Margin=new(0,9,0,0)};AutomationProperties.SetName(slider,"Limite de pressão do Guide para abrir, em milissegundos");
        slider.PreviewKeyDown+=(_,e)=>{var delta=e.Key switch{System.Windows.Input.Key.Left or System.Windows.Input.Key.Down=>-100,System.Windows.Input.Key.Right or System.Windows.Input.Key.Up=>100,System.Windows.Input.Key.PageDown=>-500,System.Windows.Input.Key.PageUp=>500,_=>0};if(delta!=0){slider.Value=Math.Clamp(slider.Value+delta,500,3000);e.Handled=true;}else if(e.Key is System.Windows.Input.Key.Home or System.Windows.Input.Key.End){slider.Value=e.Key==System.Windows.Input.Key.Home?500:3000;e.Handled=true;}};
        slider.ValueChanged+=(_,_)=>{Controller.Change(Controller.Settings with{GuideOpenMaxHoldMs=(int)Math.Round(slider.Value/100)*100});SetEntryText("guide-limit",GuideLimitTitle());UpdatePersistentState();FeedbackText("Alteração em memória · Salvar ajustes para manter.");};texts.Children.Add(slider);
    }
    private void AddFeedback()
    {
        AddRow("sound-feedback","Sons · "+(Controller.Settings.SoundFeedbackEnabled?"ligados":"desligados"),"Navegar, confirmar e voltar · prévia ao ligar",()=>Change(Controller.Settings with{SoundFeedbackEnabled=!Controller.Settings.SoundFeedbackEnabled}));
        AddRow("haptic-feedback","Vibração · "+(Controller.Settings.HapticFeedbackEnabled?"ligada":"desligada"),"Somente o controle ativo · pulsos curtos",()=>Change(Controller.Settings with{HapticFeedbackEnabled=!Controller.Settings.HapticFeedbackEnabled}));
        AddRow("test-feedback","Testar retorno",FeedbackDescription(),TestFeedback,Controller.Settings.SoundFeedbackEnabled||Controller.Settings.HapticFeedbackEnabled);
    }
    private void BuildCustomize()
    {
        Paragraph("Escolha suas ações e use as setas para mudar a ordem.");var visible=SessionHubSettings.Favorites(Controller.Settings);var ordered=visible.Concat(SessionHubSettings.Catalog.Where(id=>!visible.Contains(id))).ToArray();
        foreach(var id in ordered){var name=FavoriteName(id);var shown=visible.Contains(id);var button=AddRow("favorite:"+id,name,shown?"Visível · selecionar para ocultar":"Oculto · selecionar para mostrar",()=>Change(SessionHubSettings.Toggle(Controller.Settings,id)));button.Margin=new(0,4,104,4);var row=Grid.GetRow(button);AddReorder(id,name,row,-1,shown&&Array.IndexOf(visible,id)>0);AddReorder(id,name,row,1,shown&&Array.IndexOf(visible,id)<visible.Length-1);}
        AddRow("restore-favorites","Restaurar ordem padrão","Mostra as seis ações originais",()=>Change(Controller.Settings with{FavoriteQuickActions=null}));AddRow("back","Voltar","Alterações continuam pendentes até Salvar",Back);
    }
    private void AddReorder(string id,string name,int row,int delta,bool available)
    {
        var entryId=(delta<0?"earlier:":"later:")+id;var label="Mover "+name+(delta<0?" antes":" depois");var button=new Button{Content=delta<0?"↑":"↓",FontSize=21,Width=44,Height=44,MinHeight=44,Padding=new(4),Margin=new(0),IsEnabled=available,Tag=entryId};AutomationProperties.SetName(button,label);AutomationProperties.SetHelpText(button,available?"Muda somente a ordem das ações visíveis":"Sem posição disponível nesta direção");
        var holder=new Border{Child=button,Width=44,Height=44,Focusable=true,Tag="description:"+entryId,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,delta<0?54:4,0)};AutomationProperties.SetName(holder,label+". Sem posição disponível nesta direção");button.Resources["DescriptionTarget"]=holder;
        void Move(){navigation=new("favorite:"+id);Change(SessionHubSettings.Move(Controller.Settings,id,delta));}button.Click+=(_,_)=>ActivatePointerEntry(button);button.GotKeyboardFocus+=(_,_)=>navigation=new(entryId);Grid.SetRow(holder,row);Grid.SetColumn(holder,1);Rows.Children.Add(holder);RegisterFixed(entryId,button,Move,available,AutomationProperties.GetHelpText(button));
    }
    private static string FavoriteName(string id)=>id switch{"playnite"=>"Playnite","steam"=>"Steam Big Picture","gamebar"=>"Game Bar","steam-overlay"=>"Overlay Steam","taskview"=>"Visão de tarefas",_=>"Áudio do Windows"};
    private void BuildSetup()
    {
        var step=view.Current.SetupStep;Section($"CONFIGURAÇÃO · {step+1} DE 3");
        if(step==0){Paragraph("Seu painel, sempre à mão",23);controllerStatusParagraph=Paragraph(controllerStatus);Paragraph("Pressione e solte o botão Xbox / Guide para abrir. A leitura depende de um controle compatível. Também é possível abrir pela bandeja.");AddGuideToggle();}
        else if(step==1){Paragraph("Conforto na sua tela",23);Paragraph("Escolha o monitor, o lado e o tamanho. A prévia muda agora; salvar continua sendo sua escolha.");AddDisplay();AddRow("side","Lado · "+Controller.Settings.PanelSide,"Esquerdo / Direito",()=>Change(Controller.Settings with{PanelSide=Controller.Settings.PanelSide=="Left"?"Right":"Left"}));AddScale();}
        else{Paragraph("Bibliotecas e retorno",23);AddRow("configure-playnite","Configurar Playnite",Controller.Settings.PlaynitePath??"Escolher executável",ConfigurePlaynite);AddFeedback();}
        if(step>0)AddRow("setup-back","Passo anterior","Preserva seus ajustes",()=>SetSetupStep(step-1));
        AddRow("setup-next",step<2?"Continuar":"Concluir",step<2?"Próximo passo":"Marca o guia como visto; Salvar mantém os ajustes",()=>{if(step<2)SetSetupStep(step+1);else FinishSetup();});AddRow("setup-skip","Pular guia","Volta ao painel; nada é salvo automaticamente",FinishSetup);
    }
    private void SetSetupStep(int step){view.Remember(navigation.Id,ContentScroll.VerticalOffset);view.Replace(view.Current with{SetupStep=step,SelectedId="setup-next",ScrollOffset=0});navigation=new("setup-next");RefreshRows();ContentScroll.ScrollToTop();}
    private void FinishSetup(){Controller.Change(Controller.Settings with{SetupSeen=true});Back();RefreshRows();}
    private void RegisterFixed(string id,Button button,Action action,bool available,string reason)
    {
        button.Tag=id;button.IsEnabled=available;entries.Add(new(id,button,action,available,reason));
    }
}

internal sealed class PreviewApplicationAudio:IApplicationAudio
{
    public event Action<ApplicationAudioSnapshot>? Changed;
    private static ApplicationAudioSnapshot State(WindowRef? target,long generation)=>new(target,generation,1,ApplicationAudioAvailability.Unavailable,ApplicationMuteState.Unknown,0,0,"Prévia · sessões de áudio não consultadas");
    public void Track(WindowRef? target,bool visible,long generation){if(visible)Changed?.Invoke(State(target,generation));}
    public Task<ApplicationAudioSnapshot> RefreshAsync(WindowRef target,long generation,CancellationToken cancellation=default)=>Task.FromResult(State(target,generation));
    public Task<ApplicationAudioChange> SetMutedAsync(WindowRef target,bool desiredMute,long generation,CancellationToken cancellation=default)=>Task.FromResult(new ApplicationAudioChange(new(ResultKind.Simulated,"Prévia · áudio não alterado"),State(target,generation),0,0,0,false));
    public void Suspend(){}public void Dispose(){}
}
internal sealed class PreviewControllerDevices:IControllerDevices
{
    public void UpdateConnections(IReadOnlyList<bool> connected){}
    public ControllerDeviceSnapshot Read(uint slot)=>new(slot,0,false,ControllerBatteryType.Unknown,null,ControllerVibrationCapability.Unknown,0,"Prévia · bateria não consultada");
    public ControllerVibrationResult SetVibration(uint slot,long connectionEpoch,ushort leftMotor,ushort rightMotor)=>new(slot,connectionEpoch,0,false,"Prévia · vibração não enviada");
    public void Dispose(){}
}
internal sealed class PreviewFeedbackService:IFeedbackService
{
    public event Action<FeedbackStatus>? Changed {add{}remove{}}
    public FeedbackStatus Status=>new(false,false,"Prévia · sons e vibração não enviados");
    public void Configure(bool sound,bool haptics){}public void SetContext(FeedbackContext context){}public FeedbackDispatch Request(FeedbackCue cue,FeedbackSource source)=>new(false,false,Status.Reason);public void Suspend(){}public void Dispose(){}
}


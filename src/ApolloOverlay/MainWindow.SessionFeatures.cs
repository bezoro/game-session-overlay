using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ApolloOverlay;

public partial class MainWindow
{
    private ISessionMedia sessionMedia=null!;
    private SystemAudioDevices systemAudio=null!;
    private SessionProfiles sessionProfiles=null!;
    private ScreenCaptures screenCaptures=null!;
    private IApplicationAudio mixerAudio=null!;
    private CancellationTokenSource? featureRead;
    private long featureEpoch,nextFeatureRead;
    private Task featureTask=Task.CompletedTask;
    internal Task FeatureTask=>featureTask;
    private readonly Dictionary<string,ApplicationAudioSnapshot> mixerStates=[];
    private WindowRef? mixerTarget;
    private string selectedMedia="",editingProfile="tv",keyboardText="";
    private bool keyboardUpper,keyboardSymbols,keyboardForProfile;
    private WindowRef? keyboardTarget;
    private TextBox? keyboardEditor;
    private SessionProfile? profileDraft;
    private UserSettings? profileBefore,profileApplied;
    private SystemAudioSnapshot? profileAudioBefore,profileAudioApplied;
    private AudioSnapshot? profileMasterBefore,profileMasterApplied;
    private string profileActiveBefore="",profileActiveApplied="",artworkGameKey="";
    private ScreenCaptureItem[] captureItems=[];
    private ScreenCaptureItem? selectedCapture;
    private string artworkId="";
    private bool synchronizingFeatureSlider;
    private string renderedFeatureShape="";
    private bool DesktopPresentation=>sessionProfiles?.Items.FirstOrDefault(profile=>profile.Id==sessionProfiles.ActiveId)?.Presentation=="Desktop";
    private void OpenPrimaryAudio()
    {
        if(!NavigationAllowed||TransitionPending||Confirming||view.Current.Route==OverlayRoute.WindowsAudio)return;
        if(updateBusy)CancelUpdateInteraction();RunInterfaceAction(SettingsTab,()=>Subpage("audio","volume"),FeedbackCue.Navigation);
    }
    private void NavigatePrimaryTab(int direction)
    {
        if(Confirming||TransitionPending||updateCommitting)return;
        var current=view.Current.Route==OverlayRoute.WindowsAudio?2:page==1?1:0;
        var next=(current+direction+3)%3;
        if(next==2)OpenPrimaryAudio();else Page(next);
    }
    private FrameworkElement? SessionFeatureIcon(string id)
    {
        var glyph=id.StartsWith("output:",StringComparison.Ordinal)?"\uE767":id.StartsWith("capture:",StringComparison.Ordinal)?"\uE8B7":id switch
        {"session-volume" or "windows-volume" or "session-mixer" or "mixer-volume"=>"\uE767","microphone"=>"\uE720","session-profiles" or "profile-new"=>"\uE765","session-media" or "media-toggle"=>"\uE768","media-next"=>"\uE893","media-previous"=>"\uE892","session-keyboard" or "keyboard-editor"=>"\uE92E","keyboard-target"=>"\uE737","session-capture"=>"\uE114","session-gallery" or "capture-folder"=>"\uE8B7",_=>""};
        if(glyph.Length==0)return null;
        var icon=new TextBlock{Text=glyph,FontFamily=new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),FontSize=24,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Left};
        icon.SetResourceReference(TextBlock.ForegroundProperty,ThemeSupport.SecondaryText);return icon;
    }

    private void InitializeSessionFeatures(ISessionMedia? media,ISystemAudioBackend? audioBackend,IApplicationAudio? mixer)
    {
        sessionMedia=media??(diagnostic||Controller.Platform.IsPreview?new PreviewSessionMedia():new SessionMedia());
        systemAudio=new(audioBackend??(diagnostic||Controller.Platform.IsPreview?new PreviewSystemAudioBackend():null));
        sessionProfiles=new(Controller.Diagnostics.Root);screenCaptures=new(Controller.Diagnostics.Root);
        mixerAudio=mixer??(diagnostic||Controller.Platform.IsPreview?new PreviewApplicationAudio():new ApplicationAudio(Controller.Platform.Valid));
        sessionMedia.Changed+=SessionMediaChanged;
    }
    private void SuspendSessionFeatures()
    {
        featureEpoch++;featureRead?.Cancel();nextFeatureRead=0;
        sessionMedia?.SetVisible(false);mixerAudio?.Suspend();
    }
    private void DisposeSessionFeatures()
    {
        SuspendSessionFeatures();sessionMedia.Changed-=SessionMediaChanged;sessionMedia.Dispose();mixerAudio.Dispose();
    }
    private void ResumeSessionFeatures()
    {
        if(!NavigationAllowed)return;
        systemAudio.InvalidateObservation();
        sessionMedia.SetVisible(true);mixerAudio.Track(null,true,featureEpoch);
        StartFeatureRead();
        var current=featureTask;featureTask=Task.WhenAll(current,LoadCaptures());
    }
    private void PollSessionFeatures(long now)
    {
        if(now<nextFeatureRead||busy||TransitionPending||Confirming||!NavigationAllowed)return;
        if(view.Current.Route is OverlayRoute.Hub or OverlayRoute.WindowsAudio or OverlayRoute.Mixer or OverlayRoute.AudioDevices or OverlayRoute.Profiles or OverlayRoute.ProfileEdit)
            StartFeatureRead();
    }
    private void StartFeatureRead()
    {
        if(exiting||!IsVisible||featureRead is not null||!NavigationAllowed)return;
        nextFeatureRead=Environment.TickCount64+2500;
        var request=new CancellationTokenSource(5000);featureRead=request;var epoch=featureEpoch;
        featureTask=ReadSessionFeatures(epoch,request);
    }
    internal void RefreshSessionFeatures(){nextFeatureRead=0;StartFeatureRead();}
    private async Task ReadSessionFeatures(long epoch,CancellationTokenSource request)
    {
        try
        {
            var before=renderedFeatureShape;
            await systemAudio.RefreshAsync(request.Token);
            if(exiting||request.IsCancellationRequested||epoch!=featureEpoch||!IsVisible)return;
            if(view.Current.Route==OverlayRoute.Mixer)
            {
                foreach(var target in Controller.Apps.Where(window=>!window.Protected).DistinctBy(window=>(window.Pid,window.Started)).Take(12))
                {
                    var state=await mixerAudio.RefreshAsync(target,epoch,request.Token);
                    if(exiting||request.IsCancellationRequested||epoch!=featureEpoch||!IsVisible)return;
                    mixerStates[WindowId(target)]=state;
                }
            }
            UpdateSessionFeatureLabels();
            if(view.Current.Route is OverlayRoute.AudioDevices or OverlayRoute.Mixer)
            {
                if(before!=FeatureViewShape()&&Keyboard.FocusedElement is not (Slider or TextBox)&&!IsMouseCaptureWithin)RefreshRows();
                else UpdateMixerLabels();
            }
        }
        catch(OperationCanceledException){}
        catch(Exception error)when(error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {if(!exiting&&epoch==featureEpoch)FeedbackText("Não foi possível atualizar todos os controles da sessão. Tente atualizar a lista.");}
        finally{if(ReferenceEquals(featureRead,request))featureRead=null;request.Dispose();}
    }
    private string FeatureViewShape()=>view.Current.Route+"|"+(view.Current.Route==OverlayRoute.AudioDevices?
        string.Join("|",systemAudio.Current.Outputs.Select(item=>item.Id+":"+item.Name)):
        string.Join("|",Controller.Apps.Where(item=>!item.Protected).DistinctBy(item=>(item.Pid,item.Started)).Take(12).Select(WindowId))+"|"+
        (mixerTarget is {} target?mixerStates.GetValueOrDefault(WindowId(target))?.CanSetVolume+":"+mixerStates.GetValueOrDefault(WindowId(target))?.CanSetMute:""));
    private void UpdateMixerLabels()
    {
        if(view.Current.Route==OverlayRoute.AudioDevices)
        {
            foreach(var output in systemAudio.Current.Outputs)SetEntryDescription("output:"+output.Id,output.Id==systemAudio.Current.ConsoleOutputId&&output.Id==systemAudio.Current.MediaOutputId?"Em uso para jogos e mídia":"Usar para jogos e mídia");
            SetEntryDescription("device-refresh",systemAudio.Current.Status);return;
        }
        if(view.Current.Route!=OverlayRoute.Mixer)return;
        foreach(var window in Controller.Apps.Where(item=>!item.Protected).Take(12))
        {var state=mixerStates.GetValueOrDefault(WindowId(window));SetEntryDescription("mix:"+WindowId(window),state?.CanSetVolume==true?$"{state.VolumeMaximum:P0}"+(state.MuteState==ApplicationMuteState.Muted?" · sem som":""):"Consultar áudio deste aplicativo");}
        if(mixerTarget is not {} target||mixerStates.GetValueOrDefault(WindowId(target)) is not {} selected)return;
        SetFeatureEntry("mixer-volume",selected.CanSetVolume?$"Volume · {selected.VolumeMaximum:P0}":"Volume não informado","← / → ajusta o volume",selected.CanSetVolume);
        SetFeatureEntry("mixer-mute",selected.MuteState==ApplicationMuteState.Muted?"Ativar áudio":"Silenciar aplicativo",target.ProcessName,selected.CanSetMute);
        SyncSessionSlider("mixer-volume",selected.VolumeMaximum);
    }
    private void SessionMediaChanged(SessionMediaSnapshot snapshot)
    {
        if(exiting||Dispatcher.HasShutdownStarted)return;
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(()=>SessionMediaChanged(snapshot));return;}
        if(!IsVisible)return;
        if(!snapshot.Items.Any(item=>item.Id==selectedMedia))selectedMedia=snapshot.CurrentId;
        UpdateSessionFeatureLabels();
        if(view.Current.Route==OverlayRoute.Media&&!busy&&!TransitionPending)RefreshRows();
    }
    private SessionMediaItem? CurrentMedia=>sessionMedia.Current.Items.FirstOrDefault(item=>item.Id==selectedMedia)??sessionMedia.Current.Items.FirstOrDefault(item=>item.Id==sessionMedia.Current.CurrentId);
    private static string VolumeRange(ApplicationAudioSnapshot state)=>state.VolumeMaximum-state.VolumeMinimum>.01f?$"{state.VolumeMinimum:P0}–{state.VolumeMaximum:P0}":$"{state.VolumeMaximum:P0}";
    private string GameVolumeTitle=>appAudio.CanSetMute?"Jogo · "+(appAudio.MuteState==ApplicationMuteState.Muted?"sem som":appAudio.MuteState==ApplicationMuteState.Mixed?"áudio misto":appAudio.CanSetVolume?VolumeRange(appAudio):"som ativo"):"Som do jogo";
    private string GameVolumeDescription=>(appAudio.Availability==ApplicationAudioAvailability.Partial?"Áudio parcial · ":"")+(appAudio.CanSetVolume?"← / → volume · A "+(appAudio.MuteState==ApplicationMuteState.Muted?"ativa":"silencia"):appAudio.CanSetMute?"A "+(appAudio.MuteState==ApplicationMuteState.Muted?"ativa":"silencia")+" · volume não informado":"Abra o jogo e inicie o áudio para ajustar");
    private string WindowsVolumeTitle=>audio.Current.Available?audio.Current.Muted?"Saída · sem som":$"Saída · {audio.Current.Volume:P0}":"Saída indisponível";
    private string MicrophoneTitle=>systemAudio.Current.MicrophoneMuted switch{true=>"Microfone · sem som",false=>"Microfone · ligado",_=>"Microfone indisponível"};
    private void UpdateSessionFeatureLabels()
    {
        SetFeatureEntry("session-volume",GameVolumeTitle,GameVolumeDescription,appAudio.CanSetMute);
        SetFeatureEntry("windows-volume",WindowsVolumeTitle,systemAudio.Current.OutputName,audio.Current.Available);
        SetFeatureEntry("microphone",MicrophoneTitle,systemAudio.Current.MicrophoneName,systemAudio.Current.MicrophoneMuted is not null);
        var media=CurrentMedia;
        SyncSessionSlider("session-volume",appAudio.VolumeMaximum);SyncSessionSlider("windows-volume",audio.Current.Available?audio.Current.Volume:null);
        SetEntryDescription("session-media",media is null?"Reprodução nos aplicativos compatíveis":(media.Playing?"Tocando · ":"Pausado · ")+(media.Title.Length>0?media.Title:"Reprodução de mídia"));
    }
    private void SetFeatureEntry(string id,string title,string description,bool available)
    {
        var index=entries.FindIndex(entry=>entry.Id==id);if(index<0)return;
        var entry=entries[index];entry.Button.IsEnabled=available;entries[index]=entry with{Available=available,Reason=description};
        SetEntryText(id,title);SetEntryDescription(id,description);
        if(entry.Available!=available&&navigation.Id==id)SetSelection(false);
    }
    private void BuildSessionDashboard()
    {
        Section("Áudio da sessão");
        var gameVolume=AddRow("session-volume",GameVolumeTitle,GameVolumeDescription,ToggleGameAudio,appAudio.CanSetMute);
        AddSessionSlider(gameVolume,appAudio.VolumeMaximum??0,value=>SetGameVolume(value));SyncSessionSlider("session-volume",appAudio.VolumeMaximum);
        var first=Rows.RowDefinitions.Count;
        var outputVolume=AddRow("windows-volume",WindowsVolumeTitle,systemAudio.Current.OutputName,()=>AudioAction(audio.Set(muted:!audio.Current.Muted,expectedEndpoint:audio.Current.EndpointId)),audio.Current.Available,row:narrow?null:first,column:0);
        if(audio.Current.Available)AddSessionSlider(outputVolume,audio.Current.Volume,value=>AudioAction(audio.Set(value,expectedEndpoint:audio.Current.EndpointId)));
        AddRow("microphone",MicrophoneTitle,systemAudio.Current.MicrophoneName,ToggleMicrophone,systemAudio.Current.MicrophoneMuted is not null,row:narrow?null:first,column:1);
        AddRow("session-mixer","Mixer de aplicativos","Jogo, conversa e música separados",()=>OpenFeature(OverlayRoute.Mixer,"mixer-refresh"));
        Section("Sua sessão");first=Rows.RowDefinitions.Count;
        AddRow("session-profiles","Ambiente",sessionProfiles.Items.FirstOrDefault(profile=>profile.Id==sessionProfiles.ActiveId)?.Name??"TV ou Computador",()=>OpenFeature(OverlayRoute.Profiles,"profile:tv"),row:narrow?null:first,column:0);
        AddRow("session-capture","Capturar tela","Salvar uma imagem do monitor",CaptureSession,row:narrow?null:first,column:1);
        first=Rows.RowDefinitions.Count;
        AddRow("session-media","Mídia",CurrentMedia?.Title??"Controles de reprodução",()=>OpenFeature(OverlayRoute.Media,"media-toggle"),row:narrow?null:first,column:0);
        AddRow("session-keyboard","Teclado","Escrever usando o controle",()=>OpenKeyboard(false),row:narrow?null:first,column:1);
        AddRow("session-gallery","Suas capturas","Galeria local da sessão",OpenCaptures);
    }
    private void OpenFeature(OverlayRoute route,string first)
    {OpenView(new(route,SelectedId:first));StartFeatureRead();}
    private bool BuildSessionFeatureView()
    {
        switch(view.Current.Route)
        {
            case OverlayRoute.Mixer:BuildSessionMixer();return true;
            case OverlayRoute.AudioDevices:BuildAudioDevices();return true;
            case OverlayRoute.Profiles:BuildSessionProfiles();return true;
            case OverlayRoute.ProfileEdit:BuildProfileEditor();return true;
            case OverlayRoute.Media:BuildSessionMedia();return true;
            case OverlayRoute.Keyboard:BuildSessionKeyboard();return true;
            case OverlayRoute.Captures:BuildCaptures();return true;
            case OverlayRoute.CaptureDetail:BuildCaptureDetail();return true;
            default:return false;
        }
    }
    private string? SessionFeatureTitle=>view.Current.Route switch
    {OverlayRoute.Mixer=>"Mixer de aplicativos",OverlayRoute.AudioDevices=>"Saída e microfone",OverlayRoute.Profiles=>"Seu ambiente",OverlayRoute.ProfileEdit=>"Ajustar perfil",OverlayRoute.Media=>"Sua música",OverlayRoute.Keyboard=>"Teclado",OverlayRoute.Captures=>"Suas capturas",OverlayRoute.CaptureDetail=>"Captura da sessão",_=>null};
    private void StartSessionAction(string id,Func<CancellationToken,Task<ActionResult>> action,Action? completed=null)
    {
        if(!NavigationAllowed||busy||TransitionPending)return;
        QueueHandoff(()=>OperationTask=RunSessionAction(id,action,completed));
    }
    private async Task RunSessionAction(string id,Func<CancellationToken,Task<ActionResult>> action,Action? completed)
    {
        if(exiting||busy)return;
        var generation=++operationGeneration;busy=true;var request=new CancellationTokenSource();pending=request;
        RetainOperation("Pendente","Aplicando sua escolha…",id);
        ActionResult result;
        try{result=await action(request.Token);}
        catch(OperationCanceledException){result=new(ResultKind.Unknown,"Operação interrompida. Confira o estado antes de repetir.");}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {result=new(ResultKind.Failed,"Não foi possível concluir a operação. Confira o dispositivo e tente novamente.");}
        finally{request.Dispose();if(ReferenceEquals(pending,request))pending=null;busy=false;}
        if(exiting||generation!=operationGeneration)return;
        RecordResult(result,id);UpdateSessionFeatureLabels();UpdatePersistentState();
        if(result.Kind is ResultKind.Completed or ResultKind.Simulated)completed?.Invoke();
        if(IsVisible&&IsActive&&OwnForeground&&!focusConfirmed){CancelOpenAnimation();OpenPanel();}
    }
    private void ToggleGameAudio()
    {if(Controller.Session.Anchor is {} target)MuteApplication(target);}
    private void ToggleMicrophone()
    {
        var state=systemAudio.Current;if(state.MicrophoneMuted is not {} muted)return;
        StartSessionAction("microphone",token=>systemAudio.SetMicrophoneMutedAsync(state.MicrophoneId,!muted,token));
    }
    private bool AdjustSessionFeature(NavDirection direction)
    {
        if(direction is not(NavDirection.Left or NavDirection.Right))return false;
        var delta=direction==NavDirection.Left?-.05f:.05f;
        if(navigation.Id=="windows-volume")
        {AudioAction(audio.Set(Math.Clamp(audio.Current.Volume+delta,0,1),expectedEndpoint:audio.Current.EndpointId));return true;}
        if(navigation.Id=="session-volume"&&Controller.Session.Anchor is {} target)
        {
            if(!appAudio.CanSetVolume){FeedbackText("O volume do jogo não está confirmado. Use A para silenciar as sessões identificadas.");return true;}
            var desired=Math.Clamp(appAudio.VolumeMaximum!.Value+delta,0,1);var context=audioGeneration;
            SetGameVolume(desired);return true;
        }
        if(navigation.Id=="mixer-volume"&&mixerTarget is {} selected&&mixerStates.GetValueOrDefault(WindowId(selected)) is {CanSetVolume:true} snapshot)
        {ChangeMixerVolume(selected,Math.Clamp(snapshot.VolumeMaximum!.Value+delta,0,1));return true;}
        return false;
    }
    private void BuildSessionMixer()
    {
        Paragraph("Ajuste apenas o aplicativo escolhido. Sessões de outros processos ficam separadas.");
        AddRow("mixer-refresh","Atualizar aplicativos","Consultar sessões de áudio",()=>{nextFeatureRead=0;StartFeatureRead();});
        if(mixerTarget is {} target&&Controller.Platform.Valid(target))
        {
            Section(target.Title);var state=mixerStates.GetValueOrDefault(WindowId(target));
            var volume=AddRow("mixer-volume",state?.CanSetVolume==true?$"Volume · {state.VolumeMaximum:P0}":"Volume não informado","← / → ajusta o volume",()=>{},state?.CanSetVolume==true);
            if(state?.CanSetVolume==true)AddSessionSlider(volume,state.VolumeMaximum!.Value,value=>ChangeMixerVolume(target,value));
            AddRow("mixer-mute",state?.MuteState==ApplicationMuteState.Muted?"Ativar áudio":"Silenciar aplicativo",target.ProcessName,()=>
            {
                if(mixerStates.GetValueOrDefault(WindowId(target)) is not {CanSetMute:true} current)return;
                StartSessionAction("mixer-mute",async token=>{var change=await mixerAudio.SetMutedAsync(target,current.MuteState!=ApplicationMuteState.Muted,featureEpoch,token);mixerStates[WindowId(target)]=change.Snapshot;return change.Result;},RefreshRows);
            },state?.CanSetMute==true);
        }
        Section("Aplicativos");
        foreach(var window in Controller.Apps.Where(item=>!item.Protected).DistinctBy(item=>(item.Pid,item.Started)).Take(12))
        {
            var selected=window;var state=mixerStates.GetValueOrDefault(WindowId(window));
            AddRow("mix:"+WindowId(window),window.Title,state?.CanSetVolume==true?$"{state.VolumeMaximum:P0}"+(state.MuteState==ApplicationMuteState.Muted?" · sem som":""):"Consultar áudio deste aplicativo",()=>
            {mixerTarget=selected;RefreshRows();navigation=new("mixer-volume");SetSelection(false);nextFeatureRead=0;StartFeatureRead();});
        }
        AddRow("mixer-devices","Saída e microfone",systemAudio.Current.OutputName,()=>OpenFeature(OverlayRoute.AudioDevices,"device-refresh"));
        AddRow("back","Voltar","Painel da sessão",Back);
    }
    private void ChangeMixerVolume(WindowRef target,float value)
    {
        if(!NavigationAllowed||busy||TransitionPending)return;
        OperationTask=RunSessionAction("mixer-volume",async token=>{var change=await mixerAudio.SetVolumeAsync(target,value,featureEpoch,token);mixerStates[WindowId(target)]=change.Snapshot;return change.Result;},RefreshRows);
    }
    private void SetGameVolume(float value)
    {
        if(!NavigationAllowed||busy||TransitionPending||Controller.Session.Anchor is not {} target||!appAudio.CanSetVolume)return;
        var context=audioGeneration;
        OperationTask=RunInPlaceCore(target,async token=>{var change=await applicationAudio.SetVolumeAsync(target,Math.Clamp(value,0,1),context,token);if(!exiting)ApplicationAudioChanged(change.Snapshot);return change.Result;},"session-volume");
    }
    private void AddSessionSlider(Button button,float value,Action<float> commit)
    {
        var slider=new Slider{Minimum=0,Maximum=1,Value=Math.Clamp(value,0,1),SmallChange=.05,LargeChange=.1,TickFrequency=.05,IsSnapToTickEnabled=true,Focusable=true,Margin=new(0,8,0,0),MinHeight=24};
        slider.Style=(Style)FindResource("SessionVolumeSlider");button.Resources["SessionSlider"]=slider;
        System.Windows.Automation.AutomationProperties.SetName(slider,"Volume em porcentagem");
        slider.ValueChanged+=(_,_)=>{if(!synchronizingFeatureSlider&&slider.IsKeyboardFocusWithin&&!slider.IsMouseCaptureWithin&&NavigationAllowed&&!busy&&!TransitionPending)commit((float)slider.Value);};
        slider.PreviewMouseLeftButtonUp+=(_,_)=>{if(NavigationAllowed&&!busy&&!TransitionPending)commit((float)slider.Value);};
        ((StackPanel)((Grid)button.Content).Children[1]).Children.Add(slider);
    }
    private void SyncSessionSlider(string id,float? value)
    {
        if(entries.FirstOrDefault(entry=>entry.Id==id)?.Button.Resources["SessionSlider"] is not Slider slider)return;
        slider.IsEnabled=value is not null;if(value is null||slider.IsMouseCaptureWithin)return;
        synchronizingFeatureSlider=true;try{slider.Value=value.Value;}finally{synchronizingFeatureSlider=false;}
    }
    private bool HandleSessionSliderKey(KeyEventArgs args)
    {
        if(args.Key!=Key.Tab||args.OriginalSource is Slider or TextBox||KeyboardModifiersReader()!=ModifierKeys.None||
            entries.FirstOrDefault(entry=>entry.Id==navigation.Id)?.Button.Resources["SessionSlider"] is not Slider slider)return false;
        if(NavigationAllowed&&navigationKeysArmed&&!busy&&!TransitionPending)slider.Focus();
        args.Handled=true;return true;
    }
    private void BuildAudioDevices()
    {
        Paragraph("Escolha a saída do Windows para jogos e mídia. O volume físico da TV é independente.");
        AddRow("device-refresh","Atualizar dispositivos",systemAudio.Current.Status,()=>{nextFeatureRead=0;StartFeatureRead();});
        foreach(var output in systemAudio.Current.Outputs)
        {
            var chosen=output;var current=systemAudio.Current;
            AddRow("output:"+output.Id,output.Name,output.Id==current.ConsoleOutputId&&output.Id==current.MediaOutputId?"Em uso para jogos e mídia":"Usar para jogos e mídia",()=>
            {var expected=systemAudio.Current;StartSessionAction("audio-output",token=>systemAudio.SelectOutputAsync(expected.ConsoleOutputId,expected.MediaOutputId,chosen.Id,token),RefreshRows);});
        }
        AddRow("microphone",MicrophoneTitle,systemAudio.Current.MicrophoneName,ToggleMicrophone,systemAudio.Current.MicrophoneMuted is not null);
        AddRow("back","Voltar","Áudio da sessão",Back);
    }
    private void BuildSessionMedia()
    {
        var media=CurrentMedia;
        if(media is null)Paragraph("Abra música ou vídeo em um aplicativo compatível com os controles de mídia do Windows.");
        else{Paragraph(media.Title.Length>0?media.Title:"Reprodução de mídia",24);if(media.Artist.Length>0)Paragraph(media.Artist);}
        AddRow("media-toggle",media?.Playing==true?"Pausar":"Reproduzir",media is null?"Nenhuma mídia ativa":"Controlar a reprodução",()=>MediaCommand(SessionMediaCommand.Toggle),media?.CanToggle==true);
        var first=Rows.RowDefinitions.Count;
        AddRow("media-previous","Anterior","Voltar à faixa anterior",()=>MediaCommand(SessionMediaCommand.Previous),media?.CanPrevious==true,row:narrow?null:first,column:0);
        AddRow("media-next","Próxima","Avançar uma faixa",()=>MediaCommand(SessionMediaCommand.Next),media?.CanNext==true,row:narrow?null:first,column:1);
        if(sessionMedia.Current.Items.Count>1)foreach(var item in sessionMedia.Current.Items)
        {var selected=item;AddRow("media-source:"+item.Id,item.Title.Length>0?item.Title:"Reprodução de mídia",item.Id==media?.Id?"Selecionada":"Selecionar esta reprodução",()=>{selectedMedia=selected.Id;RefreshRows();});}
        AddRow("media-refresh","Atualizar mídia",sessionMedia.Current.Status,()=>featureTask=sessionMedia.RefreshAsync());
        AddRow("back","Voltar","Painel da sessão",Back);
    }
    private void MediaCommand(SessionMediaCommand command)
    {if(CurrentMedia is {} media)StartSessionAction("media",token=>sessionMedia.CommandAsync(media.Id,command,token),RefreshRows);}
}

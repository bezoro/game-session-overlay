using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApolloOverlay;

internal static class SessionInterfaceTests
{
    internal static void Run(Action<string,Action> test)
    {
        foreach(var size in new[]{(572,940),(360,480)})foreach(var route in new[]{"home","mixer","devices","profiles","edit","media","keyboard","gallery"})
        {
            var dimensions=size;var page=route;
            test($"session interface real focus and visible navigation {page} {dimensions.Item1}",()=>
            {
                using var host=new Host();var w=host.Window;host.Open(dimensions.Item1,dimensions.Item2);Navigate(w,page);
                for(var index=0;index<w.ActionIds.Count;index++)
                {
                    w.Sequential(1);w.UpdateLayout();RequireFocus(w);
                    if(Keyboard.FocusedElement is FrameworkElement focused&&((ScrollViewer)w.FindName("ContentScroll")).IsAncestorOf(focused))
                    {
                        var scroll=(ScrollViewer)w.FindName("ContentScroll");var bounds=focused.TransformToAncestor(scroll).TransformBounds(new Rect(new Size(focused.ActualWidth,focused.ActualHeight)));
                        Require(bounds.Top>=-3&&bounds.Bottom<=scroll.ActualHeight+3,"Focused action was clipped: "+w.SelectedAction);
                    }
                }
                var version=(TextBlock)w.FindName("HeaderIdentity");Pick(w,w.ActionIds[0]);w.UpdateLayout();
                Require(version.Visibility==Visibility.Visible&&version.Text.StartsWith("v"),"Compact page hid build version");
            });
        }
        test("session volume slider keeps native focus while keyboard edits commit exact app volume",()=>
        {
            using var host=new Host();var w=host.Window;host.Open();Pick(w,"session-volume");
            var button=ReviewFixTests.Button(w,"session-volume");TapKey(w,Key.Tab);
            Require(Keyboard.FocusedElement is Slider,"Tab did not enter the game volume slider");var slider=(Slider)Keyboard.FocusedElement;
            slider.Value=.5;Complete(w.OperationTask);
            Require(host.Audio.VolumeWrites==1&&Math.Abs(host.Audio.Volume-.5f)<.001&&slider.IsKeyboardFocusWithin,"Volume did not commit exactly once or lost child focus");
            TapKey(slider,Key.Tab);Require(w.SelectedAction!="session-volume"&&Keyboard.FocusedElement is not Slider,"Tab trapped the user in the volume slider");
        });
        test("session mixer refresh retains focused slider and applies current mute observation",()=>
        {
            using var host=new Host();var w=host.Window;host.Open();Navigate(w,"mixer");
            Pick(w,w.ActionIds.First(id=>id.StartsWith("mix:")));w.ActivateEntry();Complete(w.FeatureTask);Pick(w,"mixer-volume");
            TapKey(w,Key.Tab);Require(Keyboard.FocusedElement is Slider,"Mixer slider unavailable");var slider=(Slider)Keyboard.FocusedElement;
            host.Mixer.Volume=.35f;host.Mixer.Muted=true;w.RefreshSessionFeatures();Complete(w.FeatureTask);
            Require(ReferenceEquals(slider,Keyboard.FocusedElement)&&Math.Abs(slider.Value-.35)<.001,"Background observation rebuilt the focused mixer slider");
            Pick(w,"mixer-mute");w.ActivateEntry();Release(w);Complete(w.OperationTask);
            Require(host.Mixer.MuteWrites==1&&!host.Mixer.Muted,"Mute action used the state captured before the latest observation");
        });
        test("session keyboard edits buffer locally and returns from child editor using Tab",()=>
        {
            using var host=new Host();var w=host.Window;host.Open();Navigate(w,"keyboard");
            Pick(w,"key:q");w.ActivateEntry();Pick(w,"keyboard-editor");w.ActivateEntry();
            Require(Keyboard.FocusedElement is TextBox {IsReadOnly:false},"Editable local buffer did not receive focus");var editor=(TextBox)Keyboard.FocusedElement;
            editor.Text="Olá 👩‍🚀";TapKey(editor,Key.Tab);Pick(w,"keyboard-delete");w.ActivateEntry();
            var buffer=PremiumInterfaceTests.Descendants<TextBox>((DependencyObject)w.FindName("Rows")).Single();
            Require(buffer.Text=="Olá ","Virtual backspace split a visible character");
            Pick(w,"keyboard-insert");w.ActivateEntry();Require(w.Controller.Operation?.State=="Simulado","Diagnostic keyboard attempted native input");
        });
        test("session profile editor keeps unsaved name through keyboard back navigation",()=>
        {
            using var host=new Host();var w=host.Window;host.Open();Navigate(w,"edit");Pick(w,"profile-name");w.ActivateEntry();
            Pick(w,"keyboard-editor");w.ActivateEntry();((TextBox)Keyboard.FocusedElement).Text="Sala de jogos";TapKey(w,Key.Tab);Pick(w,"keyboard-insert");w.ActivateEntry();
            Require(w.SelectedAction=="profile-name"&&AutomationProperties.GetName(ReviewFixTests.Button(w,"profile-name")).Contains("Sala de jogos"),"Profile name or editing origin was lost");
            Require(!File.Exists(Path.Combine(host.Root,"session-profiles.json")),"Editing persisted a profile before Save");
        });
        test("session cancelled profile undo preserves layout and its undo record",()=>
        {
            using var backend=new ProfileAudioFixture();
            using var host=new Host(root=>{var profiles=new SessionProfiles(root);profiles.Save(profiles.Items[0] with{OutputId="second",OutputName="Second output"});},backend,false);
            var w=host.Window;host.Open();Navigate(w,"profiles");Pick(w,"profile:tv");w.ActivateEntry();Release(w);Complete(w.OperationTask);
            Require(w.Controller.Settings.UiScalePercent==115&&!w.Controller.SettingsDirty&&w.ActionIds.Contains("profile-undo"),"Profile was not applied and saved");
            Pick(w,"profile-undo");w.ActivateEntry();Release(w);
            try
            {
                var entered=Task.Run(()=>backend.RestoreEntered.Wait(2000));Complete(entered);Require(entered.Result,"Undo did not enter the output restoration");w.Back();Complete(w.OperationTask);
                Require(w.Controller.Settings.UiScalePercent==115&&!w.Controller.SettingsDirty&&w.ActionIds.Contains("profile-undo")&&new SessionProfiles(host.Root).ActiveId=="tv","Cancelled undo persisted layout or discarded its restore record");
            }
            finally{backend.ReleaseRestore.Set();}
        });
        test("session media invokes only the identified source and observes its result",()=>
        {
            using var host=new Host();var w=host.Window;host.Open();Navigate(w,"media");Pick(w,"media-toggle");w.ActivateEntry();Release(w);Complete(w.OperationTask);
            Require(host.Media.Commands==1&&host.Media.LastId=="music"&&!host.Media.Current.Items[0].Playing,"Media command missed its explicit source");
            Require(AutomationProperties.GetName(ReviewFixTests.Button(w,"media-toggle")).Contains("Reproduzir"),"Paused state was not reflected visually");
        });
    }
    internal static void Capture(string output)
    {
        Directory.CreateDirectory(output);var app=Application.Current??new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        foreach(var light in new[]{false,true})foreach(var width in new[]{572,360})foreach(var route in new[]{"home","mixer","devices","profiles","edit","media","keyboard","gallery"})
        {
            ThemeSupport.ForceForValidation(app,light);using var host=new Host();var w=host.Window;var height=width==572?940:600;host.Open(width,height);Navigate(w,route);w.UpdateLayout();
            var root=(FrameworkElement)w.Content;root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(output,$"{(light?"light":"dark")}-{width}-{route}.png"));png.Save(file);
        }
    }
    private static void Navigate(MainWindow w,string page)
    {
        switch(page)
        {
            case "mixer":Choose(w,"session-mixer");Complete(w.FeatureTask);break;
            case "devices":w.SetRenderState("audio");Choose(w,"audio-devices");Complete(w.FeatureTask);break;
            case "profiles":Choose(w,"session-profiles");break;
            case "edit":Choose(w,"session-profiles");Choose(w,"profile-edit:tv");break;
            case "media":Choose(w,"session-media");break;
            case "keyboard":Choose(w,"session-keyboard");break;
            case "gallery":Choose(w,"session-gallery");Complete(w.FeatureTask);break;
        }
        w.UpdateLayout();
    }
    private static void Choose(MainWindow w,string id){Pick(w,id);w.ActivateEntry();w.UpdateLayout();}
    private static void Pick(MainWindow w,string id)
    {for(var i=0;i<=w.ActionIds.Count&&w.SelectedAction!=id;i++)w.Sequential(1);Require(w.SelectedAction==id,"Action unreachable: "+id);w.UpdateLayout();}
    private static void TapKey(UIElement element,Key key)
    {
        var source=PresentationSource.FromVisual(element)??throw new Exception("Window has no source");
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent});
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyUpEvent});
    }
    private static void Release(MainWindow w){var pads=new PadFrame[]{new(true,0),default,default,default};w.AdvanceHandoff(new(pads,0,GuideValid:true),0);w.AdvanceHandoff(new(pads,100,GuideValid:true),100);}
    private static void RequireFocus(MainWindow w)
    {
        Require(w.IsActive&&Native.GetForegroundWindow()==new WindowInteropHelper(w).Handle,"Overlay does not own real foreground");
        Require(Keyboard.FocusedElement is FrameworkElement focused&&(Equals(focused.Tag,w.SelectedAction)||Equals(focused.Tag,"description:"+w.SelectedAction)),"Actual keyboard focus differs from "+w.SelectedAction);
    }
    private static void Require(bool value,string message){if(!value)throw new Exception(message);}
    private static void Complete(Task task)=>ReviewFixTests.Complete(task);
    private sealed class Host:IDisposable
    {
        private readonly SynchronizationContext? previous=SynchronizationContext.Current;
        internal readonly string Root=Path.Combine(Path.GetTempPath(),"SessionInterface",Guid.NewGuid().ToString("N"));
        internal readonly AudioFixture Audio=new(),Mixer=new();internal readonly MediaFixture Media=new();internal MainWindow Window{get;}
        internal Host(Action<string>? initialize=null,ISystemAudioBackend? backend=null,bool preview=true)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app=Application.Current??new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};if(app.TryFindResource(ThemeSupport.Card) is null)ThemeSupport.ForceForValidation(app,false);
            initialize?.Invoke(Root);
            Window=new(renderOnly:true,platform:new PremiumInterfaceTests.FixturePlatform(preview),diagnostics:new(Root),appAudioService:Audio,media:Media,systemAudioBackend:backend??new PreviewSystemAudioBackend(),mixerAudioService:Mixer);
            Window.PhysicalKeyReader=_=>0;Window.DpiReader=_=>96;Window.Controller.CaptureReturn();Complete(Window.Controller.Refresh());Window.RefreshRows();
        }
        internal void Open(int width=572,int height=940)
        {
            Window.Left=-12000;Window.Top=-12000;Window.Width=width;Window.Height=height;Window.ShowDiagnostic();Window.RefreshRows();Window.RefreshSessionFeatures();Complete(Window.FeatureTask);Window.UpdateLayout();
            RequireFocus(Window);
        }
        public void Dispose(){try{Window.Close();}finally{SynchronizationContext.SetSynchronizationContext(previous);if(Directory.Exists(Root))Directory.Delete(Root,true);}}
    }
    private sealed class AudioFixture:IApplicationAudio
    {
        private WindowRef? target;private long generation,revision;internal float Volume=.65f;internal bool Muted;internal int VolumeWrites,MuteWrites;
        public event Action<ApplicationAudioSnapshot>? Changed;
        private ApplicationAudioSnapshot Snapshot()=>new(target,generation,++revision,ApplicationAudioAvailability.Available,Muted?ApplicationMuteState.Muted:ApplicationMuteState.Audible,1,0,"Fixture readback"){VolumeMinimum=Volume,VolumeMaximum=Volume,VolumeSessions=1};
        public void Track(WindowRef? value,bool visible,long epoch){target=value;generation=epoch;if(visible)Changed?.Invoke(Snapshot());}
        public Task<ApplicationAudioSnapshot> RefreshAsync(WindowRef value,long epoch,CancellationToken cancellation=default){target=value;generation=epoch;return Task.FromResult(Snapshot());}
        public Task<ApplicationAudioChange> SetVolumeAsync(WindowRef value,float volume,long epoch,CancellationToken cancellation=default){VolumeWrites++;target=value;generation=epoch;Volume=volume;return Task.FromResult(new ApplicationAudioChange(new(ResultKind.Simulated,"Fixture volume"),Snapshot(),1,1,1,true));}
        public Task<ApplicationAudioChange> SetMutedAsync(WindowRef value,bool muted,long epoch,CancellationToken cancellation=default){MuteWrites++;target=value;generation=epoch;Muted=muted;return Task.FromResult(new ApplicationAudioChange(new(ResultKind.Simulated,"Fixture mute"),Snapshot(),1,1,1,true));}
        public void Suspend(){}public void Dispose(){Changed=null;}
    }
    private sealed class ProfileAudioFixture:ISystemAudioBackend,IDisposable
    {
        internal readonly ManualResetEventSlim RestoreEntered=new(),ReleaseRestore=new();
        private SystemAudioSnapshot state=new([new("first","First output"),new("second","Second output")],"first","first","mic","Microphone",false,"fixture");
        public SystemAudioSnapshot Read()=>state;
        public ActionResult SetMicrophoneMuted(string expectedId,bool muted,Func<bool> continuing)=>new(ResultKind.Unavailable,"fixture");
        public ActionResult SetOutputs(string expectedConsole,string expectedMedia,string console,string media,Func<bool> continuing)
        {
            if(console=="first"){RestoreEntered.Set();ReleaseRestore.Wait();}
            if(!continuing())return new(ResultKind.Unknown,"Cancelled native fixture");
            state=state with{ConsoleOutputId=console,MediaOutputId=media};return new(ResultKind.Simulated,"fixture readback");
        }
        public void Dispose(){ReleaseRestore.Set();}
    }
    private sealed class MediaFixture:ISessionMedia
    {
        public SessionMediaSnapshot Current{get;private set;}=new(1,[new("music","A moment between worlds","Session soundtrack","Fixture music",true,true,false,true)],"music","Fixture media");
        public event Action<SessionMediaSnapshot>? Changed;internal int Commands;internal string LastId="";
        public void SetVisible(bool visible){}public Task RefreshAsync(CancellationToken cancellation=default)=>Task.CompletedTask;
        public Task<ActionResult> CommandAsync(string id,SessionMediaCommand command,CancellationToken cancellation=default)
        {Commands++;LastId=id;Current=Current with{Revision=Current.Revision+1,Items=[Current.Items[0] with{Playing=false}]};Changed?.Invoke(Current);return Task.FromResult(new ActionResult(ResultKind.Simulated,"Fixture media"));}
        public void Dispose(){Changed=null;}
    }
}

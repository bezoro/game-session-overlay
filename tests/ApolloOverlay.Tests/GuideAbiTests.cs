using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ApolloOverlay;

internal static unsafe class GuideAbiTests
{
    // Synthetic native vtable follows the retained official header, not a COM marshalling/reflection stub.
    private static readonly List<string> Calls=[];
    private static delegate* unmanaged[Stdcall]<ulong,nint,nint,ulong,uint,uint,void> callback;
    private static nint context;
    private static bool registration=true,removal=true;
    private static uint policy;
    private static void Require(bool value,string reason="ABI/lifetime") {if(!value)throw new Exception(reason+": "+string.Join(',',Calls));}
    private static (nint Table,nint Instance) Allocate()
    {
        Calls.Clear();callback=null;context=0;registration=true;removal=true;policy=0;
        var table=(nint*)NativeMemory.AllocZeroed(17,(nuint)sizeof(nint));
        table[2]=(nint)(delegate* unmanaged[Stdcall]<nint,uint>)&Release;
        table[9]=(nint)(delegate* unmanaged[Stdcall]<nint,nint,uint,nint,delegate* unmanaged[Stdcall]<ulong,nint,nint,ulong,uint,uint,void>,ulong*,int>)&Register;
        table[11]=(nint)(delegate* unmanaged[Stdcall]<nint,ulong,void>)&Stop;
        table[12]=(nint)(delegate* unmanaged[Stdcall]<nint,ulong,byte>)&Unregister;
        table[16]=(nint)(delegate* unmanaged[Stdcall]<nint,uint,void>)&Policy;
        var instance=(nint*)NativeMemory.Alloc((nuint)sizeof(nint));*instance=(nint)table;
        return((nint)table,(nint)instance);
    }
    private static void Free((nint Table,nint Instance) fixture) {NativeMemory.Free((void*)fixture.Instance);NativeMemory.Free((void*)fixture.Table);}
    internal static void Success()
    {
        var fixture=Allocate();var backend=new GameInputBackend(fixture.Instance);var count=0;
        using(var connection=new GuideConnection(backend)) {
            Require(connection.Start(()=>count++));Require(policy==0x80 && context!=0 && callback!=null);
            callback(123,context,0,999,1,0);callback(123,context,0,1000,1,1);callback(123,context,0,1001,0,1);
            Require(count==1 && connection.Observed);
        }
        Require(string.Join(',',Calls)=="policy,register,stop,unregister,release");Free(fixture);
    }
    internal static void RegistrationFailure()
    {
        var fixture=Allocate();registration=false;
        using(var connection=new GuideConnection(new GameInputBackend(fixture.Instance))) Require(!connection.Start(()=>{}));
        Require(string.Join(',',Calls)=="policy,register,release");Free(fixture);
    }
    internal static void UnregisterFailure()
    {
        var fixture=Allocate();removal=false;var backend=new GameInputBackend(fixture.Instance);var count=0;
        var connection=new GuideConnection(backend);Require(connection.Start(()=>count++));connection.Dispose();
        callback(123,context,0,1001,1,0);Require(count==0);Require(!Calls.Contains("release"));
        removal=true;Require(backend.Unregister());backend.Release();Free(fixture);
    }
    internal static void DisposedPlatformBaseline()
    {
        var app=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};var window=new System.Windows.Window{Title="Owned disposed-platform fixture",Width=320,Height=200,WindowStyle=System.Windows.WindowStyle.None,ShowInTaskbar=true};using var platform=new WindowsPlatform(allowOwnedFixture:true);
        try{window.Show();window.Activate();var hwnd=new System.Windows.Interop.WindowInteropHelper(window).Handle;Require(Native.GetForegroundWindow()==hwnd,"Owned fixture could not acquire foreground");var target=platform.Foreground();Require(target is not null&&target.Handle==hwnd&&platform.Valid(target),"Owned target was not initially valid");platform.Dispose();Require(!platform.Valid(target!)&&!platform.RequestFocus(target!)&&!platform.IsForeground(target!),"Disposed platform still validates/focuses owned HWND");}finally{window.Close();}
    }
    internal static void LateLaunchBaseline()
    {
        var app=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(app);
        using var platform=new PendingLaunchPlatform();var window=new MainWindow(renderOnly:true,platform:platform);try{
            CompleteFixture(window.Controller.Refresh());var launch=window.Controller.Launch(false);Require(platform.Switches==1&&!launch.IsCompleted,"Steam launch did not suspend at focus await");
            window.Close();Require(platform.Disposed,"WPF Cleanup did not dispose platform");platform.Focus.SetResult(new(ResultKind.Completed,"Owned fake focus completed after Cleanup"));CompleteFixture(launch);
            Require(platform.Launches==0,"Steam launch escaped after Cleanup: late launches="+platform.Launches);
        }finally{window.Close();}
    }
    private static void CompleteFixture(Task task)
    {
        var until=Environment.TickCount64+5000;
        while(!task.IsCompleted){if(Environment.TickCount64>=until)throw new TimeoutException("Fixture did not settle within five seconds.");var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);System.Windows.Threading.Dispatcher.PushFrame(frame);}task.GetAwaiter().GetResult();
    }
    internal static void LateSendAfterCleanup()
    {
        using var platform=new PendingLaunchPlatform();var window=new MainWindow(renderOnly:true,platform:platform);try{var send=new GuardedActions(platform).Shortcut(platform.Target,"Win+G");Require(!send.IsCompleted,"Target shortcut did not suspend");window.Close();platform.Focus.SetResult(new(ResultKind.Completed,"Owned fake focus"));CompleteFixture(send);Require(platform.LateSends==0&&send.Result.Kind!=ResultKind.Completed,"Shortcut escaped after Cleanup");}finally{window.Close();}
    }
    private sealed class PendingLaunchPlatform:IPlatform,IWindowEffects
    {
        internal readonly TaskCompletionSource<ActionResult> Focus=new(TaskCreationOptions.RunContinuationsAsynchronously);internal int Switches,Launches,LateSends;internal bool Disposed;
        internal readonly WindowRef Target=new(202,43,100,"Owned fake Steam","steam");public bool IsPreview=>true;public WindowRef? Foreground()=>Target;public IReadOnlyList<WindowRef> Windows()=>[Target];public bool Valid(WindowRef w)=>!Disposed&&Target.Same(w);
        public IReadOnlyList<DisplayRef> Displays()=>[new("owned-fixture",0,0,1280,720,true)];public ModuleEvidence SteamEvidence(WindowRef? w)=>ModuleEvidence.Unknown;
        public Task<ActionResult> Switch(WindowRef w){Switches++;return Focus.Task;}public ActionResult Launch(bool p,string? s){Launches++;return new(ResultKind.Requested,"Owned fake launch");}
        public WindowObservation ObserveWindow(WindowRef w)=>WindowObservation.SameIdentity;public bool IsForeground(WindowRef w)=>false;public bool RequestFocus(WindowRef w)=>!Disposed;public uint SendKeys(string k){LateSends++;return 4;}public bool PostClose(WindowRef w)=>throw new Exception("Unexpected close");public Task Wait(int ms)=>Focus.Task;
        public Task<ActionResult> Close(WindowRef w,CancellationToken c)=>throw new Exception("Unexpected close");public Task<ActionResult> Shortcut(WindowRef w,string k)=>throw new Exception("Unexpected shortcut");public void Dispose()=>Disposed=true;
    }
    internal static void ReleaseBaseline(Action<string,Action> test)
    {
        var app=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(app);
        foreach(var route in new[]{"Esc","Enter","Effect"})test("Release gate holds actual WPF "+route+" before KeyUp",()=>{
            using var platform=new DismissPlatform(ResultKind.Completed);var window=new MainWindow(renderOnly:true,platform:platform);try{
                window.Controller.CaptureReturn();window.Controller.Select(platform.Target);window.RefreshRows();window.ShowDiagnostic();if(route=="Effect")window.Details();
                var key=route=="Esc"?System.Windows.Input.Key.Escape:System.Windows.Input.Key.Enter;
                var down=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(window),Environment.TickCount,key){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent};window.RaiseEvent(down);
                Require(window.IsVisible&&platform.Switches==0,"Handoff before KeyUp: visible="+window.IsVisible+"; switch effects="+platform.Switches);
            }finally{window.Close();}
        });
        test("Task View is available without ReturnTarget",()=>{
            using var platform=new DismissPlatform(ResultKind.Completed){NoTarget=true};var window=new MainWindow(renderOnly:true,platform:platform);try{
                window.Controller.CaptureReturn();window.ShowDiagnostic();var rows=(System.Windows.Controls.Grid)window.FindName("Rows");var button=rows.Children.OfType<System.Windows.Controls.Button>().Single(b=>(string?)b.Tag=="taskview");button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Require(!window.Feedback.StartsWith("Indisponível"),"Task View incorrectly blocked without a target: "+window.Feedback);
            }finally{window.Close();}
        });
    }
    internal static void FocusDismiss(Action<string,Action> test)
    {
        var app=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(app);
        foreach(var result in new[]{ResultKind.Failed,ResultKind.Unknown})foreach(var back in new[]{false,true})test("Dismiss "+(back?"B":"Toggle")+" remains hidden after focus "+result,()=>{
            using var platform=new DismissPlatform(result);var window=new MainWindow(renderOnly:true,platform:platform);try{
                window.Controller.CaptureReturn();window.Controller.Select(platform.Target);window.ShowDiagnostic();window.SetRenderState("confirmation");Require(window.Confirming,"Fixture did not arm confirmation");
                if(back){window.Back();Require(!window.Confirming,"B did not cancel confirmation");window.Back();}else window.Toggle();
                Require(window.TransitionPending&&window.IsVisible&&platform.Switches==0,"Dismiss escaped before release gate");window.AdvanceHandoff(Neutral(0),0);window.AdvanceHandoff(Neutral(99),99);Require(window.IsVisible&&platform.Switches==0);window.AdvanceHandoff(Neutral(100),100);
                Require(platform.Switches==1,"Owned fixture did not exercise restore branch; foreground unavailable");Require(!window.Confirming,"Dismiss retained close hold");Require(!window.IsVisible,"Dismiss reopened overlay after "+result+"; expected hidden independently of restore outcome");
            }finally{window.Close();}
        });
    }
    private sealed class DismissPlatform(ResultKind result):IPlatform
    {
        internal readonly WindowRef Target=new(101,42,100,"Owned fake return target","fixture");internal int Switches;internal bool NoTarget;
        public bool IsPreview=>true;public WindowRef? Foreground()=>NoTarget?null:Target;public IReadOnlyList<WindowRef> Windows()=>[Target];public bool Valid(WindowRef w)=>Target.Same(w);
        public IReadOnlyList<DisplayRef> Displays()=>[new("owned-fixture",0,0,1280,720,true)];public ModuleEvidence SteamEvidence(WindowRef? w)=>ModuleEvidence.Unknown;
        public Task<ActionResult> Switch(WindowRef w){Switches++;Require(Target.Same(w));return Task.FromResult(new ActionResult(result,"Owned fake focus restore "+result));}
        public Task<ActionResult> Close(WindowRef w,CancellationToken c)=>throw new Exception("Unexpected close");public Task<ActionResult> Shortcut(WindowRef w,string k)=>throw new Exception("Unexpected shortcut");public ActionResult Launch(bool p,string? s)=>throw new Exception("Unexpected launch");public void Dispose(){}
    }
    private static ReleaseSample Neutral(long now)=>new(new PadFrame[4],now);
    private static void Key(MainWindow w,System.Windows.Input.Key key,bool down)=>w.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(w),Environment.TickCount,key){RoutedEvent=down?System.Windows.Input.Keyboard.PreviewKeyDownEvent:System.Windows.Input.Keyboard.PreviewKeyUpEvent});
    private static System.Windows.Controls.Button Button(MainWindow w,string id)=>id=="session-card"?(System.Windows.Controls.Button)w.FindName("SessionCardButton"):((System.Windows.Controls.Grid)w.FindName("Rows")).Children.OfType<System.Windows.Controls.Button>().Single(b=>(string?)b.Tag==id);
    internal static void SystemKeyBaseline()
    {
        using var platform=new HandoffPlatform();var window=Host(platform);var observed=new List<(bool Down,System.Windows.Input.Key Key,System.Windows.Input.Key Actual)>();
        window.AddHandler(System.Windows.Input.Keyboard.PreviewKeyDownEvent,new System.Windows.Input.KeyEventHandler((_,e)=>observed.Add((true,e.Key,e.SystemKey))),true);
        window.AddHandler(System.Windows.Input.Keyboard.PreviewKeyUpEvent,new System.Windows.Input.KeyEventHandler((_,e)=>observed.Add((false,e.Key,e.SystemKey))),true);
        var hwnd=new System.Windows.Interop.WindowInteropHelper(window).Handle;
        void Send(int message,nuint key,uint bits){Native.GetWindowThreadProcessId(hwnd,out var pid);Require(pid==Environment.ProcessId&&Native.GetForegroundWindow()==hwnd,"Owned fixture lost identity/foreground; no message sent");Require(Native.PostMessage(hwnd,(uint)message,(nint)key,(nint)(long)bits),"Owned WM_SYSKEY post failed");var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);System.Windows.Threading.Dispatcher.PushFrame(frame);}
        try{
            window.Toggle();Send(0x104,0x12,0x00380001);Send(0x104,0x59,0x20150001);Send(0x105,0x12,0xE0380001);
            foreach(var item in observed)Console.WriteLine("OWNED WPF "+(item.Down?"Down":"Up")+" Key="+item.Key+" SystemKey="+item.Actual);
            Require(observed.Any(e=>e.Down&&e.Key==System.Windows.Input.Key.System&&e.Actual==System.Windows.Input.Key.Y),"Provider did not produce actual System/Y; reproduction inconclusive");
            Require(observed.Any(e=>!e.Down&&e.Key==System.Windows.Input.Key.System&&e.Actual is System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt),"Provider did not produce actual AltUp");Require(!observed.Any(e=>!e.Down&&e.Actual==System.Windows.Input.Key.Y),"Y already released; reproduction invalid");
            // The fixed physical-key list is neutral after AltUp; Y is still down in this observed routed sequence.
            window.AdvanceHandoff(Neutral(0),0);window.AdvanceHandoff(Neutral(100),100);
            Require(window.IsVisible&&platform.Switches==0,"System/Y collapsed into marker: handoff before YUp; visible="+window.IsVisible+"; effects="+platform.Switches);
            Send(0x101,0x59,0xC0150001);Require(observed.Any(e=>!e.Down&&e.Key==System.Windows.Input.Key.Y&&e.Actual==System.Windows.Input.Key.None),"Provider did not produce normal YUp after Alt release");Console.WriteLine("OWNED WPF normal YUp observed after System/Y Down");window.AdvanceHandoff(Neutral(200),200);window.AdvanceHandoff(Neutral(299),299);Require(window.IsVisible&&platform.Switches==0);window.AdvanceHandoff(Neutral(300),300);Require(!window.IsVisible&&platform.Switches==1);
        }finally{window.Close();}
    }
    internal static void HandoffTests(Action<string,Action> test)
    {
        test("guide focus and fade fixtures restore the previous synchronization context on failure",()=>{
            var previous=System.Threading.SynchronizationContext.Current;var marker=new System.Threading.SynchronizationContext();
            try{
                System.Threading.SynchronizationContext.SetSynchronizationContext(marker);
                foreach(var suite in new Action<Action<string,Action>>[]{FocusOpenRed,FadeHandoffChecks,FocusChecks}){
                    var threw=false;try{suite((_,_)=>throw new InvalidOperationException("fixture stop"));}catch(InvalidOperationException error)when(error.Message=="fixture stop"){threw=true;}
                    Require(threw&&ReferenceEquals(System.Threading.SynchronizationContext.Current,marker),"Suite failed to restore prior context after a registration failure");
                }
            }finally{System.Threading.SynchronizationContext.SetSynchronizationContext(previous);}
        });
        KnownHeldDismiss(test);
        DismissCancellation(test);
        test("actual WPF System AltY survives AltUp until YUp and100ms",SystemKeyBaseline);
        test("processed marker normalization uses real-key data and rejects unresolved markers",()=>{
            var k=System.Windows.Input.Key.Y;var none=System.Windows.Input.Key.None;Require(MainWindow.NormalizeKey(System.Windows.Input.Key.System,k,none,none)==k);Require(MainWindow.NormalizeKey(System.Windows.Input.Key.ImeProcessed,none,k,none)==k);Require(MainWindow.NormalizeKey(System.Windows.Input.Key.DeadCharProcessed,none,none,k)==k);Require(MainWindow.NormalizeKey(k,System.Windows.Input.Key.LeftAlt,System.Windows.Input.Key.Enter,System.Windows.Input.Key.Space)==k);
            foreach(var marker in new[]{System.Windows.Input.Key.System,System.Windows.Input.Key.ImeProcessed,System.Windows.Input.Key.DeadCharProcessed}){Require(MainWindow.NormalizeKey(marker,none,none,none)==none);Require(MainWindow.NormalizeKey(marker,marker,marker,marker)==none);}
        });
        test("release requires fresh continuous100ms and resets on any new input",()=>{var g=new ReleaseGate();Require(g.Update(Neutral(0),0,true,18000)==ReleaseState.Waiting);Require(g.Update(Neutral(99),99,true,18000)==ReleaseState.Waiting);var pads=new PadFrame[4];pads[3]=new(true,(PadButtons)0x8000);Require(g.Update(new(pads,100),100,true,18000)==ReleaseState.Waiting);pads[3]=new(true,0);Require(g.Update(new(pads,101),101,true,18000)==ReleaseState.Waiting);Require(g.Update(new(pads,200),200,true,18000)==ReleaseState.Waiting);Require(g.Update(new(pads,201),201,true,18000)==ReleaseState.Ready);});
        test("release covers all digital bits both sticks and trigger threshold",()=>{foreach(var frame in new[]{new PadFrame(true,(PadButtons)0x8000),new(true,(PadButtons)0x80),new(true,(PadButtons)0x40),new(true,0,12000),new(true,0,Y:-12000),new(true,0,RX:12000),new(true,0,RY:-12000),new(true,0,LT:30),new(true,0,RT:30)}){var g=new ReleaseGate();var pads=new PadFrame[4];pads[2]=frame;g.Update(new(pads,0),0,true,18000);Require(g.Update(new(pads,1000),1000,true,18000)==ReleaseState.Waiting);}var ok=new ReleaseGate();var allowed=new PadFrame[]{new(true,0,11999,-11999,11999,-11999,29,29),default,default,default};ok.Update(new(allowed,0),0,true,18000);Require(ok.Update(new(allowed,100),100,true,18000)==ReleaseState.Ready);});
        test("release keyboard Guide validity and held reset stability",()=>{foreach(var guide in new[]{false,true}){var g=new ReleaseGate();g.Update(Neutral(0),0,true,18000);Require(g.Update(Neutral(90) with{KeysNeutral=guide,GuideHeld=guide},90,true,18000)==ReleaseState.Waiting);g.Update(Neutral(91),91,true,18000);Require(g.Update(Neutral(190),190,true,18000)==ReleaseState.Waiting);Require(g.Update(Neutral(191),191,true,18000)==ReleaseState.Ready);}var invalid=new ReleaseGate();Require(invalid.Update(Neutral(0) with{GuideValid=false},0,true,18000)==ReleaseState.Cancelled);});
        test("release topology error stale snapshot and focus loss cancel safely",()=>{var g=new ReleaseGate();var pads=new PadFrame[]{new(true,0),default,default,default};g.Prime(new(pads,0,GuideConnections:1));Require(g.Update(Neutral(1),1,true,18000)==ReleaseState.Cancelled);g.Reset();g.Update(Neutral(0),0,true,18000);pads[1]=new(true,0);Require(g.Update(new(pads,100),100,true,18000)==ReleaseState.Waiting);Require(g.Update(new(pads,199),199,true,18000)==ReleaseState.Waiting);Require(g.Update(new(pads,200),200,true,18000)==ReleaseState.Ready);Require(new ReleaseGate().Update(Neutral(0),81,true,18000)==ReleaseState.Cancelled);Require(new ReleaseGate().Update(Neutral(0),0,false,18000)==ReleaseState.Cancelled);pads[0]=new(false,0,ReadFailed:true);Require(new ReleaseGate().Update(new(pads,1),1,true,18000)==ReleaseState.Cancelled);});
        test("stale observed connected controller disappearing cancels external action",()=>{
            using var p=new HandoffPlatform();var w=Host(p);try{
                var previous=new PadFrame[]{new(true,PadButtons.A),default,default,default};
                w.HandoffSampleFixture=new(previous,Environment.TickCount64-1000);
                Button(w,"taskview").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Require(w.TransitionPending,"Fixture did not queue the external action");
                w.AdvanceHandoff(Neutral(0),0);w.AdvanceHandoff(Neutral(100),100);w.AdvanceHandoff(Neutral(300),300);
                Require(!w.TransitionPending&&p.Globals==0&&p.Switches==0,"Disconnection erased stale known connection and dispatched an effect");
                Require(w.Controller.Operation is {State:"Cancelado",Dispatched:false},"Uncertain handoff did not retain cancellation");
            }finally{w.Close();}
        });
        foreach(var key in new[]{System.Windows.Input.Key.Escape,System.Windows.Input.Key.Enter})test("WPF "+key+" waits actual routed KeyUp then100ms once",()=>{using var p=new HandoffPlatform();var w=Host(p);try{Key(w,key,true);w.AdvanceHandoff(Neutral(0),0);w.AdvanceHandoff(Neutral(100),100);Require(w.IsVisible&&p.Switches==0&&w.TransitionPending);Key(w,key,false);w.AdvanceHandoff(Neutral(200),200);w.AdvanceHandoff(Neutral(299),299);Require(w.IsVisible&&p.Switches==0);w.AdvanceHandoff(Neutral(300),300);Require(!w.IsVisible&&p.Switches==1&&!w.TransitionPending);w.AdvanceHandoff(Neutral(400),400);Require(p.Switches==1);}finally{w.Close();}});
        test("WPF TaskView has no target switch and sends only after release",()=>{using var p=new HandoffPlatform{Context=null};var w=Host(p);try{Button(w,"taskview").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Require(w.TransitionPending&&p.Globals==0&&p.Switches==0);w.AdvanceHandoff(Neutral(0) with{KeysNeutral=false},0);w.AdvanceHandoff(Neutral(100),100);Require(p.Globals==0&&w.IsVisible);w.AdvanceHandoff(Neutral(200),200);Require(p.Globals==1&&p.LastKeys=="Win+Tab"&&p.Switches==0&&!w.IsVisible);}finally{w.Close();}});
        test("WPF return uses Anchor while Steam tile uses captured BigPicture context",()=>{using var p=new HandoffPlatform{Menu=true};p.Context=p.BigPicture;var w=Host(p);try{Button(w,"session-card").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Commit(w);Require(p.LastTarget?.Same(p.Game)==true);w.ShowDiagnostic();w.Controller.CaptureReturn();w.RefreshRows();var tile=Button(w,"steam-overlay");var labels=(System.Windows.Controls.StackPanel)((System.Windows.Controls.Grid)tile.Content).Children[1];Require(((System.Windows.Controls.TextBlock)labels.Children[0]).Text=="Menu do Steam Big Picture");tile.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Require(p.Menus==0);Commit(w);Require(p.Menus==1&&p.LastTarget?.Same(p.BigPicture)==true&&p.LastKeys=="F1");}finally{w.Close();}});
        test("WPF Steam game tile uses current context and mode change cancels before effect",()=>{using var p=new HandoffPlatform{Loaded=true};var w=Host(p);try{Button(w,"steam-overlay").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Commit(w);Require(p.Shortcuts==1&&p.LastTarget?.Same(p.Game)==true);w.ShowDiagnostic();p.Context=p.BigPicture;p.Menu=true;w.Controller.CaptureReturn();w.RefreshRows();Button(w,"steam-overlay").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));p.Menu=false;Commit(w);Require(w.IsVisible&&p.Menus==0&&!w.TransitionPending);}finally{w.Close();}});
        test("WPF pending action cancellation cleanup and invalid identity never escape",()=>{using var p=new HandoffPlatform();var w=Host(p);Button(w,"session-card").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));p.ValidTarget=false;Commit(w);Require(w.IsVisible&&p.Switches==0&&!w.TransitionPending);p.ValidTarget=true;w.Toggle();w.Close();Commit(w);Require(p.Switches==0&&p.Disposed);});
        test("WPF duplicate Guide chord or hotkey request commits only once",()=>{using var p=new HandoffPlatform();var w=Host(p);try{w.Toggle();w.Toggle();Native.SendMessageTimeout(new System.Windows.Interop.WindowInteropHelper(w).Handle,0x312,1,0,2,500,out _);w.AdvanceHandoff(Neutral(0) with{GuideHeld=true},0);Require(w.IsVisible&&p.Switches==0);Commit(w);Require(!w.IsVisible&&p.Switches==1);}finally{w.Close();}});
        test("WPF extra observed key and modifier prevent handoff until KeyUp",()=>{using var p=new HandoffPlatform();var w=Host(p);try{Key(w,System.Windows.Input.Key.Y,true);Key(w,System.Windows.Input.Key.LeftCtrl,true);w.Toggle();Commit(w);Require(w.IsVisible&&p.Switches==0);Key(w,System.Windows.Input.Key.Y,false);Commit(w);Require(w.IsVisible&&p.Switches==0);Key(w,System.Windows.Input.Key.LeftCtrl,false);Commit(w);Require(!w.IsVisible&&p.Switches==1);}finally{w.Close();}});
        test("WPF external deactivation cancels pending without a delayed effect",()=>{using var p=new HandoffPlatform();var w=Host(p);var other=new System.Windows.Window{Title="Owned deactivation fixture",Width=200,Height=120};try{w.Toggle();other.Show();other.Activate();var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);System.Windows.Threading.Dispatcher.PushFrame(frame);Require(!w.IsVisible&&!w.TransitionPending&&p.Switches==0);Commit(w);Require(!w.IsVisible&&p.Switches==0);}finally{other.Close();w.Close();}});
        test("Guide release sample uses existing poll and exposes invalid/disconnected state",()=>{var b=Legacy();using var c=new GuideConnection(b);Require(c.Start(()=>{}));statuses[0]=0;guideButtons[0]=0x400;b.Poll();Require(b.Held&&b.SnapshotValid&&b.Connections==1&&polls==4);guideButtons[0]=0;b.Poll();Require(!b.Held&&b.SnapshotValid&&polls==8);statuses[0]=5;b.Poll();Require(!b.SnapshotValid&&b.Connections==0&&polls==12);});
        test("target shortcut focus continuation sends zero after WPF Cleanup",LateSendAfterCleanup);
        test("F1 and global keys are explicit allowlists; disposed native adapter dispatches zero",()=>{Require(Native.KeyInputs("F1").Length==2&&Native.KeyInputs("Win+Tab").Length==4&&Native.KeyInputs("Ctrl+1").Length==0&&Native.KeyInputs("unknown").Length==0);var count=0;using var p=new WindowsPlatform(true,events=>{count++;return (uint)events.Length;});p.Dispose();p.Dispose();Require(p.SendKeys("F1")==0);Require(p.TaskView().GetAwaiter().GetResult().Kind==ResultKind.Cancelled);Require(!p.CanSteamMenu(new(101,42,100,"Steam Big Picture Mode","steamwebhelper",true)));p.SteamMenu(new(101,42,100,"Steam Big Picture Mode","steamwebhelper",true)).GetAwaiter().GetResult();Require(count==0);Require(WindowsPlatform.BigPictureIdentity(new(1,1,1,"Steam Big Picture Mode","steamwebhelper",true)));Require(!WindowsPlatform.BigPictureIdentity(new(1,1,1,"Steam","steamwebhelper",true)));});
    }
    private static MainWindow Host(HandoffPlatform platform){var w=new MainWindow(renderOnly:true,platform:platform);w.Controller.Select(platform.Game);w.Controller.CaptureReturn();w.RefreshRows();w.ShowDiagnostic();return w;}
    internal static void FocusOpenRed(Action<string,Action> test)
    {
        var previousContext=System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        try
        {
        var app=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(app);
        foreach(var accepted in new[]{false,true})test("Normal OpenPanel "+(accepted?"accepted without foreground":"refused")+" cannot navigate",()=>{
            using var p=new HandoffPlatform();var other=new System.Windows.Window{Width=100,Height=100};var w=new MainWindow(renderOnly:true,platform:p);try{
                var otherHandle=new System.Windows.Interop.WindowInteropHelper(other).EnsureHandle();w.FocusForeground=()=>otherHandle;w.FocusRequest=_=>accepted;w.RefreshFocusFixture=()=>Task.CompletedTask;w.Controller.Select(p.Game);w.OpenPanel();FinishFocus(w.OpeningTask);var selected=w.SelectedAction;w.Move(NavDirection.Down);
                Require(w.SelectedAction==selected,"Unconfirmed foreground still admitted overlay navigation; requestAccepted="+accepted);Require(!w.TransitionPending&&p.Switches==0,"Unconfirmed opening dispatched action");
            }finally{w.Close();other.Close();}
        });
        }
        finally{System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);}
    }
    internal static void GuideReleaseRed(Action<string,Action> test)
    {
        test("hidden Guide DOWN cannot open before valid UP",()=>{var b=Legacy();using var c=new GuideConnection(b);var opened=0;Require(c.Start(()=>opened++));statuses[0]=0;b.PollForOverlay(false,1,0,2000);guideButtons[0]=0x400;b.PollForOverlay(false,1,10,2000);Require(opened==0,"Existing DOWN notification opened before release");guideButtons[0]=0;b.PollForOverlay(false,1,100,2000);Require(opened==1);});
        test("hidden Guide long poweroff hold cannot open",()=>{var b=Legacy();using var c=new GuideConnection(b);var opened=0;Require(c.Start(()=>opened++));statuses[0]=0;b.PollForOverlay(false,1,0,2000);guideButtons[0]=0x400;b.PollForOverlay(false,1,10,2000);b.PollForOverlay(false,1,5010,2000);guideButtons[0]=0;b.PollForOverlay(false,1,5011,2000);Require(opened==0,"Existing press edge ignores short-opening threshold");});
    }
    internal static void GuideOpeningChecks(Action<string,Action> test,string reportDirectory)
    {
        GuideReleaseRed(test);
        foreach(var limit in new[]{500,2000,3000})foreach(var extra in new[]{0,1})test("Guide release limit "+limit+" plus "+extra,()=>{var b=Legacy();using var c=new GuideConnection(b);var opens=0;Require(c.Start(()=>opens++));statuses[0]=0;b.PollForOverlay(false,1,0,limit);guideButtons[0]=0x400;b.PollForOverlay(false,1,10,limit);guideButtons[0]=0;b.PollForOverlay(false,1,10+limit+extra,limit);b.PollForOverlay(false,1,12+limit+extra,limit);Require(opens==(extra==0?1:0));});
        test("Guide threshold frozen at DOWN for next settings change",()=>{var b=Legacy();using var c=new GuideConnection(b);var opens=0;Require(c.Start(()=>opens++));statuses[0]=0;b.PollForOverlay(false,1,0,2000);guideButtons[0]=0x400;b.PollForOverlay(false,1,10,2000);guideButtons[0]=0;b.PollForOverlay(false,1,1010,500);Require(opens==1);guideButtons[0]=0x400;b.PollForOverlay(false,1,1020,500);guideButtons[0]=0;b.PollForOverlay(false,1,1521,3000);Require(opens==1);});
        foreach(var end in new[]{"error","disconnect","generation","manual-open"})test("Guide pending release invalidated by "+end,()=>{var b=Legacy();using var c=new GuideConnection(b);var opens=0;Require(c.Start(()=>opens++));statuses[0]=0;b.PollForOverlay(false,1,0,2000);guideButtons[0]=0x400;b.PollForOverlay(false,1,10,2000);var generation=end=="generation"?2:1;var visible=end=="manual-open";if(end=="error")statuses[0]=5;if(end=="disconnect")statuses[0]=1167;b.PollForOverlay(visible,generation,20,2000);statuses[0]=0;b.PollForOverlay(visible,generation,30,2000);guideButtons[0]=0;b.PollForOverlay(visible,generation,40,2000);Require(opens==0);guideButtons[0]=0x400;b.PollForOverlay(visible,generation,50,2000);if(visible)Require(opens==1);else{Require(opens==0);guideButtons[0]=0;b.PollForOverlay(false,generation,60,2000);Require(opens==1);}});
        test("Guide startup reconnect held and other slot UP never complete a gesture",()=>{var b=Legacy();using var c=new GuideConnection(b);var opens=0;Require(c.Start(()=>opens++));statuses[0]=statuses[1]=0;guideButtons[0]=0x400;b.PollForOverlay(false,1,0,2000);guideButtons[0]=0;b.PollForOverlay(false,1,1,2000);Require(opens==0);guideButtons[0]=0x400;b.PollForOverlay(false,1,2,2000);b.PollForOverlay(false,1,3,2000);Require(opens==0);statuses[0]=1167;b.PollForOverlay(false,1,4,2000);statuses[0]=0;b.PollForOverlay(false,1,5,2000);guideButtons[0]=0;b.PollForOverlay(false,1,6,2000);Require(opens==0);});
        test("four Guide releases coalesce once; stop blocks late release",()=>{var b=Legacy();using var c=new GuideConnection(b);var opens=0;Require(c.Start(()=>opens++));Array.Fill(statuses,0u);b.PollForOverlay(false,1,0,2000);Array.Fill(guideButtons,(ushort)0x400);b.PollForOverlay(false,1,1,2000);Array.Clear(guideButtons);b.PollForOverlay(false,1,2,2000);Require(opens==1);Array.Fill(guideButtons,(ushort)0x400);b.PollForOverlay(false,1,3,2000);c.Dispose();Array.Clear(guideButtons);var reads=polls;b.PollForOverlay(false,1,4,2000);Require(opens==1&&polls==reads&&frees==1);});
        GuideSettingsChecks(test,reportDirectory);
        test("actual WPF Guide slider updates same row with controller repeat and bounds",()=>{using var p=new HandoffPlatform();var w=OpeningHost(p);try{w.OpenPanel();FinishFocus(w.OpeningTask);w.Page(2);for(var i=0;i<20&&w.SelectedAction!="guide-limit";i++)w.Sequential(1);Require(w.SelectedAction=="guide-limit");var row=Button(w,"guide-limit");var texts=(System.Windows.Controls.StackPanel)((System.Windows.Controls.Grid)row.Content).Children[1];var slider=texts.Children.OfType<System.Windows.Controls.Slider>().Single();var selection=w.SelectedAction;w.Move(NavDirection.Left);Require(slider.Value==1900&&w.Controller.Settings.GuideOpenMaxHoldMs==1900&&ReferenceEquals(Button(w,"guide-limit"),row));var input=new InputCoordinator(w.Controller.Settings);var frames=new PadFrame[]{new(true,0),default,default,default};input.Update(frames,true,0);frames[0]=new(true,PadButtons.Right);var first=input.Update(frames,true,1);if(first.Events.Direction==2)w.Move(NavDirection.Right);var quiet=input.Update(frames,true,100);Require(quiet.Events.Direction==0);var repeat=input.Update(frames,true,501);if(repeat.Events.Direction==2)w.Move(NavDirection.Right);Require(first.Events.Direction==2&&repeat.Events.Direction==2&&slider.Value==2100);for(var i=0;i<40;i++)w.Move(NavDirection.Left);Require(slider.Value==500);for(var i=0;i<40;i++)w.Move(NavDirection.Right);Require(slider.Value==3000&&w.SelectedAction==selection&&ReferenceEquals(Button(w,"guide-limit"),row));w.Move(NavDirection.Down);Require(w.SelectedAction!=selection);w.Move(NavDirection.Up);Require(w.SelectedAction==selection);w.Back();Require(w.ActionIds.Contains("session-card"));}finally{w.Close();}});
        FadeChecks(test);
    }
    internal static void GuideSettingsChecks(Action<string,Action> test,string reportDirectory)
    {
        foreach(var value in new[]{"null","\"slow\"","{}","[]","499","3001","550","2000.5"})test("invalid Guide limit "+value+" preserves optin and unrelated JSON",()=>{var settings=UserSettings.Parse("{\"Version\":1,\"GuideXInputEnabled\":true,\"DisplayId\":\"left\",\"GuideOpenMaxHoldMs\":"+value+"}");Require(settings.GuideXInputEnabled&&settings.DisplayId=="left"&&settings.GuideOpenMaxHoldMs==2000&&settings.Version==3);});
        test("Guide settings old default and normalized version3 Save/reload in temporary file",()=>{var path=Path.Combine(reportDirectory,"guide-limit-settings.json");var original="{\"Version\":1,\"GuideXInputEnabled\":true}";File.WriteAllText(path,original);var settings=UserSettings.Load(path,out _);Require(settings.GuideOpenMaxHoldMs==2000&&File.ReadAllText(path)==original);UserSettings.Save(path,settings with{GuideOpenMaxHoldMs=500});var loaded=UserSettings.Load(path,out _);Require(loaded.Version==3&&loaded.GuideXInputEnabled&&loaded.GuideOpenMaxHoldMs==500&&File.ReadAllText(path+".previous")==original);});
    }
    private static void FadeChecks(Action<string,Action> test)
    {
        foreach(var reduced in new[]{false,true})test("whole Window opacity prepared before visibility reduced="+reduced,()=>{using var p=new HandoffPlatform();var w=OpeningHost(p);try{w.Controller.Change(w.Controller.Settings with{ReducedMotion=reduced});double? first=null;w.IsVisibleChanged+=(_,_)=>{if(w.IsVisible)first=w.Opacity;};w.OpenPanel();FinishFocus(w.OpeningTask);var animated=!reduced&&!System.Windows.SystemParameters.HighContrast&&System.Windows.SystemParameters.ClientAreaAnimation;Require(first==(animated?0:1),"First visible opacity was not prepared");var sidebar=(System.Windows.FrameworkElement)w.FindName("Sidebar");Require(sidebar.Opacity==1);var deadline=System.Diagnostics.Stopwatch.StartNew();while(w.HasAnimatedProperties&&deadline.ElapsedMilliseconds<1000){PumpFocus();Thread.Sleep(1);}Require(w.Opacity==1);w.OpenPanel();FinishFocus(w.OpeningTask);Require(w.Opacity==1,"Visible reconfirm restarted fade");w.Toggle();w.AdvanceHandoff(Neutral(0) with{GuideValid=false},0);w.AdvanceHandoff(Neutral(100) with{GuideValid=false},100);Require(!w.IsVisible&&w.Opacity==1);w.OpenPanel();FinishFocus(w.OpeningTask);w.Close();PumpFocus();Require(!w.IsVisible&&w.Opacity==1);}finally{w.Close();}});
    }
    private static void FinishFocus(Task task)
    {
        var deadline=System.Diagnostics.Stopwatch.StartNew();while(!task.IsCompleted&&deadline.ElapsedMilliseconds<5000){PumpFocus();Thread.Sleep(1);}Require(task.IsCompleted,"Owned dispatcher operation exceeded5s");task.GetAwaiter().GetResult();
    }
    private static void PumpFocus(){var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);System.Windows.Threading.Dispatcher.PushFrame(frame);}
    private static MainWindow OpeningHost(HandoffPlatform p)
    {
        var w=new MainWindow(renderOnly:true,platform:p);w.Controller.Select(p.Game);w.FocusForeground=()=>new System.Windows.Interop.WindowInteropHelper(w).Handle;w.FocusRequest=_=>true;w.RefreshFocusFixture=()=>Task.CompletedTask;return w;
    }
    internal static void FadeHandoffChecks(Action<string,Action> test)
    {
        var previousContext=System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        try
        {
        foreach(var scenario in new[]{"during", "at-end", "neutral", "read-failed"})test("fade protects foreground handoff: "+scenario,()=>{
            using var p=new HandoffPlatform();var w=Host(p);var frames=new PadFrame[]{new(true,0),default,default,default};
            try{
                w.PhysicalKeyReader=_=>0;w.ForceFadeFixture=true;w.FadeFramesFixture=()=>frames;
                Require(Native.GetForegroundWindow()==new System.Windows.Interop.WindowInteropHelper(w).Handle,"Owned fade fixture lacks foreground");
                w.BeginFadeFixture();Require(w.FadePending&&w.IsVisible,"Fade was not started");
                if(scenario=="during"){w.ReviewTick(new PadFrame[]{new(true,PadButtons.A),default,default,default});frames=new PadFrame[]{new(true,0),default,default,default};}
                if(scenario=="at-end")frames=new PadFrame[]{new(true,PadButtons.A),default,default,default};
                if(scenario=="read-failed")frames=new PadFrame[]{new(false,0,ReadFailed:true),default,default,default};
                var deadline=System.Diagnostics.Stopwatch.StartNew();while(w.FadePending&&deadline.ElapsedMilliseconds<5000){PumpFocus();Thread.Sleep(1);}
                Require(!w.FadePending,"Fade did not finish");
                if(scenario=="neutral")Require(!w.IsVisible,"Neutral fade did not hide");
                else Require(w.IsVisible,"Fade ceded focus after new or unreadable input");
                Require(p.Switches==0,"Fade unexpectedly requested an external switch");
            }finally{w.Close();}
        });
        }
        finally{System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);}
    }
    internal static void FocusChecks(Action<string,Action> test)
    {
        var previousContext=System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        try
        {FocusOpenRed(test);
        test("game Steam context return reopen confirms each opening and retains Anchor",()=>{using var p=new HandoffPlatform();var w=OpeningHost(p);var requests=0;w.FocusRequest=_=>{requests++;return true;};try{
            w.OpenPanel();FinishFocus(w.OpeningTask);w.Toggle();Commit(w);Require(!w.IsVisible,"Owned context-change setup did not dismiss");p.Context=p.BigPicture;
            w.OpenPanel();FinishFocus(w.OpeningTask);Require(w.Controller.Session.Anchor?.Same(p.Game)==true&&w.Controller.ReturnTarget?.Same(p.BigPicture)==true,"BigPicture context/Anchor mismatch");Button(w,"session-card").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Commit(w);Require(!w.IsVisible&&p.LastTarget?.Same(p.Game)==true);p.Context=p.Game;
            w.OpenPanel();FinishFocus(w.OpeningTask);var before=w.SelectedAction;w.Move(NavDirection.Down);Require(w.IsVisible&&w.SelectedAction!=before&&requests==3&&w.Controller.Session.Anchor?.Same(p.Game)==true);
        }finally{w.Close();}});
        foreach(var accepted in new[]{false,true})test("new explicit opening retries "+(accepted?"timeout":"refusal")+" without continuous focus requests",()=>{using var p=new HandoffPlatform();var other=new System.Windows.Window();var w=OpeningHost(p);var requests=0;try{
            var denied=new System.Windows.Interop.WindowInteropHelper(other).EnsureHandle();w.FocusForeground=()=>denied;w.FocusRequest=_=>{requests++;return accepted;};w.OpenPanel();FinishFocus(w.OpeningTask);Require(requests==1&&w.Feedback.Contains("Windows"));var selection=w.SelectedAction;w.ActivateEntry();Require(!w.TransitionPending&&p.Switches==0);w.Move(NavDirection.Down);Require(w.SelectedAction==selection);
            w.FocusForeground=()=>new System.Windows.Interop.WindowInteropHelper(w).Handle;w.FocusRequest=_=>{requests++;return true;};w.OpenPanel();FinishFocus(w.OpeningTask);w.Move(NavDirection.Down);Require(w.SelectedAction!=selection&&requests==2);
        }finally{w.Close();other.Close();}});
        test("refresh loss revokes admission and recovery alone does not rearm",()=>{using var p=new HandoffPlatform();var other=new System.Windows.Window();var w=OpeningHost(p);try{
            var refresh=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);w.RefreshFocusFixture=()=>refresh.Task;w.OpenPanel();var operation=w.OpeningTask;var selected=w.SelectedAction;var denied=new System.Windows.Interop.WindowInteropHelper(other).EnsureHandle();w.FocusForeground=()=>denied;refresh.SetResult();FinishFocus(operation);w.Move(NavDirection.Down);Require(w.SelectedAction==selected&&w.Feedback.Contains("Foco perdido"));w.FocusForeground=()=>new System.Windows.Interop.WindowInteropHelper(w).Handle;w.Move(NavDirection.Down);Require(w.SelectedAction==selected);w.RefreshFocusFixture=()=>Task.CompletedTask;w.OpenPanel();FinishFocus(w.OpeningTask);w.Move(NavDirection.Down);Require(w.SelectedAction!=selected);
        }finally{w.Close();other.Close();}});
        test("stale refresh continuation cannot change new cycle selection or reacquire",()=>{using var p=new HandoffPlatform();var w=OpeningHost(p);try{
            var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var requests=0;w.FocusRequest=_=>{requests++;return true;};w.RefreshFocusFixture=()=>first.Task;w.OpenPanel();var old=w.OpeningTask;w.RefreshFocusFixture=()=>Task.CompletedTask;w.OpenPanel();FinishFocus(w.OpeningTask);w.Move(NavDirection.Down);var selected=w.SelectedAction;first.SetResult();FinishFocus(old);Require(w.SelectedAction==selected&&requests==2);w.Move(NavDirection.Down);Require(w.SelectedAction!=selected);
        }finally{w.Close();}});
        foreach(var close in new[]{false,true})test("pending opening continuation inert after "+(close?"Cleanup":"Hide"),()=>{using var p=new HandoffPlatform();var w=OpeningHost(p);try{
            var refresh=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var requests=0;w.FocusRequest=_=>{requests++;return true;};w.RefreshFocusFixture=()=>refresh.Task;w.OpenPanel();var old=w.OpeningTask;if(close)w.Close();else{w.Toggle();Commit(w);}Require(!w.IsVisible);var effects=p.Switches;refresh.SetResult();FinishFocus(old);Require(!w.IsVisible&&p.Switches==effects&&requests==1);
        }finally{w.Close();}});
        foreach(var ok in new[]{false,true})test("owned Playnite dialog "+(ok?"OK":"Cancel")+" reconfirms preserving selection page and Anchor",()=>{using var p=new HandoffPlatform();var w=OpeningHost(p);try{
            w.OpenPanel();FinishFocus(w.OpeningTask);w.Page(2);var requests=0;w.FocusRequest=_=>{requests++;return true;};var dialog=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);w.PlayniteDialogFixture=()=>dialog.Task;Button(w,"configure-playnite").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));var selected=w.SelectedAction;w.Move(NavDirection.Up);Require(w.SelectedAction==selected);Key(w,System.Windows.Input.Key.Enter,true);dialog.SetResult(ok?"C:/owned/Playnite.FullscreenApp.exe":null);FinishFocus(w.DialogTask);Require(requests==1&&w.SelectedAction==selected&&w.ActionIds.Contains("configure-playnite")&&w.Controller.Session.Anchor?.Same(p.Game)==true&&w.Controller.ReturnTarget?.Same(p.Game)==true);Require(ok?w.Controller.Settings.PlaynitePath=="C:/owned/Playnite.FullscreenApp.exe":w.Controller.Settings.PlaynitePath is null);
            Key(w,System.Windows.Input.Key.Up,true);Require(w.SelectedAction==selected,"Dialog held gesture navigated before release");Key(w,System.Windows.Input.Key.Up,false);Key(w,System.Windows.Input.Key.Enter,false);Key(w,System.Windows.Input.Key.Up,true);Require(w.SelectedAction!=selected);Key(w,System.Windows.Input.Key.Up,false);
        }finally{w.Close();}});
        foreach(var accepted in new[]{false,true})test("owned dialog return "+(accepted?"accepted without confirmation":"refused")+" keeps safe exit",()=>{using var p=new HandoffPlatform();var other=new System.Windows.Window();var w=OpeningHost(p);try{
            w.OpenPanel();FinishFocus(w.OpeningTask);w.Page(2);var denied=new System.Windows.Interop.WindowInteropHelper(other).EnsureHandle();w.PlayniteDialogFixture=()=>{w.FocusForeground=()=>denied;w.FocusRequest=_=>accepted;return Task.FromResult<string?>(null);};Button(w,"configure-playnite").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));FinishFocus(w.DialogTask);var selected=w.SelectedAction;w.Move(NavDirection.Up);Require(selected==w.SelectedAction&&w.Feedback.Contains("Windows"));w.Back();w.Back();w.AdvanceHandoff(Neutral(0) with{GuideValid=false},0);w.AdvanceHandoff(Neutral(100) with{GuideValid=false},100);Require(!w.IsVisible&&p.Switches==0);
        }finally{w.Close();other.Close();}});
        foreach(var end in new[]{"hide","cleanup","new-request"})test("owned dialog late return cannot rearm after "+end,()=>{using var p=new HandoffPlatform();var w=OpeningHost(p);try{
            w.OpenPanel();FinishFocus(w.OpeningTask);w.Page(2);var requests=0;w.FocusRequest=_=>{requests++;return true;};var dialog=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);w.PlayniteDialogFixture=()=>dialog.Task;Button(w,"configure-playnite").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));if(end=="cleanup")w.Close();else if(end=="new-request")w.OpenPanel();else{w.Toggle();w.AdvanceHandoff(Neutral(0) with{GuideValid=false},0);w.AdvanceHandoff(Neutral(100) with{GuideValid=false},100);}dialog.SetResult("C:/owned/late.exe");FinishFocus(w.DialogTask);Require(requests==0&&w.Controller.Settings.PlaynitePath is null&&p.Switches==0);if(end!="new-request")Require(!w.IsVisible);
        }finally{w.Close();}});
        test("removed chord and hotkey do not activate; local LB/RB remains input",()=>{var input=new InputCoordinator(new());var neutral=new PadFrame[]{new(true,0),default,default,default};input.Update(neutral,false,0);var chord=new PadFrame[]{new(true,PadButtons.LB|PadButtons.RB|PadButtons.Start),default,default,default};Require(!input.Update(chord,false,1).Toggle&&!input.Update(chord,false,1000).Toggle);input.Cancel();input.Update(neutral,true,1001);var lb=new PadFrame[]{new(true,PadButtons.LB),default,default,default};Require(input.Update(lb,true,1002).Events.Pressed.HasFlag(PadButtons.LB));using var p=new HandoffPlatform();var w=OpeningHost(p);try{Native.SendMessageTimeout(new System.Windows.Interop.WindowInteropHelper(w).Handle,0x312,1,0,2,500,out _);Require(!w.IsVisible);w.OpenPanel();FinishFocus(w.OpeningTask);w.Page(2);Require(!w.ActionIds.Contains("hotkey")&&!w.ActionIds.Contains("chord"));Require(ApolloOverlay.Program.AlreadyRunningMessage.Contains("bandeja")&&!ApolloOverlay.Program.AlreadyRunningMessage.Contains("Ctrl"));}finally{w.Close();}});
        test("tray left double-click and menu callbacks dispatch once; disposed queue inert",()=>{var opens=0;var exits=0;var tray=new TrayHost(System.Windows.Threading.Dispatcher.CurrentDispatcher,()=>opens++,()=>exits++,visible:false);tray.OnDoubleClick(null,new(System.Windows.Forms.MouseButtons.Left,1,0,0,0));tray.OnDoubleClick(null,new(System.Windows.Forms.MouseButtons.Right,2,0,0,0));PumpFocus();Require(opens==0);tray.OnDoubleClick(null,new(System.Windows.Forms.MouseButtons.Left,2,0,0,0));PumpFocus();Require(opens==1);((System.Windows.Forms.ToolStripMenuItem)tray.Menu.Items[0]).PerformClick();((System.Windows.Forms.ToolStripMenuItem)tray.Menu.Items[1]).PerformClick();PumpFocus();Require(opens==2&&exits==1&&tray.Menu.Items[0].Text=="Abrir overlay"&&tray.Menu.Items[1].Text=="Sair");tray.OnDoubleClick(null,new(System.Windows.Forms.MouseButtons.Left,2,0,0,0));tray.Dispose();tray.Dispose();PumpFocus();Require(opens==2&&exits==1);});
        test("tray Sair closes hidden owned host without focus or external effects",()=>{using var p=new HandoffPlatform();var w=OpeningHost(p);var requests=0;w.FocusRequest=_=>{requests++;return true;};w.ExitFromTray();Require(p.Disposed&&!w.IsVisible&&requests==0&&p.Switches==0);});
        test("packaged custom icon embedded and exact32px frame exists in executable",()=>{using var stream=System.Windows.Application.GetResourceStream(new Uri("/ApolloOverlay;component/Assets/overlay-controller.ico",UriKind.Relative))!.Stream;using var data=new MemoryStream();stream.CopyTo(data);var bytes=data.ToArray();Require(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))=="c509ee949898ef1100ea251c228915245bb37a917cda94c9d08b3a292709d20a");using var reader=new BinaryReader(new MemoryStream(bytes));reader.BaseStream.Position=4;var count=reader.ReadUInt16();Require(count==8);byte[]? frame=null;for(var i=0;i<count;i++){var width=reader.ReadByte();reader.ReadBytes(7);var length=reader.ReadInt32();var offset=reader.ReadInt32();if(width==32)frame=bytes.AsSpan(offset,length).ToArray();}Require(frame is not null);var exe=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"ApolloOverlay.exe"));Require(exe.AsSpan().IndexOf(frame!)>=0,"Published executable missing exact custom32px PNG frame");});
        }
        finally{System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);}
    }
    internal static void KnownHeldDismiss(Action<string,Action> test)
    {
        var app=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(app);
        test("stale known held input becoming unreadable requires explicit local dismissal",()=>{
            using var p=new HandoffPlatform();var w=Host(p);try{
                w.HandoffSampleFixture=new(new PadFrame[]{new(true,PadButtons.A),default,default,default},Environment.TickCount64-1000);
                w.Toggle();var failed=new PadFrame[]{new(false,0,ReadFailed:true),default,default,default};
                w.AdvanceHandoff(new(failed,0),0);w.AdvanceHandoff(new(failed,1000),1000);
                Require(w.IsVisible&&w.TransitionPending&&p.Switches==0,"Stale active input was forgotten when reading failed");
                Key(w,System.Windows.Input.Key.Escape,true);Key(w,System.Windows.Input.Key.Escape,false);
                w.AdvanceHandoff(new(failed,2000),2000);w.AdvanceHandoff(new(failed,2100),2100);
                Require(!w.IsVisible&&!w.TransitionPending&&p.Switches==0&&p.Globals==0,"Explicit local recovery dispatched an external effect");
            }finally{w.Close();}
        });
        foreach(var held in new[]{"Escape","CommonButton","Stick","Trigger"})test("Guide failure cannot hide dismiss while known "+held+" remains held",()=>{
            using var p=new HandoffPlatform();var w=Host(p);try{
                var pads=new PadFrame[4];if(held=="Escape")Key(w,System.Windows.Input.Key.Escape,true);else{w.Toggle();pads[0]=held switch{"CommonButton"=>new(true,PadButtons.B),"Stick"=>new(true,0,X:25000),_=>new(true,0,LT:255)};}
                Require(w.IsVisible&&w.TransitionPending,"Fixture did not queue dismiss");var beforeForeground=Native.GetForegroundWindow()==new System.Windows.Interop.WindowInteropHelper(w).Handle;
                w.AdvanceHandoff(new(pads,0,GuideValid:false),0);Console.WriteLine("OWNED WPF known-held="+held+"; GuideValid=false; foreground_before_owned="+beforeForeground+"; visible_before_release="+w.IsVisible);
                Require(w.IsVisible,"Overlay hidden before known "+held+" release");Require(p.Switches==0&&p.Globals==0&&p.Menus==0&&p.Shortcuts==0,"Known-held dismissed to outgoing effect");
                w.AdvanceHandoff(new(pads,50,GuideValid:false),50);w.AdvanceHandoff(new(pads,100),100);Require(w.IsVisible&&w.TransitionPending&&p.Switches==0,"Guide recovery rearmed restore or hid known held");
                if(held=="Escape")Key(w,System.Windows.Input.Key.Escape,false);else pads[0]=new(true,0);
                w.AdvanceHandoff(new(pads,200),200);w.AdvanceHandoff(new(pads,299),299);Require(w.IsVisible&&p.Switches==0,"Degraded dismiss before100ms release");w.AdvanceHandoff(new(pads,300),300);Require(!w.IsVisible&&!w.TransitionPending&&p.Switches==0,"Degraded dismiss did not finish locally after release100ms");Commit(w);Require(p.Switches==0);
            }finally{w.Close();}
        });
        foreach(var channel in new[]{"Pad","Guide"})test("Historic "+channel+" DOWN becoming unknown requires fresh Escape and keeps other valid DOWN protected",()=>{
            using var p=new HandoffPlatform();var w=Host(p);try{
                w.Toggle();Key(w,System.Windows.Input.Key.Escape,true);var pads=new PadFrame[4];if(channel=="Pad")pads[0]=new(true,PadButtons.A);
                w.AdvanceHandoff(new(pads,0,GuideHeld:channel=="Guide"),0);
                if(channel=="Pad")pads[0]=new(false,0,ReadFailed:true);w.AdvanceHandoff(new(pads,10,GuideValid:false),10);Require(w.IsVisible&&w.TransitionPending);
                Key(w,System.Windows.Input.Key.Escape,true);Key(w,System.Windows.Input.Key.Escape,false);w.AdvanceHandoff(new(pads,1000,GuideValid:false),1000);w.AdvanceHandoff(new(pads,2000,GuideValid:false),2000);Require(w.IsVisible&&p.Switches==0,"Original press/repeat or timeout authorized unknown exit");
                Key(w,System.Windows.Input.Key.Escape,true);w.AdvanceHandoff(new(pads,3000,GuideValid:false),3000);Require(w.IsVisible&&p.Switches==0,"Fresh EscapeDown hid before its release");Key(w,System.Windows.Input.Key.Escape,false);
                pads[1]=new(true,0,RX:25000,RY:-25000,RT:255);w.AdvanceHandoff(new(pads,3001,GuideValid:false),3001);w.AdvanceHandoff(new(pads,4000,GuideValid:false),4000);Require(w.IsVisible&&p.Switches==0,"Other currently valid stick/trigger DOWN ignored");
                pads[1]=new(true,0);w.AdvanceHandoff(new(pads,4001,GuideValid:false),4001);w.AdvanceHandoff(new(pads,4100,GuideValid:false),4100);Require(w.IsVisible&&p.Switches==0);w.AdvanceHandoff(new(pads,4101,GuideValid:false),4101);Require(!w.IsVisible&&!w.TransitionPending&&p.Switches==0&&p.Globals==0&&p.Shortcuts==0,"Explicit unknown escape dispatched outside action");Commit(w);Require(!w.IsVisible&&p.Switches==0);
            }finally{w.Close();}
        });
        test("Confirmed pad disconnect releases historical DOWN but never restores",()=>{using var p=new HandoffPlatform();var w=Host(p);try{w.Toggle();var pads=new PadFrame[]{new(true,PadButtons.B),default,default,default};w.AdvanceHandoff(new(pads,0),0);w.AdvanceHandoff(Neutral(10),10);w.AdvanceHandoff(Neutral(109),109);Require(w.IsVisible&&p.Switches==0);w.AdvanceHandoff(Neutral(110),110);Require(!w.IsVisible&&p.Switches==0);}finally{w.Close();}});
        test("Cleanup invalidates degraded known-held dismissal without delayed effects",()=>{using var p=new HandoffPlatform();var w=Host(p);Key(w,System.Windows.Input.Key.Escape,true);w.AdvanceHandoff(Neutral(0) with{GuideValid=false},0);Require(w.IsVisible&&w.TransitionPending);w.Close();w.AdvanceHandoff(Neutral(1000),1000);w.AdvanceHandoff(Neutral(1100),1100);Require(!w.IsVisible&&!w.TransitionPending&&p.Disposed&&p.Switches==0);});
    }
    internal static void DismissCancellation(Action<string,Action> test)
    {
        var app=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(app);
        foreach(var gesture in new[]{"Guide","B","Escape"})foreach(var fault in new[]{"GuideUnknown","PadReadFailed","TargetChanged"})test("Dismiss "+gesture+" hides without outgoing action after "+fault,()=>{
            using var p=new HandoffPlatform();var w=Host(p);try{
                w.SetRenderState("confirmation");Require(w.Confirming);if(gesture=="B"){w.Back();Require(!w.Confirming);w.Back();}else if(gesture=="Escape"){Key(w,System.Windows.Input.Key.Escape,true);Require(!w.Confirming);Key(w,System.Windows.Input.Key.Escape,false);Key(w,System.Windows.Input.Key.Escape,true);Key(w,System.Windows.Input.Key.Escape,false);}else w.Toggle();
                Require(w.TransitionPending&&w.IsVisible&&p.Switches==0,"Fixture must queue dismiss before fault");
                if(fault=="TargetChanged"){p.ValidTarget=false;Commit(w);}else{
                    var sample=Neutral(0);if(fault=="GuideUnknown")sample=sample with{GuideValid=false};else{var pads=new PadFrame[4];pads[0]=new(false,0,ReadFailed:true);sample=sample with{Pads=pads};}w.AdvanceHandoff(sample,0);w.AdvanceHandoff(sample with{Timestamp=99},99);Require(w.IsVisible&&p.Switches==0,"Unknown exit bypassed100ms known-release wait");w.AdvanceHandoff(sample with{Timestamp=100},100);
                }
                Require(!w.IsVisible,"Dismiss remained visible after "+fault);Require(!w.TransitionPending&&!w.Confirming,"Dismiss retained pending transition or close hold");Require(p.Switches==0&&p.Globals==0&&p.Menus==0&&p.Shortcuts==0,"Uncertain dismiss dispatched an outgoing action");Commit(w);Require(!w.IsVisible&&p.Switches==0,"Dismiss remounted or dispatched later");
            }finally{w.Close();}
        });
        foreach(var gesture in new[]{"Guide","B","Escape"})test("Repeated "+gesture+" reaffirms dismiss and still waits release100ms",()=>{
            using var p=new HandoffPlatform();var w=Host(p);try{
                w.Toggle();if(gesture=="Guide")w.Toggle();else if(gesture=="B")w.Back();else{Key(w,System.Windows.Input.Key.Escape,true);Key(w,System.Windows.Input.Key.Escape,false);}
                Require(w.TransitionPending);w.AdvanceHandoff(Neutral(0) with{GuideHeld=true},0);w.AdvanceHandoff(Neutral(1000) with{GuideHeld=true},1000);Require(w.IsVisible&&p.Switches==0);w.AdvanceHandoff(Neutral(1001),1001);w.AdvanceHandoff(Neutral(1100),1100);Require(w.IsVisible&&p.Switches==0);w.AdvanceHandoff(Neutral(1101),1101);Require(!w.IsVisible&&p.Switches==1);Commit(w);Require(p.Switches==1);
            }finally{w.Close();}
        });
        foreach(var fault in new[]{"Disconnect","Stale","CapturedTargetChanged"})test("Dismiss safely completes locally on "+fault,()=>{
            using var p=new HandoffPlatform();var w=Host(p);try{
                w.Toggle();var pads=new PadFrame[]{new(true,0),default,default,default};w.AdvanceHandoff(new(pads,0,GuideConnections:1),0);Require(w.IsVisible);
                if(fault=="Disconnect"){w.AdvanceHandoff(Neutral(1),1);w.AdvanceHandoff(Neutral(101),101);}else if(fault=="Stale"){w.AdvanceHandoff(Neutral(0),100);w.AdvanceHandoff(Neutral(200),200);}else{p.Context=p.Game with{Started=200};w.Controller.CaptureReturn();w.AdvanceHandoff(new(pads,100,GuideConnections:1),100);w.AdvanceHandoff(new(pads,200,GuideConnections:1),200);}
                Require(!w.IsVisible&&!w.TransitionPending&&p.Switches==0,"Uncertain dismiss did not finish locally");Commit(w);Require(!w.IsVisible&&p.Switches==0);
            }finally{w.Close();}
        });
        foreach(var gesture in new[]{"Guide","B","Escape"})test(gesture+" cancels pending external action before local dismiss",()=>{
            using var p=new HandoffPlatform();var w=Host(p);try{
                Button(w,"taskview").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Require(w.TransitionPending);if(gesture=="Guide")w.Toggle();else if(gesture=="B")w.Back();else{Key(w,System.Windows.Input.Key.Escape,true);Key(w,System.Windows.Input.Key.Escape,false);}Require(w.IsVisible&&w.TransitionPending&&p.Globals==0);w.AdvanceHandoff(Neutral(0) with{GuideValid=false},0);Require(w.IsVisible&&p.Globals==0);w.AdvanceHandoff(Neutral(100) with{GuideValid=false},100);Require(!w.IsVisible&&!w.TransitionPending&&p.Switches==0&&p.Globals==0);Commit(w);Require(p.Globals==0);
            }finally{w.Close();}
        });
        test("Back cancels settings exit action without forcing subpage hidden",()=>{using var p=new HandoffPlatform();var w=Host(p);try{w.Page(2);Button(w,"exit").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Require(w.TransitionPending);w.Back();Commit(w);Require(w.IsVisible&&!w.TransitionPending&&!p.Disposed&&p.Switches==0);}finally{w.Close();}});
    }
    internal static void SteamLaunchRegression(Action<string,Action> test)
    {
        test("Steam denied modules with confirmed target launch enables guarded shortcut",()=>{
            using var p=new HandoffPlatform{LaunchContext=true,Denied=true};var w=Host(p);try{
                FinishFocus(w.Controller.Refresh());w.RefreshRows();var tile=Button(w,"steam-overlay");
                Require(tile.IsEnabled,"Confirmed launch context ignored when module access denied");
                var labels=(System.Windows.Controls.StackPanel)((System.Windows.Controls.Grid)tile.Content).Children[1];Require(((System.Windows.Controls.TextBlock)labels.Children[1]).Text.Contains("Iniciado pela Steam"),"Launch label claims module injection");
                var reads=p.LaunchQueries;for(var i=0;i<4;i++)w.RefreshRows();Require(p.LaunchQueries==reads,"Row rebuild scanned process ancestry");tile=Button(w,"steam-overlay");
                var scans=p.LaunchQueries;tile.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Require(p.Shortcuts==0&&w.TransitionPending,"Shortcut bypassed handoff");
                w.AdvanceHandoff(Neutral(0) with{KeysNeutral=false},0);w.AdvanceHandoff(Neutral(100),100);Require(p.Shortcuts==0,"Held input bypassed release");w.AdvanceHandoff(Neutral(200),200);w.AdvanceHandoff(Neutral(300),300);
                Require(p.Shortcuts==1&&p.LastTarget?.Same(p.Game)==true&&p.LastKeys==w.Controller.Settings.SteamShortcut,"Configured target shortcut not requested exactly once");Require(p.LaunchQueries==scans,"Handoff rescanned process ancestry");
            }finally{w.Close();}
        });
        foreach(var mode in new[]{"cancel","target-change","identity-change"}){
            var scenario=mode;test("Steam launch fallback handoff refuses "+scenario,()=>{
                using var p=new HandoffPlatform{LaunchContext=true};var w=Host(p);try{
                    FinishFocus(w.Controller.Refresh());w.RefreshRows();Button(w,"steam-overlay").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Require(w.TransitionPending,"Fallback did not queue");
                    if(scenario=="cancel")w.Back();else if(scenario=="identity-change")p.ValidTarget=false;else{p.Context=p.BigPicture;w.Controller.CaptureReturn();}
                    Commit(w);Require(p.Shortcuts==0&&!w.TransitionPending,"Stale/cancelled fallback sent input");
                }finally{w.Close();}
            });
        }
        test("Steam launch integration action preserves module diagnostic and requests once",()=>{
            using var p=new HandoffPlatform{LaunchContext=true,Denied=true};var w=Host(p);try{
                FinishFocus(w.Controller.Refresh());w.SetRenderState("integrations");var button=Button(w,"test-steam");var texts=(System.Windows.Controls.StackPanel)((System.Windows.Controls.Grid)button.Content).Children[1];var description=((System.Windows.Controls.TextBlock)texts.Children[1]).Text;
                Require(button.IsEnabled&&description.Contains("Iniciado pela Steam")&&!description.Contains("Módulo carregado"),"Integration fallback misstates module evidence");button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Require(p.Shortcuts==0,"Integration bypassed handoff");Commit(w);Commit(w);Require(p.Shortcuts==1&&p.LastTarget?.Same(p.Game)==true,"Integration fallback duplicated or retargeted request");
            }finally{w.Close();}
        });
    }
    private static void Commit(MainWindow w){w.AdvanceHandoff(Neutral(1000),1000);w.AdvanceHandoff(Neutral(1100),1100);}
    private sealed class HandoffPlatform:IPlatform
    {
        internal readonly WindowRef Game=new(301,44,100,"Owned game fixture","game"),BigPicture=new(302,45,100,"Steam Big Picture Mode","steamwebhelper",true);internal WindowRef? Context;internal bool Menu,Loaded,LaunchContext,Denied,ValidTarget=true,Disposed;internal int Switches,Globals,Menus,Shortcuts,LaunchQueries;internal string? LastKeys;internal WindowRef? LastTarget;
        public bool HasSteamLaunchContext(WindowRef? target){LaunchQueries++;return LaunchContext&&target?.Same(Game)==true;}
        internal HandoffPlatform(){Context=Game;}public bool IsPreview=>true;public WindowRef? Foreground()=>Context;public IReadOnlyList<WindowRef> Windows()=>[Game];public bool Valid(WindowRef w)=>!Disposed&&ValidTarget&&w.Same(Game);public IReadOnlyList<DisplayRef> Displays()=>[new("owned-fixture",0,0,1280,720,true)];public ModuleEvidence SteamEvidence(WindowRef? w)=>Loaded&&w?.Same(Game)==true?ModuleEvidence.Loaded:Denied?ModuleEvidence.Denied:ModuleEvidence.Absent;
        public bool CanSteamMenu(WindowRef? w)=>!Disposed&&Menu&&w is not null&&w.Same(Context)&&w.Same(BigPicture);public Task<ActionResult> SteamMenu(WindowRef w){Menus++;LastTarget=w;LastKeys="F1";return Task.FromResult(new ActionResult(ResultKind.Requested,"fixture F1"));}public Task<ActionResult> TaskView(){Globals++;LastKeys="Win+Tab";return Task.FromResult(new ActionResult(ResultKind.Requested,"fixture WinTab"));}
        public Task<ActionResult> Switch(WindowRef w){Switches++;LastTarget=w;return Task.FromResult(new ActionResult(ResultKind.Completed,"fixture focus"));}public Task<ActionResult> Shortcut(WindowRef w,string k){Shortcuts++;LastTarget=w;LastKeys=k;return Task.FromResult(new ActionResult(ResultKind.Requested,"fixture keys"));}public ActionResult Launch(bool p,string? s)=>new(ResultKind.Requested,"fixture launch");public Task<ActionResult> Close(WindowRef w,CancellationToken c)=>throw new Exception("Unexpected close");public void Dispose()=>Disposed=true;
    }
    internal static void LegacyStatusView()
    {
        var app=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(app);
        var backend=Legacy();using var connection=new GuideConnection(backend);Require(connection.Start(()=>{}));
        var window=new MainWindow(renderOnly:true);try{
            window.SetRenderState("integrations");window.ShowDiagnostic();
            var rows=(System.Windows.Controls.Grid)window.FindName("Rows");
            var button=rows.Children.OfType<System.Windows.Controls.Button>().Single(b=>b.Content is System.Windows.Controls.Grid grid&&grid.Children[1] is System.Windows.Controls.StackPanel labels&&((System.Windows.Controls.TextBlock)labels.Children[0]).Text.StartsWith("Botão Xbox / Guide"));
            var texts=(System.Windows.Controls.StackPanel)((System.Windows.Controls.Grid)button.Content).Children[1];var subtitle=(System.Windows.Controls.TextBlock)texts.Children[1];var selected=window.SelectedAction;
            window.PollGuideStatus(backend);Require(subtitle.Text==backend.Status&&subtitle.Text.StartsWith("Nenhum controle"),"G3-01 disconnected status did not reach actual Guide subtitle");
            statuses[0]=0;window.PollGuideStatus(backend);Require(subtitle.Text==backend.Status&&subtitle.Text.Contains("ativo"),"G3-01 connected status stale");
            Require(rows.Children.Contains(button)&&window.SelectedAction==selected,"Status update rebuilt rows or changed selection");var unchanged=subtitle.Text;window.PollGuideStatus(backend);Require(subtitle.Text==unchanged&&rows.Children.Contains(button));
            window.Hide();statuses[0]=1167;window.PollGuideStatus(backend);Require(subtitle.Text==unchanged,"Hidden status update touched UI");window.ShowDiagnostic();window.PollGuideStatus(backend);Require(subtitle.Text==backend.Status&&subtitle.Text.StartsWith("Nenhum controle"),"G3-01 disconnected transition stale");
            window.Page(0);statuses[0]=0;window.PollGuideStatus(backend);Require(subtitle.Text.StartsWith("Nenhum controle"),"Non-integration page updated detached subtitle");
        }finally{window.Close();}
    }
    private static readonly uint[] statuses=new uint[4];private static readonly ushort[] guideButtons=new ushort[4];private static int polls,loads,frees;
    private static XInputGuideBackend Legacy(bool module=true,bool export=true)
    {
        Array.Fill(statuses,1167u);Array.Clear(guideButtons);polls=loads=frees=0;
        return new(()=>{loads++;return(module?(nint)99:0,module&&export?(nint)(delegate* unmanaged[Stdcall]<uint,XInputGuideBackend.State*,uint>)&ReadGuide:0);},handle=>{Require(handle==99);frees++;});
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static uint ReadGuide(uint slot,XInputGuideBackend.State* state){polls++;state->Packet=123;state->Buttons=guideButtons[slot];return statuses[slot];}
    internal static void LegacyAbi()
    {
        Require(sizeof(XInputGuideBackend.State)==16);XInputGuideBackend.State state=default;Require((byte*)&state.Buttons-(byte*)&state==4);
        var b=Legacy();var edges=0;using(var c=new GuideConnection(b)){Require(c.Start(()=>edges++));Require(loads==1&&polls==0);statuses[0]=0;guideButtons[0]=0x0400;b.Poll();Require(edges==0&&polls==4);guideButtons[0]=0;b.Poll();guideButtons[0]=0x0400;b.Poll();Require(edges==1&&c.Observed);b.Poll();Require(edges==1);}Require(frees==1);
    }
    internal static void LegacyCycles()
    {
        var b=Legacy();var edges=0;using(var c=new GuideConnection(b)){Require(c.Start(()=>edges++));statuses[0]=0;b.Poll();guideButtons[0]=0x0400;b.Poll();b.Poll();Require(edges==1);guideButtons[0]=0;b.Poll();guideButtons[0]=0x0400;b.Poll();Require(edges==2);statuses[0]=5;b.Poll();statuses[0]=0;b.Poll();Require(edges==2,"reconnect held");guideButtons[0]=0;b.Poll();guideButtons[0]=0x0400;b.Poll();Require(edges==3);}Require(frees==1);
    }
    internal static void LegacySlots()
    {
        var b=Legacy();var edges=0;using(var c=new GuideConnection(b)){Require(c.Start(()=>edges++));Array.Fill(statuses,0u);b.Poll();Array.Fill(guideButtons,(ushort)0x0400);b.Poll();Require(edges==1,"four edges coalesce");b.Poll();Require(edges==1);guideButtons[2]=0;b.Poll();guideButtons[2]=0x0400;b.Poll();Require(edges==2,"independent slot rearm");}Require(frees==1);
    }
    internal static void LegacyFailures()
    {
        foreach(var module in new[]{false,true}){var b=Legacy(module,false);using(var c=new GuideConnection(b)){Require(!c.Start(()=>throw new Exception("failed export invoked")));b.Poll();Require(polls==0&&!b.Active);}b.Release();Require(frees==(module?1:0));}
        var good=Legacy();var count=0;var connection=new GuideConnection(good);Require(connection.Start(()=>count++));statuses[0]=0;good.Poll();connection.Dispose();connection.Dispose();guideButtons[0]=0x0400;var before=polls;good.Poll();Require(polls==before&&count==0&&frees==1&&!good.Active);
    }
    internal static void LegacyTick()
    {
        var b=Legacy();var request=false;using(var c=new GuideConnection(b)){Require(c.Start(()=>request=true));statuses[0]=0;b.Poll();guideButtons[0]=0x0400;b.Poll();Require(GuidePolling.Consume(ref request,true)&&!request);b.Poll();Require(!GuidePolling.Consume(ref request,false),"hold or simultaneous chord retriggered");Require(GuidePolling.Interval(b.Active,false,false)==20&&GuidePolling.Interval(b.Active,true,true)==20);}Require(GuidePolling.Interval(false,true,false)==30&&GuidePolling.Interval(false,false,true)==120&&GuidePolling.Interval(false,false,false)==500);
        Require(!GuidePolling.Allowed(true,true,false)&&!GuidePolling.Allowed(true,false,true)&&!GuidePolling.Allowed(false,false,false)&&GuidePolling.Allowed(true,false,false));
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static uint Release(nint self) {Calls.Add("release");return 0;}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static void Policy(nint self,uint value) {policy=value;Calls.Add("policy");}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static int Register(nint self,nint device,uint filter,nint ctx,delegate* unmanaged[Stdcall]<ulong,nint,nint,ulong,uint,uint,void> cb,ulong* token)
    {
        Calls.Add("register");if(device!=0 || filter!=1)return unchecked((int)0x80070057);
        context=ctx;callback=cb;*token=registration ? 123UL : 0UL;return registration ? 0 : unchecked((int)0x80004005);
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static void Stop(nint self,ulong token) {Calls.Add(token==123 ? "stop" : "bad-token");}
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])] private static byte Unregister(nint self,ulong token) {Calls.Add(token==123 ? "unregister" : "bad-token");return removal ? (byte)1 : (byte)0;}
}


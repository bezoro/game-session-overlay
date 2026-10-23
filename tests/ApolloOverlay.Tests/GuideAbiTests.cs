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
        while(!task.IsCompleted){var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);System.Windows.Threading.Dispatcher.PushFrame(frame);}task.GetAwaiter().GetResult();
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
    private static System.Windows.Controls.Button Button(MainWindow w,string id)=>((System.Windows.Controls.Grid)w.FindName("Rows")).Children.OfType<System.Windows.Controls.Button>().Single(b=>(string?)b.Tag==id);
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
        test("actual WPF System AltY survives AltUp until YUp and100ms",SystemKeyBaseline);
        test("processed marker normalization uses real-key data and rejects unresolved markers",()=>{
            var k=System.Windows.Input.Key.Y;var none=System.Windows.Input.Key.None;Require(MainWindow.NormalizeKey(System.Windows.Input.Key.System,k,none,none)==k);Require(MainWindow.NormalizeKey(System.Windows.Input.Key.ImeProcessed,none,k,none)==k);Require(MainWindow.NormalizeKey(System.Windows.Input.Key.DeadCharProcessed,none,none,k)==k);Require(MainWindow.NormalizeKey(k,System.Windows.Input.Key.LeftAlt,System.Windows.Input.Key.Enter,System.Windows.Input.Key.Space)==k);
            foreach(var marker in new[]{System.Windows.Input.Key.System,System.Windows.Input.Key.ImeProcessed,System.Windows.Input.Key.DeadCharProcessed}){Require(MainWindow.NormalizeKey(marker,none,none,none)==none);Require(MainWindow.NormalizeKey(marker,marker,marker,marker)==none);}
        });
        test("release requires fresh continuous100ms and resets on any new input",()=>{var g=new ReleaseGate();Require(g.Update(Neutral(0),0,true,18000)==ReleaseState.Waiting);Require(g.Update(Neutral(99),99,true,18000)==ReleaseState.Waiting);var pads=new PadFrame[4];pads[3]=new(true,(PadButtons)0x8000);Require(g.Update(new(pads,100),100,true,18000)==ReleaseState.Waiting);pads[3]=new(true,0);Require(g.Update(new(pads,101),101,true,18000)==ReleaseState.Waiting);Require(g.Update(new(pads,200),200,true,18000)==ReleaseState.Waiting);Require(g.Update(new(pads,201),201,true,18000)==ReleaseState.Ready);});
        test("release covers all digital bits both sticks and trigger threshold",()=>{foreach(var frame in new[]{new PadFrame(true,(PadButtons)0x8000),new(true,(PadButtons)0x80),new(true,(PadButtons)0x40),new(true,0,12000),new(true,0,Y:-12000),new(true,0,RX:12000),new(true,0,RY:-12000),new(true,0,LT:30),new(true,0,RT:30)}){var g=new ReleaseGate();var pads=new PadFrame[4];pads[2]=frame;g.Update(new(pads,0),0,true,18000);Require(g.Update(new(pads,1000),1000,true,18000)==ReleaseState.Waiting);}var ok=new ReleaseGate();var allowed=new PadFrame[]{new(true,0,11999,-11999,11999,-11999,29,29),default,default,default};ok.Update(new(allowed,0),0,true,18000);Require(ok.Update(new(allowed,100),100,true,18000)==ReleaseState.Ready);});
        test("release keyboard Guide validity and held reset stability",()=>{foreach(var guide in new[]{false,true}){var g=new ReleaseGate();g.Update(Neutral(0),0,true,18000);Require(g.Update(Neutral(90) with{KeysNeutral=guide,GuideHeld=guide},90,true,18000)==ReleaseState.Waiting);g.Update(Neutral(91),91,true,18000);Require(g.Update(Neutral(190),190,true,18000)==ReleaseState.Waiting);Require(g.Update(Neutral(191),191,true,18000)==ReleaseState.Ready);}var invalid=new ReleaseGate();Require(invalid.Update(Neutral(0) with{GuideValid=false},0,true,18000)==ReleaseState.Cancelled);});
        test("release topology error stale snapshot and focus loss cancel safely",()=>{var g=new ReleaseGate();var pads=new PadFrame[]{new(true,0),default,default,default};g.Prime(new(pads,0,GuideConnections:1));Require(g.Update(Neutral(1),1,true,18000)==ReleaseState.Cancelled);g.Reset();g.Update(Neutral(0),0,true,18000);pads[1]=new(true,0);Require(g.Update(new(pads,100),100,true,18000)==ReleaseState.Waiting);Require(g.Update(new(pads,199),199,true,18000)==ReleaseState.Waiting);Require(g.Update(new(pads,200),200,true,18000)==ReleaseState.Ready);Require(new ReleaseGate().Update(Neutral(0),81,true,18000)==ReleaseState.Cancelled);Require(new ReleaseGate().Update(Neutral(0),0,false,18000)==ReleaseState.Cancelled);pads[0]=new(false,0,ReadFailed:true);Require(new ReleaseGate().Update(new(pads,1),1,true,18000)==ReleaseState.Cancelled);});
        foreach(var key in new[]{System.Windows.Input.Key.Escape,System.Windows.Input.Key.Enter})test("WPF "+key+" waits actual routed KeyUp then100ms once",()=>{using var p=new HandoffPlatform();var w=Host(p);try{Key(w,key,true);w.AdvanceHandoff(Neutral(0),0);w.AdvanceHandoff(Neutral(100),100);Require(w.IsVisible&&p.Switches==0&&w.TransitionPending);Key(w,key,false);w.AdvanceHandoff(Neutral(200),200);w.AdvanceHandoff(Neutral(299),299);Require(w.IsVisible&&p.Switches==0);w.AdvanceHandoff(Neutral(300),300);Require(!w.IsVisible&&p.Switches==1&&!w.TransitionPending);w.AdvanceHandoff(Neutral(400),400);Require(p.Switches==1);}finally{w.Close();}});
        test("WPF TaskView has no target switch and sends only after release",()=>{using var p=new HandoffPlatform{Context=null};var w=Host(p);try{Button(w,"taskview").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Require(w.TransitionPending&&p.Globals==0&&p.Switches==0);w.AdvanceHandoff(Neutral(0) with{KeysNeutral=false},0);w.AdvanceHandoff(Neutral(100),100);Require(p.Globals==0&&w.IsVisible);w.AdvanceHandoff(Neutral(200),200);Require(p.Globals==1&&p.LastKeys=="Win+Tab"&&p.Switches==0&&!w.IsVisible);}finally{w.Close();}});
        test("WPF return uses Anchor while Steam tile uses captured BigPicture context",()=>{using var p=new HandoffPlatform{Menu=true};p.Context=p.BigPicture;var w=Host(p);try{Button(w,"return").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Commit(w);Require(p.LastTarget?.Same(p.Game)==true);w.ShowDiagnostic();w.Controller.CaptureReturn();w.RefreshRows();var tile=Button(w,"steam-overlay");var labels=(System.Windows.Controls.StackPanel)((System.Windows.Controls.Grid)tile.Content).Children[1];Require(((System.Windows.Controls.TextBlock)labels.Children[0]).Text=="Menu do Steam Big Picture");tile.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Require(p.Menus==0);Commit(w);Require(p.Menus==1&&p.LastTarget?.Same(p.BigPicture)==true&&p.LastKeys=="F1");}finally{w.Close();}});
        test("WPF Steam game tile uses current context and mode change cancels before effect",()=>{using var p=new HandoffPlatform{Loaded=true};var w=Host(p);try{Button(w,"steam-overlay").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Commit(w);Require(p.Shortcuts==1&&p.LastTarget?.Same(p.Game)==true);w.ShowDiagnostic();p.Context=p.BigPicture;p.Menu=true;w.Controller.CaptureReturn();w.RefreshRows();Button(w,"steam-overlay").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));p.Menu=false;Commit(w);Require(w.IsVisible&&p.Menus==0&&!w.TransitionPending);}finally{w.Close();}});
        test("WPF pending action cancellation cleanup and invalid identity never escape",()=>{using var p=new HandoffPlatform();var w=Host(p);w.Toggle();w.Back();Commit(w);Require(w.IsVisible&&p.Switches==0&&!w.TransitionPending);Button(w,"return").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));p.ValidTarget=false;Commit(w);Require(w.IsVisible&&p.Switches==0&&!w.TransitionPending);p.ValidTarget=true;w.Toggle();w.Close();Commit(w);Require(p.Switches==0&&p.Disposed);});
        test("WPF duplicate Guide chord or hotkey request commits only once",()=>{using var p=new HandoffPlatform();var w=Host(p);try{w.Toggle();w.Toggle();Native.SendMessageTimeout(new System.Windows.Interop.WindowInteropHelper(w).Handle,0x312,1,0,2,500,out _);w.AdvanceHandoff(Neutral(0) with{GuideHeld=true},0);Require(w.IsVisible&&p.Switches==0);Commit(w);Require(!w.IsVisible&&p.Switches==1);}finally{w.Close();}});
        test("WPF extra observed key and modifier prevent handoff until KeyUp",()=>{using var p=new HandoffPlatform();var w=Host(p);try{Key(w,System.Windows.Input.Key.Y,true);Key(w,System.Windows.Input.Key.LeftCtrl,true);w.Toggle();Commit(w);Require(w.IsVisible&&p.Switches==0);Key(w,System.Windows.Input.Key.Y,false);Commit(w);Require(w.IsVisible&&p.Switches==0);Key(w,System.Windows.Input.Key.LeftCtrl,false);Commit(w);Require(!w.IsVisible&&p.Switches==1);}finally{w.Close();}});
        test("WPF external deactivation cancels pending without a delayed effect",()=>{using var p=new HandoffPlatform();var w=Host(p);var other=new System.Windows.Window{Title="Owned deactivation fixture",Width=200,Height=120};try{w.Toggle();other.Show();other.Activate();var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);System.Windows.Threading.Dispatcher.PushFrame(frame);Require(!w.TransitionPending&&p.Switches==0);Commit(w);Require(p.Switches==0);}finally{other.Close();w.Close();}});
        test("Guide release sample uses existing poll and exposes invalid/disconnected state",()=>{var b=Legacy();using var c=new GuideConnection(b);Require(c.Start(()=>{}));statuses[0]=0;guideButtons[0]=0x400;b.Poll();Require(b.Held&&b.SnapshotValid&&b.Connections==1&&polls==4);guideButtons[0]=0;b.Poll();Require(!b.Held&&b.SnapshotValid&&polls==8);statuses[0]=5;b.Poll();Require(!b.SnapshotValid&&b.Connections==0&&polls==12);});
        test("target shortcut focus continuation sends zero after WPF Cleanup",LateSendAfterCleanup);
        test("F1 and global keys are explicit allowlists; disposed native adapter dispatches zero",()=>{Require(Native.KeyInputs("F1").Length==2&&Native.KeyInputs("Win+Tab").Length==4&&Native.KeyInputs("Ctrl+1").Length==0&&Native.KeyInputs("unknown").Length==0);var count=0;using var p=new WindowsPlatform(true,events=>{count++;return (uint)events.Length;});p.Dispose();p.Dispose();Require(p.SendKeys("F1")==0);Require(p.TaskView().GetAwaiter().GetResult().Kind==ResultKind.Cancelled);Require(!p.CanSteamMenu(new(101,42,100,"Steam Big Picture Mode","steamwebhelper",true)));p.SteamMenu(new(101,42,100,"Steam Big Picture Mode","steamwebhelper",true)).GetAwaiter().GetResult();Require(count==0);Require(WindowsPlatform.BigPictureIdentity(new(1,1,1,"Steam Big Picture Mode","steamwebhelper",true)));Require(!WindowsPlatform.BigPictureIdentity(new(1,1,1,"Steam","steamwebhelper",true)));});
    }
    private static MainWindow Host(HandoffPlatform platform){var w=new MainWindow(renderOnly:true,platform:platform);w.Controller.Select(platform.Game);w.Controller.CaptureReturn();w.RefreshRows();w.ShowDiagnostic();return w;}
    private static void Commit(MainWindow w){w.AdvanceHandoff(Neutral(1000),1000);w.AdvanceHandoff(Neutral(1100),1100);}
    private sealed class HandoffPlatform:IPlatform
    {
        internal readonly WindowRef Game=new(301,44,100,"Owned game fixture","game"),BigPicture=new(302,45,100,"Steam Big Picture Mode","steamwebhelper",true);internal WindowRef? Context;internal bool Menu,Loaded,ValidTarget=true,Disposed;internal int Switches,Globals,Menus,Shortcuts;internal string? LastKeys;internal WindowRef? LastTarget;
        internal HandoffPlatform(){Context=Game;}public bool IsPreview=>true;public WindowRef? Foreground()=>Context;public IReadOnlyList<WindowRef> Windows()=>[Game];public bool Valid(WindowRef w)=>!Disposed&&ValidTarget&&w.Same(Game);public IReadOnlyList<DisplayRef> Displays()=>[new("owned-fixture",0,0,1280,720,true)];public ModuleEvidence SteamEvidence(WindowRef? w)=>Loaded&&w?.Same(Game)==true?ModuleEvidence.Loaded:ModuleEvidence.Absent;
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

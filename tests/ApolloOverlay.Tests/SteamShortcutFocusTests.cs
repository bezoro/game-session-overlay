using ApolloOverlay;
using System.Windows.Threading;

internal static class SteamShortcutFocusTests
{
    internal static readonly WindowRef Game=new(701,72,100,"Owned focus fixture","game");
    private static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
    internal static void Run(Action<string,Action> test)
    {
        test("Steam shortcut stays pending and sends zero until 500ms stable foreground",()=>{
            var effects=new Effects();var task=new GuardedActions(effects).Shortcut(Game,"Shift+Tab");
            Require(!task.IsCompleted&&effects.Sends==0,"Steam shortcut sent before first stable-focus wait");
            Complete(task,effects);Require(effects.Elapsed>=500&&effects.Sends==1&&task.Result.Kind==ResultKind.Requested,"Stable focus did not request exactly once");
        });
        foreach(var point in Enumerable.Range(1,10)){var cut=point;test("Steam cancellation at stable wait "+cut+" sends zero",()=>{using var cancel=new CancellationTokenSource();var e=new Effects();e.AfterWait=()=>{if(e.Elapsed>=cut*50)cancel.Cancel();};var task=new GuardedActions(e).Shortcut(Game,"Shift+Tab",cancel.Token,()=>true);Complete(task,e);Require(e.Sends==0&&e.FocusRequests==1&&task.Result.Kind==ResultKind.Cancelled,"Cancelled wait sent or refocused");});}
        foreach(var identity in new[]{false,true})test("Steam stable loss is terminal identity="+identity,()=>{var e=new Effects();e.AfterWait=()=>{if(e.Elapsed==100){if(identity)e.ValidIdentity=false;else e.Foreground=false;}};var task=new GuardedActions(e).Shortcut(Game,"Shift+Tab");Complete(task,e);Require(e.Sends==0&&e.FocusRequests==1&&e.Elapsed==100,"Lost focus retried or sent");});
        test("Steam continuation guard immediately before send blocks zero late keys",()=>{var e=new Effects();var after500=0;var task=new GuardedActions(e).Shortcut(Game,"Shift+Tab",CancellationToken.None,()=>e.Elapsed<500||++after500<2);Complete(task,e);Require(e.Sends==0,"Final continuation guard ignored");});
        test("non-Steam shortcut preserves immediate existing path",()=>{var e=new Effects();var result=new GuardedActions(e).Shortcut(Game,"Win+G").GetAwaiter().GetResult();Require(e.Elapsed==0&&e.Sends==1&&result.Kind==ResultKind.Requested,"Non-Steam path changed");});
        OwnerChecks(test);
    }
    private static void OwnerChecks(Action<string,Action> test)
    {
        _=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(System.Windows.Application.Current!);
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));try{
        foreach(var action in new[]{"focus","steam-overlay","test-steam","route:apps"})foreach(var intent in new[]{"left","right"}){var id=action;var direction=intent;test("CLI rejects unsupported intent before effects "+id+" "+direction,()=>{using var p=new Platform();var w=Window(p);try{var before=w.AutomationSnapshot();var task=w.AutomationExecute(new(){ActionId=id,Intent=direction,Revision=before.Revision},CancellationToken.None,_=>{},()=>true);Complete(task,p.Effect);Require(task.Result.Outcome=="Unsupported"&&p.Effect.FocusRequests==0&&p.Effect.Sends==0&&w.AutomationSnapshot().Route==before.Route,"Unsupported intent focused, sent, navigated, or was accepted");}finally{w.Close();}});}
        foreach(var action in new[]{"operation-details","dismiss-operation"}){var id=action;test("hidden CLI reports interactive operation control "+id,()=>{using var p=new Platform();var w=Window(p);try{w.Controller.SetOperation("Solicitado","Owned result fixture","steam-overlay",Game,false);w.RefreshRows();w.Hide();var before=w.AutomationSnapshot();var operation=w.Controller.Operation;var result=w.AutomationExecute(new(){ActionId=id,Revision=before.Revision},CancellationToken.None,_=>{},()=>true).GetAwaiter().GetResult();Require(result.Outcome=="RequiresInteractive"&&w.AutomationSnapshot().Route==before.Route&&ReferenceEquals(operation,w.Controller.Operation),"Hidden operation control falsely reported completion or mutated");}finally{w.Close();}});}
        test("Steam UI owner is cancellable from the same CLI host",()=>{using var p=new Platform();var w=Window(p);var name="BezoroUiOwner."+Guid.NewGuid().ToString("N");try{Complete(w.StartAutomation(name),new Effects());Click(w,"steam-overlay");Commit(w);var old=w.OperationTask;var id=w.AutomationSnapshot().PendingRequestId;Require(id.Length>0&&!old.IsCompleted,"UI owner not pending");var cancel=Task.Run(()=>AutomationTests.Exchange(name,new(){Verb="cancel",RequestId=id}));Complete(cancel,new Effects());Require(cancel.Result.Outcome=="Cancelled","CLI could not cancel UI owner");Complete(old,p.Effect);Require(p.Effect.Sends==0,"UI owner sent after CLI cancel");}finally{w.Close();}});
        test("CLI rejects mutation while UI handoff is pending",()=>{using var p=new Platform();var w=Window(p);try{Click(w,"steam-overlay");Require(w.TransitionPending,"UI handoff not pending");var result=w.AutomationExecute(new(){ActionId="route:apps",Revision=w.AutomationSnapshot().Revision},CancellationToken.None,_=>{},()=>true).GetAwaiter().GetResult();Require(result.Outcome=="Busy"&&w.TransitionPending,"CLI bypassed pending UI handoff");}finally{w.Close();}});
        test("old UI logging failure cannot overwrite a reopened owner",()=>{using var p=new Platform();var w=Window(p);var name="BezoroUiLogging."+Guid.NewGuid().ToString("N");try{Complete(w.StartAutomation(name),new Effects());Click(w,"steam-overlay");Commit(w);var old=w.OperationTask;w.OpenPanel();Complete(w.OpeningTask,new Effects());var expected=w.Controller.Operation;var root=w.Controller.Diagnostics.Root;System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root,"automation",System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString(),"automation-ledger.json.tmp"));Complete(old,p.Effect);Require(ReferenceEquals(expected,w.Controller.Operation),"Old log failure replaced current operation");}finally{w.Close();}});
        foreach(var interruption in new[]{"reopen","cleanup","stale-input","new-B"}){var mode=interruption;test("Steam shared owner hidden cancellation "+mode,()=>{
            using var p=new Platform();var w=Window(p);try{Click(w,"steam-overlay");Commit(w);var old=w.OperationTask;Require(!w.IsVisible&&!old.IsCompleted&&p.Effect.Sends==0&&w.Controller.Operation is{Dispatched:false},"Pending owner did not intentionally hide before stable focus");
                if(mode=="reopen"){w.OpenPanel();Complete(w.OpeningTask,new Effects());var handle=new System.Windows.Interop.WindowInteropHelper(w).Handle;Require(w.IsActive&&Native.GetForegroundWindow()==handle,"PRECONDITION: reopened native focus was not granted");Console.WriteLine("OWNER-REOPEN-GRANTED: visible="+w.IsVisible+"; active="+w.IsActive+"; foreground="+Native.GetForegroundWindow());}else if(mode=="cleanup")w.Close();else if(mode=="stale-input")p.Stale=true;else w.ReviewTick([new(true,PadButtons.B),default,default,default]);
                Complete(old,p.Effect);Require(p.Effect.Sends==0,"Cancelled hidden owner sent late keys");if(mode=="reopen")Require(w.IsVisible,"Old continuation hid a focus-granted reopened panel");
            }finally{w.Close();}
        });}
        test("Steam UI owner retains ReturnTarget while integrations retain Anchor",()=>{using var p=new Platform();var w=Window(p);try{p.Current=p.Other;w.Controller.CaptureReturn();w.Controller.Select(Game);w.RefreshRows();Click(w,"steam-overlay");Commit(w);Complete(w.OperationTask,p.Effect);Require(p.LastTarget?.Same(p.Other)==true&&p.Effect.Sends==1,"Main tile changed captured ReturnTarget");w.ShowDiagnostic();w.SetRenderState("integrations");Click(w,"test-steam");Commit(w);Complete(w.OperationTask,p.Effect);Require(p.LastTarget?.Same(Game)==true&&p.Effect.Sends==2,"Integration changed Anchor");}finally{w.Close();}});
        }finally{SynchronizationContext.SetSynchronizationContext(previous);}
    }
    private static MainWindow Window(Platform p)
    {
        var w=new MainWindow(renderOnly:true,platform:p,diagnostics:new(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"BezoroFocusTests",Guid.NewGuid().ToString("N"))));w.InputContextReader=()=>new(true,w.IsVisible,true,true);w.PhysicalKeyReader=_=>0;w.AutomationInputReader=()=>new(new PadFrame[4],Environment.TickCount64-(p.Stale?1000:0),GuideValid:true);w.Controller.CaptureReturn();Complete(w.Controller.Refresh(),p.Effect);w.RefreshRows();w.Left=-12000;w.Top=-12000;w.ShowDiagnostic();return w;
    }
    private static void Click(MainWindow w,string id)=>ReviewFixTests.Button(w,id).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static void Commit(MainWindow w){w.AdvanceHandoff(new(new PadFrame[4],0),0);w.AdvanceHandoff(new(new PadFrame[4],100),100);}
    private sealed class Platform:IPlatform
    {
        internal readonly Effects Effect=new();internal readonly WindowRef Other=new(702,73,200,"Other owned fixture","other");internal WindowRef Current=Game;internal WindowRef? LastTarget;internal bool Stale=false;
        public bool IsPreview=>true;public WindowRef? Foreground()=>Current;public IReadOnlyList<WindowRef> Windows()=>[Game,Other];public bool Valid(WindowRef w)=>w.Same(Game)||w.Same(Other);public IReadOnlyList<DisplayRef> Displays()=>[new("fixture",0,0,1280,720,true)];public ModuleEvidence SteamEvidence(WindowRef? w)=>ModuleEvidence.Loaded;
        public ShortcutDispatch? LastShortcutDispatch=>Effect.Sends==0?null:new(Effect.Sends,4,4,0,0);
        public Task<ActionResult> Shortcut(WindowRef w,string keys)=>Shortcut(w,keys,CancellationToken.None,()=>true);
        public Task<ActionResult> Shortcut(WindowRef w,string keys,CancellationToken token,Func<bool> continuing){LastTarget=w;return new GuardedActions(Effect).Shortcut(w,keys,token,continuing);}
        public Task<ActionResult> Focus(WindowRef w,CancellationToken token,Func<bool> continuing)=>new GuardedActions(Effect).FocusStable(w,token,continuing);
        public Task<ActionResult> Switch(WindowRef w)=>Task.FromResult(new ActionResult(ResultKind.Simulated,"Owned switch"));public Task<ActionResult> Close(WindowRef w,CancellationToken token)=>throw new Exception("No close expected");public ActionResult Launch(bool playnite,string? path)=>throw new Exception("No launch expected");public void Dispose(){}
    }
    internal static void Complete(Task task,Effects effects)
    {
        var end=DateTime.UtcNow.AddSeconds(3);while(!task.IsCompleted&&DateTime.UtcNow<end){effects.Release();var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
        Require(task.IsCompleted,"Fixture focus deadline");task.GetAwaiter().GetResult();
    }
    internal sealed class Effects:IWindowEffects
    {
        internal int Sends,FocusRequests,Elapsed;internal bool ValidIdentity=true,Foreground=true,FocusAccepted=true;internal Action? AfterWait=null;private TaskCompletionSource? wait;
        public bool Valid(WindowRef w)=>ValidIdentity;public WindowObservation ObserveWindow(WindowRef w)=>WindowObservation.SameIdentity;public bool IsForeground(WindowRef w)=>Foreground;public bool RequestFocus(WindowRef w){FocusRequests++;return FocusAccepted;}
        public uint SendKeys(string keys){Sends++;return 4;}public bool PostClose(WindowRef w)=>false;
        public Task Wait(int milliseconds){Elapsed+=milliseconds;AfterWait?.Invoke();wait=new(TaskCreationOptions.RunContinuationsAsynchronously);return wait.Task;}
        internal void Release(){var next=wait;wait=null;next?.TrySetResult();}
    }
}

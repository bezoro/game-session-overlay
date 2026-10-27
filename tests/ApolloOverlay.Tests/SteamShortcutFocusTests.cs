using ApolloOverlay;
using Automation=ApolloOverlay.Automation;
using System.Windows.Threading;

internal static class SteamShortcutFocusTests
{
    internal static readonly WindowRef Game=new(701,72,100,"Owned focus fixture","game");
    private static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
    internal static void EffectsHandshakeChecks(Action<string,Action> test)
    {
        test("Effects release cannot erase a concurrently published next wait",()=>{
            var e=new Effects();var first=e.Wait(10);e.Release();Require(first.IsCompleted,"Initial handshake failed");
            using var publish=new ManualResetEventSlim();using var ready=new ManualResetEventSlim();Task? second=null;
            var producer=Task.Run(()=>{Require(publish.Wait(1000),"Producer barrier deadline");second=e.Wait(10);ready.Set();});
            e.AfterReleaseTake=()=>{publish.Set();Require(ready.Wait(1000),"Publication barrier deadline");};e.Release();e.AfterReleaseTake=null;producer.GetAwaiter().GetResult();e.Release();
            Require(second?.IsCompleted==true,"Release erased the concurrently published wait; coroutine would hit fixture deadline");
        });
        test("Effects wait returns its own task if release wins publication race",()=>{
            var e=new Effects();e.AfterWaitPublish=e.Release;var task=e.Wait(10);Require(task.IsCompletedSuccessfully,"Published wait was not released");
        });
    }
    internal static void Run(Action<string,Action> test)
    {
        EffectsHandshakeChecks(test);
        SequenceChecks(test);
        NativeSequence(test);
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
        SequenceOwnerChecks(test);
        ClosingChecks(test);
    }
    internal static void SequenceChecks(Action<string,Action> test)
    {
        test("Steam sequence has four individual events after stable focus",()=>{var e=new Effects();var task=new GuardedActions(e).Shortcut(Game,"Shift+Tab");Complete(task,e);Require(e.NativeCalls.SequenceEqual(new[]{1,1,1,1}),"Expected separate event calls, got "+string.Join(",",e.NativeCalls));Require(e.Events.Select(x=>x.Key).SequenceEqual(new ushort[]{16,9,9,16})&&e.Events.Select(x=>x.Up).SequenceEqual(new[]{false,false,true,true}),"Wrong order");Require(e.Events.Select(x=>x.OffsetMs).SequenceEqual(new long[]{0,20,80,100})&&e.Elapsed==600,"Dwell/focus minima changed");Require(task.Result.Kind==ResultKind.Requested&&e.Dispatch?.Sequence is{Released:true,PossiblyHeld:0},"False Requested or missing release evidence");});
        foreach(var cut in new[]{0,10,20,30,70,80,90,100}){var point=cut;test("Steam cancellation at sequence offset "+point,()=>{
            using var cancel=new CancellationTokenSource();var e=new Effects();e.Recorded=sequence=>{if(point==0&&sequence.Events.Length==0)cancel.Cancel();};e.AfterWait=()=>{if(e.Elapsed>=500+point)cancel.Cancel();};
            var task=new GuardedActions(e).Shortcut(Game,"Shift+Tab",cancel.Token,()=>true);Complete(task,e);Require(e.Events.Count(x=>!x.Up)<=2&&e.NativeHeld==0&&e.Mask==0,"Late down or held key after cancellation");if(point<=20)Require(!e.Events.Any(x=>x.Key==9&&!x.Up),"Late Tab-down after cancel");if(point==0)Require(e.NativeCalls.Count==0,"Guard rejection emitted key-up without down");Require(task.Result.Kind is ResultKind.Cancelled or ResultKind.Partial,"Cancellation lost partial/zero distinction");
        });}
        foreach(var fault in new[]{"identity","focus","connection","deadline"}){var mode=fault;test("Steam terminal guard loss after Shift "+mode,()=>{
            var e=new Effects();var connected=true;e.AfterWait=()=>{if(e.Elapsed>=510){if(mode=="identity")e.ValidIdentity=false;else if(mode=="focus")e.Foreground=false;else if(mode=="connection")connected=false;else e.Elapsed=11000;}};
            var task=new GuardedActions(e).Shortcut(Game,"Shift+Tab",CancellationToken.None,()=>connected);Complete(task,e);Require(e.Events.Count(x=>!x.Up)==1&&e.NativeHeld==0&&task.Result.Kind==ResultKind.Partial,"Context loss sent late Tab or discarded partial effect");
        });}
        foreach(var index in Enumerable.Range(0,4)){var reject=index;test("Steam known zero event "+reject+" cleans only accepted downs",()=>{
            var e=new Effects{Response=(_,_,n)=>new(true,n==reject?0u:1u)};var task=new GuardedActions(e).Shortcut(Game,"Shift+Tab");Complete(task,e);
            Require(e.NativeHeld==0&&e.Events.Count(x=>!x.Up)<=2&&task.Result.Kind==(reject==0?ResultKind.Failed:ResultKind.Partial),"Zero acknowledgement retried down or wrong result");
            if(reject==0)Require(e.Events.All(x=>!x.Up),"Released unowned first key");if(reject==1)Require(e.Events.Where(x=>x.Up).All(x=>x.Key==16),"Released unsent Tab");
        });}
        test("Steam unknown acknowledgement retains cleanup ownership and Unknown",()=>{var e=new Effects{Response=(_,_,n)=>new(true,n==0?null:1)};var task=new GuardedActions(e).Shortcut(Game,"Shift+Tab");Complete(task,e);Require(e.Events.Count==2&&e.Events[1] is{Key:16,Up:true,Cleanup:true}&&e.NativeHeld==0&&task.Result.Kind==ResultKind.Unknown,"Unknown call not cleaned or falsely completed");});
        test("Steam known no native call grants zero cleanup ownership",()=>{var e=new Effects{Response=(_,_,_)=>new(false,0)};var task=new GuardedActions(e).Shortcut(Game,"Shift+Tab");Complete(task,e);Require(e.NativeCalls.Count==0&&!e.Events.Any(x=>x.Up)&&task.Result.Kind==ResultKind.Failed,"Unstarted down released physical key");});
        test("Steam receipt log failure stops new downs but preserves cleanup",()=>{var e=new Effects();var task=new GuardedActions(e,sequence=>{if(sequence.Events.Length>0)throw new System.IO.IOException("Owned journal failure");}).Shortcut(Game,"Shift+Tab");Complete(task,e);Require(e.Events.Count(x=>!x.Up)==1&&e.NativeHeld==0&&task.Result.Kind==ResultKind.Unknown,"Log failure escaped cleanup or allowed Tab");});
        test("Steam release observation failure is bounded Unknown",()=>{var e=new Effects{ObserveRelease=false};var task=new GuardedActions(e).Shortcut(Game,"Shift+Tab");Complete(task,e);Require(task.Result.Kind==ResultKind.Unknown&&e.Elapsed<=700&&e.Events.Count<=6&&e.Dispatch?.Sequence?.Released==false,"Unconfirmed release claimed success or exceeded bound");});
        test("Steam refused cleanup remains Unknown even if later observed released",()=>{using var cancel=new CancellationTokenSource();var e=new Effects();e.AfterWait=()=>{if(e.Elapsed>=510)cancel.Cancel();};e.Response=(_,up,_)=>{if(up){e.NativeHeld=0;return new(true,0);}return new(true,1);};var task=new GuardedActions(e).Shortcut(Game,"Shift+Tab",cancel.Token,()=>true);Complete(task,e);Require(task.Result.Kind==ResultKind.Unknown&&e.Dispatch?.CleanupAccepted==0,"Refused cleanup was disguised by later key state");});
        test("Ctrl Tab remains one batch after stable focus",()=>{var e=new Effects();var task=new GuardedActions(e).Shortcut(Game,"Ctrl+Tab");Complete(task,e);Require(e.NativeCalls.SequenceEqual(new[]{4})&&e.Elapsed==500&&task.Result.Kind==ResultKind.Requested,"Ctrl Tab adopted unproven sequence");});
        test("Windows native-call boundary reports exception and disposed refusal truthfully",()=>{var calls=0;using var p=new WindowsPlatform(true,_=>{calls++;throw new System.IO.IOException("Owned sender exception");});p.Dispose();Require(p.SendSteamEvent(16,false) is{Started:false,Accepted:0}&&calls==0,"Disposed pre-call guard dispatched");using var active=new WindowsPlatform(true,_=>{calls++;throw new System.IO.IOException("Owned native boundary failure");});Require(active.SendSteamEvent(16,false) is{Started:true,Accepted:null}&&active.SteamOwnedKeys==1&&calls==1,"Uncertain native call lost ownership");});
    }
    internal static void ClosingChecks(Action<string,Action> test)
    {
        _=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try{
            foreach(var downs in new[]{1,2})foreach(var tray in new[]{false,true})foreach(var fault in new[]{"none","zero","exception","unobserved"}){
                var point=downs;var fromTray=tray;var failure=fault;
                test("real Closing drains accepted keys downs="+point+" tray="+fromTray+" release="+failure,()=>CloseCase(point,fromTray,failure));
            }
        }finally{SynchronizationContext.SetSynchronizationContext(previous);}
    }
    internal static void SequenceOwnerChecks(Action<string,Action> test)
    {
        _=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try{
            test("Steam unknown cleanup ownership is not inherited by next request",()=>{
                using var p=new Platform();var w=Window(p);
                try{
                    w.PhysicalKeyReader=key=>(p.Effect.NativeHeld&1)!=0&&key is 16 or 0xA0 or 0xA1?unchecked((short)0x8000):(short)0;
                    Click(w,"steam-overlay");Commit(w);DriveUntil(()=>p.Effect.Events.Any(e=>!e.Up),p.Effect);
                    p.Effect.Response=(_,up,_)=>new(true,up?0u:1u);w.Back();Complete(w.OperationTask,p.Effect);
                    Require(p.LastResult?.Kind==ResultKind.Unknown&&p.Effect.Mask==1&&p.Effect.NativeHeld==1,"Fixture did not retain uncertain Shift after refused cleanup");
                    Require(w.Controller.Operation is{State:"Não confirmado",Dispatched:true},"Unknown cleanup left UI pending or denied accepted Shift");
                    var focus=p.Effect.FocusRequests;var sends=p.Effect.Sends;using var deadline=new CancellationTokenSource(200);
                    var next=w.AutomationExecute(new(){ActionId="steam-overlay",RequestId="second-owner",Revision=w.AutomationSnapshot().Revision},deadline.Token,_=>{},()=>true);Complete(next,p.Effect);
                    Require(p.Effect.FocusRequests==focus&&p.Effect.Sends==sends,"Next owner masked residual Shift and focused/sent before physical release");
                    p.Effect.NativeHeld=0;p.Effect.Response=null;var released=w.AutomationExecute(new(){ActionId="steam-overlay",RequestId="after-observed-release",Revision=w.AutomationSnapshot().Revision},CancellationToken.None,_=>{},()=>true);Complete(released,p.Effect);
                    Require(released.Result.Outcome=="Requested"&&p.Effect.FocusRequests==focus+1,"New request did not qualify after physical release was observed");
                }finally{p.Effect.NativeHeld=0;p.Effect.Mask=0;p.Effect.ObserveRelease=true;Complete(w.OperationTask,p.Effect);w.Close();Complete(w.ClosingTask,p.Effect);}
            });
            foreach(var cancellation in new[]{"B","Escape","context","CLI"}){var mode=cancellation;test("Steam cancelled UI record publishes terminal partial "+mode,()=>{
                using var p=new Platform();var w=Window(p);
                var endpoint="Bezoro.TerminalUi."+Guid.NewGuid().ToString("N");
                try{if(mode=="CLI")Complete(w.StartAutomation(endpoint),new Effects());Click(w,"steam-overlay");Commit(w);DriveUntil(()=>p.Effect.Events.Any(e=>!e.Up),p.Effect);
                    if(mode=="B")w.Back();else if(mode=="Escape"){w.PhysicalKeyReader=key=>key==0x1B?unchecked((short)0x8000):(short)0;w.ReviewTick(new PadFrame[4]);}else if(mode=="context")p.ValidTarget=false;else{var id=w.AutomationSnapshot().PendingRequestId;var cancel=Task.Run(()=>AutomationTests.Exchange(endpoint,new(){Verb="cancel",RequestId=id}));Complete(cancel,new Effects());Require(cancel.Result is{Outcome:"Cancelled",Terminal:false},"CLI claimed terminal cleanup before owner drained");}
                    Complete(w.OperationTask,p.Effect);Require(p.LastResult?.Kind==ResultKind.Partial,"Fixture did not produce partial result");Require(w.Controller.Operation is{State:"Parcial",Dispatched:true},"UI remained pending after terminal partial cleanup");
                }finally{Complete(w.OperationTask,p.Effect);w.Close();Complete(w.ClosingTask,p.Effect);}
            });}
            test("Steam terminal result does not replace a later UI record",()=>{
                using var p=new Platform();var w=Window(p);
                try{Click(w,"steam-overlay");Commit(w);DriveUntil(()=>p.Effect.Events.Any(e=>!e.Up),p.Effect);w.Back();w.Controller.SetOperation("Concluído","Later owned result","focus",Game,false);var later=w.Controller.Operation;Complete(w.OperationTask,p.Effect);Require(ReferenceEquals(later,w.Controller.Operation),"Old terminal result replaced later UI record");}
                finally{Complete(w.OperationTask,p.Effect);w.Close();Complete(w.ClosingTask,p.Effect);}
            });
            test("Steam focus runner registers native adapter regression without executing it",()=>{
                var registered=new List<string>();Run((name,_)=>registered.Add(name));Require(registered.Contains("Steam public Windows adapter uses four timed individual events"),"Native adapter regression is unreachable from steam-focus runner");
            });
            foreach(var foreign in new[]{0,0x57,9}){var key=foreign;test("Steam owned masking and post-release foreign input key="+key,()=>{
                using var p=new Platform();var w=Window(p);var physical=0;
                try{
                    w.PhysicalKeyReader=vk=>((p.Effect.NativeHeld&1)!=0&&vk is 16 or 0xA0 or 0xA1||(p.Effect.NativeHeld&2)!=0&&vk==9||vk==physical&&physical!=0)?unchecked((short)0x8000):(short)0;
                    p.Effect.AfterWait=()=>{if(key==0x57&&p.Effect.Elapsed>=510)physical=key;};
                    p.Effect.Recorded=e=>{if(key==9&&e.Events.LastOrDefault() is{Key:9,Up:true,Cleanup:false,Accepted:1})physical=9;};
                    Click(w,"steam-overlay");Commit(w);Complete(w.OperationTask,p.Effect);
                    Require(p.LastResult?.Kind==(key==0?ResultKind.Requested:ResultKind.Partial),"Self-input cancelled owner or foreign input was masked");
                    if(key==0x57)Require(p.Effect.Events.Count(e=>!e.Up)==1,"Foreign W allowed late Tab-down");
                    if(key==9)Require(p.Effect.Events.Where(e=>e.Cleanup).All(e=>e.Key==16)&&!w.PhysicalKeysNeutral(),"New physical Tab was masked or released by cleanup");
                }finally{Complete(w.OperationTask,p.Effect);w.Close();Complete(w.ClosingTask,p.Effect);}
            });}
            test("Steam cancelled owner holds exclusive slot through cleanup",()=>{
                using var p=new Platform();var w=Window(p);
                try{Click(w,"steam-overlay");Commit(w);DriveUntil(()=>p.Effect.Events.Any(e=>!e.Up),p.Effect);p.Effect.ObserveRelease=false;w.Back();DriveUntil(()=>p.Effect.Events.Any(e=>e.Cleanup),p.Effect);
                    var denied=w.AutomationExecute(new(){ActionId="steam-overlay",Revision=w.AutomationSnapshot().Revision},CancellationToken.None,_=>{},()=>true).GetAwaiter().GetResult();Require(denied.Outcome=="Busy"&&!w.OperationTask.IsCompleted,"Cancel freed owner before cleanup");
                    p.Effect.ObserveRelease=true;Complete(w.OperationTask,p.Effect);Require(!w.AutomationSnapshot().Busy&&p.Effect.NativeHeld==0,"Settled cleanup failed to release owner");
                }finally{p.Effect.ObserveRelease=true;Complete(w.OperationTask,p.Effect);w.Close();Complete(w.ClosingTask,p.Effect);}
            });
            test("Steam CLI uses same sequence runner and progress receipts",()=>{
                using var p=new Platform();var w=Window(p);var phases=new List<Automation.AutomationReply>();
                try{var task=w.AutomationExecute(new(){ActionId="steam-overlay",RequestId="owned-cli",Revision=w.AutomationSnapshot().Revision},CancellationToken.None,phases.Add,()=>true);Complete(task,p.Effect);var result=task.Result;
                    Require(result is{Outcome:"Requested",Dispatched:true,Accepted:4,Expected:4,Sequence:{Released:true,PossiblyHeld:0}}&&result.Sequence.Events.Length==4,"CLI lost sequence result or release");Require(phases.Any(p=>p.Sequence is not null&&p.Accepted==1&&!p.Terminal),"Per-event pending receipt not published");
                }finally{Complete(w.OperationTask,p.Effect);w.Close();Complete(w.ClosingTask,p.Effect);}
            });
        }finally{SynchronizationContext.SetSynchronizationContext(previous);}
    }
    private static void CloseCase(int downs,bool tray,string fault)
    {
        using var p=new Platform();var w=Window(p);var closed=0;var entries=0;w.Closing+=(_,_)=>{entries++;p.Effect.Timeline.Add("closing");};w.Closed+=(_,_)=>{closed++;p.Effect.Timeline.Add("closed");};
        try{
            Click(w,"steam-overlay");Commit(w);var operation=w.OperationTask;DriveUntil(()=>p.Effect.Events.Count(e=>!e.Up)==downs,p.Effect);
            Require(p.Effect.NativeHeld==(downs==1?1:3),"Fixture did not reach accepted native downs");var before=p.Effect.Elapsed;p.Effect.ObserveRelease=false;
            p.Effect.Response=(key,up,_)=>new(true,up&&key==(downs==1?16:9)?fault=="zero"?0u:fault=="exception"?null:1u:1u);
            if(tray)w.ExitFromTray();else w.Close();Require(entries>0,"Actual Closing event was not entered");Require(!p.Disposed&&closed==0,"Closing disposed transport before cleanup drained");
            w.Close();w.ExitFromTray();Require(!p.Disposed&&closed==0,"Repeated close created concurrent disposal");
            var denied=w.AutomationExecute(new(){ActionId="steam-overlay",Revision=w.AutomationSnapshot().Revision},CancellationToken.None,_=>{},()=>true).GetAwaiter().GetResult();Require(denied.Outcome=="Busy","New operation entered while cleanup owned slot");
            var sentinel=false;w.Dispatcher.BeginInvoke(()=>sentinel=true);Pump();Require(sentinel&&!operation.IsCompleted&&!p.Disposed,"Dispatcher blocked or transport disposed before controlled release");
            DriveUntil(()=>p.Effect.Events.Any(e=>e.Cleanup),p.Effect);if(fault!="unobserved")p.Effect.ObserveRelease=true;
            Complete(operation,p.Effect);Complete(w.ClosingTask,p.Effect);
            var expected=downs==1?new ushort[]{16}:new ushort[]{9,16};Require(p.Effect.Events.Where(e=>e.Cleanup).Select(e=>e.Key).SequenceEqual(expected),"Cleanup omitted, duplicated, or released unsent key");
            Require(p.Effect.Events.Count(e=>!e.Up)==downs,"Late down after close");Require(p.Disposed&&p.DisposeCalls==1&&closed==1,"Expected one final dispose and close");
            var dispose=p.Effect.Timeline.IndexOf("dispose");Require(dispose>p.Effect.Timeline.FindLastIndex(e=>e.StartsWith("up",StringComparison.Ordinal)),"Dispose preceded cleanup attempts");
            Require(p.Effect.Elapsed-before<=110,"Cleanup exceeded100ms bound plus pending10ms slice");Require(p.LastResult?.Kind==(fault=="none"?ResultKind.Partial:ResultKind.Unknown),"False release/result after close failure");
            w.Close();w.ExitFromTray();Require(p.DisposeCalls==1&&closed==1,"Settled close repeated effects");Console.WriteLine("CLOSE-TIMELINE "+string.Join("/",p.Effect.Timeline)+"; result="+p.LastResult?.Kind+"; released="+p.Effect.Dispatch?.Sequence?.Released);
        }finally{p.Effect.ObserveRelease=true;Complete(w.OperationTask,p.Effect);Complete(w.ClosingTask,p.Effect);w.Close();}
    }
    internal static void NativeSequence(Action<string,Action> test)
    {
        test("Steam public Windows adapter uses four timed individual events",()=>{
            _=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};
            var own=new System.Windows.Window{Title="Owned Steam transport fixture",Width=200,Height=120};var calls=new List<(long At,Native.Input[] Inputs)>();using var platform=new WindowsPlatform(true,events=>{calls.Add((Environment.TickCount64,events.ToArray()));return (uint)events.Length;});
            try{own.Show();own.Activate();var frame=new DispatcherFrame();own.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);var target=platform.Foreground();Require(target is not null&&target.Handle==new System.Windows.Interop.WindowInteropHelper(own).Handle,"Owned native foreground prerequisite");var task=platform.Shortcut(target!,"Shift+Tab");Complete(task,new Effects());Require(calls.Count==4&&calls.All(c=>c.Inputs.Length==1),"Expected four individual native calls, got "+string.Join(",",calls.Select(c=>c.Inputs.Length)));Require(calls.Select(c=>c.Inputs[0].Key).SequenceEqual(new ushort[]{16,9,9,16})&&calls.Select(c=>c.Inputs[0].Flags).SequenceEqual(new uint[]{0,0,2,2}),"Encoding/order changed");Require(calls[1].At-calls[0].At>=20&&calls[2].At-calls[1].At>=60&&calls[3].At-calls[2].At>=20,"Intervals shorter than 20/60/20");}finally{own.Close();}
        });
    }
    private static void OwnerChecks(Action<string,Action> test)
    {
        _=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(System.Windows.Application.Current!);
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));try{
        foreach(var action in new[]{"focus","steam-overlay","test-steam","route:apps"})foreach(var intent in new[]{"left","right"}){var id=action;var direction=intent;test("CLI rejects unsupported intent before effects "+id+" "+direction,()=>{using var p=new Platform();var w=Window(p);try{var before=w.AutomationSnapshot();var task=w.AutomationExecute(new(){ActionId=id,Intent=direction,Revision=before.Revision},CancellationToken.None,_=>{},()=>true);Complete(task,p.Effect);Require(task.Result.Outcome=="Unsupported"&&p.Effect.FocusRequests==0&&p.Effect.Sends==0&&w.AutomationSnapshot().Route==before.Route,"Unsupported intent focused, sent, navigated, or was accepted");}finally{w.Close();}});}
        foreach(var action in new[]{"operation-details","dismiss-operation"}){var id=action;test("hidden CLI reports interactive operation control "+id,()=>{using var p=new Platform();var w=Window(p);try{w.Controller.SetOperation("Solicitado","Owned result fixture","steam-overlay",Game,false);w.RefreshRows();w.Hide();var before=w.AutomationSnapshot();var operation=w.Controller.Operation;var result=w.AutomationExecute(new(){ActionId=id,Revision=before.Revision},CancellationToken.None,_=>{},()=>true).GetAwaiter().GetResult();Require(result.Outcome=="RequiresInteractive"&&w.AutomationSnapshot().Route==before.Route&&ReferenceEquals(operation,w.Controller.Operation),"Hidden operation control falsely reported completion or mutated");}finally{w.Close();}});}
        test("Steam UI owner is cancellable from the same CLI host",()=>{using var p=new Platform();var w=Window(p);var name="BezoroUiOwner."+Guid.NewGuid().ToString("N");try{Complete(w.StartAutomation(name),new Effects());Click(w,"steam-overlay");Commit(w);var old=w.OperationTask;var id=w.AutomationSnapshot().PendingRequestId;Require(id.Length>0&&!old.IsCompleted,"UI owner not pending");var cancel=Task.Run(()=>AutomationTests.Exchange(name,new(){Verb="cancel",RequestId=id}));Complete(cancel,new Effects());Require(cancel.Result.Outcome=="Cancelled","CLI could not cancel UI owner");Complete(old,p.Effect);Require(p.Effect.Sends==0,"UI owner sent after CLI cancel");}finally{w.Close();}});
        test("CLI rejects mutation while UI handoff is pending",()=>{using var p=new Platform();var w=Window(p);try{Click(w,"steam-overlay");Require(w.TransitionPending,"UI handoff not pending");var result=w.AutomationExecute(new(){ActionId="route:apps",Revision=w.AutomationSnapshot().Revision},CancellationToken.None,_=>{},()=>true).GetAwaiter().GetResult();Require(result.Outcome=="Busy"&&w.TransitionPending,"CLI bypassed pending UI handoff");}finally{w.Close();}});
        test("old UI logging failure cannot overwrite a reopened owner",()=>{using var p=new Platform();var w=Window(p);var name="BezoroUiLogging."+Guid.NewGuid().ToString("N");try{Complete(w.StartAutomation(name),new Effects());Click(w,"steam-overlay");Commit(w);var old=w.OperationTask;w.OpenPanel();Complete(w.OpeningTask,new Effects());w.Controller.SetOperation("Concluído","Later owned result","focus",Game,false);var expected=w.Controller.Operation;var root=w.Controller.Diagnostics.Root;System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root,"automation",System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString(),"automation-ledger.json.tmp"));Complete(old,p.Effect);Require(ReferenceEquals(expected,w.Controller.Operation),"Old log failure replaced current operation");}finally{w.Close();}});
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
        var w=new MainWindow(renderOnly:true,platform:p,diagnostics:new(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"BezoroFocusTests",Guid.NewGuid().ToString("N"))));w.InputContextReader=()=>new(true,w.IsVisible,true,true);w.PhysicalKeyReader=_=>0;w.AutomationInputReader=()=>new(new PadFrame[4],Environment.TickCount64-(p.Stale?1000:0),GuideValid:true);w.Controller.CaptureReturn();Complete(w.Controller.Refresh(),p.Effect);w.RefreshRows();w.Left=-12000;w.Top=-12000;w.ShowActivated=false;w.Show();return w;
    }
    private static void Click(MainWindow w,string id)=>ReviewFixTests.Button(w,id).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static void Commit(MainWindow w){w.AdvanceHandoff(new(new PadFrame[4],0),0);w.AdvanceHandoff(new(new PadFrame[4],100),100);}
    private sealed class Platform:IPlatform
    {
        internal readonly Effects Effect=new();internal readonly WindowRef Other=new(702,73,200,"Other owned fixture","other");internal WindowRef Current=Game;internal WindowRef? LastTarget;internal bool Stale=false;
        public bool IsPreview=>true;public WindowRef? Foreground()=>Current;public IReadOnlyList<WindowRef> Windows()=>[Game,Other];public bool Valid(WindowRef w)=>ValidTarget&&(w.Same(Game)||w.Same(Other));public IReadOnlyList<DisplayRef> Displays()=>[new("fixture",0,0,1280,720,true)];public ModuleEvidence SteamEvidence(WindowRef? w)=>ModuleEvidence.Loaded;
        internal bool Disposed;internal int DisposeCalls;internal ActionResult? LastResult;internal bool ValidTarget=true;
        public int SteamOwnedKeys=>Effect.Mask;
        public ShortcutDispatch? LastShortcutDispatch=>Effect.Dispatch;
        public Task<ActionResult> Shortcut(WindowRef w,string keys)=>Shortcut(w,keys,CancellationToken.None,()=>true);
        public async Task<ActionResult> Shortcut(WindowRef w,string keys,CancellationToken token,Func<bool> continuing){LastTarget=w;return LastResult=await new GuardedActions(Effect).Shortcut(w,keys,token,continuing);}
        public async Task<ActionResult> Shortcut(WindowRef w,string keys,CancellationToken token,Func<bool> continuing,Action<ShortcutDispatch> progress){LastTarget=w;return LastResult=await new GuardedActions(Effect,_=>progress(Effect.Dispatch!.Value)).Shortcut(w,keys,token,continuing);}
        public Task<ActionResult> Focus(WindowRef w,CancellationToken token,Func<bool> continuing)=>new GuardedActions(Effect).FocusStable(w,token,continuing);
        public Task<ActionResult> Switch(WindowRef w)=>Task.FromResult(new ActionResult(ResultKind.Simulated,"Owned switch"));public Task<ActionResult> Close(WindowRef w,CancellationToken token)=>throw new Exception("No close expected");public ActionResult Launch(bool playnite,string? path)=>throw new Exception("No launch expected");public void Dispose(){DisposeCalls++;if(Disposed)return;Disposed=true;Effect.Timeline.Add("dispose");}
    }
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    private static void DriveUntil(Func<bool> ready,Effects effects){var limit=DateTime.UtcNow.AddSeconds(3);while(!ready()&&DateTime.UtcNow<limit){effects.Release();Pump();}Require(ready(),"Fixture did not reach controlled checkpoint");}
    internal static void Complete(Task task,Effects effects)
    {
        var end=DateTime.UtcNow.AddSeconds(3);while(!task.IsCompleted&&DateTime.UtcNow<end){effects.Release();var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
        Require(task.IsCompleted,"Fixture focus deadline");task.GetAwaiter().GetResult();
    }
    internal sealed class Effects:IWindowEffects
    {
        internal readonly List<int> NativeCalls=[];internal int Sends,FocusRequests,Elapsed;internal bool ValidIdentity=true,Foreground=true,FocusAccepted=true;internal Action? AfterWait=null;private TaskCompletionSource? wait;
        internal Action? AfterReleaseTake,AfterWaitPublish;
        internal readonly List<Automation.SteamKeyEvent> Events=[];internal readonly List<string> Timeline=[];internal int Mask,NativeHeld,Attempts;internal bool ObserveRelease=true;internal ShortcutDispatch? Dispatch;internal Func<ushort,bool,int,SteamKeyReceipt>? Response;internal Action<Automation.SteamSequenceEvidence>? Recorded;
        public long ShortcutTime=>Elapsed;
        public SteamKeyReceipt SendSteamEvent(ushort key,bool up){var bit=key==16?1:2;var receipt=Response?.Invoke(key,up,Attempts++)??new(true,1);if(receipt.Started){NativeCalls.Add(1);if(!up&&receipt.Accepted!=0){NativeHeld|=bit;if(key==16)Sends++;}else if(up&&receipt.Accepted==1)NativeHeld&=~bit;Timeline.Add((up?"up":"down")+key);}return receipt;}
        public bool SteamKeysReleased(int mask)=>ObserveRelease&&(NativeHeld&mask)==0;
        public void SteamOwnership(int mask)=>Mask=mask;
        public void RecordSteamSequence(Automation.SteamSequenceEvidence evidence){Events.Clear();Events.AddRange(evidence.Events);Dispatch=new((Dispatch?.Revision??0)+1,4,evidence.Events.Any(e=>!e.Cleanup&&e.Accepted is null)?null:(uint)evidence.Events.Where(e=>!e.Cleanup).Sum(e=>(long)(e.Accepted??0)),evidence.Events.Count(e=>e.Cleanup),evidence.Events.Any(e=>e.Cleanup&&e.Accepted is null)?null:(uint)evidence.Events.Where(e=>e.Cleanup).Sum(e=>(long)(e.Accepted??0)),evidence);Recorded?.Invoke(evidence);}
        public bool Valid(WindowRef w)=>ValidIdentity;public WindowObservation ObserveWindow(WindowRef w)=>WindowObservation.SameIdentity;public bool IsForeground(WindowRef w)=>Foreground;public bool RequestFocus(WindowRef w){FocusRequests++;return FocusAccepted;}
        public uint SendKeys(string keys){Sends++;NativeCalls.Add(4);return 4;}public bool PostClose(WindowRef w)=>false;
        public Task Wait(int milliseconds){Elapsed+=milliseconds;AfterWait?.Invoke();var next=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Volatile.Write(ref wait,next);AfterWaitPublish?.Invoke();return next.Task;}
        internal void Release(){var next=Interlocked.Exchange(ref wait,null);AfterReleaseTake?.Invoke();next?.TrySetResult();}
    }
}

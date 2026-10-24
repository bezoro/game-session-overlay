using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ApolloOverlay;

internal static unsafe class SessionHubServiceTests
{
    private static readonly WindowRef Target=new((nint)123,42,100,"Owned fixture","fixture",false,7);
    private static void Require(bool condition,string reason){if(!condition)throw new Exception(reason);}
    private static T Done<T>(Task<T> task){Require(task.Wait(5000),"Service did not finish within its bounded deadline");return task.GetAwaiter().GetResult();}
    private sealed class Session(string key="render-a/session-a",uint pid=42) : IApplicationAudioSession
    {
        public string Key {get;}=key;
        public uint Pid=pid;
        public int PidResult=0,SessionState=1,StateResult=0,ReadResult=0,WriteResult=0,Writes=0;
        public bool Muted,IgnoreWrite;
        public Action? BeforeWrite,AfterWrite,AfterRead;
        public int ProcessId(out uint pid){pid=Pid;return PidResult;}
        public int State(out int state){state=SessionState;return StateResult;}
        public int ReadMute(out bool muted){muted=Muted;AfterRead?.Invoke();return ReadResult;}
        public int WriteMute(bool muted){BeforeWrite?.Invoke();Writes++;if(WriteResult==0&&!IgnoreWrite)Muted=muted;AfterWrite?.Invoke();return WriteResult;}
    }
    private sealed class Catalog(params IApplicationAudioSession[] sessions) : IApplicationAudioCatalog
    {
        public List<IApplicationAudioSession> Sessions=sessions.ToList();
        public int Gaps,Discoveries,OwnerThread,DisposedThread;
        public Action? BeforeDiscover;
        public Action? Changed;
        public readonly ManualResetEventSlim Disposed=new();
        public ApplicationAudioCatalogSnapshot Discover(Func<bool> continuing)
        {
            OwnerThread=Environment.CurrentManagedThreadId;BeforeDiscover?.Invoke();Discoveries++;
            return new(Sessions.ToArray(),Gaps);
        }
        public void Dispose(){DisposedThread=Environment.CurrentManagedThreadId;Disposed.Set();}
    }
    private static ApplicationAudio Audio(Catalog catalog,Func<WindowRef,bool>? valid=null,Func<long>? clock=null)=>new(valid??(_=>true),(changed,_)=>{catalog.Changed=changed;return catalog;},clock??(()=>0));

    internal static void Run(Action<string,Action> test)
    {
        test("session audio unavailable without positively identified sessions",()=>
        {
            using var audio=Audio(new Catalog());var result=Done(audio.SetMutedAsync(Target,true,11));
            Require(result.Result.Kind==ResultKind.Unavailable&&!result.Dispatched&&result.Snapshot.Target!.Same(Target)&&result.Snapshot.Generation==11,"No-session identity/result contract");
        });
        test("session audio exact HRESULT PID inactive and expired filtering",()=>
        {
            var good=new Session{SessionState=0};var foreign=new Session("foreign",99);var system=new Session("system",0);var expired=new Session("expired"){SessionState=2};var ambiguous=new Session("shared"){PidResult=0x0889000D};
            using var audio=Audio(new Catalog(good,foreign,system,expired,ambiguous));var result=Done(audio.SetMutedAsync(Target,true,1));
            Require(good.Writes==1&&foreign.Writes==0&&system.Writes==0&&expired.Writes==0&&ambiguous.Writes==0,"Only exact S_OK single-process sessions can receive writes");
            Require(result.Result.Kind==ResultKind.Partial&&result.Verified==1&&result.Snapshot.SkippedOrFailedSessions>0,"Ambiguity must remain a scope gap");
        });
        test("session audio covers identified sessions across endpoints with explicit desired state",()=>
        {
            var a=new Session("endpoint-a/one");var b=new Session("endpoint-b/two"){Muted=true};var catalog=new Catalog(a,b);
            using var audio=Audio(catalog);var before=Done(audio.RefreshAsync(Target,3));Require(before.MuteState==ApplicationMuteState.Mixed,"Mixed state lost");
            var result=Done(audio.SetMutedAsync(Target,true,3));Require(result.Result.Kind==ResultKind.Completed&&result.Attempted==2&&result.Succeeded==2&&result.Verified==2&&a.Muted&&b.Muted,"All identified sessions/readback");
            var unmute=Done(audio.SetMutedAsync(Target,false,3));Require(unmute.Result.Kind==ResultKind.Completed&&!a.Muted&&!b.Muted,"Unmute must use deliberate desired value");
        });
        test("session audio same-process windows share audio only after each identity validation",()=>
        {
            var session=new Session();using var audio=Audio(new Catalog(session),window=>window.Started==Target.Started&&window.Generation==Target.Generation);
            var second=Target with{Handle=(nint)124,Title="Second window"};Require(Done(audio.SetMutedAsync(second,true,5)).Verified==1,"Same process window should match");
            foreach(var stale in new[]{Target with{Started=101},Target with{Generation=8},Target with{Protected=true}})
                Require(!Done(audio.SetMutedAsync(stale,false,5)).Dispatched,"Reused/protected identity must not dispatch");
            Require(session.Writes==1,"Identity refusal wrote audio");
        });
        test("session audio partial endpoint and failed read remain honest",()=>
        {
            var session=new Session();using var audio=Audio(new Catalog(session){Gaps=1});var result=Done(audio.SetMutedAsync(Target,true,1));
            Require(result.Result.Kind==ResultKind.Partial&&result.Snapshot.Availability==ApplicationAudioAvailability.Partial,"Denied endpoint must prevent complete success");
            session.ReadResult=unchecked((int)0x80004005);var snapshot=Done(audio.RefreshAsync(Target,1));Require(snapshot.MuteState==ApplicationMuteState.Unknown&&snapshot.IdentifiedSessions==1,"Unread identified mute must remain unknown");
        });
        test("session audio failed write/readback cannot report completed",()=>
        {
            var good=new Session();var refused=new Session("render-b/fail"){WriteResult=unchecked((int)0x80004005)};
            using var audio=Audio(new Catalog(good,refused));var result=Done(audio.SetMutedAsync(Target,true,1));Require(result.Result.Kind==ResultKind.Partial&&result.Attempted==2&&result.Succeeded==1&&result.Verified==1,"Partial write counts");
            refused.WriteResult=0;refused.IgnoreWrite=true;result=Done(audio.SetMutedAsync(Target,true,1));Require(result.Result.Kind==ResultKind.Partial&&result.Verified==1,"Set success alone must not pass readback");
        });
        test("session audio cancellation before dispatch sends zero writes",()=>
        {
            var session=new Session();using var cancel=new CancellationTokenSource();cancel.Cancel();using var audio=Audio(new Catalog(session));
            var result=Done(audio.SetMutedAsync(Target,true,1,cancel.Token));Require(result.Result.Kind==ResultKind.Cancelled&&!result.Dispatched&&session.Writes==0,"Pre-dispatch cancellation");
        });
        test("session audio cancellation after first write forbids later writes and rollback",()=>
        {
            using var cancel=new CancellationTokenSource();var first=new Session(){AfterWrite=cancel.Cancel};var next=new Session("render-b/next");using var audio=Audio(new Catalog(first,next));
            var result=Done(audio.SetMutedAsync(Target,true,1,cancel.Token));Require(result.Result.Kind==ResultKind.Unknown&&result.Dispatched&&first.Writes==1&&next.Writes==0&&first.Muted,"Cancellation must not retry or undo dispatched writes");
        });
        test("session audio deadline between native calls prevents later writes",()=>
        {
            long now=0;var first=new Session(){AfterWrite=()=>now=2001};var next=new Session("render-b/next");using var audio=Audio(new Catalog(first,next),clock:()=>Interlocked.Read(ref now));
            var result=Done(audio.SetMutedAsync(Target,true,1));Require(result.Result.Kind==ResultKind.Unknown&&result.Dispatched&&first.Writes==1&&next.Writes==0,"Deadline fencing");
        });
        test("session audio watchdog returns unknown while native owner finishes then releases",()=>
        {
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var first=new Session(){BeforeWrite=()=>{entered.Set();Require(release.Wait(5000),"Fixture release deadline");}};var next=new Session("next");var catalog=new Catalog(first,next);var audio=Audio(catalog);
            try
            {
                var task=audio.SetMutedAsync(Target,true,1);Require(entered.Wait(1000),"Native fixture did not enter");var result=Done(task);
                Require(result.Result.Kind==ResultKind.Unknown&&result.Dispatched&&!catalog.Disposed.IsSet,"Watchdog must not release a live owner");audio.Dispose();release.Set();Require(catalog.Disposed.Wait(1500),"Owner did not clean up");
                Require(first.Writes==1&&next.Writes==0&&catalog.OwnerThread==catalog.DisposedThread,"Post-deadline writes or wrong-owner cleanup");
            }
            finally{release.Set();audio.Dispose();}
        });
        test("session audio churn never applies future-session policy",()=>
        {
            var first=new Session();var future=new Session("render-b/future");var catalog=new Catalog(first);first.AfterWrite=()=>catalog.Sessions.Add(future);using var audio=Audio(catalog);
            var result=Done(audio.SetMutedAsync(Target,true,1));Require(result.Result.Kind==ResultKind.Partial&&first.Writes==1&&future.Writes==0&&result.Snapshot.IdentifiedSessions==2,"Future/replacement session must remain unwritten");
        });
        test("session audio target identity change after dispatch stops remaining sessions",()=>
        {
            var valid=true;var first=new Session(){AfterWrite=()=>valid=false};var next=new Session("next");using var audio=Audio(new Catalog(first,next),_=>valid);
            var result=Done(audio.SetMutedAsync(Target,true,1));Require(result.Result.Kind==ResultKind.Unknown&&first.Writes==1&&next.Writes==0,"Identity changed after dispatch");
        });
        foreach(var desiredMute in new[]{true,false})
        {
            var desired=desiredMute;
            test(desired?"session audio final external unmute prevents completed mute":"session audio final external mute prevents completed unmute",()=>
            {
                var session=new Session(){Muted=!desired};var catalog=new Catalog(session);
                catalog.BeforeDiscover=()=>{if(catalog.Discoveries==1)session.Muted=!desired;};
                using var audio=Audio(catalog);var result=Done(audio.SetMutedAsync(Target,desired,1));
                var finalState=desired?ApplicationMuteState.Audible:ApplicationMuteState.Muted;
                Require(result.Attempted==1&&result.Succeeded==1&&result.Verified==1&&session.Writes==1,"Fixture must verify the original write before the external change");
                Require(result.Snapshot.MuteState==finalState,"Final fresh snapshot must retain the external change");
                Require(result.Result.Kind!=ResultKind.Completed,"Final external mute change must prevent a completed claim despite earlier verified readback");
            });
        }
        test("session audio suspend invalidates queued mutation and fresh show observes again",()=>
        {
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var session=new Session();var catalog=new Catalog(session){BeforeDiscover=()=>{entered.Set();Require(release.Wait(3000),"Discovery fixture deadline");}};using var audio=Audio(catalog);
            var pending=audio.SetMutedAsync(Target,true,1);Require(entered.Wait(1000),"Discovery did not enter");audio.Suspend();Require(Done(pending).Result.Kind==ResultKind.Cancelled,"Suspension before writes");release.Set();Require(catalog.Disposed.Wait(1500),"Suspension must clean owner resources");Require(session.Writes==0,"Late suspended write");
            catalog.BeforeDiscover=null;Require(Done(audio.RefreshAsync(Target,2)).MuteState==ApplicationMuteState.Audible,"Fresh reopen discovery");
        });
        test("session audio callback observes external mute without mutation",()=>
        {
            var session=new Session();var catalog=new Catalog(session);using var audio=Audio(catalog);using var observed=new ManualResetEventSlim();
            audio.Changed+=snapshot=>{if(snapshot.MuteState==ApplicationMuteState.Muted&&snapshot.Generation==9)observed.Set();};audio.Track(Target,true,9);Done(audio.RefreshAsync(Target,9));session.Muted=true;catalog.Changed!();Require(observed.Wait(1000),"External mute callback did not refresh");Require(session.Writes==0,"Observation wrote audio");
        });
        test("session audio callback arriving after read schedules follow-up observation",()=>
        {
            using var captured=new ManualResetEventSlim();using var release=new ManualResetEventSlim();using var observed=new ManualResetEventSlim();
            var session=new Session();var catalog=new Catalog(session);using var audio=Audio(catalog);
            audio.Changed+=snapshot=>{if(snapshot.MuteState==ApplicationMuteState.Muted&&snapshot.Generation==9)observed.Set();};
            audio.Track(Target,true,9);Done(audio.RefreshAsync(Target,9));var blockOnce=1;
            session.AfterRead=()=>{if(Interlocked.Exchange(ref blockOnce,0)==1){captured.Set();Require(release.Wait(3000),"Blocked read fixture release deadline");}};
            try
            {
                catalog.Changed!();Require(captured.Wait(1000),"Callback refresh did not capture its read");
                session.Muted=true;catalog.Changed!();release.Set();
                Require(observed.Wait(1000),"A notification after the in-flight read must trigger a fresh follow-up observation");
                Require(session.Writes==0,"Callback recovery must never retry a mutation");
            }
            finally{release.Set();}
        });
        RunMinimize(test);RunDevices(test);RunFeedback(test);RunAbi(test);
    }
    private sealed class WindowEffects : IWindowEffects
    {
        public bool IsValid=true,Protected,Minimized,Accepted=true,MinimizeSupported=true;
        public int Requests,Focus,Keys,Closes,Waits;
        public Action? Waiting;
        public bool Valid(WindowRef window)=>IsValid&&!Protected;
        public WindowObservation ObserveWindow(WindowRef window)=>WindowObservation.SameIdentity;
        public bool IsForeground(WindowRef window)=>false;
        public bool RequestFocus(WindowRef window){Focus++;return true;}
        public uint SendKeys(string keys){Keys++;return 0;}
        public bool PostClose(WindowRef window){Closes++;return true;}
        public Task Wait(int milliseconds){Waits+=milliseconds;Waiting?.Invoke();return Task.CompletedTask;}
        public bool SupportsMinimize=>MinimizeSupported;
        public bool IsMinimized(WindowRef window)=>Minimized;
        public bool RequestMinimize(WindowRef window){Requests++;return Accepted;}
    }
    private static void RunMinimize(Action<string,Action> test)
    {
        test("minimize blocked protected stale unsupported and pre-cancelled targets",()=>
        {
            foreach(var effects in new[]{new WindowEffects{IsValid=false},new WindowEffects{Protected=true},new WindowEffects{MinimizeSupported=false}}){Done(new GuardedActions(effects).Minimize(Target,CancellationToken.None));Require(effects.Requests==0,"Unsafe request");}
            var protectedEffects=new WindowEffects();Done(new GuardedActions(protectedEffects).Minimize(Target with{Protected=true},CancellationToken.None));Require(protectedEffects.Requests==0,"Protected request");
            using var cancel=new CancellationTokenSource();cancel.Cancel();Require(Done(new GuardedActions(protectedEffects).Minimize(Target,cancel.Token)).Kind==ResultKind.Cancelled&&protectedEffects.Requests==0,"Pre-cancel minimize");
        });
        test("minimize already confirmed avoids dispatch and never focuses or injects",()=>
        {
            var effects=new WindowEffects{Minimized=true};Require(Done(new GuardedActions(effects).Minimize(Target,CancellationToken.None)).Kind==ResultKind.Completed&&effects.Requests==0,"Already minimized");Require(effects.Focus==0&&effects.Keys==0&&effects.Closes==0,"Minimize side effects");
        });
        test("minimize accepted request needs same-identity IsIconic confirmation",()=>
        {
            var effects=new WindowEffects();var result=Done(new GuardedActions(effects).Minimize(Target,CancellationToken.None));Require(result.Kind==ResultKind.Unknown&&effects.Waiting is null&&effects.Waits==1000&&effects.Requests==1,"Accepted is not completed");
            effects=new();effects.Waiting=()=>effects.Minimized=true;Require(Done(new GuardedActions(effects).Minimize(Target,CancellationToken.None)).Kind==ResultKind.Completed,"Same target confirmation");
            effects=new(){Accepted=false};Require(Done(new GuardedActions(effects).Minimize(Target,CancellationToken.None)).Kind==ResultKind.Failed&&effects.Waits==0,"Native refusal");
        });
        test("minimize replacement or cancellation after dispatch remains unknown",()=>
        {
            var effects=new WindowEffects();effects.Waiting=()=>{effects.IsValid=false;effects.Minimized=true;};Require(Done(new GuardedActions(effects).Minimize(Target,CancellationToken.None)).Kind==ResultKind.Unknown,"Replaced IsIconic is not success");
            using var cancel=new CancellationTokenSource();effects=new();effects.Waiting=cancel.Cancel;Require(Done(new GuardedActions(effects).Minimize(Target,cancel.Token)).Kind==ResultKind.Unknown&&effects.Requests==1,"Post-dispatch cancellation");
        });
    }
    private sealed class ControllerNative : IControllerNative
    {
        public uint ConnectionResult=0,BatteryResult=0,CapabilityResult=0,WriteResult=0;
        public Native.XBattery BatteryValue=new(){Type=2,Level=3};
        public Native.XCapabilities CapabilitiesValue=new(){Flags=1};
        public int BatteryReads,CapabilityReads;
        public readonly List<(uint Slot,ushort Left,ushort Right)> Writes=[];
        public uint Connected(uint slot)=>ConnectionResult;
        public uint Battery(uint slot,out Native.XBattery battery){BatteryReads++;battery=BatteryValue;return BatteryResult;}
        public uint Capabilities(uint slot,out Native.XCapabilities capabilities){CapabilityReads++;capabilities=CapabilitiesValue;return CapabilityResult;}
        public uint Vibrate(uint slot,ushort left,ushort right){Writes.Add((slot,left,right));return WriteResult;}
    }
    private static void RunDevices(Action<string,Action> test)
    {
        test("controller battery categorical wired unknown invalid and error states",()=>
        {
            foreach(var sample in new[]{(Type:(byte)1,Level:(byte)3,Expected:ControllerBatteryType.Wired,HasLevel:false),(Type:(byte)2,Level:(byte)0,Expected:ControllerBatteryType.Alkaline,HasLevel:true),(Type:(byte)3,Level:(byte)2,Expected:ControllerBatteryType.NickelMetalHydride,HasLevel:true),(Type:(byte)255,Level:(byte)3,Expected:ControllerBatteryType.Unknown,HasLevel:false),(Type:(byte)2,Level:(byte)9,Expected:ControllerBatteryType.Alkaline,HasLevel:false)})
            {
                var native=new ControllerNative{BatteryValue=new(){Type=sample.Type,Level=sample.Level}};using var devices=new ControllerDevices(native,()=>0);var value=devices.Read(0);Require(value.BatteryType==sample.Expected&&value.BatteryLevel.HasValue==sample.HasLevel,"Categorical battery decoding");
            }
            var failed=new ControllerNative{BatteryResult=5};using var errorDevices=new ControllerDevices(failed,()=>0);Require(errorDevices.Read(0).BatteryLevel is null,"Error must erase level");
        });
        test("controller cache cadence connection epochs and hidden connection-only updates",()=>
        {
            long now=0;var native=new ControllerNative();using var devices=new ControllerDevices(native,()=>now);devices.UpdateConnections([true,false,false,false]);Require(native.BatteryReads==0,"Connection frames must not query battery");
            var first=devices.Read(0);now=4999;devices.Read(0);Require(native.BatteryReads==1,"Battery polled before five seconds");now=5000;devices.Read(0);Require(native.BatteryReads==2,"Visible cadence refresh");
            devices.UpdateConnections([false,false,false,false]);devices.UpdateConnections([true,false,false,false]);native.BatteryValue=new(){Type=255,Level=3};var reconnected=devices.Read(0);Require(reconnected.ConnectionEpoch>first.ConnectionEpoch&&reconnected.BatteryLevel is null&&native.BatteryReads==3,"Reconnect reused stale battery");
            var refused=devices.SetVibration(0,first.ConnectionEpoch,1000,1000);Require(!refused.Accepted&&native.Writes.Count==0,"Old epoch vibration");
            native.ConnectionResult=1167;Require(!devices.Read(0).Connected,"Disconnect must clear level");
        });
        test("controller native results and capability are explicit without guessed percentages",()=>
        {
            var native=new ControllerNative{CapabilitiesValue=new(){Flags=0},WriteResult=5};using var devices=new ControllerDevices(native,()=>0);var value=devices.Read(1);Require(value.Vibration==ControllerVibrationCapability.Unavailable,"Unsupported haptics");var write=devices.SetVibration(1,value.ConnectionEpoch,0,0);Require(!write.Accepted&&write.NativeResult==5,"Native vibration error hidden");
        });
    }
    private sealed class Sound : IFeedbackSound
    {
        public int Plays,Stops,Disposals;
        public bool Fail;
        public void Play(FeedbackCue cue){if(Fail)throw new InvalidOperationException("Owned fake audio failure");Plays++;}
        public void Stop(){Stops++;}
        public void Dispose(){Disposals++;}
    }
    private sealed class Scheduler : IFeedbackScheduler
    {
        public readonly List<(int Delay,Action Action)> Actions=[];
        private sealed class Lease : IDisposable{public void Dispose() { }}
        public IDisposable Schedule(int milliseconds,Action action){Actions.Add((milliseconds,action));return new Lease();}
    }
    private static void RunFeedback(Action<string,Action> test)
    {
        test("feedback opt-ins independent and foreground deliberate source gates",()=>
        {
            var native=new ControllerNative();using var devices=new ControllerDevices(native,()=>0);var device=devices.Read(0);var sound=new Sound();var scheduler=new Scheduler();using var feedback=new FeedbackService(devices,sound,scheduler,()=>0);var source=new FeedbackSource(FeedbackSourceKind.Controller,0,device.ConnectionEpoch);
            feedback.SetContext(new(true,true,0,device.ConnectionEpoch));feedback.Request(FeedbackCue.Test,source);Require(sound.Plays==0&&native.Writes.Count==0,"Defaults must be off");
            feedback.Configure(true,false);feedback.Request(FeedbackCue.Test,new(FeedbackSourceKind.Pointer));Require(sound.Plays==1&&native.Writes.Count==0,"Sound opt-in alone");
            feedback.Configure(false,true);feedback.SetContext(new(true,false,0,device.ConnectionEpoch));feedback.Request(FeedbackCue.Confirmation,source);Require(native.Writes.Count==0,"Foreign foreground");feedback.SetContext(new(true,true,0,device.ConnectionEpoch));feedback.Request(FeedbackCue.Confirmation,new(FeedbackSourceKind.Keyboard));Require(native.Writes.Count==0,"Keyboard must not rumble");
            feedback.Request(FeedbackCue.Confirmation,source);Require(native.Writes.Count==1&&scheduler.Actions.Single().Delay<=60,"Deliberate controller pulse");
        });
        test("feedback rate limits and old timer cannot stop newer pulse",()=>
        {
            long now=0;var native=new ControllerNative();using var devices=new ControllerDevices(native,()=>now);var device=devices.Read(0);var sound=new Sound();var scheduler=new Scheduler();using var feedback=new FeedbackService(devices,sound,scheduler,()=>now);feedback.Configure(true,true);feedback.SetContext(new(true,true,0,device.ConnectionEpoch));var source=new FeedbackSource(FeedbackSourceKind.Controller,0,device.ConnectionEpoch);
            feedback.Request(FeedbackCue.Navigation,source);now=79;feedback.Request(FeedbackCue.Navigation,source);Require(sound.Plays==1,"Navigation 80ms limit");now=80;feedback.Request(FeedbackCue.Navigation,source);Require(sound.Plays==2,"Navigation boundary");var writes=native.Writes.Count;scheduler.Actions[0].Action();Require(native.Writes.Count==writes,"Old timer stopped newer pulse");scheduler.Actions[1].Action();Require(native.Writes.Last().Left==0&&native.Writes.Last().Right==0,"Current lease stop");
            now=100;feedback.Request(FeedbackCue.Unavailable,source);now=349;feedback.Request(FeedbackCue.Unavailable,source);Require(sound.Plays==3,"Unavailable 250ms limit");now=350;feedback.Request(FeedbackCue.Unavailable,source);Require(sound.Plays==4,"Unavailable boundary");
        });
        test("feedback hide disable disconnect and dispose fence all late nonzero writes",()=>
        {
            foreach(var stop in new[]{"hide","disable","disconnect","dispose","focus"})
            {
                var native=new ControllerNative();using var devices=new ControllerDevices(native,()=>0);var device=devices.Read(0);var scheduler=new Scheduler();var feedback=new FeedbackService(devices,new Sound(),scheduler,()=>0);feedback.Configure(false,true);feedback.SetContext(new(true,true,0,device.ConnectionEpoch));var source=new FeedbackSource(FeedbackSourceKind.Controller,0,device.ConnectionEpoch);feedback.Request(FeedbackCue.Confirmation,source);
                if(stop=="hide")feedback.Suspend();else if(stop=="disable")feedback.Configure(false,false);else if(stop=="disconnect"){devices.UpdateConnections([false,false,false,false]);feedback.SetContext(new(true,true,null));}else if(stop=="focus")feedback.SetContext(new(true,false,0,device.ConnectionEpoch));else feedback.Dispose();
                var count=native.Writes.Count;scheduler.Actions[0].Action();Require(native.Writes.Count==count,"Stale callback wrote after "+stop);Require(native.Writes.Count(w=>w.Left!=0)==1&&native.Writes.All(w=>w.Slot==0),"Unexpected slot/nonzero write after "+stop);feedback.Dispose();
            }
        });
        test("feedback sound failure remains nonfatal and reports unavailability",()=>
        {
            using var devices=new ControllerDevices(new ControllerNative(),()=>0);using var feedback=new FeedbackService(devices,new Sound{Fail=true},new Scheduler(),()=>0);feedback.Configure(true,false);feedback.SetContext(new(true,true,null));var result=feedback.Request(FeedbackCue.Test,new(FeedbackSourceKind.Pointer));Require(!result.SoundRequested&&!feedback.Status.SoundAvailable,"Sound failure must be reported");
        });
    }
    private static void RunAbi(Action<string,Action> test)
    {
        test("XInput native layout matches documented x64 ABI",()=>
        {
            Require(Marshal.SizeOf<Native.XBattery>()==2&&Marshal.SizeOf<Native.XVibration>()==4&&Marshal.SizeOf<Native.Gamepad>()==12&&Marshal.SizeOf<Native.XCapabilities>()==20,"XInput ABI size");Require(Marshal.OffsetOf<Native.XCapabilities>(nameof(Native.XCapabilities.Vibration)).ToInt32()==16,"Capabilities vibration offset");
        });
        test("Core Audio flattened native vtable preserves GetProcessId HRESULT and DWORD",()=>
        {
            using var native=new ComFixture(false);var control=(IHubAudioSessionControl2)native.Interface;
            Require(control.GetProcessId(out var pid)==0&&pid==42,"S_OK PID ABI");native.Value->Result=0x0889000D;Require(control.GetProcessId(out pid)==0x0889000D&&pid==42,"Positive shared-process HRESULT must not be thrown or normalized");
        });
        test("Core Audio simple volume native BOOL marshalling and explicit mute slots",()=>
        {
            using var native=new ComFixture(true);var volume=(IHubSimpleAudioVolume)native.Interface;var context=Guid.Empty;
            Require(volume.SetMute(true,ref context)==0&&native.Value->Muted==1&&volume.GetMute(out var muted)==0&&muted,"Set/Get mute BOOL ABI");Require(volume.SetMute(false,ref context)==0&&native.Value->Muted==0,"Explicit unmute ABI");
        });
    }
    [StructLayout(LayoutKind.Sequential)]private struct ComValue{public nint Table;public int References,Result,Muted;}
    private sealed unsafe class ComFixture : IDisposable
    {
        public ComValue* Value {get;}
        private readonly nint* table;
        public object Interface {get;}
        public ComFixture(bool volume)
        {
            Value=(ComValue*)NativeMemory.AllocZeroed((nuint)sizeof(ComValue));table=(nint*)NativeMemory.AllocZeroed(17,(nuint)sizeof(nint));Value->Table=(nint)table;Value->References=1;Value->Muted=volume?0:-1;
            table[0]=(nint)(delegate* unmanaged[Stdcall]<ComValue*,Guid*,nint*,int>)&Query;table[1]=(nint)(delegate* unmanaged[Stdcall]<ComValue*,uint>)&AddRef;table[2]=(nint)(delegate* unmanaged[Stdcall]<ComValue*,uint>)&Release;
            if(volume){table[5]=(nint)(delegate* unmanaged[Stdcall]<ComValue*,int,Guid*,int>)&SetMute;table[6]=(nint)(delegate* unmanaged[Stdcall]<ComValue*,int*,int>)&GetMute;}
            else table[14]=(nint)(delegate* unmanaged[Stdcall]<ComValue*,uint*,int>)&GetPid;
            Interface=Marshal.GetObjectForIUnknown((nint)Value);
        }
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static int Query(ComValue* self,Guid* iid,nint* result)
        {
            if(*iid==new Guid("00000000-0000-0000-C000-000000000046")||*iid==(self->Muted<0?new Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"):new Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"))){*result=(nint)self;self->References++;return 0;}*result=0;return unchecked((int)0x80004002);
        }
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static uint AddRef(ComValue* self)=>(uint)++self->References;
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static uint Release(ComValue* self)=>(uint)--self->References;
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static int GetPid(ComValue* self,uint* pid){*pid=42;return self->Result;}
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static int SetMute(ComValue* self,int muted,Guid* context){self->Muted=muted;return self->Result;}
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]private static int GetMute(ComValue* self,int* muted){*muted=self->Muted;return self->Result;}
        public void Dispose(){Marshal.ReleaseComObject(Interface);Marshal.Release((nint)Value);NativeMemory.Free(table);NativeMemory.Free(Value);}
    }
}

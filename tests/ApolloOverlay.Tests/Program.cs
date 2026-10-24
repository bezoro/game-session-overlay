using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using ApolloOverlay;

static class Program
{
    private static readonly WindowRef Game=new(101,42,100,"Game","game");
    private static readonly List<TestCaseResult> results=[];
    private static void Equal<T>(T expected,T actual) {if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new Exception($"Expected {expected}, got {actual}");}
    private static void True(bool actual) {if(!actual)throw new Exception("Expected true");}
    private static void Case(string name,Action test) {try{test();results.Add(new(name,true,null));Console.WriteLine("PASS "+name);}catch(Exception e){results.Add(new(name,false,e.Message));Console.Error.WriteLine("FAIL "+name+": "+e.Message);failed++;}}
    private static int failed;
    private static ActionResult Run(Task<ActionResult> task)=>task.GetAwaiter().GetResult();
    [STAThread] public static int Main(string[] args)
    {
        if(args.Length is not (1 or 2)) {Console.Error.WriteLine("Expected report path");return 2;}
        if(args.Length==2&&args[1]=="--review-captures"){ReviewFixTests.Capture(args[0]);return 0;}
        if(args.Length==2&&args[1]=="--busy-pad"){SessionHubTests.BusyControllerChecks(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--guide-settings"){GuideAbiTests.GuideSettingsChecks(Case,Path.GetDirectoryName(Path.GetFullPath(args[0]))!);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--session-hub-services"){SessionHubServiceTests.Run(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--session-hub-captures"){SessionHubTests.Capture(args[0]);return 0;}
        if(args.Length==2&&args[1]=="--session-hub-focus-captures"){SessionHubTests.Capture(args[0],true);return 0;}
        if(args.Length==2&&args[1]=="--session-hub"){SessionHubTests.Run(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--session-hub-telemetry"){SessionHubTests.TelemetryFailures(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--premium"){PremiumInterfaceTests.Run(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--premium-safety"){PremiumInterfaceTests.RunSafety(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--premium-local-dismiss"){PremiumInterfaceTests.LocalDismissalRegression(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--premium-navigation"){PremiumInterfaceTests.RunNavigation(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--reader-evidence"){PremiumInterfaceTests.ReaderEvidence(Case,Path.GetDirectoryName(Path.GetFullPath(args[0]))!);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--premium-safety-captures"){ReviewFixTests.Capture(args[0],true);return 0;}
        if(args.Length==2&&args[1]=="--review-fixes"){ReviewFixTests.Run(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--guide-release-red"){GuideAbiTests.GuideReleaseRed(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--focus-open-red"){GuideAbiTests.FocusOpenRed(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--focus-suite"){GuideAbiTests.HandoffTests(Case);GuideAbiTests.FocusDismiss(Case);GuideAbiTests.FocusChecks(Case);GuideAbiTests.GuideOpeningChecks(Case,Path.GetDirectoryName(Path.GetFullPath(args[0]))!);Case("Guide status actual row",GuideAbiTests.LegacyStatusView);Case("legacy Guide ABI lifetime",GuideAbiTests.LegacyAbi);Case("legacy Guide failures",GuideAbiTests.LegacyFailures);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--dismiss-known-held"){GuideAbiTests.KnownHeldDismiss(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--dismiss-cancel"){GuideAbiTests.DismissCancellation(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--dismiss-cancel-suite"){GuideAbiTests.HandoffTests(Case);GuideAbiTests.FocusDismiss(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--system-key-baseline"){Case("System AltY holds handoff after AltUp until YUp plus100ms",GuideAbiTests.SystemKeyBaseline);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--disposed-platform-baseline"){Case("Disposed platform refuses owned target validation and activation",GuideAbiTests.DisposedPlatformBaseline);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--late-launch-baseline"){Case("Cleanup prevents late Steam launch after pending focus completes",GuideAbiTests.LateLaunchBaseline);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--release-baseline"){GuideAbiTests.ReleaseBaseline(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--focus-dismiss"){GuideAbiTests.FocusDismiss(Case);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--guide-status"){Case("G3-01 Guide subtitle follows actual poll without rebuilding or hidden UI work",GuideAbiTests.LegacyStatusView);return Finish(args[0]);}
        if(args.Length==2&&args[1]=="--corrections"){CorrectionTests.Run(Case,Path.GetDirectoryName(Path.GetFullPath(args[0]))!);return Finish(args[0]);}
        Case("session survives our focus and launcher chain",()=>{var s=new SessionTracker();s.Observe(Game);s.Observe(Game with{Handle=2,ProcessName="Playnite.FullscreenApp"});s.Observe(Game with{Handle=3,ProcessName="steam"});s.Observe(Game with{Handle=4,Protected=true});True(Game.Same(s.Anchor));s.Observe(Game with{Handle=5,Pid=50});Equal((nint)5,s.Anchor!.Handle);});
        Case("launcher alone does not become game",()=>{var s=new SessionTracker();s.Observe(Game with{ProcessName="steam"});Equal<WindowRef?>(null,s.Anchor);});
        Case("session anchor immutable across panel observation",()=>{var s=new SessionTracker();s.Observe(Game);var held=s.Anchor;s.Observe(Game with{Handle=8,Protected=true});True(Game.Same(held));Equal(2L,s.Sequence);});
        Case("process exit clears anchor",()=>{var s=new SessionTracker();s.Observe(Game);s.ClearIfStale(_=>false);Equal<WindowRef?>(null,s.Anchor);});
        Case("reused PID or HWND start identity rejected",()=>{True(!Game.Same(Game with{Started=200}));True(!Game.Same(Game with{Pid=43}));True(!Game.Same(Game with{Handle=102}));});
        Case("same-process HWND replacement uses destruction generation",()=>{True(!Game.Same(Game with{Generation=1}));var f=new FakeEffects{IsValid=false};Equal(ResultKind.Failed,Run(new GuardedActions(f).Close(Game,CancellationToken.None)).Kind);Equal(0,f.Closes);});
        Case("system and unknown identities cannot close",()=>{True(!(Game with{Protected=true}).CanClose);True(!(Game with{Started=0}).CanClose);});
        Case("launchers cannot close even with valid native identity",()=>{foreach(var name in new[]{"steam","Playnite.FullscreenApp","Playnite.DesktopApp"}){var f=new FakeEffects();Equal(ResultKind.Failed,Run(new GuardedActions(f).Close(Game with{ProcessName=name},CancellationToken.None)).Kind);Equal(0,f.Closes);}});
        Case("steam running alone stays unknown",()=>{var s=new SteamPermission();True(!s.ToggleAllowed(Game,ModuleEvidence.Unknown,"Shift+Tab"));});
        Case("steam missing and access denied stay disabled",()=>{var s=new SteamPermission();s.Verify(Game,"Shift+Tab");True(!s.ToggleAllowed(Game,ModuleEvidence.Absent,"Shift+Tab"));True(!s.ToggleAllowed(Game,ModuleEvidence.Denied,"Shift+Tab"));});
        Case("loaded non-steam shortcut requires manual verification",()=>{var s=new SteamPermission();True(!s.ToggleAllowed(Game,ModuleEvidence.Loaded,"Shift+Tab"));s.Verify(Game,"Shift+Tab");True(s.ToggleAllowed(Game,ModuleEvidence.Loaded,"Shift+Tab"));});
        Case("steam verification invalidated by session or shortcut",()=>{var s=new SteamPermission();s.Verify(Game,"Shift+Tab");True(!s.ToggleAllowed(Game with{Started=200},ModuleEvidence.Loaded,"Shift+Tab"));True(!s.ToggleAllowed(Game,ModuleEvidence.Loaded,"Ctrl+Tab"));s.Invalidate();True(!s.ToggleAllowed(Game,ModuleEvidence.Loaded,"Shift+Tab"));});
        Case("A release before rearm and no repeated activation",()=>{var p=new PadInterpreter();Equal(PadButtons.None,p.Update(new(true,PadButtons.A),0).Pressed);p.Update(new(true,0),1);Equal(PadButtons.A,p.Update(new(true,PadButtons.A),2).Pressed);Equal(PadButtons.None,p.Update(new(true,PadButtons.A),3).Pressed);});
        Case("reconnect requires neutral",()=>{var p=new PadInterpreter();p.Update(new(true,0),0);p.Update(default,1);Equal(PadButtons.None,p.Update(new(true,PadButtons.A),2).Pressed);});
        Case("direction deadzone and bounded repeat",()=>{var p=new PadInterpreter();p.Update(new(true,0),0);Equal(0,p.Update(new(true,0,0,9000),1).Direction);Equal(-1,p.Update(new(true,PadButtons.Up),2).Direction);Equal(0,p.Update(new(true,PadButtons.Up),100).Direction);Equal(-1,p.Update(new(true,PadButtons.Up),422).Direction);Equal(0,p.Update(new(true,PadButtons.Up),430).Direction);});
        Case("initial held A cannot close",()=>{var h=new CloseHold();h.Open(Game);True(!h.Tick(true,false,true,true,Game,0));True(!h.Tick(true,false,true,true,Game,5000));Equal(0d,h.Progress);});
        Case("fresh 1.5 second hold closes exactly once",()=>{var h=new CloseHold();h.Open(Game);h.Tick(false,false,true,true,Game,0);True(!h.Tick(true,false,true,true,Game,1));True(!h.Tick(true,false,true,true,Game,1500));True(h.Tick(true,false,true,true,Game,1501));True(!h.Tick(true,false,true,true,Game,3000));});
        Case("release restarts hold deadline",()=>{var h=new CloseHold();h.Open(Game);h.Tick(false,false,true,true,Game,0);h.Tick(true,false,true,true,Game,1);h.Tick(false,false,true,true,Game,1400);True(!h.Tick(true,false,true,true,Game,1600));Equal(0d,h.Progress);});
        Case("B cancellation before dispatch",()=>{var h=new CloseHold();h.Open(Game);True(!h.Tick(true,true,true,true,Game,5000));Equal<WindowRef?>(null,h.Target);});
        Case("focus disconnect and target changes cancel hold",()=>{foreach(var cause in new[]{0,1,2}){var h=new CloseHold();h.Open(Game);h.Tick(false,false,true,true,Game,0);h.Tick(true,false,true,true,Game,1);True(!h.Tick(true,false,cause!=0,cause!=1,cause==2?Game with{Generation=1}:Game,2000));Equal<WindowRef?>(null,h.Target);}});
        Case("foreground refusal prevents shortcut dispatch",()=>{var f=new FakeEffects{FocusAllowed=false};Equal(ResultKind.Failed,Run(new GuardedActions(f).Shortcut(Game,"Win+G")).Kind);Equal(0,f.Sends);});
        Case("foreground timeout is unknown and sends nothing",()=>{var f=new FakeEffects{Foreground=false};Equal(ResultKind.Unknown,Run(new GuardedActions(f).Shortcut(Game,"Win+G")).Kind);Equal(0,f.Sends);Equal(6,f.Waits);});
        Case("target change during focus prevents dispatch",()=>{var f=new FakeEffects{InvalidateOnFocus=true};Equal(ResultKind.Failed,Run(new GuardedActions(f).Shortcut(Game,"Shift+Tab")).Kind);Equal(0,f.Sends);});
        Case("partial SendInput does not retry shortcut",()=>{var f=new FakeEffects{Sent=2};Equal(ResultKind.Partial,Run(new GuardedActions(f).Shortcut(Game,"Shift+Tab")).Kind);Equal(1,f.Sends);});
        Case("dispatch is requested not opened",()=>{var f=new FakeEffects();Equal(ResultKind.Requested,Run(new GuardedActions(f).Shortcut(Game,"Win+Tab")).Kind);});
        Case("close target does not follow current foreground",()=>{var f=new FakeEffects{StillExists=false};Equal(ResultKind.Completed,Run(new GuardedActions(f).Close(Game,CancellationToken.None)).Kind);True(Game.Same(f.Closed));});
        Case("stale target blocks close",()=>{var f=new FakeEffects{IsValid=false};Equal(ResultKind.Failed,Run(new GuardedActions(f).Close(Game,CancellationToken.None)).Kind);Equal(0,f.Closes);});
        Case("close timeout never escalates",()=>{var f=new FakeEffects();Equal(ResultKind.Unknown,Run(new GuardedActions(f).Close(Game,CancellationToken.None)).Kind);Equal(1,f.Closes);Equal(20,f.Waits);});
        Case("close cancellation before dispatch prevents effects",()=>{var f=new FakeEffects();using var c=new CancellationTokenSource();c.Cancel();Equal(ResultKind.Cancelled,Run(new GuardedActions(f).Close(Game,c.Token)).Kind);Equal(0,f.Closes);});
        Case("close cancellation after dispatch is unknown",()=>{using var c=new CancellationTokenSource();var f=new FakeEffects{OnWait=()=>c.Cancel()};Equal(ResultKind.Unknown,Run(new GuardedActions(f).Close(Game,c.Token)).Kind);Equal(1,f.Closes);});
        Case("close API failure has no observation or retry",()=>{var f=new FakeEffects{CloseAllowed=false};Equal(ResultKind.Failed,Run(new GuardedActions(f).Close(Game,CancellationToken.None)).Kind);Equal(1,f.Closes);Equal(0,f.Waits);});
        Case("blank caption with live identity never confirms close",()=>{var f=new FakeEffects{Probe=new(true,Game.Pid,Game.Started,Game.Generation,"")};Equal(ResultKind.Unknown,Run(new GuardedActions(f).Close(Game,CancellationToken.None)).Kind);Equal(1,f.Closes);Equal(20,f.Waits);});
        Case("denied identity inspection never confirms close",()=>{var f=new FakeEffects{Probe=new(true,Game.Pid,null,Game.Generation,"Still open")};Equal(ResultKind.Unknown,Run(new GuardedActions(f).Close(Game,CancellationToken.None)).Kind);Equal(20,f.Waits);});
        Case("failed PID inspection never confirms close",()=>{var f=new FakeEffects{Probe=new(true,null,null,Game.Generation,"Still open")};Equal(ResultKind.Unknown,Run(new GuardedActions(f).Close(Game,CancellationToken.None)).Kind);});
        Case("reliable destruction confirms close",()=>{var f=new FakeEffects{Probe=new(false,null,null,Game.Generation,"")};Equal(ResultKind.Completed,Run(new GuardedActions(f).Close(Game,CancellationToken.None)).Kind);});
        Case("reliable identity replacement confirms old window gone",()=>{foreach(var probe in new[]{new WindowProbe(true,Game.Pid+1,null,Game.Generation,""),new(true,Game.Pid,Game.Started+1,Game.Generation,""),new(true,Game.Pid,null,Game.Generation+1,"")}){var f=new FakeEffects{Probe=probe};Equal(ResultKind.Completed,Run(new GuardedActions(f).Close(Game,CancellationToken.None)).Kind);}});
        Case("display removal falls back to current primary",()=>{DisplayRef[] d=[new("left",-1920,0,1920,1080,false),new("primary",0,0,3840,2160,true)];Equal("primary",DisplayRef.Choose(d,"removed").Id);Equal("left",DisplayRef.Choose(d,"left").Id);});
        Case("negative origin and DPI convert once",()=>{var d=new DisplayRef("left",-1920,-300,1920,1080,false).Dips(144);Equal(-1280d,d.X);Equal(-200d,d.Y);Equal(1280d,d.Width);});
        Case("invalid settings rejected",()=>{foreach(var text in new[]{"{", "null", "{\"SteamShortcut\":\"Alt+F4\"}","{\"PlaynitePath\":\"relative.exe\"}"}){var rejected=false;try{UserSettings.Parse(text);}catch(Exception e) when(e is JsonException or InvalidDataException){rejected=true;}True(rejected);}});
        Case("valid settings source-generated roundtrip",()=>{var s=UserSettings.Parse("{\"SteamShortcut\":\"Ctrl+Tab\",\"DisplayId\":\"left\"}");Equal("Ctrl+Tab",s.SteamShortcut);Equal("left",s.DisplayId);});
        Case("saving recovered settings preserves previous-good backup",()=>{var folder=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!,"settings-fixtures");Directory.CreateDirectory(folder);var path=Path.Combine(folder,"recovery.json");UserSettings.Save(path,new(DisplayId:"first"));UserSettings.Save(path,new(DisplayId:"second"));File.WriteAllText(path,"{");var recovered=UserSettings.Load(path,out var message);Equal("first",recovered.DisplayId);True(message.Contains("recuperada"));UserSettings.Save(path,recovered);Equal("first",UserSettings.Parse(File.ReadAllText(path+".previous")).DisplayId);});
        Case("preview actions remain simulated",()=>{using var p=new PreviewPlatform(Game);var target=p.Foreground()!;Equal(ResultKind.Simulated,p.Launch(true,"missing.exe").Kind);Equal(ResultKind.Simulated,Run(p.Shortcut(target,"Win+G")).Kind);Equal(ResultKind.Simulated,Run(p.Switch(target)).Kind);Equal(ResultKind.Simulated,Run(p.Close(target,CancellationToken.None)).Kind);True(!p.Valid(target));});
        Case("guide initialization failure releases without registration",()=>{var f=new FakeGuide{Init=false};using(var c=new GuideConnection(f)){True(!c.Start(()=>{}));}Equal("initialize,release",string.Join(',',f.Calls));});
        Case("guide registration failure releases initialized instance",()=>{var f=new FakeGuide{Registration=false};using(var c=new GuideConnection(f)){True(!c.Start(()=>{}));}Equal("initialize,register,release",string.Join(',',f.Calls));});
        Case("guide stops and unregisters before release",()=>{var f=new FakeGuide();using(var c=new GuideConnection(f)){True(c.Start(()=>{}));True(!c.Observed);f.Press!();True(c.Observed);}Equal("initialize,register,stop,unregister,release",string.Join(',',f.Calls));});
        Case("guide failed unregister never unloads live callback",()=>{var f=new FakeGuide{Removal=false};var c=new GuideConnection(f);c.Start(()=>{});c.Dispose();c.Dispose();Equal("initialize,register,stop,unregister",string.Join(',',f.Calls));});
        Case("guide native vtable callback ABI and successful lifetime",GuideAbiTests.Success);
        Case("guide native registration failure releases safely",GuideAbiTests.RegistrationFailure);
        Case("guide native failed unregister retains callback context",GuideAbiTests.UnregisterFailure);
        Case("legacy Guide ABI16 offset4 and held startup never toggles",GuideAbiTests.LegacyAbi);
        Case("legacy Guide repress error disconnect reconnect latch",GuideAbiTests.LegacyCycles);
        Case("legacy Guide four independent slots coalesce one toggle",GuideAbiTests.LegacySlots);
        Case("legacy Guide DLL export failure and disposed late poll unload once",GuideAbiTests.LegacyFailures);
        Case("legacy Guide timer hidden active and simultaneous chord consumed once",GuideAbiTests.LegacyTick);
        Case("G3-01 Guide subtitle follows actual poll without rebuilding or hidden UI work",GuideAbiTests.LegacyStatusView);
        GuideAbiTests.FocusDismiss(Case);
        GuideAbiTests.ReleaseBaseline(Case);
        Case("Cleanup prevents late Steam launch after pending focus completes",GuideAbiTests.LateLaunchBaseline);
        Case("Disposed platform refuses owned target validation and activation",GuideAbiTests.DisposedPlatformBaseline);
        GuideAbiTests.HandoffTests(Case);
        Case("legacy Guide settings absent off on save preserves default and backup",()=>{True(!UserSettings.Parse("{}").GuideXInputEnabled);True(!UserSettings.Parse("{\"GuideXInputEnabled\":false}").GuideXInputEnabled);True(UserSettings.Parse("{\"GuideXInputEnabled\":true}").GuideXInputEnabled);var path=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!,"guide-settings.json");UserSettings.Save(path,new());UserSettings.Save(path,new(GuideXInputEnabled:true));True(UserSettings.Load(path,out _).GuideXInputEnabled);True(!UserSettings.Parse(File.ReadAllText(path+".previous")).GuideXInputEnabled);});
        GuideAbiTests.GuideSettingsChecks(Case,Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        PremiumInterfaceTests.Run(Case);
        SessionHubTests.Run(Case);
        SessionHubServiceTests.Run(Case);
        ReviewFixTests.Run(Case);
        NavigationTests.Run(Case);
        CorrectionTests.Run(Case,Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        return Finish(args[0]);
    }
    private static int Finish(string path)
    {
        var report=new TestReport(results.Count,results.Count-failed,failed,results.ToArray());
        File.WriteAllText(path,JsonSerializer.Serialize(report,TestJson.Default.TestReport));Console.WriteLine($"RESULT {results.Count-failed}/{results.Count} passed; failed={failed}");return failed==0 ? 0 : 1;
    }
    private sealed class FakeEffects : IWindowEffects
    {
        public bool IsValid=true,Foreground=true,FocusAllowed=true,InvalidateOnFocus,StillExists=true,CloseAllowed=true;
        public uint Sent=4;public int Sends,Closes,Waits;public WindowRef? Closed;public Action? OnWait;
        public WindowProbe? Probe;
        public bool Valid(WindowRef window)=>IsValid;
        public WindowObservation ObserveWindow(WindowRef window)=>Probe is {} probe ? ClosureObservation.Classify(window,probe) : StillExists ? WindowObservation.SameIdentity : WindowObservation.GoneOrReplaced;
        public bool IsForeground(WindowRef window)=>Foreground;
        public bool RequestFocus(WindowRef window){if(InvalidateOnFocus)IsValid=false;return FocusAllowed;}
        public uint SendKeys(string keys){Sends++;return Sent;}
        public bool PostClose(WindowRef window){Closes++;Closed=window;return CloseAllowed;}
        public Task Wait(int milliseconds){Waits++;OnWait?.Invoke();return Task.CompletedTask;}
    }
    private sealed class FakeGuide : IGuideBackend
    {
        public bool Init=true,Registration=true,Removal=true;public readonly List<string> Calls=[];public Action? Press;
        public bool Initialize(out string reason){Calls.Add("initialize");reason="test";return Init;}
        public bool Register(Action pressed,out string reason){Calls.Add("register");Press=pressed;reason="test";return Registration;}
        public void Stop()=>Calls.Add("stop");
        public bool Unregister(){Calls.Add("unregister");return Removal;}
        public void Release()=>Calls.Add("release");
    }
}
internal sealed record TestCaseResult(string Name,bool Passed,string? Error);
internal sealed record TestReport(int Total,int Passed,int Failed,TestCaseResult[] Cases);
[System.Text.Json.Serialization.JsonSerializable(typeof(TestReport))] internal partial class TestJson : System.Text.Json.Serialization.JsonSerializerContext;

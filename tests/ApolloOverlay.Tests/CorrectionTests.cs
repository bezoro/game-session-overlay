using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ApolloOverlay;

internal static class CorrectionTests
{
    private static void Require(bool value,string message){if(!value)throw new Exception(message);}
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    private static void Complete(Task task){while(!task.IsCompleted)Pump();task.GetAwaiter().GetResult();}
    internal static void Run(Action<string,Action> test,string output)
    {
        var app=Application.Current??new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(app);
        test("NT01 normal Fluent resources exist without palette override",()=>{ThemeSupport.FollowSystem(app);var w=new MainWindow(renderOnly:true);try{foreach(var key in new[]{ThemeSupport.Surface,ThemeSupport.Success,ThemeSupport.Text})Require(w.TryFindResource(key) is System.Windows.Media.SolidColorBrush brush&&brush.Color.A>0,"Missing normal Fluent brush: "+key);Require(ThemeSupport.FollowsSystem(app),"Unexpected theme override");}finally{w.Close();}});
        foreach(var name in new[]{"Hotkey","Chord"})test("NI02 null "+name+" recovers previous then defaults",()=>{var folder=Path.Combine(output,"null-"+name);Directory.CreateDirectory(folder);var path=Path.Combine(folder,"settings.json");File.WriteAllText(path,"{\""+name+"\":null}");File.WriteAllText(path+".previous","{\"Deadzone\":23000}");var recovered=UserSettings.Load(path,out var message);Require(recovered.Deadzone==23000&&message.Contains("recuperada"),"Previous-good configuration must recover");File.WriteAllText(path+".previous","{");var fallback=UserSettings.Load(path,out message);Require(fallback==new UserSettings(),"Invalid previous must recover defaults");});
        test("NI06 custom23000 and preset deadzone UI remain valid",()=>{var w=new MainWindow(renderOnly:true);w.InputContextReader=()=>new(true,true,true,true);w.PhysicalKeyReader=_=>0;try{foreach(var initial in new[]{23000,8000,12000,18000,24000,28000}){w.Controller.Change(new(Deadzone:initial));w.Page(2);for(var n=0;n<w.ActionIds.Count&&w.SelectedAction!="motion";n++)w.Sequential(1);Require(w.SelectedAction=="motion","Appearance UI selection");for(var n=0;n<w.ActionIds.Count&&w.SelectedAction!="deadzone";n++)w.Sequential(1);Require(w.SelectedAction=="deadzone","Deadzone UI selection");for(var i=0;i<5;i++){w.ActivateEntry();Require(w.Controller.Settings.Deadzone is >=8000 and <=28000,"UI generated invalid deadzone");}}}finally{w.Close();}});
        test("NI03 native display message refreshes populated topology cache",()=>{using var p=new MutableDisplays();var w=new MainWindow(renderOnly:true,platform:p);try{Complete(w.Controller.Refresh());w.Controller.Change(new(DisplayId:"removed"));w.ShowDiagnostic();p.Screens=[new("current",0,0,800,600,true)];var reads=p.Reads;var handle=new WindowInteropHelper(w).Handle;Native.SendMessageTimeout(handle,0x7E,0,0,2,500,out _);Pump();Require(p.Reads>reads&&w.Controller.Displays.Single().Id=="current","WM_DISPLAYCHANGE reused stale populated topology");Require(w.Width<=800&&w.Height<=600,"Panel exceeds current topology");}finally{w.Close();}});
        test("NI04 placement reads own HWND DPI after destination choice",()=>{using var p=new MutableDisplays();var w=new MainWindow(renderOnly:true,platform:p);try{Complete(w.Controller.Refresh());w.Controller.CaptureReturn();var own=new WindowInteropHelper(w).Handle;var observed=new List<nint>();w.DpiReader=h=>{observed.Add(h);return h==own?192u:96u;};w.Place();Require(observed.Count>0&&observed.All(h=>h==own),"Placement queried target-app DPI instead of own overlay HWND");}finally{w.Close();}});
        foreach(var state in new[]{WindowState.Normal,WindowState.Maximized,WindowState.Minimized})test("NI05 own focus preserves "+state,()=>{var w=new Window{Title="Owned focus regression",Width=320,Height=200,WindowStyle=WindowStyle.None,ShowInTaskbar=false,Topmost=true};try{w.Show();w.WindowState=state;Pump();using var platform=new WindowsPlatform(allowOwnedFixture:true);var handle=new WindowInteropHelper(w).Handle;var identity=new WindowRef(handle,Environment.ProcessId,NativeCapture.StartTicks,w.Title,"fixture");platform.RequestFocus(identity);Pump();Require(w.WindowState==(state==WindowState.Minimized?WindowState.Normal:state),"Focus changed presentation state: "+w.WindowState);}finally{w.Close();}});
    }
    private sealed class MutableDisplays:IPlatform
    {
        public IReadOnlyList<DisplayRef> Screens=[new("removed",0,0,1000,800,true)];public int Reads;public bool IsPreview=>true;
        public WindowRef? Foreground()=>new(101,42,100,"Owned policy fixture","fixture");public IReadOnlyList<WindowRef> Windows()=>[Foreground()!];public bool Valid(WindowRef w)=>true;public IReadOnlyList<DisplayRef> Displays(){Reads++;return Screens;}public ModuleEvidence SteamEvidence(WindowRef? w)=>ModuleEvidence.Unknown;
        public Task<ActionResult> Switch(WindowRef w)=>Task.FromResult(new ActionResult(ResultKind.Simulated,"fixture"));public Task<ActionResult> Close(WindowRef w,CancellationToken c)=>throw new Exception("Unexpected close");public Task<ActionResult> Shortcut(WindowRef w,string k)=>throw new Exception("Unexpected shortcut");public ActionResult Launch(bool p,string? s)=>throw new Exception("Unexpected launch");public void Dispose(){}
    }
}

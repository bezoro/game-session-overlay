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
    internal static void FocusDismiss(Action<string,Action> test)
    {
        var app=System.Windows.Application.Current??new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(app);
        foreach(var result in new[]{ResultKind.Failed,ResultKind.Unknown})foreach(var back in new[]{false,true})test("Dismiss "+(back?"B":"Toggle")+" remains hidden after focus "+result,()=>{
            using var platform=new DismissPlatform(result);var window=new MainWindow(renderOnly:true,platform:platform);try{
                window.Controller.CaptureReturn();window.Controller.Select(platform.Target);window.ShowDiagnostic();window.SetRenderState("confirmation");Require(window.Confirming,"Fixture did not arm confirmation");
                if(back){window.Back();Require(!window.Confirming,"B did not cancel confirmation");window.Back();}else window.Toggle();
                Require(platform.Switches==1,"Owned fixture did not exercise restore branch; foreground unavailable");Require(!window.Confirming,"Dismiss retained close hold");Require(!window.IsVisible,"Dismiss reopened overlay after "+result+"; expected hidden independently of restore outcome");
            }finally{window.Close();}
        });
    }
    private sealed class DismissPlatform(ResultKind result):IPlatform
    {
        internal readonly WindowRef Target=new(101,42,100,"Owned fake return target","fixture");internal int Switches;
        public bool IsPreview=>true;public WindowRef? Foreground()=>Target;public IReadOnlyList<WindowRef> Windows()=>[Target];public bool Valid(WindowRef w)=>Target.Same(w);
        public IReadOnlyList<DisplayRef> Displays()=>[new("owned-fixture",0,0,1280,720,true)];public ModuleEvidence SteamEvidence(WindowRef? w)=>ModuleEvidence.Unknown;
        public Task<ActionResult> Switch(WindowRef w){Switches++;Require(Target.Same(w));return Task.FromResult(new ActionResult(result,"Owned fake focus restore "+result));}
        public Task<ActionResult> Close(WindowRef w,CancellationToken c)=>throw new Exception("Unexpected close");public Task<ActionResult> Shortcut(WindowRef w,string k)=>throw new Exception("Unexpected shortcut");public ActionResult Launch(bool p,string? s)=>throw new Exception("Unexpected launch");public void Dispose(){}
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

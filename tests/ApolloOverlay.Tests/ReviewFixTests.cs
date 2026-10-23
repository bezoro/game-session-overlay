using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using ApolloOverlay;

internal static class ReviewFixTests
{
    internal static void Require(bool value,string message){if(!value)throw new Exception(message);}
    internal static Button Button(MainWindow w,string id)=>((Grid)w.FindName("Rows")).Children.OfType<Button>().Concat(((Grid)w.FindName("CloseArea")).Children.OfType<Button>()).First(b=>Equals(b.Tag,id));
    internal static MainWindow Window(){var w=new MainWindow(renderOnly:true,platform:new PreviewPlatform(new(101,42,100,"Janela de teste","fixture")));w.InputContextReader=()=>new(true,true,true,true);w.PhysicalKeyReader=_=>0;w.Controller.CaptureReturn();w.Controller.Refresh().GetAwaiter().GetResult();w.RefreshRows();return w;}
    internal static void Run(Action<string,Action> test)
    {
        var app=Application.Current??new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(app);
        test("RF keyboard pre-held W without overlay key event blocks neutrality",()=>{var w=Window();try{w.PhysicalKeyReader=k=>k==0x57?unchecked((short)0x8000):(short)0;Require(!w.PhysicalKeysNeutral(),"Omitted pre-held W was reported neutral by shortcut whitelist");}finally{w.Close();}});
        foreach(var key in new[]{0x03,0x08,0x30,0x57,0x70,0xA0,0xA1,0xA2,0xA3,0xA4,0xA5,0xBA,0xFE})test("RF keyboard high-bit pre-held key "+key,()=>{var w=Window();try{w.PhysicalKeyReader=k=>k==key?unchecked((short)0x8000):(short)0;Require(!w.PhysicalKeysNeutral(),"Pre-held VK ignored: "+key);}finally{w.Close();}});
        test("RF complete keyboard domain excludes physical mouse and ignores low bit",()=>{var w=Window();try{var read=new HashSet<int>();w.PhysicalKeyReader=k=>{read.Add(k);return 1;};Require(w.PhysicalKeysNeutral(),"Low bit must not mean held");Require(read.SetEquals(new[]{3}.Concat(Enumerable.Range(8,0xFE-8+1))),"Keyboard VK coverage differs");}finally{w.Close();}});
        foreach(var context in new[]{new OverlayInputContext(true,true,true,true),new(false,true,true,true),new(true,false,true,true),new(true,true,false,true),new(true,true,true,false),new(true,true,true,true,true),new(true,true,true,true,false,true)})test("RF navigation same predicate "+context,()=>{var w=Window();try{w.InputContextReader=()=>context;var before=w.SelectedAction;w.Move(NavDirection.Down);Require((w.SelectedAction!=before)==(context.Confirmed&&context.Visible&&context.Active&&context.OwnForeground&&!context.OwnedDialog&&!context.Exiting),"Focus predicate changed");}finally{w.Close();}});
        foreach(var degraded in new[]{false,true})test("RF pre-held W blocks "+(degraded?"degraded dismissal":"normal handoff")+" and renewed input resets 100ms",()=>{
            var w=Window();try{var held=true;w.PhysicalKeyReader=k=>held&&k==0x57?unchecked((short)0x8000):(short)0;
                if(degraded)w.Back();else Button(w,"taskview").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Require(w.TransitionPending,"Handoff not queued");ReleaseSample Sample(long t)=>new(new PadFrame[4],t,GuideValid:!degraded);
                w.AdvanceHandoff(Sample(0),0);w.AdvanceHandoff(Sample(1000),1000);Require(w.TransitionPending,"Pre-held W allowed dispatch");
                held=false;w.AdvanceHandoff(Sample(1001),1001);w.AdvanceHandoff(Sample(1100),1100);Require(w.TransitionPending,"Committed before100ms");
                held=true;w.AdvanceHandoff(Sample(1101),1101);held=false;w.AdvanceHandoff(Sample(1102),1102);w.AdvanceHandoff(Sample(1201),1201);Require(w.TransitionPending,"Renewed held key did not restart interval");w.AdvanceHandoff(Sample(1202),1202);Require(!w.TransitionPending,"100ms release did not commit");w.AdvanceHandoff(Sample(1302),1302);Require(!w.TransitionPending,"Commit did not remain final");
            }finally{w.Close();}});
    }
}

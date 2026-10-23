using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ApolloOverlay;

namespace Bezoro.NativeProof;

// The test-copy replaces only Native.ReadPad with this provider. No injection into another app.
internal static class PadFixture
{
    internal static PadFrame[] Frames=[default,default,default,default];
    internal static PadFrame Read(uint slot)=>Frames[slot];
    internal static async Task Feed(params PadFrame[] frames){Frames=Enumerable.Range(0,4).Select(i=>i<frames.Length?frames[i]:default).ToArray();await Task.Delay(130);}
}
internal static class FocusedInput
{
    private static Button[] Buttons(ProofWindows windows)=>((Grid)((Grid)((Border)windows.Overlay.Content).Child).Children[2]).Children.Cast<Button>().ToArray();
    private static void Route(Key key)
    {
        if(Keyboard.FocusedElement is not UIElement target)throw new InvalidOperationException("No actual WPF focused element.");
        var source=PresentationSource.FromVisual(target)??throw new InvalidOperationException("No WPF presentation source.");
        foreach(var down in new[]{true,false}){
            var args=new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,key){RoutedEvent=down?Keyboard.PreviewKeyDownEvent:Keyboard.PreviewKeyUpEvent};target.RaiseEvent(args);
            if(!args.Handled){args.RoutedEvent=down?Keyboard.KeyDownEvent:Keyboard.KeyUpEvent;target.RaiseEvent(args);}
        }
    }
    internal static async Task<ProofReport> Execute(string output)
    {
        var report=new ProofReport();report.Facts["inputMethod"]="WPF focused-element Preview/Bubble KeyDown+KeyUp pair, actual Button class handlers; deterministic frame provider replaces only Native.ReadPad expression in attributed test copy. Not physical input.";
        async Task Test(string name,Func<ProofWindows,Task> body){PadFixture.Frames=[default,default,default,default];using var windows=new ProofWindows(ProofConfig.Parse("Ctrl+Alt+Space","LB+RB+Start",650));windows.Exit=()=>{};try{await windows.NextFrame(()=>windows.Show());await body(windows);report.Cases.Add(new(name,true,null));Console.WriteLine("PASS "+name);}catch(Exception e){report.Cases.Add(new(name,false,e.Message));Console.WriteLine("FAIL "+name+": "+e.Message);}}
        void Assert(bool pass,string actual){if(!pass)throw new InvalidOperationException(actual);}
        foreach(var key in new[]{Key.Space,Key.Enter})await Test("keyboard Right then "+key+" dispatches green-selected marker",async w=>{var buttons=Buttons(w);Keyboard.Focus(buttons[0]);Route(Key.Right);Assert(w.Selection==1,"Expected selection1; actual="+w.Selection);var previous=w.Marker.Background;Route(key);await Task.Delay(40);Assert(w.Overlay.IsVisible&&w.Marker.Background!=previous,$"Expected marker action with overlay visible; actual visible={w.Overlay.IsVisible}, selection={w.Selection}, markerChanged={w.Marker.Background!=previous}");});
        await Test("Tab keeps keyboard focus and green selection synchronized",async w=>{var buttons=Buttons(w);Keyboard.Focus(buttons[0]);Route(Key.Tab);await Task.Delay(20);Assert(w.Selection==1&&ReferenceEquals(Keyboard.FocusedElement,buttons[1]),$"selection={w.Selection}; focusedIndex={Array.IndexOf(buttons,Keyboard.FocusedElement)}");});
        await Test("keyboard-open neutral then first Dpad is retained",async w=>{await PadFixture.Feed(new PadFrame(true,0));await PadFixture.Feed(new PadFrame(true,PadButtons.Right));Assert(w.Selection==1,"Expected selection1; actual="+w.Selection);});
        await Test("keyboard-open neutral then first A is retained",async w=>{var buttons=Buttons(w);buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var previous=w.Marker.Background;await PadFixture.Feed(new PadFrame(true,0));await PadFixture.Feed(new PadFrame(true,PadButtons.A));Assert(w.Overlay.IsVisible&&w.Marker.Background!=previous,"Expected first A marker dispatch; markerChanged="+(w.Marker.Background!=previous));});
        await Test("held A at opening remains blocked until release",async w=>{await PadFixture.Feed(new PadFrame(true,PadButtons.A));Assert(w.Overlay.IsVisible,"Initial held A dispatched Return.");await PadFixture.Feed(new PadFrame(true,0));await PadFixture.Feed(new PadFrame(true,PadButtons.A));Assert(!w.Overlay.IsVisible,"A after neutral release did not dispatch Return.");});
        await Test("disconnect cancels and reconnect held A is blocked",async w=>{await PadFixture.Feed(new PadFrame(true,0));await PadFixture.Feed(new PadFrame(true,PadButtons.Right));await PadFixture.Feed(new PadFrame(true,0));await PadFixture.Feed(default(PadFrame));await PadFixture.Feed(new PadFrame(true,PadButtons.A));Assert(w.Overlay.IsVisible,"Reconnect held A dispatched.");await PadFixture.Feed(new PadFrame(true,0));var before=w.Marker.Background;await PadFixture.Feed(new PadFrame(true,PadButtons.A));Assert(w.Marker.Background!=before,"Released reconnect A was not dispatched to selected marker.");});
        await Test("two controllers cannot combine chord",async w=>{w.Hide();await PadFixture.Feed(new PadFrame(true,0),new PadFrame(true,0));await PadFixture.Feed(new PadFrame(true,PadButtons.LB),new PadFrame(true,PadButtons.RB|PadButtons.Start));await Task.Delay(750);Assert(!w.Overlay.IsVisible,"Cross-controller chord opened overlay.");});
        await Test("navigation owner ignores another controller A",async w=>{await PadFixture.Feed(new PadFrame(true,0),new PadFrame(true,0));await PadFixture.Feed(new PadFrame(true,PadButtons.Right),new PadFrame(true,0));await PadFixture.Feed(new PadFrame(true,0),new PadFrame(true,0));var before=w.Marker.Background;await PadFixture.Feed(new PadFrame(true,0),new PadFrame(true,PadButtons.A));Assert(w.Selection==1&&ReferenceEquals(w.Marker.Background,before)&&w.Overlay.IsVisible,"Another controller changed selection or dispatched A.");await PadFixture.Feed(new PadFrame(true,PadButtons.A),new PadFrame(true,0));Assert(w.Marker.Background!=before,"Owning controller A did not dispatch marker.");});
        await Test("chord opening release then first A dispatches",async w=>{w.Hide();await PadFixture.Feed(new PadFrame(true,0));await PadFixture.Feed(new PadFrame(true,PadButtons.LB|PadButtons.RB|PadButtons.Start));await Task.Delay(750);Assert(w.Overlay.IsVisible,"Complete chord did not open own overlay.");await PadFixture.Feed(new PadFrame(true,0));await PadFixture.Feed(new PadFrame(true,PadButtons.A));Assert(!w.Overlay.IsVisible,"Post-chord release A did not dispatch Return.");});
        await Test("ordinary mouse Click route still dispatches clicked action",async w=>{var before=w.Marker.Background;Buttons(w)[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(20);Assert(w.Selection==1&&w.Marker.Background!=before&&w.Overlay.IsVisible,"Click route did not dispatch marker.");});
        await Test("focused native lifecycle keeps owned HWND and returns focus",async w=>{for(var i=0;i<5;i++){Assert(OwnedNative.GetForegroundWindow()==w.OverlayHandle,"Own overlay lost foreground.");Assert(w.Hide(),"Owned return focus refused.");await w.NextFrame(()=>w.Show());Assert(OwnedNative.GetForegroundWindow()==w.OverlayHandle,"Owned show focus refused.");Assert(w.ActiveRenderHandlers==0,"Stuck Rendering handler.");}Assert(Application.Current.Windows.Count==2,"Duplicate windows.");});
        report.Status=report.Cases.All(c=>c.Passed)?"passed":"failed";File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(report,ProofJson.Default.ProofReport));return report;
    }
}

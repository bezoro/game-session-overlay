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
    internal static void Capture(string output)
    {
        Directory.CreateDirectory(output);var app=Application.Current??new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        foreach(var theme in new[]{"dark","light","contrast-palette"})foreach(var height in new[]{940,600})foreach(var state in new[]{"home","system","audio","confirmation"}){
            ThemeSupport.ForceForValidation(app,theme=="light");var palette=new ResourceDictionary();
            if(theme=="contrast-palette"){
                foreach(var key in new[]{ThemeSupport.Surface,ThemeSupport.Card,ThemeSupport.Control,ThemeSupport.SuccessBackground,ThemeSupport.CriticalBackground})palette[key]=Brushes.Black;
                foreach(var key in new[]{ThemeSupport.Text,ThemeSupport.SecondaryText,ThemeSupport.DisabledText})palette[key]=Brushes.White;
                palette[ThemeSupport.Stroke]=Brushes.White;palette[ThemeSupport.Success]=Brushes.Yellow;palette[ThemeSupport.Critical]=Brushes.Cyan;app.Resources.MergedDictionaries.Add(palette);
            }
            var w=Window();try{
                w.SetRenderState(state);var root=(FrameworkElement)w.Content;w.Content=null;root.Resources.MergedDictionaries.Add(w.Resources);root.SetResourceReference(TextElement.ForegroundProperty,ThemeSupport.Text);root.SetValue(TextElement.FontFamilyProperty,w.FontFamily);root.SetValue(TextElement.FontSizeProperty,w.FontSize);
                root.Measure(new Size(572,height));root.Arrange(new Rect(0,0,572,height));root.UpdateLayout();
                var scroll=(ScrollViewer)w.FindName("ContentScroll");var mode=(TextBlock)w.FindName("ModeLabel");var footer=mode.TransformToAncestor(root).TransformBounds(new Rect(new Size(mode.ActualWidth,mode.ActualHeight)));
                Require(footer.Bottom<=height+1&&footer.Top>=0,"Footer clipped: "+theme+state+height);Require(mode.ActualHeight>=24,"Runtime tab/movement hints did not wrap");
                Require(mode.Text.Contains("PgUp/PgDn")&&mode.Text.Contains("Tab"),"Runtime keyboard hint missing");
                if(state!="confirmation"){Require(scroll.ViewportHeight>0,"No scroll viewport");scroll.ScrollToEnd();root.UpdateLayout();Require(scroll.VerticalOffset+scroll.ViewportHeight>=scroll.ExtentHeight-1,"Scroll end unreachable");scroll.ScrollToTop();root.UpdateLayout();}
                else Require(w.Confirming,"Confirmation fixture absent");
                var bitmap=new RenderTargetBitmap(572,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));var name=$"{theme}-{height}-{state}";using(var stream=File.Create(Path.Combine(output,name+".png")))encoder.Save(stream);
                Console.WriteLine($"CAPTURE {name}; footer={footer}; viewport={scroll.ViewportHeight}; extent={scroll.ExtentHeight}; offscreen={!w.IsVisible}");
            }finally{w.Close();if(theme=="contrast-palette")app.Resources.MergedDictionaries.Remove(palette);ThemeSupport.FollowSystem(app);}
        }
    }
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
        foreach(var state in new[]{"home","apps","system","details","audio","integrations"})test("RF UIA all page actions "+state,()=>{var w=Window();try{
            w.SetRenderState(state);var gear=(Button)w.FindName("SettingsGear");Require(new ButtonAutomationPeer(gear).GetName()=="Abrir Sistema","Gear name missing");Require(new ButtonAutomationPeer(gear).GetHelpText().Length>0,"Gear help missing");
            var buttons=((Grid)w.FindName("Rows")).Children.OfType<Button>().Concat(((Grid)w.FindName("CloseArea")).Children.OfType<Button>()).ToArray();Require(buttons.Length>0,"Empty page fixture");
            foreach(var button in buttons){var texts=(StackPanel)((Grid)button.Content).Children[1];var peer=new ButtonAutomationPeer(button);Require(peer.GetName()==((TextBlock)texts.Children[0]).Text,"Action name differs: "+button.Tag);Require(peer.GetHelpText().Length>0,"Action help missing: "+button.Tag);if(texts.Children.Count>1&&texts.Children[1] is TextBlock desc)Require(peer.GetHelpText()==desc.Text,"Description differs: "+button.Tag);}
            Require(!w.IsVisible,"Accessibility probe must stay offscreen");
        }finally{w.Close();}});
        test("RF audio description stays accessible despite omitted home subtitle",()=>{var w=Window();try{var button=Button(w,"audio");var peer=new ButtonAutomationPeer(button);w.UpdateAudioEntries(new("fixture",.35f,true,"fixture"));Require(peer.GetHelpText()=="Sem som","Mute help stale");w.UpdateAudioEntries(new("fixture",.35f,false,"fixture"));Require(peer.GetHelpText()==.35f.ToString("P0"),"Volume help stale");w.UpdateAudioEntries(new(null,0,false,"fixture"));Require(peer.GetHelpText()=="Saída indisponível","Unavailable help stale");}finally{w.Close();}});
        test("RF Guide status reason and limit title update existing peers",()=>{var w=Window();try{w.SetRenderState("integrations");var button=Button(w,"guide");var peer=new ButtonAutomationPeer(button);w.UpdateGuideStatus("Controle indisponível · fixture");Require(peer.GetHelpText()=="Controle indisponível · fixture","Guide help stale");Require(ReferenceEquals(button,Button(w,"guide")),"Guide rebuilt button");w.Page(2);var limit=Button(w,"guide-limit");var limitPeer=new ButtonAutomationPeer(limit);var texts=(StackPanel)((Grid)limit.Content).Children[1];texts.Children.OfType<Slider>().Single().Value=1500;Require(limitPeer.GetName()==((TextBlock)texts.Children[0]).Text&&limitPeer.GetName().Contains("1,5"),"Guide limit name stale");}finally{w.Close();}});
        test("RF UIA action exposes visible title and reason without Show",()=>{var w=Window();try{var button=Button(w,"return");var peer=new ButtonAutomationPeer(button);Require(peer.GetName()=="Voltar ao jogo","Missing action name: '"+peer.GetName()+"'");Require(peer.GetHelpText()=="Janela de teste","Missing action reason: '"+peer.GetHelpText()+"'");Require(!w.IsVisible,"Probe must stay offscreen");}finally{w.Close();}});
    }
}

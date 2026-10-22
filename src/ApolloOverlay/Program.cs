using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ApolloOverlay;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("pt-BR");
        if(args.Length==1 && args[0]=="--probe-guide") {
            var backend=new GameInputBackend(); var available=backend.Initialize(out var message);backend.Release();
            Console.WriteLine($"Guide v2 initialization only: available={available}; {message}; no subscription or focus policy was registered.");return 0;
        }
        if(args.Length==2 && args[0]=="--render-preview") {
            var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            ThemeSupport.FollowSystem(app);
            try { Render(Path.GetFullPath(args[1]));return 0; }
            catch(Exception e) {Console.Error.WriteLine($"Render failed: {e.Message}");return 1;}
            finally {app.Shutdown();}
        }
        if(args.Length>0 && !(args.Length==1 && args[0]=="--pilot")) {Console.Error.WriteLine("Uso: ApolloOverlay.exe [--pilot | --render-preview DIRETÓRIO | --probe-guide]");return 2;}
        var application=new Application {ShutdownMode=ShutdownMode.OnLastWindowClose};
        ThemeSupport.FollowSystem(application);
        return application.Run(new MainWindow(args.Length==1));
    }
    private static void Render(string output)
    {
        Directory.CreateDirectory(output);
        var reports=new List<RenderReport>();
        var cases=new List<(int Width,int Height,string State)>{(1280,720,"home"),(1920,1080,"home"),(3840,2160,"home")};
        foreach(var state in new[]{"apps","system","details","confirmation","long-error","steam-unavailable","audio-unavailable","integrations","empty-apps","pilot-warning"})cases.Add((1280,720,state));
        cases.Add((440,684,"pilot-layout"));
        foreach(var light in new[]{false,true})foreach(var (width,height,state) in cases) {
            ThemeSupport.ForceForValidation(Application.Current,light);
            var window=new MainWindow(pilotRequested:state=="pilot-warning",renderOnly:true);window.SetRenderState(state);
            var visual=(FrameworkElement)window.Content;
            visual.Measure(new Size(width,height));visual.Arrange(new Rect(0,0,width,height));visual.UpdateLayout();
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            var palette=light ? "light" : "dark";
            var name=$"preview-{palette}-{state}-{width}x{height}.png";
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var stream=File.Create(Path.Combine(output,name))) encoder.Save(stream);
            var rows=(Grid)window.FindName("Rows");
            var status=(TextBlock)window.FindName("StatusLabel");
            var sidebar=(Border)window.FindName("Sidebar");
            var bounds=sidebar.TransformToAncestor(visual).TransformBounds(new Rect(0,0,sidebar.ActualWidth,sidebar.ActualHeight));
            if(rows.Children.Count==0 || visual.ActualWidth!=width || visual.ActualHeight!=height || bounds.Left<0 || bounds.Top<0 || bounds.Right>width+.1 || bounds.Bottom>height+.1 || (state!="pilot-layout" && bounds.Width>width*.4)) throw new InvalidDataException($"Sidebar bounds failed: {state} {bounds} in {width}x{height}.");
            var measuredStatus=new TextBlock {Text=status.Text,FontFamily=status.FontFamily,FontSize=status.FontSize,LineHeight=18,LineStackingStrategy=LineStackingStrategy.BlockLineHeight,TextWrapping=TextWrapping.Wrap};measuredStatus.Measure(new Size(status.ActualWidth,double.PositiveInfinity));
            if(measuredStatus.DesiredSize.Height>54.1)throw new InvalidDataException($"Status is clipped in {state}: {measuredStatus.DesiredSize.Height}; {status.Text}");
            foreach(var element in rows.Children)if(element is Button row && ((FrameworkElement)row.Content).DesiredSize.Height>row.ActualHeight-12.1)throw new InvalidDataException($"Action content clipped in {state}.");
            var placement=SidebarLayout.Place(new("sample",0,0,width,height,true));
            reports.Add(new(name,width,height,state,palette,rows.Children.Count,status.ActualHeight,bounds.X,bounds.Y,bounds.Width,bounds.Height,placement,"synthetic/offscreen",false,0));
            window.Close();
        }
        File.WriteAllText(Path.Combine(output,"layout-report.json"),JsonSerializer.Serialize(reports.ToArray(),ReportJson.Default.RenderReportArray));
        ThemeSupport.FollowSystem(Application.Current);
        Console.WriteLine($"Rendered {reports.Count} actual WPF preview states. No window shown, no external actions, no global hooks.");
    }
}
public sealed record RenderReport(string File,int Width,int Height,string State,string Palette,int RowCount,double StatusHeight,double SidebarX,double SidebarY,double SidebarWidth,double SidebarHeight,SidebarBounds PilotPlacement,string Mode,bool NativeWindowShown,int ExternalActions);
[System.Text.Json.Serialization.JsonSerializable(typeof(RenderReport[]))] internal partial class ReportJson : System.Text.Json.Serialization.JsonSerializerContext;

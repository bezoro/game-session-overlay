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
            try { Render(Path.GetFullPath(args[1]));return 0; }
            catch(Exception e) {Console.Error.WriteLine($"Render failed: {e.Message}");return 1;}
            finally {app.Shutdown();}
        }
        if(args.Length>0 && !(args.Length==1 && args[0]=="--pilot")) {Console.Error.WriteLine("Uso: ApolloOverlay.exe [--pilot | --render-preview DIRETÓRIO | --probe-guide]");return 2;}
        var application=new Application {ShutdownMode=ShutdownMode.OnLastWindowClose};
        return application.Run(new MainWindow(args.Length==1));
    }
    private static void Render(string output)
    {
        Directory.CreateDirectory(output);
        var reports=new List<RenderReport>();
        foreach(var (width,height,longTitle) in new[]{(1280,720,false),(1920,1080,false),(3840,2160,false),(1280,720,true)}) {
            var window=new MainWindow(renderOnly:true);window.SetRenderState(longTitle);
            var visual=(FrameworkElement)window.Content;
            visual.Measure(new Size(width,height));visual.Arrange(new Rect(0,0,width,height));visual.UpdateLayout();
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            var name=$"preview-{width}x{height}{(longTitle ? "-long-title" : "")}.png";
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var stream=File.Create(Path.Combine(output,name))) encoder.Save(stream);
            var rows=(StackPanel)window.FindName("Rows");
            var status=(TextBlock)window.FindName("StatusLabel");
            if(rows.Children.Count!=6 || visual.ActualWidth!=width || visual.ActualHeight!=height || status.ActualHeight>50.1) throw new InvalidDataException($"Preview layout acceptance failed: rows={rows.Children.Count}; visual={visual.ActualWidth}x{visual.ActualHeight}; expected={width}x{height}; status={status.ActualHeight}.");
            foreach(Button row in rows.Children) if(((FrameworkElement)row.Content).DesiredSize.Height>row.ActualHeight-14.0) throw new InvalidDataException("Row content is clipped.");
            reports.Add(new(name,width,height,longTitle,rows.Children.Count,status.ActualHeight,"synthetic/offscreen",false,0));
            window.Close();
        }
        File.WriteAllText(Path.Combine(output,"layout-report.json"),JsonSerializer.Serialize(reports.ToArray(),ReportJson.Default.RenderReportArray));
        Console.WriteLine("Rendered 4 actual WPF preview states. No window shown, no external actions, no global hooks.");
    }
}
public sealed record RenderReport(string File,int Width,int Height,bool LongTitle,int RowCount,double StatusHeight,string Mode,bool NativeWindowShown,int ExternalActions);
[System.Text.Json.Serialization.JsonSerializable(typeof(RenderReport[]))] internal partial class ReportJson : System.Text.Json.Serialization.JsonSerializerContext;

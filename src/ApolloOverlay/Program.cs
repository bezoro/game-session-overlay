using System.Globalization;
using System.IO;
using System.Windows;

namespace ApolloOverlay;

public static class Program
{
 internal const string AlreadyRunningMessage="A overlay já está em execução. Dê duplo clique no ícone da bandeja ou escolha Abrir overlay no menu desse ícone.";
 [STAThread]public static int Main(string[] args)
 {
  CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("pt-BR");
  if(args.Length==2&&args[0] is "--capture-native" or "--native-check" or "--capture-alpha"){
   var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(app);var code=1;app.Startup+=async(_,_)=>{code=await NativeValidation.Execute(Path.GetFullPath(args[1]),args[0]!="--native-check",args[0]=="--capture-alpha");app.Shutdown(code);};app.Run();return code;
  }
  if(args.Length>1||args.Length==1&&args[0] is not ("--show" or "--preview")){MessageBox.Show("Uso: ApolloOverlay.exe [--show | --preview]","Bezoro");return 2;}
  using var singleton=new Mutex(true,@"Local\Bezoro.GameSessionOverlay",out var created);
  if(!created){MessageBox.Show(AlreadyRunningMessage,"Bezoro");return 3;}
  var application=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(application);
  var preview=args.Contains("--preview");var window=new MainWindow(platform:preview?new PreviewPlatform():null);application.MainWindow=window;window.Closed+=(_,_)=>application.Shutdown();application.Startup+=(_,_)=>{if(args.Contains("--show")||preview)window.OpenPanel();};return application.Run();
 }
}

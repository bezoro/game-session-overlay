using System.Globalization;
using System.IO;
using System.Windows;
using ApolloOverlay.Automation;

namespace ApolloOverlay;

public static class Program
{
 internal const string AlreadyRunningMessage="A overlay já está em execução. Dê duplo clique no ícone da bandeja ou escolha Abrir overlay no menu desse ícone.";
 [STAThread]public static int Main(string[] args)
 {
  CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("pt-BR");
  if(ManagedInstall.TryDispatch(args,out var managedExit))return managedExit;
  if(args.Length==2&&args[0] is "--capture-native" or "--native-check" or "--capture-alpha"){
   var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(app);var code=1;app.Startup+=async(_,_)=>{code=await NativeValidation.Execute(Path.GetFullPath(args[1]),args[0]!="--native-check",args[0]=="--capture-alpha");app.Shutdown(code);};app.Run();return code;
  }
  string? bridge=null;var selfShutdown=false;var show=false;var preview=false;var startup=false;
  for(var i=0;i<args.Length;i++)switch(args[i]){case "--show":show=true;break;case "--preview":preview=true;break;case "--startup":startup=true;break;case "--automation-bridge":if(++i>=args.Length)return 2;bridge=args[i];break;case "--allow-self-shutdown":selfShutdown=true;break;default:MessageBox.Show("Uso: ApolloOverlay.exe [--show | --preview] [--automation-bridge PID:START:HWND --allow-self-shutdown]","Bezoro");return 2;}
  if(selfShutdown&&bridge is null||bridge is not null&&(!AutomationPipeSecurity.Elevated()||preview)){MessageBox.Show("Ponte requer execução elevada explicitamente autorizada e alvo fixo. Nada foi elevado automaticamente.","Bezoro");return 2;}
  using var singleton=new Mutex(true,@"Local\Bezoro.GameSessionOverlay",out var created);
  if(!created){if(!startup)MessageBox.Show(AlreadyRunningMessage,"Bezoro");return startup?0:3;}
  if(!preview&&bridge is null&&!ManagedInstall.IsManagedChild&&!StartupRegistration.TryEnsure(Environment.ProcessPath))
      new LocalDiagnostics().Log("startup-registration",ResultKind.Unavailable);
  var application=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};ThemeSupport.FollowSystem(application);
  var window=new MainWindow(platform:preview?new PreviewPlatform():null,diagnostics:ManagedInstall.DiagnosticsRootOverride is {} proofRoot?new LocalDiagnostics(proofRoot):null);application.MainWindow=window;window.Closed+=(_,_)=>application.Shutdown();application.Startup+=async(_,_)=>{try{AutomationGrant? grant=null;if(bridge is not null){var parts=bridge.Split(':');if(parts.Length!=3)throw new InvalidOperationException("Alvo requer PID:START:HWND.");grant=window.ResolveAutomationGrant(int.Parse(parts[0],CultureInfo.InvariantCulture),long.Parse(parts[1],CultureInfo.InvariantCulture),long.Parse(parts[2],CultureInfo.InvariantCulture),selfShutdown);}await window.StartAutomation(grant:grant);ManagedInstall.MarkHealthy();window.ScheduleUpdateNotice();if(show||preview)window.OpenPanel();}catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException){if(ManagedInstall.IsManagedChild){var failureRoot=ManagedInstall.DiagnosticsRootOverride??new LocalDiagnostics().Root;try{Directory.CreateDirectory(failureRoot);File.AppendAllText(Path.Combine(failureRoot,"startup-error.log"),error.GetType().Name+": "+error.Message+Environment.NewLine);}catch(IOException){}}else MessageBox.Show("Automação indisponível: "+error.Message,"Bezoro");application.Shutdown(1);}};return application.Run();
 }
}


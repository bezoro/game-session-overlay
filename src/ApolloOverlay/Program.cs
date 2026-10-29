using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using ApolloOverlay.Automation;

namespace ApolloOverlay;

public static class Program
{
    internal const string AlreadyRunningMessage = "A overlay já está em execução. Dê duplo clique no ícone da bandeja ou escolha Abrir overlay no menu desse ícone.";
    [STAThread]
    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        var proofRoot = ReadProofArguments(ref args);
        if (proofRoot is not null)
            VelopackInstallation.ConfigureDataRootForTests(proofRoot);
        // Official installer/update hooks must run before WPF, singleton or strict argument parsing.
        VelopackInstallation.Initialize(args, () => StartupRegistration.TryRemoveMatching(VelopackInstallation.StartupExecutable));
        if (args.Length == 2 && args[0] is "--capture-native" or "--native-check" or "--capture-alpha")
        {
            var app = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            ThemeSupport.FollowSystem(app);
            var code = 1;
            app.Startup += async (_, _) =>
            {
                code = await NativeValidation.Execute(Path.GetFullPath(args[1]), args[0] != "--native-check", args[0] == "--capture-alpha");
                app.Shutdown(code);
            };
            app.Run();
            return code;
        }

        string? bridge = null;
        var selfShutdown = false;
        var show = false;
        var preview = false;
        var startup = false;
        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--show":
                    show = true;
                    break;
                case "--preview":
                    preview = true;
                    break;
                case "--startup":
                    startup = true;
                    break;
                case "--automation-bridge":
                    if (++i >= args.Length)
                        return 2;
                    bridge = args[i];
                    break;
                case "--allow-self-shutdown":
                    selfShutdown = true;
                    break;
                default:
                    MessageBox.Show("Uso: ApolloOverlay.exe [--show | --preview] [--automation-bridge PID:START:HWND --allow-self-shutdown]", "Game Session Overlay");
                    return 2;
            }

        if (selfShutdown && bridge is null || bridge is not null && (!AutomationPipeSecurity.Elevated() || preview))
        {
            MessageBox.Show("Ponte requer execução elevada explicitamente autorizada e alvo fixo. Nada foi elevado automaticamente.", "Game Session Overlay");
            return 2;
        }

        using var singleton = new Mutex(true, @"Local\Bezoro.GameSessionOverlay", out var created);
        if (!created)
        {
            if (!startup)
                MessageBox.Show(AlreadyRunningMessage, "Game Session Overlay");
            return startup ? 0 : 3;
        }

        if (!preview
            && bridge is null
            && proofRoot is null
            && !StartupRegistration.TryEnsure(VelopackInstallation.IsInstalled ? VelopackInstallation.StartupExecutable : Environment.ProcessPath))
            new LocalDiagnostics().Log("startup-registration", ResultKind.Unavailable);
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        ThemeSupport.FollowSystem(application);
        var window = new MainWindow(platform: preview ? new PreviewPlatform() : null, diagnostics: proofRoot is null ? null : new LocalDiagnostics(proofRoot));
        application.MainWindow = window;
        window.Closed += (_, _) => application.Shutdown();
        application.Startup += async (_, _) =>
        {
            try
            {
                AutomationGrant? grant = null;
                if (bridge is not null)
                {
                    var parts = bridge.Split(':');
                    if (parts.Length != 3)
                        throw new InvalidOperationException("Alvo requer PID:START:HWND.");
                    grant = window.ResolveAutomationGrant(int.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture), long.Parse(parts[2], CultureInfo.InvariantCulture), selfShutdown);
                }

                await window.StartAutomation(grant: grant);
                try
                {
                    await VelopackInstallation.RecordCurrentAsync();
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                {
                    window.ShowUpdateHistoryWarning();
                }

                window.ScheduleUpdateNotice();
                if (proofRoot is not null)
                {
                    Directory.CreateDirectory(proofRoot);
                    File.WriteAllText(Path.Combine(proofRoot, "ready.json"), JsonSerializer.Serialize(new
                    {
                        pid = Environment.ProcessId,
                        started = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                        version = AppBuild.Current.Version,
                        buildId = AppBuild.Current.BuildId,
                        commit = AppBuild.Current.Commit,
                        installed = VelopackInstallation.IsInstalled,
                        root = Path.GetDirectoryName(VelopackInstallation.StartupExecutable)
                    }));
                }

                if (show || preview || VelopackInstallation.FirstRun)
                    window.OpenPanel();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
                if (proofRoot is not null)
                {
                    try
                    {
                        File.AppendAllText(Path.Combine(proofRoot, "startup-error.log"), error.GetType().Name + ": " + error.Message);
                    }
                    catch (IOException)
                    {
                    }
                }
                else
                    MessageBox.Show("Não foi possível iniciar a overlay: " + error.Message, "Game Session Overlay");
                application.Shutdown(1);
            }
        };
        return application.Run();
    }

    private static string? ReadProofArguments(ref string[] args)
    {
        var index = Array.IndexOf(args, "--velopack-proof");
        if (index < 0)
            return null;
        var temporary = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || temporary is null || index + 1 >= args.Length)
            throw new InvalidOperationException("Este modo é exclusivo do teste isolado de instalação no CI.");
        var root = Path.GetFullPath(args[index + 1]);
        if (!string.Equals(Path.GetDirectoryName(root), Path.GetFullPath(temporary).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(Path.GetFileName(root), "^overlay-velopack-proof-[a-f0-9]{32}$"))
            throw new InvalidOperationException("Diretório de teste inválido.");
        args = args.Where((_, i) => i != index && i != index + 1).ToArray();
        return root;
    }
}

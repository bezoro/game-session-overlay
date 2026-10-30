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
    internal const string AlreadyRunningMessage = "The overlay is already running. Double-click its tray icon or choose Open overlay from the tray menu.";
    [STAThread]
    public static int Main(string[] args)
    {
        // This private child performs no installation hooks, WPF or singleton work.
        if (args is [IsolatedWindowActivation.HostArgument, var activation])
            return IsolatedWindowActivation.RunHost(activation);
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        var proofRoot = ReadProofArguments(ref args);
        if (proofRoot is not null)
            VelopackInstallation.ConfigureDataRootForTests(proofRoot);
        // Official installer/update hooks must run before WPF, singleton or strict argument parsing.
        VelopackInstallation.Initialize(args, RemoveInstalledIntegration, EnsureInstalledIntegration, EnsureInstalledIntegration);
        if (AdministratorRestart.Bootstrap(ref args) is { } restartCode)
            return restartCode;
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
                case "--hidden":
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
                    MessageBox.Show("Usage: ApolloOverlay.exe [--show | --startup | --hidden | --preview] [--automation-bridge PID:START:HWND --allow-self-shutdown]", "Game Session Overlay");
                    return 2;
            }

        if (selfShutdown && bridge is null || bridge is not null && (!AutomationPipeSecurity.Elevated() || preview))
        {
            MessageBox.Show("The bridge requires explicitly authorized elevation and a fixed target. Nothing was elevated automatically.", "Game Session Overlay");
            return 2;
        }

        // An explicitly approved task is reusable without asking for UAC on each shell launch.
        // Its fixed action starts hidden; only this explicit launch asks it to open the panel.
        if (!startup && !preview && bridge is null && proofRoot is null && VelopackInstallation.IsInstalled
            && ScheduledStartup.TryRunElevated(VelopackInstallation.StartupExecutable))
        {
            if (SingleInstanceActivation.TryOpenExisting(timeoutMs: 10000))
                return 0;
            MessageBox.Show("Windows started the configured administrator session, but the panel did not become available. Try Open overlay from its tray icon.", "Game Session Overlay");
            return 3;
        }

        using var singleton = SingleInstanceActivation.AcquireMutex(out var created);
        if (!created)
        {
            if (!startup && SingleInstanceActivation.TryOpenExisting())
                return 0;
            if (!startup)
                MessageBox.Show(AlreadyRunningMessage, "Game Session Overlay");
            return startup ? 0 : 3;
        }

        if (!preview
            && bridge is null
            && proofRoot is null
            && !StartupRegistration.TryEnsure(VelopackInstallation.IsInstalled ? VelopackInstallation.StartupExecutable : Environment.ProcessPath))
            new LocalDiagnostics().Log("startup-registration", ResultKind.Unavailable);
        if (!preview && bridge is null && proofRoot is null && VelopackInstallation.IsInstalled
            && !StartMenuRegistration.TryEnsure(VelopackInstallation.StartupExecutable))
            new LocalDiagnostics().Log("start-menu-registration", ResultKind.Unavailable);
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
                        throw new InvalidOperationException("Target requires PID:START:HWND.");
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

                if (show || preview || !startup && bridge is null)
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
                    MessageBox.Show("Could not start the overlay: " + error.Message, "Game Session Overlay");
                application.Shutdown(1);
            }
        };
        return application.Run();
    }

    private static void EnsureInstalledIntegration()
    {
        if (!VelopackInstallation.IsInstalled)
            return;
        var executable = VelopackInstallation.StartupExecutable;
        var startup = StartupRegistration.TryEnsure(executable);
        var shortcut = StartMenuRegistration.TryEnsure(executable);
        if (!startup || !shortcut)
            throw new InvalidOperationException($"Windows integration could not be registered. Startup: {startup}; Start menu: {shortcut}.");
    }

    private static void RemoveInstalledIntegration()
    {
        var executable = VelopackInstallation.StartupExecutable;
        StartupRegistration.TryRemoveMatching(executable);
        StartMenuRegistration.TryRemoveMatching(executable);
    }

    private static string? ReadProofArguments(ref string[] args)
    {
        var index = Array.IndexOf(args, "--velopack-proof");
        if (index < 0)
            return null;
        var temporary = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || temporary is null || index + 1 >= args.Length)
            throw new InvalidOperationException("This mode is reserved for the isolated CI installation test.");
        var root = Path.GetFullPath(args[index + 1]);
        if (!string.Equals(Path.GetDirectoryName(root), Path.GetFullPath(temporary).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(Path.GetFileName(root), "^overlay-velopack-proof-[a-f0-9]{32}$"))
            throw new InvalidOperationException("Invalid test directory.");
        args = args.Where((_, i) => i != index && i != index + 1).ToArray();
        return root;
    }
}

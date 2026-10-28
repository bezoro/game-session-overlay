using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace ApolloOverlay;

internal sealed record InstallPlan(string Nonce, string Kind, string Source, string BuildId, string Version, string Commit,
    int ParentPid, long ParentStarted, string ParentExecutable, Dictionary<string, string> Files);

/// <summary>Small stable bootstrap plus replaceable, immutable application versions.</summary>
public static class ManagedInstall
{
    private static string? ProofRoot
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("BEZORO_INSTALL_PROOF_ROOT");
            if (root is null) return null;
            var temporary = Environment.GetEnvironmentVariable("RUNNER_TEMP");
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || temporary is null ||
                !SamePath(Path.GetDirectoryName(Path.GetFullPath(root))!, temporary) ||
                !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(root), "^overlay-install-proof-[a-f0-9]{32}$"))
                throw new InvalidDataException("A prova de instalação exige um diretório isolado do runner de CI.");
            return Path.GetFullPath(root);
        }
    }
    public static string DefaultRoot => ProofRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GameSessionOverlay");
    public static string? DiagnosticsRootOverride => ProofRoot is {} root ? Path.Combine(root, "user-data") : null;
    private static string SettingsPath => Path.Combine(DiagnosticsRootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "GameSessionOverlay"), "settings.json");
    private const string RootVariable = "BEZORO_MANAGED_ROOT";
    private const string TicketVariable = "BEZORO_MANAGED_HEALTH";
    private static string? healthTicket;
    public static bool IsManagedChild { get; private set; }
    public static string StartupExecutable => Path.Combine(DefaultRoot, "ApolloOverlay.exe");
    public static bool IsInstalled => File.Exists(Path.Combine(DefaultRoot, "install-state.json"));
    public static IReadOnlyList<InstalledBuild> History() => IsInstalled ? Store().History() : [];
    private static ManagedInstallStore Store() => new(DefaultRoot, SettingsPath);
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    /// <summary>Call before creating WPF or acquiring the application's singleton mutex.</summary>
    public static bool TryDispatch(string[] args, out int code)
    {
        code = 0;
        try
        {
            if (args is ["--install"])
            {
                if (AppRunning()) throw new InvalidOperationException("Encerre a overlay pela bandeja antes de instalar este pacote.");
                InstallCurrentPackage();
                return true;
            }
            if (args is ["--managed-proof-apply", var source])
            {
                if (ProofRoot is null) throw new InvalidDataException("Este modo exige o runner isolado de CI.");
                var build = ManagedInstallStore.Identity(source);
                PrepareTransition(source, build.BuildId, build.Version, build.Commit); return true;
            }
            if (args is ["--apply-update", var path]) { code = ApplyPlan(path); return true; }
            if (args.Any(a => a is "--install" or "--apply-update")) throw new InvalidDataException("Argumentos de instalação inválidos.");
            var executable = Environment.ProcessPath;
            if (executable is not null && SamePath(executable, StartupExecutable) && IsInstalled)
            {
                if (args.Any(a => a is not ("--show" or "--startup"))) throw new InvalidDataException("Argumentos inválidos para a instalação gerenciada.");
                if (AppRunning())
                {
                    if (args.Contains("--show")) MessageBox.Show(Program.AlreadyRunningMessage, "Game Session Overlay");
                    return true;
                }
                var store = Store(); using var lease = store.Acquire();
                var state = store.RecoverPending();
                if (state.Current is not {} current) throw new InvalidDataException("A instalação está incompleta. Execute Instalar overlay novamente a partir do pacote.");
                var build = state.Builds.Single(b => b.BuildId == current);
                if (!LaunchHealthy(store, build, args.Contains("--show")))
                {
                    if (state.Previous is not {} previous || !Activate(store, state.Builds.Single(b => b.BuildId == previous), true, true))
                        throw new InvalidOperationException("A overlay não iniciou; abra o pacote para reparar a instalação.");
                    WriteNotice("A versão anterior foi restaurada porque a versão ativa não iniciou.");
                }
                return true;
            }
            var root = Environment.GetEnvironmentVariable(RootVariable);
            var ticket = Environment.GetEnvironmentVariable(TicketVariable);
            if (root is null && ticket is null && executable is not null && executable.StartsWith(Path.Combine(DefaultRoot, "versions") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                LaunchBootstrap(args.Contains("--show")); return true;
            }
            if (root is not null || ticket is not null)
            {
                if (root is null || ticket is null || !SamePath(root, DefaultRoot) || !Guid.TryParseExact(ticket, "N", out _))
                    throw new InvalidDataException("Contexto de instalação inválido.");
                var store = Store(); var state = store.Load();
                if (state.Current is null || executable is null || !SamePath(executable, Path.Combine(store.VersionPath(state.Current), "ApolloOverlay.exe")))
                    throw new InvalidDataException("A execução não corresponde à versão ativa.");
                var retained = state.Builds.Single(b => b.BuildId == state.Current);
                ValidateCurrentIdentity(ManagedInstallStore.Identity(AppContext.BaseDirectory));
                if (retained.BuildId != AppBuild.Current.BuildId || retained.Version != AppBuild.Current.Version || retained.Commit != AppBuild.Current.Commit)
                    throw new InvalidDataException("A identidade compilada não corresponde à versão escolhida.");
                IsManagedChild = true; healthTicket = ticket;
                Environment.SetEnvironmentVariable(RootVariable, null); Environment.SetEnvironmentVariable(TicketVariable, null);
            }
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or ArgumentException)
        {
            if (Environment.GetEnvironmentVariable("BEZORO_INSTALL_PROOF_ROOT") is not null)
            {
                Console.Error.WriteLine("INSTALL-ERROR " + error.Message);
                try { if (ProofRoot is {} proof && Directory.Exists(proof)) File.AppendAllText(Path.Combine(proof, "install-proof.log"), DateTimeOffset.UtcNow.ToString("O") + " " + error.GetType().Name + " " + error.Message + Environment.NewLine); }
                catch (Exception diagnostic) when (diagnostic is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
            else if (args.FirstOrDefault() == "--apply-update" || Environment.GetEnvironmentVariable(RootVariable) is not null)
            {
                try { WriteNotice("Não foi possível iniciar a versão escolhida. " + error.Message); }
                catch (Exception diagnostic) when (diagnostic is IOException or UnauthorizedAccessException) { }
            }
            else MessageBox.Show(error.Message, "Game Session Overlay · Instalação", MessageBoxButton.OK, MessageBoxImage.Error);
            code = 1; return true;
        }
    }

    public static void MarkHealthy()
    {
        if (!IsManagedChild || healthTicket is not {} ticket) return;
        var path = Path.Combine(DefaultRoot, "health", ticket + ".ready");
        ManagedInstallStore.SafeDirectory(DefaultRoot, "health");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream)) { writer.Write(Environment.ProcessId); writer.Flush(); stream.Flush(true); }
        healthTicket = null;
    }

    public static string? ReadNotice()
    {
        var path = Path.Combine(DefaultRoot, "last-update.txt");
        if (!File.Exists(path)) return null;
        ManagedInstallStore.RejectLink(path);
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Registro de atualização inválido.");
        var claimed = Path.Combine(DefaultRoot, ".notice-" + Guid.NewGuid().ToString("N"));
        try { File.Move(path, claimed); }
        catch (FileNotFoundException) { return null; }
        try
        {
            ManagedInstallStore.RejectLink(claimed);
            if (new FileInfo(claimed).Length > 16384) throw new InvalidDataException("Registro de atualização inválido.");
            return File.ReadAllText(claimed);
        }
        finally { File.Delete(claimed); }
    }
    private static void WriteNotice(string text)
    {
        Directory.CreateDirectory(DefaultRoot);
        var path = Path.Combine(DefaultRoot, "last-update.txt");
        var temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, text); File.Move(temporary, path, true);
    }

    /// <summary>Returns only once the helper has claimed the operation. The caller can then close normally.</summary>
    public static void PrepareTransition(string stagedDirectory, string buildId, string version, string commit, bool rollback = false)
    {
        var identity = ManagedInstallStore.Identity(stagedDirectory);
        if (SamePath(stagedDirectory, AppContext.BaseDirectory)) ValidateCurrentIdentity(identity);
        if (identity.BuildId != buildId || identity.Version != version || identity.Commit != commit) throw new InvalidDataException("Pacote diferente da versão escolhida.");
        var inventory = ManagedInstallStore.Inventory(stagedDirectory);
        Prepare(new(Guid.NewGuid().ToString("N"), rollback ? "rollback" : "update", Path.GetFullPath(stagedDirectory), buildId, version, commit,
            Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, Environment.ProcessPath ?? throw new InvalidOperationException("Executável indisponível."), inventory));
    }
    public static void PrepareRollback(string buildId)
    {
        var store = Store(); var build = store.History().SingleOrDefault(b => b.BuildId == buildId) ?? throw new InvalidDataException("Versão anterior não está instalada.");
        if (build.LastStartFailed) throw new InvalidOperationException("Esta versão falhou ao iniciar e não pode ser usada para reversão. Baixe uma versão validada novamente.");
        PrepareTransition(store.VersionPath(buildId), build.BuildId, build.Version, build.Commit, true);
    }
    public static void PrepareInstallCurrent()
    {
        var identity = ManagedInstallStore.Identity(AppContext.BaseDirectory);
        ValidateCurrentIdentity(identity);
        var source = AppContext.BaseDirectory;
        if (SamePath(source, DefaultRoot))
        {
            var marker = Path.Combine(DefaultRoot, "bootstrap-files.json");
            var files = File.Exists(marker) ? ManagedInstallStore.Read<Dictionary<string, string>>(marker) : ManagedInstallStore.Inventory(source);
            var payload = Path.Combine(Path.GetDirectoryName(SettingsPath)!, "update-cache", Guid.NewGuid().ToString("N"), "payload");
            Directory.CreateDirectory(payload); ManagedInstallStore.CopyFiles(source, payload, files); ManagedInstallStore.CheckInventory(payload, files);
            source = payload;
        }
        PrepareTransition(source, identity.BuildId, identity.Version, identity.Commit);
    }
    private static void Prepare(InstallPlan plan)
    {
        if (!string.Equals(Path.GetFileName(Environment.ProcessPath), "ApolloOverlay.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use o executável publicado para instalar ou atualizar a overlay.");
        var store = Store(); ManagedInstallStore.SafeDirectory(store.Root, "pending");
        var path = Path.Combine(store.Root, "pending", plan.Nonce + ".json");
        ManagedInstallStore.AtomicWrite(path, plan);
        var helper = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        helper.ArgumentList.Add("--apply-update"); helper.ArgumentList.Add(path);
        helper.Environment.Remove(RootVariable); helper.Environment.Remove(TicketVariable);
        using var process = Process.Start(helper) ?? throw new InvalidOperationException("Não foi possível iniciar o atualizador.");
        var watch = Stopwatch.StartNew(); var claimed = path + ".claimed";
        while (watch.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (File.Exists(claimed)) return;
            if (process.HasExited) { File.Delete(path); throw new InvalidOperationException("O atualizador recusou a operação. A versão atual continua aberta."); }
            Thread.Sleep(25);
        }
        if (File.Exists(claimed)) return;
        File.WriteAllText(path + ".cancelled", plan.Nonce);
        throw new TimeoutException("O atualizador não respondeu. A overlay atual continua aberta; tente novamente.");
    }
    private static int ApplyPlan(string path)
    {
        var store = Store();
        var file = Path.GetFileName(path);
        if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _) || !SamePath(path, Path.Combine(store.Root, "pending", file)))
            throw new InvalidDataException("Solicitação de atualização fora da instalação.");
        var plan = ManagedInstallStore.Read<InstallPlan>(path);
        if (plan.Nonce != Path.GetFileNameWithoutExtension(file) || plan.Kind is not ("update" or "rollback") || plan.Files is null)
            throw new InvalidDataException("Solicitação de atualização inválida.");
        using var lease = store.Acquire();
        using var parent = Process.GetProcessById(plan.ParentPid);
        if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStarted || !SamePath(parent.MainModule?.FileName ?? "", plan.ParentExecutable) || !SamePath(Environment.ProcessPath!, plan.ParentExecutable))
            throw new InvalidDataException("A instância que pediu a atualização mudou.");
        ManagedInstallStore.CheckInventory(plan.Source, plan.Files);
        if (File.Exists(path + ".cancelled")) { File.Delete(path); File.Delete(path + ".cancelled"); return 2; }
        File.WriteAllText(path + ".claimed", plan.Nonce);
        if (!parent.WaitForExit(20000))
        {
            WriteNotice("Atualização cancelada porque a overlay continuou aberta. A versão ativa foi preservada.");
            File.Delete(path); File.Delete(path + ".claimed"); File.Delete(path + ".cancelled");
            return 2;
        }
        if (File.Exists(path + ".cancelled")) { File.Delete(path); File.Delete(path + ".claimed"); File.Delete(path + ".cancelled"); return 2; }
        var previous = store.Load();
        var imported = false;
        try
        {
            EnsureBootstrap(plan.Source, plan.Files);
            var build = store.Import(plan.Source, plan.BuildId, plan.Version, plan.Commit, plan.Files);
            imported = true;
            if (!Activate(store, build, plan.Kind == "rollback", true))
            {
                WriteNotice("A nova versão não iniciou. A versão e os ajustes anteriores foram restaurados.");
                if (previous.Current is {} old) LaunchHealthy(store, previous.Builds.Single(b => b.BuildId == old), true);
                else StartPortable(plan.ParentExecutable);
                return 1;
            }
            var warning = ConfigureInstallation();
            WriteNotice((plan.Kind == "rollback" ? "Versão anterior restaurada. Seus ajustes compatíveis foram recuperados." : "Atualização concluída. A versão anterior continua disponível no histórico.") + (warning.Length == 0 ? "" : " " + warning));
            return 0;
        }
        catch
        {
            var recovered = store.RecoverPending();
            if (recovered.Current is {} old) LaunchHealthy(store, recovered.Builds.Single(b => b.BuildId == old), true);
            else StartPortable(plan.ParentExecutable);
            throw;
        }
        finally
        {
            File.Delete(path); File.Delete(path + ".claimed"); File.Delete(path + ".cancelled");
            if (imported) ConsumeCache(plan.Source);
        }
    }
    private static bool Activate(ManagedInstallStore store, InstalledBuild build, bool rollback, bool show) => store.Activate(build, rollback, target => LaunchHealthy(store, target, show));
    private static bool LaunchHealthy(ManagedInstallStore store, InstalledBuild build, bool show)
    {
        var payload = store.Payload(build.BuildId); ManagedInstallStore.CheckInventory(store.VersionPath(build.BuildId), payload.Files);
        var ticket = Guid.NewGuid().ToString("N"); var folder = Path.Combine(store.Root, "health"); Directory.CreateDirectory(folder); ManagedInstallStore.RejectLink(folder);
        var ready = Path.Combine(folder, ticket + ".ready");
        var start = new ProcessStartInfo(Path.Combine(store.VersionPath(build.BuildId), "ApolloOverlay.exe")) { UseShellExecute = false, WorkingDirectory = store.VersionPath(build.BuildId) };
        start.ArgumentList.Add(show ? "--show" : "--startup"); start.Environment[RootVariable] = store.Root; start.Environment[TicketVariable] = ticket;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Falha ao iniciar a versão escolhida.");
        var started = process.StartTime.ToUniversalTime().Ticks;
        var healthy = false;
        try
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(25))
            {
                if (process.HasExited) return false;
                if (File.Exists(ready) && File.ReadAllText(ready) == process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    // A ready event is not enough if the child immediately crashes.
                    healthy = !process.WaitForExit(2000);
                    return healthy;
                }
                Thread.Sleep(50);
            }
            return false;
        }
        finally
        {
            if (File.Exists(ready)) File.Delete(ready);
            // Only the child created by this bootstrap can be terminated after failed health.
            if (!healthy && !process.HasExited && watchExpired(process, started))
            { process.Kill(entireProcessTree: false); process.WaitForExit(5000); }
        }
        static bool watchExpired(Process process, long started) => process.StartTime.ToUniversalTime().Ticks == started;
    }
    private static void EnsureBootstrap(string source, IReadOnlyDictionary<string, string> inventory)
    {
        var marker = Path.Combine(DefaultRoot, "bootstrap-files.json");
        if (File.Exists(marker))
        {
            var retained = ManagedInstallStore.Read<Dictionary<string, string>>(marker);
            if (retained.Count > 5000 || new[] { "ApolloOverlay.exe", "ApolloOverlay.dll", "ApolloOverlay.runtimeconfig.json", "build-info.json" }.Any(name => !retained.ContainsKey(name)))
                throw new InvalidDataException("O inventário do iniciador está incompleto. Reinstale usando o pacote original.");
            foreach (var item in retained)
            {
                var path = ManagedInstallStore.Child(DefaultRoot, item.Key); ManagedInstallStore.RejectLink(path);
                using var stream = File.OpenRead(path);
                if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), item.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("O iniciador instalado está danificado. Reinstale usando o pacote original.");
            }
            return;
        }
        // Root files are the stable bootstrap and are never overwritten by updates.
        var root = DefaultRoot; Directory.CreateDirectory(root); ManagedInstallStore.RejectLink(root);
        foreach (var file in inventory.Keys)
        {
            var target = ManagedInstallStore.Child(root, file);
            if (File.Exists(target))
            {
                using var stream = File.OpenRead(target);
                if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), inventory[file], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A pasta de instalação contém arquivos diferentes. Use uma pasta limpa para reparar.");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(ManagedInstallStore.Child(source, file), target, false);
        }
        ManagedInstallStore.AtomicWrite(marker, inventory);
    }
    private static string ConfigureInstallation()
    {
        if (ProofRoot is not null) return "Prova isolada de CI; atalhos e inicialização do usuário não foram alterados.";
        var warning = StartupRegistration.TryEnsure(StartupExecutable) ? "" : "A inicialização automática precisa ser reconfigurada.";
        if (!OperatingSystem.IsWindows()) return warning;
        try { if (MigrateElevatedTask()) warning = ""; }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or System.Security.SecurityException)
        { warning = "A tarefa de inicialização elevada precisa ser reconfigurada pelo administrador para o iniciador instalado."; }
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return warning + " O atalho do menu Iniciar não pôde ser criado.";
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(type)!;
            dynamic dynamicShell = shell;
            Directory.CreateDirectory(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
            dynamic shortcut = dynamicShell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Game Session Overlay.lnk"));
            try { shortcut.TargetPath = StartupExecutable; shortcut.Arguments = "--show"; shortcut.WorkingDirectory = DefaultRoot; shortcut.Description = "Abrir Game Session Overlay"; shortcut.IconLocation = StartupExecutable + ",0"; shortcut.Save(); }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        { warning += " O atalho do menu Iniciar não pôde ser criado."; }
        finally { if (shell is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        return warning.Trim();
    }
    private static bool MigrateElevatedTask()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null) return false;
        var service = Activator.CreateInstance(type)!;
        object? folder = null, task = null, definition = null;
        try
        {
            dynamic scheduler = service; scheduler.Connect(); folder = scheduler.GetFolder("\\");
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var name = "Bezoro Game Session Overlay - " + identity.User!.Value;
            try { task = ((dynamic)folder).GetTask(name); }
            catch (System.Runtime.InteropServices.COMException e) when (e.HResult is unchecked((int)0x80070002) or unchecked((int)0x8004130F)) { return false; }
            definition = ((dynamic)task).Definition;
            dynamic configured = definition;
            var account = (string)configured.Principal.UserId;
            if (!string.Equals(account, identity.Name, StringComparison.OrdinalIgnoreCase) && !string.Equals(account, identity.User.Value, StringComparison.OrdinalIgnoreCase) || (int)configured.Principal.LogonType != 3)
                throw new System.Runtime.InteropServices.COMException("A tarefa não pertence à sessão interativa atual.");
            if ((int)configured.Actions.Count == 1)
            {
                object existing = configured.Actions.Item(1);
                try
                {
                    dynamic value = existing;
                    if ((int)value.Type == 0 && SamePath((string)value.Path, StartupExecutable) && (string)value.Arguments == "--startup") return true;
                }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(existing); }
            }
            configured.Actions.Clear();
            dynamic action = configured.Actions.Create(0);
            try { action.Path = StartupExecutable; action.Arguments = "--startup"; action.WorkingDirectory = DefaultRoot; }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(action); }
            object registered = ((dynamic)folder).RegisterTaskDefinition(name, configured, 6, null, null, 3, null);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(registered);
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegistration.RunKey, true);
            run?.DeleteValue(StartupRegistration.RunValue, false);
            using var settings = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(StartupRegistration.SettingsKey, true);
            settings?.SetValue(StartupRegistration.MarkerValue, 1, Microsoft.Win32.RegistryValueKind.DWord);
            return true;
        }
        finally
        {
            foreach (var item in new[] { definition, task, folder, service }) if (item is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(item);
        }
    }
    private static void LaunchBootstrap(bool show)
    {
        var start = new ProcessStartInfo(StartupExecutable) { UseShellExecute = false, WorkingDirectory = DefaultRoot }; if (show) start.ArgumentList.Add("--show"); Process.Start(start)?.Dispose();
    }
    private static void StartPortable(string executable)
    {
        if (!File.Exists(executable) || SamePath(executable, StartupExecutable)) return;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false }; start.ArgumentList.Add("--show"); start.Environment.Remove(RootVariable); start.Environment.Remove(TicketVariable); Process.Start(start)?.Dispose();
    }
    private static bool AppRunning()
    {
        using var mutex = new Mutex(false, @"Local\Bezoro.GameSessionOverlay");
        try
        {
            if (!mutex.WaitOne(0)) return true;
            mutex.ReleaseMutex(); return false;
        }
        catch (AbandonedMutexException) { mutex.ReleaseMutex(); return false; }
    }
    private static void ValidateCurrentIdentity(InstallIdentity identity)
    {
        var build = AppBuild.Current;
        if (identity.BuildId != build.BuildId || identity.Version != build.Version || identity.Commit != build.Commit || identity.Channel != build.Channel)
            throw new InvalidDataException("Os metadados do pacote não correspondem à identificação compilada do aplicativo.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(build.Commit, "^[0-9a-fA-F]{40}$"))
            throw new InvalidDataException("A instalação gerenciada exige um pacote publicado pelo CI. Compilações locais não têm uma revisão publicada.");
    }
    private static void InstallCurrentPackage()
    {
        var original = AppContext.BaseDirectory;
        var identity = ManagedInstallStore.Identity(original); ValidateCurrentIdentity(identity);
        var self = SamePath(original, DefaultRoot);
        if (self && IsInstalled && Store().Load().Current is not null) { LaunchBootstrap(true); return; }
        var marker = Path.Combine(DefaultRoot, "bootstrap-files.json");
        var files = self && File.Exists(marker) ? ManagedInstallStore.Read<Dictionary<string, string>>(marker) : ManagedInstallStore.Inventory(original);
        string? copy = null;
        try
        {
            if (self)
            {
                // Adopting a package extracted directly into the destination must not copy
                // the new lock, state or versions back inside the payload recursively.
                copy = Path.Combine(Path.GetTempPath(), "overlay-install-source-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(copy); ManagedInstallStore.CopyFiles(original, copy, files); ManagedInstallStore.CheckInventory(copy, files);
            }
            var source = copy ?? original;
            var store = Store(); using var lease = store.Acquire();
            EnsureBootstrap(source, files);
            var build = store.Import(source, identity.BuildId, identity.Version, identity.Commit, files);
            if (store.Load().Current == build.BuildId) { lease.Dispose(); LaunchBootstrap(true); return; }
            if (!Activate(store, build, false, true)) throw new InvalidOperationException("A instalação não iniciou. Os arquivos e ajustes anteriores foram preservados.");
            WriteNotice("Instalação concluída. " + ConfigureInstallation());
        }
        finally { if (copy is not null && Directory.Exists(copy)) Directory.Delete(copy, true); }
    }
    private static void ConsumeCache(string source)
    {
        var cache = Path.Combine(Path.GetDirectoryName(SettingsPath)!, "update-cache");
        var folder = Path.GetDirectoryName(Path.GetFullPath(source));
        if (folder is null || Path.GetFileName(source) != "payload" || !Guid.TryParseExact(Path.GetFileName(folder), "N", out _) || !SamePath(Path.GetDirectoryName(folder)!, cache)) return;
        try
        {
            ManagedInstallStore.RejectLink(cache); ManagedInstallStore.RejectLink(folder); ManagedInstallStore.RejectLink(source);
            Directory.Delete(source, true);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }
}

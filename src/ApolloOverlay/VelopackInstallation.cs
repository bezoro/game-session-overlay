using System.IO;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace ApolloOverlay;

public sealed record PreparedVeloUpdate(VelopackAsset Asset, UpdateManager Manager, string PackagePath, bool IsRollback);

/// <summary>Application policy around the official Velopack installer and update APIs.</summary>
public static class VelopackInstallation
{
    public const string PackageId = "GameSessionOverlay";
    public const string LauncherName = "Game Session Overlay.exe";
    private static LocalUpdateHistory Retained = new();
    private static string? proofDataRoot;
    public static bool FirstRun { get; private set; }
    public static string? Notice { get; private set; }
    public static bool IsInstalled => VelopackLocator.IsCurrentSet && VelopackLocator.Current.CurrentlyInstalledVersion is not null;
    public static string? CurrentVersion => VelopackLocator.IsCurrentSet ? VelopackLocator.Current.CurrentlyInstalledVersion?.ToFullString() : null;
    public static string StartupExecutable => VelopackLocator.IsCurrentSet && VelopackLocator.Current.RootAppDir is {} root ? Path.Combine(root, LauncherName) : Environment.ProcessPath ?? "";
    public static string? PackagesDirectory => VelopackLocator.IsCurrentSet ? VelopackLocator.Current.PackagesDir : null;
    public static void ConfigureDataRootForTests(string root)
    {
        var temporary = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        var path = Path.GetFullPath(root);
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || string.IsNullOrEmpty(temporary) ||
            !path.StartsWith(Path.GetFullPath(temporary).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A prova de atualização exige dados isolados no diretório temporário do runner.");
        Retained = new(path);proofDataRoot=path;
    }
    public static void Initialize(string[]? args = null, Action? beforeUninstall = null)
    {
        var app = VelopackApp.Build().SetAutoApplyOnStartup(false).OnFirstRun(_ => FirstRun = true);
        if (beforeUninstall is not null && OperatingSystem.IsWindows()) app.OnBeforeUninstallFastCallback(_ => beforeUninstall());
        if (args is not null) app.SetArgs(args);
        app.Run();
        if (IsInstalled)
        {
            try { Notice = Retained.CompleteSettingsRestore(AppBuild.Current); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            { Notice = "Não foi possível recuperar o backup de ajustes. Os ajustes atuais foram preservados; confira o histórico antes de tentar outra reversão."; }
        }
    }
    public static string? ReadNotice() { var value = Notice; Notice = null; return value; }
    public static IReadOnlyList<InstalledBuild> History() => Retained.List(AppBuild.Current.BuildId);
    public static Task RecordCurrentAsync(CancellationToken cancellation = default) => IsInstalled ? Retained.RecordCurrentAsync(AppBuild.Current, cancellation: cancellation) : Task.CompletedTask;
    public static Task ArchiveVerifiedAsync(string packagePath, VelopackAsset expected, AppBuildInfo build, CancellationToken cancellation = default)
        => Retained.ArchiveVerifiedAsync(packagePath, expected, build, false, cancellation);
    public static string LocalPackagePath(VelopackAsset asset)
    {
        if (PackagesDirectory is not {} directory) throw new InvalidOperationException("Instale a overlay pelo Setup para aplicar atualizações.");
        return Path.Combine(directory, LocalUpdateHistory.SafeFileName(asset.FileName));
    }
    public static async Task<PreparedVeloUpdate> PrepareRollbackAsync(string buildId, Action<int>? progress = null, CancellationToken cancellation = default)
    {
        if (!IsInstalled) throw new InvalidOperationException("Instale a overlay pelo Setup para voltar a uma versão anterior.");
        await RecordCurrentAsync(cancellation).ConfigureAwait(false);
        var retained = await Retained.GetVerifiedAsync(buildId, cancellation).ConfigureAwait(false);
        if (retained.Build.BuildId == AppBuild.Current.BuildId) throw new InvalidOperationException("Esta versão já está em execução.");
        var manager = new UpdateManager(new SimpleFileSource(new DirectoryInfo(Path.GetDirectoryName(retained.PackagePath)!)), new UpdateOptions { AllowVersionDowngrade = true, ExplicitChannel = retained.Build.Channel });
        var destination = LocalPackagePath(retained.Asset);
        if (File.Exists(destination))
        {
            try { await LocalUpdateHistory.VerifyPackageAsync(destination, retained.Asset, retained.Build, cancellation).ConfigureAwait(false); }
            catch (InvalidDataException) { File.Delete(destination); }
        }
        await manager.DownloadUpdatesAsync(new UpdateInfo(retained.Asset, true), progress, cancellation).ConfigureAwait(false);
        await LocalUpdateHistory.VerifyPackageAsync(destination, retained.Asset, retained.Build, cancellation).ConfigureAwait(false);
        return new(retained.Asset, manager, destination, true);
    }
    /// <summary>Schedules the official updater; the caller then closes WPF gracefully.</summary>
    public static async Task ScheduleApplyAsync(PreparedVeloUpdate target, CancellationToken cancellation = default)
    {
        if (!IsInstalled) throw new InvalidOperationException("Instale a overlay pelo Setup para aplicar atualizações.");
        if (!string.Equals(Path.GetFullPath(target.PackagePath), Path.GetFullPath(LocalPackagePath(target.Asset)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O pacote preparado não corresponde ao cache de atualização do Velopack.");
        var build = await LocalUpdateHistory.ReadBuildInfoAsync(target.PackagePath, cancellation).ConfigureAwait(false);
        await LocalUpdateHistory.VerifyPackageAsync(target.PackagePath, target.Asset, build, cancellation).ConfigureAwait(false);
        await RecordCurrentAsync(cancellation).ConfigureAwait(false);
        Retained.SnapshotSettings(AppBuild.Current.BuildId);
        if (target.IsRollback) Retained.QueueSettingsRestore(build);
        try { target.Manager.WaitExitThenApplyUpdates(target.Asset, silent: false, restart: true, restartArgs: proofDataRoot is {} proof?["--velopack-proof",proof,"--show"]:["--show"]); }
        catch { if (target.IsRollback) Retained.CancelSettingsRestore(); throw; }
    }
}

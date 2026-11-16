using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using ApolloOverlay.Automation;
using Microsoft.Win32;

namespace ApolloOverlay;

internal sealed record StartupStatus(bool Available, bool Enabled, bool Elevated, string Description);
/// <summary>Registers the current user's packaged application for a hidden start at sign-in.</summary>
internal static class StartupRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string SettingsKey = @"Software\Bezoro\GameSessionOverlay";
    internal const string RunValue = "Bezoro.GameSessionOverlay";
    internal const string MarkerValue = "StartupConfigured";
    internal const string PreferenceValue = "StartupEnabled";
    internal const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private static bool ValidExecutable(string? executable) => !string.IsNullOrEmpty(executable)
        && Path.IsPathFullyQualified(executable)
        && (Path.GetFileName(executable).Equals("ApolloOverlay.exe", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(executable).Equals("Game Session Overlay.exe", StringComparison.OrdinalIgnoreCase))
        && !executable.Contains('"');
    internal static bool DisabledByWindows(object? value) => value is byte[] bytes && bytes.Length >= 12 && bytes[0] is 3 or 7;
    private static string Command(string executable) => "\"" + executable + "\" --startup";
    internal static bool MatchesHiddenCommand(string command, string executable)
    {
        if (!ValidExecutable(executable))
            return false;
        var legacy = Path.Combine(Path.GetDirectoryName(executable)!, "current", "ApolloOverlay.exe");
        return new[] { executable, legacy }.Any(path => string.Equals(command, Command(path), StringComparison.OrdinalIgnoreCase)
            || string.Equals(command, "\"" + path + "\" --hidden", StringComparison.OrdinalIgnoreCase));
    }
    // A missing Run value after initial setup is respected: the user may have removed it.
    // Updating an existing entry makes moving the packaged application recoverable.
    internal static bool Apply(string? executable, Func<bool> configured, Func<string?> read, Action<string> write, Action mark)
    {
        if (!ValidExecutable(executable))
            return false;
        var command = Command(executable!);
        var previous = read();
        if (configured() && previous is null)
            return true;
        if (!string.Equals(previous, command, StringComparison.OrdinalIgnoreCase))
            write(command);
        mark();
        return true;
    }

    internal static bool TryEnsure(string? executable)
    {
        if (!ValidExecutable(executable))
            return false;
        try
        {
            using var mutation = StartupMutationLease.TryAcquire();
            if (mutation is null)
                return false;
            using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true);
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (settings is null || run is null)
                return false;
            if (settings.GetValue(PreferenceValue) is int enabled && enabled == 0)
                return true;
            var task = ScheduledStartup.Read(executable!);
            if (task.Exists)
            {
                if (!task.Owned)
                    return false;
                if (run.GetValue(RunValue) is string duplicate && MatchesHiddenCommand(duplicate, executable!))
                    run.DeleteValue(RunValue, false);
                settings.SetValue(MarkerValue, 1, RegistryValueKind.DWord);
                return true;
            }
            return Apply(executable, () => settings.GetValue(MarkerValue) is int value && value == 1, () => run.GetValue(RunValue) as string, command => run.SetValue(RunValue, command, RegistryValueKind.String), () => settings.SetValue(MarkerValue, 1, RegistryValueKind.DWord));
        }
        catch (Exception error) when (RegistrationError(error))
        {
            return false;
        }
    }

    internal static StartupStatus ReadStatus() => ReadStatus(VelopackInstallation.StartupExecutable);
    private static StartupStatus ReadStatus(string executable)
    {
        if (!VelopackInstallation.IsInstalled || !ValidExecutable(executable) || !File.Exists(executable))
            return new(false, false, false, "Install the overlay using Setup to configure Windows startup.");
        try
        {
            var task = ScheduledStartup.Read(executable);
            if (task.Exists)
                return task.Owned
                    ? new(true, task.Enabled, task.Elevated, task.Enabled ? task.Elevated ? "Registered for hidden elevated launch at sign-in." : "Registered for hidden launch at sign-in." : "The configured startup task is disabled.")
                    : new(false, false, false, "The startup task belongs to another installation.");
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(RunValue) is not string command)
                return new(true, false, false, "Hidden startup is off. Enable it to repair registration.");
            if (!MatchesHiddenCommand(command, executable))
                return new(true, false, false, "Startup points to another location. Enable it to repair registration.");
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return DisabledByWindows(approved?.GetValue(RunValue))
                ? new(true, false, false, "Disabled in Windows Startup apps. Enable it to allow hidden launch at sign-in.")
                : new(true, true, false, "Registered for hidden launch at sign-in.");
        }
        catch (Exception error) when (RegistrationError(error))
        {
            return new(false, false, false, "Windows startup registration could not be read.");
        }
    }

    internal static ActionResult SetEnabled(bool enabled, CancellationToken cancellation = default)
    {
        if (cancellation.IsCancellationRequested)
            return StartupMutationLease.Cancelled();
        var executable = VelopackInstallation.StartupExecutable;
        if (!VelopackInstallation.IsInstalled || !ValidExecutable(executable) || !File.Exists(executable))
            return new(ResultKind.Unavailable, "Install the overlay using Setup to configure Windows startup.");
        try
        {
            return StartupMutationLease.Execute(cancellation, () => SetEnabledCore(executable, enabled, cancellation));
        }
        catch (OperationCanceledException)
        {
            return StartupMutationLease.Cancelled();
        }
        catch (Exception error) when (RegistrationError(error))
        {
            return new(ResultKind.Unknown, "Windows startup could not be confirmed. Existing task permissions may require an elevated launch.");
        }
    }

    private static ActionResult SetEnabledCore(string executable, bool enabled, CancellationToken cancellation)
    {
        var task = ScheduledStartup.Read(executable);
        cancellation.ThrowIfCancellationRequested();
        if (task.Exists && (!task.Owned || !ScheduledStartup.SetEnabled(executable, enabled, cancellation)))
            return new(ResultKind.Unavailable, "The existing startup task could not be changed. Its registration was preserved.");
        // The task request above, or the registry writes below, begin the mutation.
        // Finish persistence and observation even if cancellation arrives afterwards.
        using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true);
        using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            if (!task.Exists)
                run.SetValue(RunValue, Command(executable), RegistryValueKind.String);
            else if (run.GetValue(RunValue) is string duplicate && MatchesHiddenCommand(duplicate, executable))
                run.DeleteValue(RunValue, false);
            // Only a deliberate enable action clears this application's Windows opt-out.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(RunValue, false);
        }
        else
            run.DeleteValue(RunValue, false);
        settings.SetValue(PreferenceValue, enabled ? 1 : 0, RegistryValueKind.DWord);
        settings.SetValue(MarkerValue, 1, RegistryValueKind.DWord);
        var observed = ReadStatus(executable);
        return observed.Available && observed.Enabled == enabled
            ? new(ResultKind.Completed, enabled ? "Hidden startup is enabled." : "Hidden startup is disabled.")
            : new(ResultKind.Unknown, "Windows startup could not be confirmed. Check Startup apps before trying again.");
    }

    /// <summary>Changes startup privilege without changing whether this user starts the app at sign-in.</summary>
    internal static ActionResult SetElevated(bool elevated, CancellationToken cancellation = default) =>
        ConfigureMode(elevated, cancellation: cancellation);

    /// <summary>Explicit administrative repair uses the same ownership and rollback boundary as the application.</summary>
    internal static ActionResult Repair(bool install) => ConfigureMode(elevated: install, enabled: install);

    private static ActionResult ConfigureMode(bool elevated, bool? enabled = null, CancellationToken cancellation = default)
    {
        if (cancellation.IsCancellationRequested)
            return StartupMutationLease.Cancelled();
        var executable = VelopackInstallation.StartupExecutable;
        if (!VelopackInstallation.IsInstalled || !ValidExecutable(executable) || !File.Exists(executable))
            return new(ResultKind.Unavailable, "Install the overlay using Setup to remember administrator startup.");
        if (!AutomationPipeSecurity.Elevated())
            return new(ResultKind.Unavailable, "Restart as administrator before changing startup privileges.");
        try
        {
            return StartupMutationLease.Execute(cancellation, () => ConfigureModeCore(executable, elevated, enabled, cancellation));
        }
        catch (OperationCanceledException)
        {
            return StartupMutationLease.Cancelled();
        }
        catch (Exception error) when (RegistrationError(error))
        {
            return new(ResultKind.Unknown, "Startup privileges could not be confirmed. Check Windows startup settings before trying again.");
        }
    }

    private static ActionResult ConfigureModeCore(string executable, bool elevated, bool? enabled, CancellationToken cancellation)
    {
        var before = ReadStatus(executable);
        cancellation.ThrowIfCancellationRequested();
        if (!before.Available)
            return new(ResultKind.Unavailable, "The existing startup registration could not be verified. It was preserved.");
        var requestedEnabled = enabled ?? before.Enabled;
        // Snapshot without creating keys: cancellation during a slow read must not
        // create a registration before the native mutation is admitted.
        using var settingsSnapshot = Registry.CurrentUser.OpenSubKey(SettingsKey);
        using var runSnapshot = Registry.CurrentUser.OpenSubKey(RunKey);
        using var approvedSnapshot = enabled == true ? Registry.CurrentUser.OpenSubKey(ApprovedKey) : null;
        var savedRun = RegistryValueSnapshot.Read(runSnapshot, RunValue);
        var savedMarker = RegistryValueSnapshot.Read(settingsSnapshot, MarkerValue);
        var savedPreference = RegistryValueSnapshot.Read(settingsSnapshot, PreferenceValue);
        var savedApproval = RegistryValueSnapshot.Read(approvedSnapshot, RunValue);
        if (savedRun.Value is string command && !MatchesHiddenCommand(command, executable))
            return new(ResultKind.Unavailable, "The Run entry belongs to another location. It was preserved.");
        cancellation.ThrowIfCancellationRequested();
        var registryStarted = false;
        try
        {
            ScheduledStartup.ConfigureElevation(executable, elevated, requestedEnabled, () =>
            {
                // Task mutation has begun, or the scheduler has admitted this first
                // registry mutation. Cancellation must not interrupt commit/rollback.
                registryStarted = true;
                using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true);
                using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                using var approved = enabled == true ? Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true) : null;
                if (elevated || !requestedEnabled)
                    run.DeleteValue(RunValue, false);
                else
                    run.SetValue(RunValue, Command(executable), RegistryValueKind.String);
                settings.SetValue(MarkerValue, 1, RegistryValueKind.DWord);
                settings.SetValue(PreferenceValue, requestedEnabled ? 1 : 0, RegistryValueKind.DWord);
                if (enabled == true)
                    approved?.DeleteValue(RunValue, false);
                var observed = ReadStatus(executable);
                if (!observed.Available || observed.Enabled != requestedEnabled || observed.Elevated != elevated)
                    throw new InvalidOperationException("Windows did not confirm the requested startup mode.");
            }, cancellation);
        }
        catch
        {
            if (registryStarted)
            {
                using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true);
                using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                using var approved = enabled == true ? Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true) : null;
                savedRun.Restore(run, RunValue);
                savedMarker.Restore(settings, MarkerValue);
                savedPreference.Restore(settings, PreferenceValue);
                if (approved is not null)
                    savedApproval.Restore(approved, RunValue);
            }
            throw;
        }
        return new(ResultKind.Completed, elevated
            ? requestedEnabled ? "Administrator startup is configured. The overlay will remain hidden at sign-in." : "Administrator mode is remembered. Startup remains off."
            : requestedEnabled ? "Standard hidden startup is restored. This session remains elevated until you exit." : "Administrator startup is removed. Startup remains off.");
    }

    private sealed record RegistryValueSnapshot(object? Value, RegistryValueKind Kind)
    {
        internal static RegistryValueSnapshot Read(RegistryKey? key, string name)
        {
            if (key is null)
                return new(null, RegistryValueKind.None);
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return new(value, value is null ? RegistryValueKind.None : key.GetValueKind(name));
        }

        internal void Restore(RegistryKey key, string name)
        {
            if (Value is null)
                key.DeleteValue(name, false);
            else
                key.SetValue(name, Value, Kind);
        }
    }

    internal static bool RegistrationError(Exception error) => error is IOException or UnauthorizedAccessException or SecurityException or COMException or InvalidOperationException or System.ComponentModel.Win32Exception or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;

    internal static void TryRemoveMatching(string executable)
    {
        try
        {
            using var mutation = StartupMutationLease.TryAcquire();
            if (mutation is not null)
                RemoveMatching(executable);
        }
        catch (Exception error) when (RegistrationError(error))
        {
        }
    }

    private static void RemoveMatching(string executable)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(RunValue) is string command && MatchesHiddenCommand(command, executable))
            {
                run.DeleteValue(RunValue, false);
                using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true);
                settings?.SetValue(MarkerValue, 1, RegistryValueKind.DWord);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
        }

        // Only remove this user's exact named task when its executable belongs to this installation.
        try
        {
            ScheduledStartup.RemoveMatching(executable);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
        }
    }
}

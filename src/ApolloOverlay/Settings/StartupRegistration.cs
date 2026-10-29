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

    internal static ActionResult SetEnabled(bool enabled)
    {
        var executable = VelopackInstallation.StartupExecutable;
        if (!VelopackInstallation.IsInstalled || !ValidExecutable(executable) || !File.Exists(executable))
            return new(ResultKind.Unavailable, "Install the overlay using Setup to configure Windows startup.");
        try
        {
            var task = ScheduledStartup.Read(executable);
            if (task.Exists && (!task.Owned || !ScheduledStartup.SetEnabled(executable, enabled)))
                return new(ResultKind.Unavailable, "The existing startup task could not be changed. Its registration was preserved.");
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
        catch (Exception error) when (RegistrationError(error))
        {
            return new(ResultKind.Unknown, "Windows startup could not be confirmed. Existing task permissions may require an elevated launch.");
        }
    }

    /// <summary>Changes startup privilege without changing whether this user starts the app at sign-in.</summary>
    internal static ActionResult SetElevated(bool elevated)
    {
        var executable = VelopackInstallation.StartupExecutable;
        if (!VelopackInstallation.IsInstalled || !ValidExecutable(executable) || !File.Exists(executable))
            return new(ResultKind.Unavailable, "Install the overlay using Setup to remember administrator startup.");
        if (!AutomationPipeSecurity.Elevated())
            return new(ResultKind.Unavailable, "Restart as administrator before changing startup privileges.");
        try
        {
            var before = ReadStatus(executable);
            if (!before.Available)
                return new(ResultKind.Unavailable, "The existing startup registration could not be verified. It was preserved.");
            using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true);
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            var savedRun = RegistryValueSnapshot.Read(run, RunValue);
            var savedMarker = RegistryValueSnapshot.Read(settings, MarkerValue);
            var savedPreference = RegistryValueSnapshot.Read(settings, PreferenceValue);
            if (savedRun.Value is string command && !MatchesHiddenCommand(command, executable))
                return new(ResultKind.Unavailable, "The Run entry belongs to another location. It was preserved.");
            try
            {
                ScheduledStartup.ConfigureElevation(executable, elevated, before.Enabled, () =>
                {
                    if (elevated || !before.Enabled)
                        run.DeleteValue(RunValue, false);
                    else
                        run.SetValue(RunValue, Command(executable), RegistryValueKind.String);
                    settings.SetValue(MarkerValue, 1, RegistryValueKind.DWord);
                    settings.SetValue(PreferenceValue, before.Enabled ? 1 : 0, RegistryValueKind.DWord);
                    var observed = ReadStatus(executable);
                    if (!observed.Available || observed.Enabled != before.Enabled || observed.Elevated != elevated)
                        throw new InvalidOperationException("Windows did not confirm the requested startup mode.");
                });
            }
            catch
            {
                savedRun.Restore(run, RunValue);
                savedMarker.Restore(settings, MarkerValue);
                savedPreference.Restore(settings, PreferenceValue);
                throw;
            }
            return new(ResultKind.Completed, elevated
                ? before.Enabled ? "Administrator startup is configured. The overlay will remain hidden at sign-in." : "Administrator mode is remembered. Startup remains off."
                : before.Enabled ? "Standard hidden startup is restored. This session remains elevated until you exit." : "Administrator startup is removed. Startup remains off.");
        }
        catch (Exception error) when (RegistrationError(error))
        {
            return new(ResultKind.Unknown, "Startup privileges could not be confirmed. Check Windows startup settings before trying again.");
        }
    }

    private sealed record RegistryValueSnapshot(object? Value, RegistryValueKind Kind)
    {
        internal static RegistryValueSnapshot Read(RegistryKey key, string name)
        {
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

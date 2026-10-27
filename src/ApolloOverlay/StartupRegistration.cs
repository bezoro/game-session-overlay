using System.IO;
using System.Security;
using Microsoft.Win32;

namespace ApolloOverlay;

/// <summary>Registers the current user's packaged application for a hidden start at sign-in.</summary>
internal static class StartupRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string SettingsKey = @"Software\Bezoro\GameSessionOverlay";
    internal const string RunValue = "Bezoro.GameSessionOverlay";
    internal const string MarkerValue = "StartupConfigured";

    // A missing Run value after initial setup is respected: the user may have removed it.
    // Updating an existing entry makes moving the packaged application recoverable.
    internal static bool Apply(string? executable, Func<bool> configured, Func<string?> read,
        Action<string> write, Action mark)
    {
        if (string.IsNullOrEmpty(executable) || !Path.IsPathFullyQualified(executable) ||
            !Path.GetFileName(executable).Equals("ApolloOverlay.exe", StringComparison.OrdinalIgnoreCase) ||
            executable.Contains('"'))
            return false;

        var command = "\"" + executable + "\" --startup";
        var previous = read();
        if (configured() && previous is null) return true;
        if (!string.Equals(previous, command, StringComparison.OrdinalIgnoreCase))
            write(command);
        mark();
        return true;
    }

    internal static bool TryEnsure(string? executable)
    {
        try
        {
            using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true);
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (settings is null || run is null) return false;
            return Apply(executable,
                () => settings.GetValue(MarkerValue) is int value && value == 1,
                () => run.GetValue(RunValue) as string,
                command => run.SetValue(RunValue, command, RegistryValueKind.String),
                () => settings.SetValue(MarkerValue, 1, RegistryValueKind.DWord));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }
}

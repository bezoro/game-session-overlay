using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ApolloOverlay;

internal static class InstalledIntegrationTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("Windows startup approval distinguishes enabled and explicitly disabled registrations", () =>
        {
            foreach (var flag in new byte[] { 2, 3, 6, 7 })
            {
                var bytes = new byte[12];
                bytes[0] = flag;
                Require(StartupRegistration.DisabledByWindows(bytes) == (flag is 3 or 7), "Windows startup approval was misclassified");
            }
            Require(!StartupRegistration.DisabledByWindows(null) && !StartupRegistration.DisabledByWindows("invalid"), "Missing Windows approval was treated as a user opt-out");
        });
        test("startup ownership accepts hidden legacy launchers and rejects visible or foreign commands", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "Owned overlay");
            var launcher = Path.Combine(root, "Game Session Overlay.exe");
            var legacy = Path.Combine(root, "current", "ApolloOverlay.exe");
            Require(StartupRegistration.MatchesHiddenCommand("\"" + launcher + "\" --startup", launcher)
                && StartupRegistration.MatchesHiddenCommand("\"" + legacy + "\" --hidden", launcher), "An owned hidden startup command was not recognized");
            Require(!StartupRegistration.MatchesHiddenCommand("\"" + launcher + "\" --show", launcher)
                && !StartupRegistration.MatchesHiddenCommand("\"" + Path.Combine(root, "other", "ApolloOverlay.exe") + "\" --startup", launcher), "A visible or unrelated command was treated as hidden startup");
        });
        test("searchable shortcut targets the stable launcher and explicitly opens the overlay", () =>
        {
            WithDirectory(root =>
            {
                var executable = Path.Combine(root, "Game Session Overlay.exe");
                var programs = Path.Combine(root, "Programs");
                File.WriteAllText(executable, "fixture, never executed");
                Require(StartMenuRegistration.TryEnsure(executable, programs), "Searchable shortcut creation failed");
                var path = Path.Combine(programs, StartMenuRegistration.ShortcutName);
                var shortcut = ReadShortcut(path);
                Require(shortcut.Target.Equals(executable, StringComparison.OrdinalIgnoreCase) && shortcut.Arguments == "--show" && shortcut.WorkingDirectory.Equals(root, StringComparison.OrdinalIgnoreCase), "Shortcut does not identify the stable launcher and visible launch arguments");
                var before = File.GetLastWriteTimeUtc(path);
                Require(StartMenuRegistration.TryEnsure(executable, programs) && File.GetLastWriteTimeUtc(path) == before, "Correct shortcut was needlessly rewritten");
                File.Delete(path);
                Require(StartMenuRegistration.TryEnsure(executable, programs) && File.Exists(path), "A missing shortcut could not be repaired");
            });
        });
        test("shortcut migration repairs the application target but preserves another installation", () =>
        {
            WithDirectory(root =>
            {
                var executable = Path.Combine(root, "Game Session Overlay.exe");
                var current = Path.Combine(root, "current", "ApolloOverlay.exe");
                var programs = Path.Combine(root, "Programs");
                Directory.CreateDirectory(Path.GetDirectoryName(current)!);
                Directory.CreateDirectory(programs);
                File.WriteAllText(executable, "fixture, never executed");
                File.WriteAllText(current, "fixture, never executed");
                var path = Path.Combine(programs, StartMenuRegistration.ShortcutName);
                WriteShortcut(path, current);
                Require(StartMenuRegistration.TryEnsure(executable, programs) && ReadShortcut(path).Arguments == "--show", "Installed shortcut without visible launch arguments was not repaired");
                var foreign = Path.Combine(root, "other", "Game Session Overlay.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
                File.WriteAllText(foreign, "fixture, never executed");
                WriteShortcut(path, foreign);
                var original = SHA256.HashData(File.ReadAllBytes(path));
                Require(!StartMenuRegistration.TryEnsure(executable, programs) && original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Shortcut belonging to another target was overwritten");
            });
        });
    }

    private sealed record Shortcut(string Target, string Arguments, string WorkingDirectory);
    private static Shortcut ReadShortcut(string path) => WithShortcut(path, link => new Shortcut((string)link.TargetPath, (string)link.Arguments, (string)link.WorkingDirectory));
    private static void WriteShortcut(string path, string target) => WithShortcut(path, link =>
    {
        link.TargetPath = target;
        link.Arguments = "";
        link.Save();
        return true;
    });

    private static T WithShortcut<T>(string path, Func<dynamic, T> action)
    {
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic instance = shell!;
            shortcut = instance.CreateShortcut(path);
            return action(shortcut);
        }
        finally
        {
            if (shortcut is not null)
                Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null)
                Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void WithDirectory(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "OverlayIntegration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            action(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }
}

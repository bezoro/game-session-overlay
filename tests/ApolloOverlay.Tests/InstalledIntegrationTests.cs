using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ApolloOverlay;

internal static class InstalledIntegrationTests
{
    internal const string StartupLeaseOwnerArgument = "--startup-test-lease-owner";

    internal static int RunStartupLeaseOwner(string readyName, string releaseName, string statePath)
    {
        using var ready = EventWaitHandle.OpenExisting(readyName);
        using var release = EventWaitHandle.OpenExisting(releaseName);
        using var lease = StartupMutationLease.TryAcquire();
        if (lease is null)
            return 3;
        var snapshot = File.ReadAllText(statePath);
        try
        {
            File.WriteAllText(statePath, "pending");
            ready.Set();
            return release.WaitOne(TimeSpan.FromSeconds(10)) ? 0 : 4;
        }
        finally
        {
            // Model rollback while retaining exactly the same ownership required by
            // task/registry recovery. No real startup registration is modified here.
            File.WriteAllText(statePath, snapshot);
        }
    }

    internal static void Run(Action<string, Action> test)
    {
        test("startup mutations wait across processes until the preceding rollback releases ownership", () =>
        {
            WithDirectory(root => WithStartupLeaseOwner(root, (child, release, statePath) =>
            {
                var elapsed = Stopwatch.StartNew();
                using var unavailable = StartupMutationLease.TryAcquire(100);
                Require(unavailable is null && elapsed.Elapsed < TimeSpan.FromSeconds(2), "A competing mutation bypassed ownership or exceeded its wait bound");
                Require(File.ReadAllText(statePath) == "pending", "A timed-out contender changed the owner's pending state");

                using var attempting = new ManualResetEventSlim();
                var contender = Task.Run(() =>
                {
                    attempting.Set();
                    using var lease = StartupMutationLease.TryAcquire(3000)
                        ?? throw new InvalidOperationException("The waiting mutation did not acquire released ownership.");
                    var snapshot = File.ReadAllText(statePath);
                    File.WriteAllText(statePath, "committed");
                    return snapshot;
                });
                try
                {
                    Require(attempting.Wait(TimeSpan.FromSeconds(1)) && !contender.Wait(100), "The next mutation ran before the owner's rollback");
                    release.Set();
                    Require(child.WaitForExit(3000) && child.ExitCode == 0, "The preceding owner failed to roll back and exit");
                    Require(contender.Wait(3000) && contender.GetAwaiter().GetResult() == "before", "The next owner took a snapshot before rollback completed");
                    Require(File.ReadAllText(statePath) == "committed", "A stale rollback overwrote the next owner's commit");
                }
                finally
                {
                    release.Set();
                    Require(contender.Wait(4000), "The contender outlived the bounded startup lease test");
                }
            }));
        });
        test("startup mutation ownership recovers after an owner process exits without releasing", () =>
        {
            WithDirectory(root => WithStartupLeaseOwner(root, (child, _, statePath) =>
            {
                child.Kill(entireProcessTree: true);
                Require(child.WaitForExit(3000), "The abandoned owner process did not exit");
                using var recovered = StartupMutationLease.TryAcquire(1000);
                Require(recovered is not null && File.ReadAllText(statePath) == "pending",
                    "An abandoned lease remained blocked or falsely implied its unfinished transaction had recovered");
            }));
        });
        test("startup cancellation while another process owns the lease never enters the released transaction", () =>
        {
            WithDirectory(root => WithStartupLeaseOwner(root, (child, release, statePath) =>
            {
                using var cancellation = new CancellationTokenSource();
                using var acquiring = new ManualResetEventSlim();
                var effects = 0;
                var contender = Task.Run(() => StartupMutationLease.Execute(cancellation.Token, () =>
                {
                    Interlocked.Increment(ref effects);
                    File.WriteAllText(statePath, "cancelled mutation");
                    return new(ResultKind.Completed, "Unexpected mutation.");
                }, acquire: () =>
                {
                    // This signal is after Execute's initial cancellation check.
                    // Use the actual cross-process lease, rather than a simulated wait.
                    acquiring.Set();
                    return StartupMutationLease.TryAcquire();
                }));
                try
                {
                    Require(acquiring.Wait(TimeSpan.FromSeconds(1)) && !contender.Wait(100),
                        "The contender did not reach the blocked native ownership boundary");
                    cancellation.Cancel();
                    release.Set();
                    Require(child.WaitForExit(3000) && child.ExitCode == 0, "The preceding owner failed to finish rollback");
                    Require(contender.Wait(3000) && contender.GetAwaiter().GetResult().Kind == ResultKind.Cancelled,
                        "The request cancelled during acquisition was admitted after ownership became available");
                    Require(effects == 0 && File.ReadAllText(statePath) == "before",
                        "A cancelled contender dispatched a change after the previous owner released its lease");
                }
                finally
                {
                    release.Set();
                    Require(contender.Wait(4000), "The cancelled contender outlived the bounded startup lease test");
                }
            }));
        });
        test("startup APIs reject an already cancelled request before installation or privilege checks", () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Require(StartupRegistration.SetEnabled(true, cancellation.Token).Kind == ResultKind.Cancelled
                && StartupRegistration.SetElevated(true, cancellation.Token).Kind == ResultKind.Cancelled,
                "A cancelled startup request reached installation or privilege validation");
        });
        test("startup repair requires exactly one explicit administrative action", () =>
        {
            Require(StartupRepairCommand.TryReadAction([StartupRepairCommand.Argument, "Install"], out var install) && install,
                "Explicit installation was not recognized");
            Require(StartupRepairCommand.TryReadAction([StartupRepairCommand.Argument, "uninstall"], out install) && !install,
                "Explicit removal was not recognized");
            foreach (var args in new string[][]
            {
                [StartupRepairCommand.Argument],
                [StartupRepairCommand.Argument, "Enable"],
                [StartupRepairCommand.Argument, "Install", "--show"]
            })
                Require(StartupRepairCommand.Execute(args) == 2, "Ambiguous repair arguments reached registration");
            Require(StartupRepairCommand.Execute(["--startup"]) is null,
                "Repair handling intercepted ordinary hidden startup");
        });
        test("Task Scheduler missing-task detection handles managed HRESULT mapping without hiding permission errors", () =>
        {
            Require(ScheduledStartup.IsMissingTask(new FileNotFoundException())
                && ScheduledStartup.IsMissingTask(new COMException("Missing task", unchecked((int)0x80070002))), "A missing task prevented first startup registration");
            Require(!ScheduledStartup.IsMissingTask(new UnauthorizedAccessException())
                && !ScheduledStartup.IsMissingTask(new COMException("Access denied", unchecked((int)0x80070005)))
                && !ScheduledStartup.IsMissingTask(new IOException("Other failure")), "A task-read failure was silently treated as absence");
        });
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

    private static void WithStartupLeaseOwner(string root, Action<Process, EventWaitHandle, string> action)
    {
        var statePath = Path.Combine(root, "transaction.txt");
        File.WriteAllText(statePath, "before");
        var readyName = @"Local\Overlay.StartupLease.Ready." + Guid.NewGuid().ToString("N");
        var releaseName = @"Local\Overlay.StartupLease.Release." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var job = new ActivationProcessJob();
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Test executable unavailable."))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        foreach (var argument in new[] { StartupLeaseOwnerArgument, readyName, releaseName, statePath })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("The startup lease owner did not start.");
        try
        {
            job.Attach(child);
            Require(ready.WaitOne(TimeSpan.FromSeconds(5)), "The separate process did not acquire startup mutation ownership");
            action(child, release, statePath);
        }
        finally
        {
            release.Set();
            if (!child.WaitForExit(3000))
            {
                child.Kill(entireProcessTree: true);
                Require(child.WaitForExit(3000), "The startup lease fixture did not terminate");
            }
        }
    }

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

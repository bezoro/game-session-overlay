using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ApolloOverlay;

internal static class ForeignWindowActivationTests
{
    internal const string HungWindowArgument = "--activation-test-hung-window";
    private sealed record WindowIdentity(int Pid, long Started, long Handle);

    internal static int RunHungWindowHost(string readyName, string identityPath)
    {
        using var process = Process.GetCurrentProcess();
        using var ready = EventWaitHandle.OpenExisting(readyName);
        // STATIC is a system window class. Its real GUI thread deliberately stops
        // pumping after minimizing; no custom window procedure simulates the hang.
        var window = CreateWindowExW(0, "STATIC", "Unresponsive minimized activation fixture",
            0x00CF0000, -12000, -12000, 180, 100, 0, 0, 0, 0);
        if (window == 0)
            return 2;
        Native.ShowWindow(window, 6);
        if (!Native.IsIconic(window))
            return 3;
        File.WriteAllText(identityPath, JsonSerializer.Serialize(new WindowIdentity(process.Id,
            process.StartTime.ToUniversalTime().Ticks, window.ToInt64())));
        ready.Set();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    internal static void Run(Action<string, Action> test)
    {
        test("restoring an unresponsive minimized foreign window keeps the dispatcher responsive", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "overlay-hung-window-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var identityPath = Path.Combine(root, "window.json");
            var readyName = @"Local\Overlay.HungWindow.Test." + Guid.NewGuid().ToString("N");
            var previous = SynchronizationContext.Current;
            try
            {
                using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
                using var job = new ActivationProcessJob();
                using var child = Process.Start(ChildStart(readyName, identityPath))
                    ?? throw new InvalidOperationException("The foreign-window fixture did not start.");
                job.Attach(child);
                Require(ready.WaitOne(TimeSpan.FromSeconds(5)), "The minimized foreign window did not become ready.");
                var identity = JsonSerializer.Deserialize<WindowIdentity>(File.ReadAllText(identityPath))
                    ?? throw new InvalidDataException("The foreign-window identity was absent.");
                Require(identity.Pid == child.Id && identity.Started == child.StartTime.ToUniversalTime().Ticks,
                    "The fixture returned another process identity.");
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                using var platform = new WindowsPlatform();
                var target = platform.Windows().Single(window => window.Handle == (nint)identity.Handle && window.Pid == identity.Pid);
                Require(platform.Valid(target) && Native.IsIconic(target.Handle), "The exact fixture window was not minimized.");

                // A regression must fail within a bound rather than hang the entire
                // test runner inside synchronous cross-process window activation.
                using var watchdog = new Timer(_ => Stop(child), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
                var heartbeat = Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task;
                var elapsed = Stopwatch.StartNew();
                var activation = platform.Focus(target, CancellationToken.None, () => true);
                ReviewFixTests.Complete(activation);
                ReviewFixTests.Complete(heartbeat);
                Require(elapsed.Elapsed < TimeSpan.FromSeconds(2) && !child.HasExited,
                    "The foreign window blocked dispatcher activation before its observation deadline.");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                Directory.Delete(root, recursive: true);
            }
        });
    }

    private static ProcessStartInfo ChildStart(string readyName, string identityPath)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Test executable unavailable."))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add(HungWindowArgument);
        start.ArgumentList.Add(readyName);
        start.ArgumentList.Add(identityPath);
        return start;
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            // The owning job independently terminates the fixture during cleanup.
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
}

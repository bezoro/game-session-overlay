using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ApolloOverlay;

/// <summary>Forwards an explicit shell launch to the existing window, including a hidden one.</summary>
internal static class SingleInstanceActivation
{
    internal const uint OpenMessage = 0x8002;

    internal static Mutex? AcquireMutex(out bool created)
    {
        try
        {
            return new Mutex(true, @"Local\Bezoro.GameSessionOverlay", out created);
        }
        catch (UnauthorizedAccessException)
        {
            // A higher-integrity instance can own the existing object. Keep its ACL and
            // use the same-installation, same-session opening message instead.
            created = false;
            return null;
        }
    }

    internal static void AllowRequests(nint window)
    {
        // Opening the panel is the only cross-integrity message allowed; it grants no action authority.
        ChangeWindowMessageFilterEx(window, OpenMessage, 1, 0);
    }

    internal static bool TryOpenExisting(int timeoutMs = 2000)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } executable)
            return false;
        using var self = Process.GetCurrentProcess();
        var watch = Stopwatch.StartNew();
        do
        {
            var sent = false;
            Native.EnumWindows((window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out var pid);
                if (pid == (uint)self.Id || !ProcessIdToSessionId(pid, out var session) || session != (uint)self.SessionId)
                    return true;
                var title = new StringBuilder(256);
                Native.GetWindowText(window, title, title.Capacity);
                if (title.ToString() != "Game Session Overlay")
                    return true;
                using var process = OpenProcess(0x1000, false, pid);
                if (process.IsInvalid)
                    return true;
                var path = new StringBuilder(32768);
                var count = path.Capacity;
                if (!QueryFullProcessImageName(process, 0, path, ref count)
                    || !string.Equals(path.ToString(), executable, StringComparison.OrdinalIgnoreCase)
                    && !StartMenuRegistration.MatchesInstallation(path.ToString(), VelopackInstallation.StartupExecutable))
                    return true;
                AllowSetForegroundWindow(pid);
                sent = Native.PostMessage(window, OpenMessage, 0, 0);
                return !sent;
            }, 0);
            if (sent)
                return true;
            Thread.Sleep(25);
        } while (watch.ElapsedMilliseconds < timeoutMs);
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint change);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
}

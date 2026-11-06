using System.Runtime.InteropServices;
using System.Text;

namespace ApolloOverlay;

internal static class ActiveDisplay
{
    // Capture borrowed native observations before the overlay takes focus. A null
    // identity is safe: placement will fall back to an available display.
    internal static string? Capture(nint foreground, nint overlay, Func<nint, string?> readWindow, Func<string?> readCursor) =>
        (foreground != 0 && foreground != overlay ? readWindow(foreground) : null) ?? readCursor();

    internal static bool IsShellSurface(string className) => className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    internal static string? ReadWindow(nint window)
    {
        if (window == 0 || !Native.IsWindow(window) || !Native.IsWindowVisible(window) || Native.IsIconic(window))
            return null;
        if (Native.GetWindowThreadProcessId(window, out var pid) == 0 || pid == Environment.ProcessId)
            return null;
        var className = new StringBuilder(128);
        if (Native.GetClassName(window, className, className.Capacity) == 0 || IsShellSurface(className.ToString()))
            return null;
        return ReadMonitor(Native.MonitorFromWindow(window, 0));
    }

    internal static string? ReadCursor() => Native.GetCursorPos(out var point) ? ReadMonitor(Native.MonitorFromPoint(point, 2)) : null;

    private static string? ReadMonitor(nint monitor)
    {
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
        return monitor != 0 && Native.GetMonitorInfo(monitor, ref info) ? info.Device : null;
    }
}

namespace ApolloOverlay;

internal interface IWindowActivationNative
{
    uint CurrentThread { get; }
    uint WindowThread(nint window);
    nint Foreground { get; }
    bool SetForeground(nint window);
    bool Attach(uint current, uint foreground, bool attach);
}

/// <summary>A single, explicit activation request. Its result never grants input ownership.</summary>
internal static class WindowActivation
{
    private static readonly IWindowActivationNative native = new Win32Activation();

    internal static bool Request(nint window) => Request(window, native);

    internal static bool Request(nint window, IWindowActivationNative api)
    {
        var current = api.CurrentThread;
        if (window == 0 || current == 0 || api.WindowThread(window) != current) return false;
        var foreground = api.Foreground;
        var foregroundThread = api.WindowThread(foreground);
        if (api.SetForeground(window) || api.Foreground == window) return true;

        // A polled XInput Guide press does not give this process Win32 last-input
        // ownership. Temporarily join the foreground input queue for this explicit
        // request only. Never follow a foreground change or keep queues attached.
        if (foreground == 0 || foregroundThread == 0 || foregroundThread == current ||
            api.Foreground != foreground || api.WindowThread(foreground) != foregroundThread ||
            api.WindowThread(window) != current || !api.Attach(current, foregroundThread, true)) return false;
        try
        {
            if (api.Foreground != foreground || api.WindowThread(foreground) != foregroundThread ||
                api.WindowThread(window) != current) return false;
            return api.SetForeground(window);
        }
        finally { api.Attach(current, foregroundThread, false); }
    }

    private sealed class Win32Activation : IWindowActivationNative
    {
        public uint CurrentThread => Native.GetCurrentThreadId();
        public uint WindowThread(nint window) => window != 0 ? Native.GetWindowThreadProcessId(window, out _) : 0;
        public nint Foreground => Native.GetForegroundWindow();
        public bool SetForeground(nint window) => Native.SetForegroundWindow(window);
        public bool Attach(uint current, uint foreground, bool attach) => Native.AttachThreadInput(current, foreground, attach);
    }
}

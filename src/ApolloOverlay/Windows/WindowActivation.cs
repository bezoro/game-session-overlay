using System.Runtime.InteropServices;

namespace ApolloOverlay;

internal interface IWindowActivationNative
{
    uint CurrentThread
    {
        get;
    }

    uint WindowThread(nint window);
    nint Foreground
    {
        get;
    }

    void EnsureQueue();
    bool SetForeground(nint window);
    bool AwaitActivation(nint window, int timeoutMs);
    bool Attach(uint current, uint foreground, bool attach);
}

/// <summary>Best-effort activation after a normal explicit request was refused.</summary>
internal sealed class WindowActivation(IWindowActivationNative api)
{
    internal static int ExecuteNative(nint window, nint foreground, uint owner)
    {
        var api = new Win32Activation();
        return new WindowActivation(api).Execute(window, foreground, owner, CancellationToken.None) ? 0 : api.FailureCode;
    }

    internal bool Execute(nint window, nint foreground, uint owner, CancellationToken cancellation)
    {
        if (window == 0 || foreground == 0 || owner == 0)
            return false;
        api.EnsureQueue();
        var current = api.CurrentThread;
        var other = api.WindowThread(foreground);
        if (cancellation.IsCancellationRequested
            || current == owner
            || current == 0
            || other == 0
            || other == current
            || other == owner
            || api.Foreground != foreground
            || api.WindowThread(window) != owner
            || !api.Attach(current, other, true))
            return false;
        try
        {
            if (cancellation.IsCancellationRequested || api.Foreground != foreground || api.WindowThread(foreground) != other || api.WindowThread(window) != owner)
                return false;
            if (!api.SetForeground(window) || cancellation.IsCancellationRequested)
                return false;
            // Cross-queue activation is asynchronous. Drain our own window's
            // activation notification before detaching the foreground broker.
            return api.AwaitActivation(window, 100)
                && !cancellation.IsCancellationRequested
                && api.Foreground == window
                && api.WindowThread(window) == owner;
        }
        finally
        {
            api.Attach(current, other, false);
        }
    }

    private sealed class Win32Activation : IWindowActivationNative
    {
        internal int FailureCode { get; private set; } = 1;
        // SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT.
        private const uint AcknowledgementFlags = 0x23;
        public uint CurrentThread => Native.GetCurrentThreadId();

        public uint WindowThread(nint window) => window != 0 ? Native.GetWindowThreadProcessId(window, out _) : 0;
        public nint Foreground => Native.GetForegroundWindow();

        public void EnsureQueue() => Native.PeekMessage(out _, 0, 0, 0, 0);
        public bool SetForeground(nint window)
        {
            var accepted = Native.SetForegroundWindow(window);
            if (!accepted)
                FailureCode = 2;
            return accepted;
        }
        public bool AwaitActivation(nint window, int timeoutMs)
        {
            var acknowledged = Native.SendMessageTimeout(window, 0, 0, 0, AcknowledgementFlags, (uint)timeoutMs, out _) != 0;
            if (!acknowledged)
                FailureCode = 0x20000 | (Marshal.GetLastPInvokeError() & 0xffff);
            return acknowledged;
        }
        public bool Attach(uint current, uint foreground, bool attach)
        {
            var attached = Native.AttachThreadInput(current, foreground, attach);
            if (attach && !attached)
                FailureCode = 0x10000 | (Marshal.GetLastPInvokeError() & 0xffff);
            return attached;
        }
    }
}

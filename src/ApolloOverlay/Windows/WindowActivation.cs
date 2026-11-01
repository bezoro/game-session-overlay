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
    nint CreateBrokerWindow();
    void DestroyBrokerWindow(nint window);
}

/// <summary>Best-effort activation after a normal explicit request was refused.</summary>
internal sealed class WindowActivation(IWindowActivationNative api)
{
    internal const int BrokerConfirmed = 3;
    internal static int ExecuteNative(nint window, nint foreground, uint owner)
    {
        var api = new Win32Activation();
        // Tray activation may delegate authority to the child without requiring
        // queue attachment, including when the foreground belongs to the parent.
        if (window == 0 || foreground == 0 || owner == 0
            || api.Foreground != foreground || api.WindowThread(window) != owner)
            return 1;
        api.EnsureQueue();
        if (api.SetForeground(window))
        {
            if (api.AwaitActivation(window, 100) && api.Foreground == window && api.WindowThread(window) == owner)
                return 0;
            if (api.Foreground != foreground)
                return api.FailureCode;
        }
        return new WindowActivation(api).Execute(window, foreground, owner, CancellationToken.None)
            ? api.UsedBroker ? BrokerConfirmed : 0 : api.FailureCode;
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
            if (!api.SetForeground(window))
                return TransferThroughBroker(window, foreground, owner, other, current, cancellation);
            if (cancellation.IsCancellationRequested)
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

    private bool TransferThroughBroker(nint window, nint foreground, uint owner, uint other, uint current, CancellationToken cancellation)
    {
        bool Current() => !cancellation.IsCancellationRequested && api.WindowThread(window) == owner
            && api.WindowThread(foreground) == other;
        if (!Current() || api.Foreground != foreground)
            return false;
        var broker = api.CreateBrokerWindow();
        if (broker == 0)
            return false;
        try
        {
            // Acquire the helper's own window before transferring to a different
            // process. Queue attachment alone does not grant the parent authority.
            if (!Current() || api.Foreground != foreground || api.WindowThread(broker) != current
                || !api.SetForeground(broker) || !api.AwaitActivation(broker, 100)
                || !Current() || api.Foreground != broker)
                return false;
            return api.SetForeground(window) && api.AwaitActivation(window, 100)
                && Current() && api.Foreground == window;
        }
        finally
        {
            try
            {
                // A failed transfer must not leave the invisible broker owning input.
                // Never redirect focus after a different window has taken ownership.
                if (api.Foreground == broker && api.WindowThread(foreground) == other)
                    api.SetForeground(foreground);
            }
            finally { api.DestroyBrokerWindow(broker); }
        }
    }

    internal sealed class Win32Activation : IWindowActivationNative
    {
        internal int FailureCode { get; private set; } = 1;
        internal bool UsedBroker
        {
            get; private set;
        }
        private nint brokerWindow;
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
                FailureCode = window == brokerWindow ? 10 : brokerWindow != 0 ? 12 : 2;
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

        public nint CreateBrokerWindow()
        {
            // A predefined system class needs no managed window procedure. Keep
            // the tool window outside the entire virtual desktop and taskbar.
            brokerWindow = CreateWindowEx(0x80, "STATIC", "", 0x80000000,
                GetSystemMetrics(76) - 2, GetSystemMetrics(77) - 2, 1, 1, 0, 0, 0, 0);
            if (brokerWindow != 0)
            {
                UsedBroker = true;
                Native.ShowWindow(brokerWindow, 4); // SW_SHOWNOACTIVATE.
            }
            else
                FailureCode = 0x30000 | (Marshal.GetLastPInvokeError() & 0xffff);
            return brokerWindow;
        }

        public void DestroyBrokerWindow(nint window)
        {
            DestroyWindow(window);
            brokerWindow = 0;
        }

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
    }
}

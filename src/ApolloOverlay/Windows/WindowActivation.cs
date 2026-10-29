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
    bool Attach(uint current, uint foreground, bool attach);
}

/// <summary>Best-effort activation after a normal explicit request was refused.</summary>
internal sealed class WindowActivation(IWindowActivationNative api)
{
    private static readonly WindowActivation native = new(new Win32Activation());
    private int pending;
    internal static Task<bool> RequestFallbackAsync(nint window, nint foreground, CancellationToken cancellation) => native.RequestAsync(window, foreground, cancellation);
    internal async Task<bool> RequestAsync(nint window, nint foreground, CancellationToken cancellation, int timeoutMs = 200)
    {
        var owner = api.CurrentThread;
        if (cancellation.IsCancellationRequested
            || owner == 0
            || window == 0
            || foreground == 0
            || api.WindowThread(window) != owner
            || api.Foreground != foreground
            || Interlocked.CompareExchange(ref pending, 1, 0) != 0)
            return false;
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Never join the WPF input queue to another application's queue. The broker
        // has its own queue, and the target remains in a different input group so
        // SetForegroundWindow can notify it asynchronously. A stalled native call
        // occupies this one background thread; it cannot block UI or spawn retries.
        var broker = new Thread(() =>
        {
            var accepted = false;
            try
            {
                accepted = Attempt(window, foreground, owner, token);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or ExternalException)
            {
            }
            finally
            {
                Interlocked.Exchange(ref pending, 0);
                completed.TrySetResult(accepted);
                deadline.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "Overlay activation broker"
        };
        try
        {
            broker.Start();
        }
        catch (Exception error) when (error is ThreadStateException or OutOfMemoryException)
        {
            Interlocked.Exchange(ref pending, 0);
            deadline.Dispose();
            return false;
        }

        try
        {
            return await completed.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private bool Attempt(nint window, nint foreground, uint owner, CancellationToken cancellation)
    {
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
            return api.SetForeground(window);
        }
        finally
        {
            api.Attach(current, other, false);
        }
    }

    private sealed class Win32Activation : IWindowActivationNative
    {
        public uint CurrentThread => Native.GetCurrentThreadId();

        public uint WindowThread(nint window) => window != 0 ? Native.GetWindowThreadProcessId(window, out _) : 0;
        public nint Foreground => Native.GetForegroundWindow();

        public void EnsureQueue() => Native.PeekMessage(out _, 0, 0, 0, 0);
        public bool SetForeground(nint window) => Native.SetForegroundWindow(window);
        public bool Attach(uint current, uint foreground, bool attach) => Native.AttachThreadInput(current, foreground, attach);
    }
}

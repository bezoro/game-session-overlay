using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ApolloOverlay;

/// <summary>
/// Serializes this user's startup mutations across installations, sessions and privilege levels.
/// Acquisition and disposal belong to the same thread; the guarded transaction must not await.
/// </summary>
internal sealed class StartupMutationLease : IDisposable
{
    private const uint SynchronizeAndModify = 0x00100001;
    private const uint Acquired = 0, Abandoned = 0x80, TimedOut = 0x102, Failed = 0xffffffff;
    private readonly SafeWaitHandle handle;
    private readonly uint ownerThread;

    private StartupMutationLease(SafeWaitHandle handle)
    {
        this.handle = handle;
        ownerThread = Native.GetCurrentThreadId();
    }

    /// <summary>Admits a synchronous transaction only while its caller still wants it.</summary>
    internal static ActionResult Execute(CancellationToken cancellation, Func<ActionResult> transaction,
        Func<StartupMutationLease?>? acquire = null)
    {
        if (cancellation.IsCancellationRequested)
            return Cancelled();
        using var lease = acquire is null ? TryAcquire() : acquire();
        // Acquisition is bounded but not interruptible. A request cancelled while
        // another process owns the registration must never enter its transaction.
        if (cancellation.IsCancellationRequested)
            return Cancelled();
        return lease is null
            ? new(ResultKind.Unavailable, "Another startup change is active. Try again after it finishes.")
            : transaction();
    }

    internal static ActionResult Cancelled() => new(ResultKind.Cancelled, "Cancelled before changing Windows startup.");

    internal static StartupMutationLease? TryAcquire(int timeoutMilliseconds = 2000)
    {
        if (timeoutMilliseconds is < 0 or > 30000)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("The startup mutation user could not be identified.");
        // The registration is shared by this SID in HKCU and Task Scheduler, including
        // different interactive sessions. Medium integrity permits its ordinary and
        // elevated processes to wait/release without granting another user access.
        var name = @"Global\Bezoro.GameSessionOverlay.Startup." + sid;
        var sddl = $"D:P(A;;0x{SynchronizeAndModify:X8};;;{sid})S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var mutex = CreateMutexExW(ref attributes, name, 0, SynchronizeAndModify);
            if (mutex.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                mutex.Dispose();
                throw new Win32Exception(error);
            }
            try
            {
                var observed = WaitForSingleObject(mutex, (uint)timeoutMilliseconds);
                if (observed is Acquired or Abandoned)
                    // An abandoned transaction still requires the caller's normal
                    // current-state and ownership checks; acquisition is not recovery.
                    return new(mutex);
                if (observed == Failed)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (observed != TimedOut)
                    throw new InvalidOperationException("Windows returned an unexpected startup mutation wait result.");
                mutex.Dispose();
                return null;
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    public void Dispose()
    {
        if (handle.IsClosed)
            return;
        if (Native.GetCurrentThreadId() != ownerThread)
            throw new InvalidOperationException("The startup mutation lease must be released by its acquiring thread.");
        try
        {
            if (!ReleaseMutex(handle))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            handle.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal nint Descriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool Inherit;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexExW(ref SecurityAttributes attributes, string name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle mutex, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseMutex(SafeWaitHandle mutex);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}

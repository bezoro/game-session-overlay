using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ApolloOverlay.Automation;
using Microsoft.Win32.SafeHandles;

namespace ApolloOverlay;

/// <summary>Native queue attachment runs in a disposable child, never in the overlay process.</summary>
internal sealed class IsolatedWindowActivation
{
    internal const string HostArgument = "--focus-activation";
    private readonly Func<string, ProcessStartInfo> startInfo;
    private readonly Action<string, ResultKind>? trace;
    private int pending;

    internal IsolatedWindowActivation(Func<string, ProcessStartInfo>? startInfo = null, Action<string, ResultKind>? trace = null)
    {
        this.startInfo = startInfo ?? CreateStartInfo;
        this.trace = trace;
    }

    internal async Task<bool> RequestAsync(nint window, nint foreground, CancellationToken cancellation, int timeoutMs = 900)
    {
        if (cancellation.IsCancellationRequested || window == 0 || foreground == 0
            || Native.GetForegroundWindow() != foreground || Interlocked.CompareExchange(ref pending, 1, 0) != 0)
            return false;
        Process? child = null;
        ActivationProcessJob? job = null;
        try
        {
            var owner = Native.GetWindowThreadProcessId(window, out var pid);
            var other = Native.GetWindowThreadProcessId(foreground, out var otherPid);
            if (pid != Environment.ProcessId || owner != Native.GetCurrentThreadId() || other == 0)
            {
                trace?.Invoke($"focus-host/rejected/window-pid={pid}/owner-thread={owner}/caller-thread={Native.GetCurrentThreadId()}/foreground-thread={other}", ResultKind.Unavailable);
                return false;
            }
            var peer = AutomationPipeSecurity.Current();
            job = new ActivationProcessJob();
            var request = new ActivationRequest(peer, window.ToInt64(), foreground.ToInt64(), owner, other, otherPid, job.Name);
            var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(timeoutMs);
            child = Process.Start(startInfo(encoded));
            if (child is null)
                return false;
            // If this process already owns foreground authority, delegate it to
            // this exact child before its job-membership gate permits native work.
            // A game-owned foreground may refuse delegation; the guarded native
            // attempt and final foreground observations still decide the result.
            var delegated = Native.AllowSetForegroundWindow(checked((uint)child.Id));
            trace?.Invoke("focus-host/foreground-delegated=" + delegated, ResultKind.Requested);
            try
            {
                job.Attach(child);
            }
            catch
            {
                // A child which could not join the job must not outlive this request.
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
                throw;
            }
            trace?.Invoke("focus-host/start/pid=" + child.Id, ResultKind.Requested);
            // Deadline cleanup must run even if the WPF dispatcher stops pumping.
            await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            trace?.Invoke($"focus-host/exit/code={child.ExitCode}/stage={ExitStage(child.ExitCode)}/foreground=0x{Native.GetForegroundWindow():X}", child.ExitCode == 0 ? ResultKind.Completed : ResultKind.Failed);
            // The host can finish just before Windows delivers its posted
            // activation. Its exit code is diagnostic, not input ownership.
            var observed = Native.GetForegroundWindow();
            var observedOwner = Native.GetWindowThreadProcessId(window, out var observedPid);
            var confirmed = !deadline.IsCancellationRequested && observed == window
                && observedOwner == owner && observedPid == peer.Pid;
            trace?.Invoke($"focus-host/observed/foreground=0x{observed:X}/target=0x{window:X}/owner-thread={observedOwner}/owner-pid={observedPid}", confirmed ? ResultKind.Completed : ResultKind.Unknown);
            return confirmed;
        }
        catch (OperationCanceledException)
        {
            trace?.Invoke("focus-host/cancelled-or-expired", ResultKind.Cancelled);
            return false;
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            trace?.Invoke("focus-host/error/" + error.GetType().Name + "/hresult=" + error.HResult.ToString(CultureInfo.InvariantCulture), ResultKind.Failed);
            return false;
        }
        finally
        {
            // Job closure terminates native calls even if they never returned to managed code.
            job?.Dispose();
            if (child is not null)
            {
                try
                {
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }
                catch (Exception error) when (error is TimeoutException or InvalidOperationException or Win32Exception)
                {
                    trace?.Invoke("focus-host/termination-unconfirmed", ResultKind.Unknown);
                }
                child.Dispose();
            }
            Interlocked.Exchange(ref pending, 0);
        }
    }

    private static string ExitStage(int code) => code switch
    {
        0 => "confirmed",
        1 => "native-guard-or-observation",
        2 => "foreground-request-refused",
        4 => "invalid-envelope-or-peer-read",
        5 => "target-or-original-foreground-changed",
        6 => "owning-job-unavailable",
        7 => "parent-process-mismatch",
        8 => "parent-identity-mismatch",
        >= 0x10000 and < 0x20000 => "queue-attachment-failed",
        >= 0x20000 and < 0x30000 => "activation-acknowledgement-failed",
        _ => "host-error"
    };

    internal static ProcessStartInfo CreateStartInfo(string encoded)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Application executable unavailable."))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Application entry assembly unavailable."));
        info.ArgumentList.Add(HostArgument);
        info.ArgumentList.Add(encoded);
        return info;
    }

    internal static int RunHost(string encoded)
    {
        try
        {
            if (string.IsNullOrEmpty(encoded) || encoded.Length > 4096)
                return 4;
            var request = JsonSerializer.Deserialize<ActivationRequest>(Convert.FromBase64String(encoded));
            if (request is null || request.Parent is null || string.IsNullOrEmpty(request.Job)
                || request.Window == 0 || request.Foreground == 0)
                return 4;
            if (request.Parent.Pid != ParentProcessId() || request.Parent.Pid == Environment.ProcessId)
                return 7;
            var actualParent = AutomationPipeSecurity.ReadPeer(request.Parent.Pid);
            if (actualParent.Started != request.Parent.Started || actualParent != request.Parent
                || !AutomationPipeSecurity.SameSession(AutomationPipeSecurity.Current(), actualParent))
                return 8;
            var membership = Stopwatch.StartNew();
            while (!ActivationProcessJob.ContainsCurrentProcess(request.Job))
            {
                if (membership.ElapsedMilliseconds >= 250)
                    return 6;
                Thread.Sleep(5);
            }
            var owner = Native.GetWindowThreadProcessId((nint)request.Window, out var pid);
            var other = Native.GetWindowThreadProcessId((nint)request.Foreground, out var otherPid);
            if (pid != actualParent.Pid || owner != request.Owner || other != request.Other || otherPid != request.OtherPid
                || Native.GetForegroundWindow() != (nint)request.Foreground)
                return 5;
            return WindowActivation.ExecuteNative((nint)request.Window, (nint)request.Foreground, owner);
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException or Win32Exception or UnauthorizedAccessException or InvalidOperationException or OverflowException)
        {
            return 4;
        }
    }

    private sealed record ActivationRequest(AutomationPipeSecurity.Peer Parent, long Window, long Foreground, uint Owner, uint Other, uint OtherPid, string Job);

    private static int ParentProcessId()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
        if (Process32First(snapshot, ref entry))
            do
            {
                if (entry.Pid == Environment.ProcessId)
                    return checked((int)entry.ParentPid);
            } while (Process32Next(snapshot, ref entry));
        throw new InvalidOperationException("Activation parent is no longer available.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, Pid;
        internal nuint Heap;
        internal uint Module, Threads, ParentPid;
        internal int Priority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Executable;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}

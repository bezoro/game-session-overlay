using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using ApolloOverlay.Automation;

namespace ApolloOverlay;

internal static class ElevatedWindowClose
{
    internal const string HostArgument = "--elevated-window-close";
    private const string PipePrefix = "Bezoro.GameSessionOverlay.Close.v1.";
    private const int ConsentMilliseconds = 60_000;
    internal const int ExchangeMilliseconds = 5_000;

    internal enum Receipt : byte
    {
        Accepted = 0, Rejected = 2, Refused = 3
    }

    internal readonly record struct HostRequest(Guid Channel, int ParentPid, long ParentStarted)
    {
        internal string PipeName => PipePrefix + Channel.ToString("N");
        internal string Encode() => FormattableString.Invariant($"{Channel:N}:{ParentPid}:{ParentStarted}");

        internal static bool TryDecode(string encoded, out HostRequest request)
        {
            request = default;
            if (encoded.Length is < 36 or > 64)
                return false;
            var fields = encoded.Split(':');
            if (fields is not [var channel, var pid, var started] || channel.Length != 32
                || !Guid.TryParseExact(channel, "N", out var nonce) || nonce == Guid.Empty
                || !int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid) || parentPid <= 0
                || !long.TryParse(started, NumberStyles.None, CultureInfo.InvariantCulture, out var parentStarted) || parentStarted <= 0)
                return false;
            request = new(nonce, parentPid, parentStarted);
            return encoded == request.Encode();
        }
    }

    internal readonly record struct Grant(nint Handle, int Pid, uint Thread, long Started, long Deadline)
    {
        private const byte Version = 1;
        internal const int Size = 33;

        internal byte[] Encode()
        {
            var bytes = new byte[Size];
            bytes[0] = Version;
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(1), Handle);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(9), Pid);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(13), Thread);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(17), Started);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(25), Deadline);
            return bytes;
        }

        internal static bool TryDecode(ReadOnlySpan<byte> bytes, out Grant grant)
        {
            grant = default;
            if (bytes.Length != Size || bytes[0] != Version)
                return false;
            var handle = BinaryPrimitives.ReadInt64LittleEndian(bytes[1..]);
            var pid = BinaryPrimitives.ReadInt32LittleEndian(bytes[9..]);
            var thread = BinaryPrimitives.ReadUInt32LittleEndian(bytes[13..]);
            var started = BinaryPrimitives.ReadInt64LittleEndian(bytes[17..]);
            var deadline = BinaryPrimitives.ReadInt64LittleEndian(bytes[25..]);
            if (handle == 0 || handle != (nint)handle || pid <= 0 || thread == 0 || started <= 0 || deadline <= 0)
                return false;
            grant = new((nint)handle, pid, thread, started, deadline);
            return true;
        }

        internal bool IsCurrent(long now) => Deadline > now && Deadline - now <= ExchangeMilliseconds;
    }

    /// <summary>Returns null for ordinary startup. Any internal close invocation exits before normal application setup.</summary>
    internal static int? Bootstrap(string[] args)
    {
        if (!args.Any(arg => arg.Equals(HostArgument, StringComparison.OrdinalIgnoreCase)
            || arg.StartsWith(HostArgument + "=", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (args is not [HostArgument, var encoded] || !HostRequest.TryDecode(encoded, out var request)
            || !OwnAppHost(out var executable))
            return (int)Receipt.Rejected;
        try
        {
            return RunHost(request, executable).GetAwaiter().GetResult();
        }
        catch (Exception error) when (AdministratorRestart.RestartError(error) || error is OperationCanceledException)
        {
            return (int)Receipt.Rejected;
        }
    }

    // This entry is called on the dispatcher. The callbacks guard the original operation and
    // the window destruction generation on that dispatcher. Token/process checks run on workers.
    internal static async Task<ActionResult> RequestAsync(WindowRef target, uint thread, Func<bool> prepareForConsent,
        Func<bool> originalWindowIsCurrent, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return Cancelled();
        if (!OwnAppHost(out var executable))
            return new(ResultKind.Unavailable, "Administrator closing requires the Windows application executable.");
        try
        {
            var current = await Task.Run(() =>
            {
                var parent = AutomationPipeSecurity.Current();
                return parent.Elevated || !IsLiveElevatedTarget(target, thread, parent) ? null : parent;
            });
            if (cancellation.IsCancellationRequested)
                return Cancelled();
            if (current is null)
                return TargetUnavailable();

            var request = new HostRequest(Guid.NewGuid(), current.Pid, current.Started);
            using var pipe = await Task.Run(() => AutomationPipeSecurity.CreateOneShot(request.PipeName));
            using var consent = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            if (!AdministratorRestart.TryReservePrompt())
                return new(ResultKind.Unavailable, "A Windows administrator approval request is already open.");
            var promptTransferred = false;
            Process? launched;
            ActionResult? consentFailure;
            long shellReturnedAt;
            long deadline;
            try
            {
                if (!originalWindowIsCurrent())
                    return TargetUnavailable();
                if (cancellation.IsCancellationRequested || !prepareForConsent())
                    return Cancelled();

                deadline = Environment.TickCount64 + ConsentMilliseconds;
                consent.CancelAfter(ConsentMilliseconds);
                var launchCancellation = consent.Token;
                promptTransferred = true;
                (launched, consentFailure, shellReturnedAt) = await AwaitConsentAsync(() =>
                {
                    if (launchCancellation.IsCancellationRequested || !IsLiveElevatedTarget(target, thread, current)
                        || Environment.TickCount64 >= deadline || launchCancellation.IsCancellationRequested)
                        return null;
                    return AdministratorRestart.StartElevated(executable, HostArgument, request.Encode());
                }, launchCancellation, cancellation);
            }
            finally
            {
                if (!promptTransferred)
                    AdministratorRestart.ReleasePrompt();
            }

            using var child = launched;
            if (consentFailure is not null)
                return consentFailure;
            if (child is null)
                return cancellation.IsCancellationRequested ? Cancelled()
                    : Environment.TickCount64 >= deadline ? Expired() : TargetUnavailable();
            var exchangeDeadline = Math.Min(deadline, shellReturnedAt + ExchangeMilliseconds);
            if (Environment.TickCount64 >= exchangeDeadline)
                return Expired();
            using var exchange = CancellationTokenSource.CreateLinkedTokenSource(consent.Token);
            exchange.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(0, exchangeDeadline - Environment.TickCount64)));
            await pipe.WaitForConnectionAsync(exchange.Token);
            var verified = await Task.Run(() => AutomationPipeSecurity.IsClientProcess(pipe, child.Id)
                && VerifiedProcess(child, current, child.Id, child.StartTime.ToUniversalTime().Ticks,
                    requireElevated: true, executable), exchange.Token);
            if (!verified)
                return new(ResultKind.Unavailable, "Windows could not verify the administrator close process.");
            var grant = new Grant(target.Handle, target.Pid, thread, target.Started, exchangeDeadline);
            return await ExchangeAsync(pipe, grant, () => !child.HasExited && originalWindowIsCurrent()
                && AutomationPipeSecurity.Alive(pipe), exchange.Token, cancellation);
        }
        catch (OperationCanceledException)
        {
            return cancellation.IsCancellationRequested ? Cancelled() : Expired();
        }
        catch (Exception error) when (AdministratorRestart.RestartError(error))
        {
            return new(ResultKind.Unavailable, "The administrator close request could not be verified.");
        }
    }

    // Owns the caller's prompt reservation until ShellExecute returns. The returned process
    // belongs to the caller. A process returned after cancellation is disposed here.
    internal static async Task<(Process? Child, ActionResult? Failure, long ReturnedAt)> AwaitConsentAsync(Func<Process?> start,
        CancellationToken consent, CancellationToken cancellation)
    {
        var launch = Task.Run(() =>
        {
            try
            {
                return (Child: start(), ReturnedAt: Environment.TickCount64);
            }
            finally
            {
                AdministratorRestart.ReleasePrompt();
            }
        });
        try
        {
            var completion = await launch.WaitAsync(consent);
            return (completion.Child, null, completion.ReturnedAt);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return (null, Cancelled(), 0);
        }
        catch (OperationCanceledException)
        {
            _ = DisposeLateLaunch(launch);
            return (null, cancellation.IsCancellationRequested ? Cancelled() : Expired(), 0);
        }
    }

    private static async Task DisposeLateLaunch(Task<(Process? Child, long ReturnedAt)> launch)
    {
        try
        {
            (await launch.ConfigureAwait(false)).Child?.Dispose();
        }
        catch (Exception error) when (AdministratorRestart.RestartError(error))
        {
            // The operation was cancelled or expired before ShellExecute returned.
        }
    }

    internal static async Task<ActionResult> ExchangeAsync(Stream pipe, Grant grant, Func<bool> canGrant,
        CancellationToken exchange, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return Cancelled();
        if (exchange.IsCancellationRequested || !grant.IsCurrent(Environment.TickCount64))
            return Expired();
        var bytes = grant.Encode();
        if (!canGrant())
            return TargetUnavailable();
        if (cancellation.IsCancellationRequested)
            return Cancelled();
        if (exchange.IsCancellationRequested || !grant.IsCurrent(Environment.TickCount64))
            return Expired();

        // Once a write starts, cancellation or a lost receipt cannot prove that it sent nothing.
        try
        {
            await pipe.WriteAsync(bytes, exchange);
            var receipt = new byte[1];
            if (await pipe.ReadAsync(receipt, exchange) == 1)
            {
                switch ((Receipt)receipt[0])
                {
                    case Receipt.Accepted:
                        return new(ResultKind.Requested, "WM_CLOSE was queued for the verified window.");
                    case Receipt.Rejected:
                        return TargetUnavailable();
                    case Receipt.Refused:
                        return new(ResultKind.Failed, "Windows rejected the administrator close request.");
                }
            }
        }
        catch (Exception error) when (AdministratorRestart.RestartError(error) || error is OperationCanceledException)
        {
        }

        return new(ResultKind.Unknown, "Administrator closing could not be confirmed. Check the window and any save dialogs.");
    }

    private static async Task<int> RunHost(HostRequest request, string executable)
    {
        var current = AutomationPipeSecurity.Current();
        if (!current.Elevated || current.Pid == request.ParentPid)
            return (int)Receipt.Rejected;
        using var timeout = new CancellationTokenSource(ExchangeMilliseconds);
        using var pipe = new NamedPipeClientStream(".", request.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token);
        using var parent = Process.GetProcessById(request.ParentPid);
        bool ParentIsCurrent() => AutomationPipeSecurity.IsServerProcess(pipe, request.ParentPid)
            && VerifiedProcess(parent, current, request.ParentPid, request.ParentStarted, requireElevated: false, executable);
        if (!ParentIsCurrent())
            return (int)Receipt.Rejected;
        var bytes = new byte[Grant.Size];
        await pipe.ReadExactlyAsync(bytes, timeout.Token);
        var receipt = Receipt.Rejected;
        try
        {
            if (Grant.TryDecode(bytes, out var grant))
                receipt = Dispatch(grant, timeout.Token,
                    () => ParentIsCurrent() && AutomationPipeSecurity.Current() == current,
                    () => IsLiveElevatedTarget(grant.Handle, grant.Pid, grant.Thread, grant.Started, current),
                    () => AutomationPipeSecurity.Alive(pipe), WindowsPlatform.PostCloseMessage);
        }
        catch (Exception error) when (AdministratorRestart.RestartError(error))
        {
            // Unreadable identity is a pre-post rejection.
        }

        try
        {
            await pipe.WriteAsync(new byte[] { (byte)receipt }, timeout.Token);
        }
        catch (Exception error) when (AdministratorRestart.RestartError(error) || error is OperationCanceledException)
        {
            // Report the observed disposition as the exit code even if the receipt channel closed.
        }

        return (int)receipt;
    }

    internal static Receipt Dispatch(Grant grant, CancellationToken cancellation, Func<bool> parentIsCurrent,
        Func<bool> targetIsCurrent, Func<bool> channelIsLive, Func<nint, ClosePost> post)
    {
        if (cancellation.IsCancellationRequested || !grant.IsCurrent(Environment.TickCount64)
            || !parentIsCurrent() || !targetIsCurrent() || !channelIsLive()
            || cancellation.IsCancellationRequested || !grant.IsCurrent(Environment.TickCount64))
            return Receipt.Rejected;
        return post(grant.Handle) == ClosePost.Accepted ? Receipt.Accepted : Receipt.Refused;
    }

    private static bool OwnAppHost(out string executable) => AdministratorRestart.OwnExecutable(out executable)
        && OperatingSystem.IsWindows() && Path.GetFileName(executable).Equals("ApolloOverlay.exe", StringComparison.OrdinalIgnoreCase);

    private static bool VerifiedProcess(Process process, AutomationPipeSecurity.Peer current, int pid, long started,
        bool requireElevated, string executable)
    {
        if (process.Id != pid || process.HasExited || process.StartTime.ToUniversalTime().Ticks != started)
            return false;
        var actual = AutomationPipeSecurity.ReadPeer(pid);
        return MatchesProcess(actual, current, pid, started, requireElevated)
            && AdministratorRestart.SameExecutable(AdministratorRestart.ProcessImage(pid), executable) && !process.HasExited;
    }

    internal static bool MatchesProcess(AutomationPipeSecurity.Peer actual, AutomationPipeSecurity.Peer current,
        int pid, long started, bool requireElevated) =>
        current.Pid != pid && actual.Pid == pid && actual.Started == started
        && actual.Elevated == requireElevated && current.Elevated != requireElevated
        && AutomationPipeSecurity.SameSession(current, actual);

    private static bool IsLiveElevatedTarget(WindowRef target, uint thread, AutomationPipeSecurity.Peer current) =>
        IsLiveElevatedTarget(target.Handle, target.Pid, thread, target.Started, current);

    private static bool IsLiveElevatedTarget(nint handle, int pid, uint thread, long started, AutomationPipeSecurity.Peer current)
    {
        if (!WindowsPlatform.HasOwner(handle, pid, thread))
            return false;
        var target = AutomationPipeSecurity.ReadPeer(pid);
        return ElevatedTarget(target, current, pid, started) && WindowsPlatform.CanPostElevatedClose(handle, pid, thread, started);
    }

    internal static bool ElevatedTarget(AutomationPipeSecurity.Peer target, AutomationPipeSecurity.Peer current, int pid, long started) =>
        target.Elevated && target.Pid == pid && target.Started == started && target.Pid != current.Pid
        && AutomationPipeSecurity.SameSession(current, target);

    private static ActionResult Cancelled() => new(ResultKind.Cancelled, "Administrator closing was cancelled before authorization.");
    private static ActionResult Expired() => new(ResultKind.Unavailable, "Administrator close approval expired. Confirm again to retry.");
    private static ActionResult TargetUnavailable() => new(ResultKind.Unavailable, "The exact elevated window could not be verified. No administrator close was sent.");
}

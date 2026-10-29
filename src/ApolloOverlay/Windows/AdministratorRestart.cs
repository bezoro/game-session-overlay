using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApolloOverlay.Automation;
using Microsoft.Win32.SafeHandles;

namespace ApolloOverlay;

/// <summary>Hands an explicit restart to a UAC-approved copy of this exact executable.</summary>
internal static class AdministratorRestart
{
    internal const string HostArgument = "--administrator-restart";
    private const string EventPrefix = @"Local\Bezoro.GameSessionOverlay.Admin.";
    private const int ReadyTimeoutMilliseconds = 30_000;
    private const int ParentExitTimeoutMilliseconds = 15_000;
    internal sealed record Request(AutomationPipeSecurity.Peer Parent, string Nonce);

    internal static Task<ActionResult> RequestAsync(CancellationToken cancellation = default) =>
        Task.Run(() => RequestCore(cancellation), CancellationToken.None);

    private static ActionResult RequestCore(CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return Cancelled();
        if (!OperatingSystem.IsWindows() || !OwnExecutable(out var executable))
            return new(ResultKind.Unavailable, "Administrator restart requires the Windows application executable.");
        if (AutomationPipeSecurity.Elevated())
            return new(ResultKind.Unavailable, "The overlay is already running as administrator.");
        try
        {
            var current = AutomationPipeSecurity.Current();
            var request = new Request(current, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventPrefix + request.Nonce, out var created);
            using var accepted = new EventWaitHandle(false, EventResetMode.ManualReset, EventPrefix + request.Nonce + ".accepted", out var acceptedCreated);
            if (!created || !acceptedCreated)
                return new(ResultKind.Failed, "The restart handshake could not be created. This overlay remains open.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add(HostArgument);
            start.ArgumentList.Add(Encode(request));
            // This is the only UAC request. Guide, focus recovery and sign-in never use this path.
            using var child = Process.Start(start);
            if (child is null)
                return new(ResultKind.Failed, "Windows did not start the administrator session. This overlay remains open.");
            var expected = AutomationPipeSecurity.ReadPeer(child.Id);
            if (!MatchesPeer(expected, expected, current, requireElevated: true)
                || !SameExecutable(ProcessImage(child.Id), executable))
                return new(ResultKind.Failed, "The approved session belongs to a different user or application. This overlay remains open.");
            using var exited = new ProcessExitHandle(child);
            if (WaitHandle.WaitAny([ready, cancellation.WaitHandle, exited], ReadyTimeoutMilliseconds) != 0 || cancellation.IsCancellationRequested)
                return cancellation.IsCancellationRequested ? Cancelled() : new(ResultKind.Unknown, "The administrator session did not become ready. This overlay remains open.");
            var actual = AutomationPipeSecurity.ReadPeer(child.Id);
            if (!MatchesPeer(expected, actual, current, requireElevated: true)
                || actual.Pid != child.Id || child.HasExited || !SameExecutable(ProcessImage(child.Id), executable))
                return new(ResultKind.Failed, "Windows could not verify the administrator session. This overlay remains open.");
            if (!accepted.Set())
                return new(ResultKind.Failed, "The administrator session could not accept the handoff. This overlay remains open.");
            return new(ResultKind.Completed, "The administrator session is ready. The current overlay can now close.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return Cancelled();
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (Exception error) when (RestartError(error))
        {
            return new(ResultKind.Failed, "The administrator restart could not be verified. This overlay remains open.");
        }
    }

    /// <summary>Returns an exit code for rejected internal requests, or null to continue normal startup.</summary>
    internal static int? Bootstrap(ref string[] args)
    {
        if (!args.Contains(HostArgument, StringComparer.Ordinal))
            return null;
        if (args is not [HostArgument, var encoded] || !TryDecode(encoded, out var request))
            return 2;
        try
        {
            if (!OperatingSystem.IsWindows() || !OwnExecutable(out var executable))
                return 2;
            var current = AutomationPipeSecurity.Current();
            if (!current.Elevated || request!.Parent.Pid == current.Pid)
                return 2;
            var actual = AutomationPipeSecurity.ReadPeer(request.Parent.Pid);
            if (!MatchesPeer(request.Parent, actual, current, requireElevated: false)
                || !SameExecutable(ProcessImage(actual.Pid), executable))
                return 2;
            using var parent = Process.GetProcessById(actual.Pid);
            using var ready = EventWaitHandle.OpenExisting(EventPrefix + request.Nonce);
            using var accepted = EventWaitHandle.OpenExisting(EventPrefix + request.Nonce + ".accepted");
            if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != actual.Started)
                return 2;
            if (!ready.Set() || !accepted.WaitOne(ReadyTimeoutMilliseconds) || !parent.WaitForExit(ParentExitTimeoutMilliseconds))
                return 2;
            args = ["--show"];
            return null;
        }
        catch (Exception error) when (RestartError(error))
        {
            return 2;
        }
    }

    internal static bool MatchesPeer(AutomationPipeSecurity.Peer expected, AutomationPipeSecurity.Peer actual,
        AutomationPipeSecurity.Peer current, bool requireElevated) => expected.Pid > 0 && expected.Started > 0
        && expected.Pid == actual.Pid && expected.Started == actual.Started
        && AutomationPipeSecurity.SameSession(expected, actual)
        && AutomationPipeSecurity.SameSession(current, actual)
        && (!requireElevated || actual.Elevated);

    internal static bool SameExecutable(string? actual, string expected) => !string.IsNullOrEmpty(actual)
        && Path.IsPathFullyQualified(actual) && Path.IsPathFullyQualified(expected)
        && string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);

    internal static string Encode(Request request) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request));

    internal static bool TryDecode(string encoded, out Request? request)
    {
        request = null;
        if (encoded.Length is < 1 or > 4096)
            return false;
        try
        {
            request = JsonSerializer.Deserialize<Request>(Convert.FromBase64String(encoded));
            return request?.Parent is { Pid: > 0, Started: > 0, Session: >= 0 } parent
                && !string.IsNullOrWhiteSpace(parent.User) && !string.IsNullOrWhiteSpace(parent.Logon)
                && request.Nonce is { Length: 64 } && request.Nonce.All(c => char.IsAsciiHexDigit(c));
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return false;
        }
    }

    private static bool OwnExecutable(out string executable)
    {
        executable = Environment.ProcessPath ?? "";
        return Path.IsPathFullyQualified(executable)
            && string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(executable);
    }

    private static string ProcessImage(int pid)
    {
        using var process = OpenProcess(0x1000, false, checked((uint)pid));
        var image = new StringBuilder(32768);
        var length = image.Capacity;
        if (process.IsInvalid || !QueryFullProcessImageNameW(process, 0, image, ref length))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return image.ToString();
    }

    private static bool RestartError(Exception error) => error is IOException or UnauthorizedAccessException
        or Win32Exception or InvalidOperationException or ArgumentException or System.Security.SecurityException;
    private static ActionResult Cancelled() => new(ResultKind.Cancelled, "Administrator restart was cancelled. This overlay remains open.");

    // The enclosing Process owns the handle and outlives this wait-only wrapper.
    private sealed class ProcessExitHandle : WaitHandle
    {
        internal ProcessExitHandle(Process process) => SafeWaitHandle = new SafeWaitHandle(process.SafeHandle.DangerousGetHandle(), ownsHandle: false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref int length);
}

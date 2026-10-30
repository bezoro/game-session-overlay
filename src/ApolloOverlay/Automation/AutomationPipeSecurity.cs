using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ApolloOverlay.Automation;

public static class AutomationPipeSecurity
{
    public sealed record Peer(string User, string Logon, int Session, int Pid, long Started, bool Elevated = false);
    public static Peer Current() => ReadPeer(Environment.ProcessId);
    public static string DefaultName
    {
        get
        {
            var p = Current();
            return "Bezoro.Overlay.v1." + p.Session + "." + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.User)))[..16];
        }
    }

    public static bool SameSession(Peer a, Peer b) => a.User == b.User && a.Logon == b.Logon && a.Session == b.Session;
    public static bool Elevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static Peer ReadPeer(int pid)
    {
        using var process = Process.GetProcessById(pid);
        using var handle = OpenProcess(0x1000, false, (uint)pid);
        if (handle.IsInvalid || !OpenProcessToken(handle, 8, out var token))
            throw new UnauthorizedAccessException("Peer token unavailable.");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            var user = identity.User?.Value ?? throw new UnauthorizedAccessException();
            var info = Marshal.AllocHGlobal(4);
            try
            {
                if (!GetTokenInformation(token, 20, info, 4, out _))
                    throw new UnauthorizedAccessException("Peer elevation unavailable.");
                return new(user, LogonSid(token), process.SessionId, pid, process.StartTime.ToUniversalTime().Ticks, Marshal.ReadInt32(info) != 0);
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }
    }

    private static string LogonSid(SafeAccessTokenHandle token)
    {
        // TokenLogonSid returns TOKEN_GROUPS; WindowsIdentity.Groups omits this special group.
        GetTokenInformation(token, 28, 0, 0, out var length);
        if (length < 16 || length > 65536)
            throw new UnauthorizedAccessException("Logon SID buffer unavailable.");
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetTokenInformation(token, 28, buffer, length, out _) || Marshal.ReadInt32(buffer) != 1)
                throw new UnauthorizedAccessException("Logon SID unavailable.");
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4)).Value;
            if (!sid.StartsWith("S-1-5-5-", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Unexpected logon SID.");
            return sid;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool ClientAllowed(NamedPipeServerStream pipe)
    {
        if (!LocalChannel(ClientLocalityError(pipe)))
            return false; // ERROR_PIPE_LOCAL only; success is an SMB connection, even to this machine.
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
            return false;
        try
        {
            return SameSession(Current(), ReadPeer(checked((int)pid)));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    internal static int ClientLocalityError(NamedPipeServerStream pipe)
    {
        var computer = new System.Text.StringBuilder(256);
        return GetNamedPipeClientComputerNameW(pipe.SafePipeHandle, computer, 512) ? 0 : Marshal.GetLastPInvokeError();
    }

    internal static bool LocalChannel(int error) => error == 229;
    public static bool ServerAllowed(NamedPipeClientStream pipe, AutomationReply handshake, bool requireElevated = false)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != handshake.Pid)
            return false;
        try
        {
            var peer = ReadPeer((int)pid);
            return peer.Started == handshake.Started && SameSession(Current(), peer) && (!requireElevated || peer.Elevated);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    public static bool Alive(PipeStream stream) => stream.IsConnected && PeekNamedPipe(stream.SafePipeHandle, 0, 0, 0, 0, 0);
    internal static NamedPipeServerStream Create(string name, bool bridge, bool first)
    {
        if (!bridge)
            return new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : 0));
        var sid = Current().Logon;
        var sddl = $"D:P(A;;GRGW;;;{sid})S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
            throw new UnauthorizedAccessException("Restricted bridge ACL/label unavailable.");
        try
        {
            var security = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = descriptor
            };
            var handle = CreateNamedPipeW(@"\\.\pipe\" + name, 3 | 0x40000000u | (first ? 0x80000u : 0), 8, 255, 65536, 16384, 0, ref security);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new UnauthorizedAccessException("Restricted bridge creation refused.");
            }

            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Inherit;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint rights, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information, nint buffer, uint length, out uint required);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientComputerNameW(SafePipeHandle pipe, System.Text.StringBuilder computer, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafePipeHandle pipe, nint buffer, uint size, nint read, nint available, nint left);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint version, out nint descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint mode, uint pipeMode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}

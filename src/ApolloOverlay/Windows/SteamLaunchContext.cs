using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ApolloOverlay;

internal sealed record SteamProcessEntry(uint Pid, uint ParentPid, string Name);
internal sealed record SteamProcessIdentity(uint Pid, string Name, long Started, int SessionId);
internal sealed record SteamProcessSnapshot(IReadOnlyList<SteamProcessEntry> Entries, bool Complete);
/// <summary>Observes launch ancestry, never current overlay injection or visibility.</summary>
internal static class SteamLaunchContext
{
    internal const int ProcessLimit = 4096, AncestorLimit = 8;
    internal static bool Observe(WindowRef target, Func<WindowRef, bool> valid, Func<SteamProcessSnapshot>? capture = null, Func<uint, SteamProcessIdentity?>? read = null)
    {
        if (target.Protected || target.Launcher || target.Handle == 0 || target.Pid <= 0 || target.Started <= 0)
            return false;
        try
        {
            if (!valid(target))
                return false;
            var confirmed = Matches(target, (capture ?? Capture)(), read ?? ReadIdentity);
            return confirmed && valid(target);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool Matches(WindowRef target, SteamProcessSnapshot snapshot, Func<uint, SteamProcessIdentity?> read)
    {
        if (!snapshot.Complete || snapshot.Entries.Count > ProcessLimit || target.Pid <= 0)
            return false;
        var entries = new Dictionary<uint, SteamProcessEntry>();
        foreach (var entry in snapshot.Entries)
            if (!entries.TryAdd(entry.Pid, entry))
                return false;
        if (!entries.TryGetValue((uint)target.Pid, out var child))
            return false;
        var identity = read(child.Pid);
        if (!Coherent(child, identity) || identity!.Started != target.Started || !string.Equals(identity.Name, target.ProcessName + ".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var session = identity.SessionId;
        var visited = new HashSet<uint>
        {
            child.Pid
        };
        for (var depth = 0; depth < AncestorLimit; depth++)
        {
            if (child.ParentPid == 0 || !visited.Add(child.ParentPid) || !entries.TryGetValue(child.ParentPid, out var parent))
                return false;
            var parentIdentity = read(parent.Pid);
            if (!Coherent(parent, parentIdentity) || parentIdentity!.SessionId != session || parentIdentity.Started > identity.Started)
                return false;
            if (string.Equals(parent.Name, "steam.exe", StringComparison.OrdinalIgnoreCase))
                return true;
            child = parent;
            identity = parentIdentity;
        }

        return false;
    }

    private static bool Coherent(SteamProcessEntry entry, SteamProcessIdentity? identity) => identity is not null && identity.Pid == entry.Pid && identity.Started > 0 && identity.SessionId >= 0 && string.Equals(entry.Name, identity.Name, StringComparison.OrdinalIgnoreCase);
    internal static SteamProcessIdentity? ReadIdentity(uint pid)
    {
        if (pid == 0 || pid > int.MaxValue)
            return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName + ".exe";
            var started = process.StartTime.ToUniversalTime().Ticks;
            var session = process.SessionId;
            return process.HasExited ? null : new(pid, name, started, session);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static SteamProcessSnapshot Capture()
    {
        using var handle = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS only; no module access.
        if (handle.IsInvalid)
            return new([], false);
        var entry = new ProcessEntry
        {
            Size = (uint)Marshal.SizeOf<ProcessEntry>(),
            Name = ""
        };
        var entries = new List<SteamProcessEntry>();
        if (!Process32FirstW(handle, ref entry))
            return new(entries, Marshal.GetLastPInvokeError() == 18);
        while (true)
        {
            if (entries.Count == ProcessLimit)
                return new(entries, false);
            entries.Add(new(entry.Pid, entry.ParentPid, entry.Name));
            if (!Process32NextW(handle, ref entry))
                return new(entries, Marshal.GetLastPInvokeError() == 18);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public nuint DefaultHeap;
        public uint ModuleId, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Name;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
}

using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ApolloOverlay;

/// <summary>Maintains the application's searchable shortcut without replacing another target.</summary>
internal static class StartMenuRegistration
{
    internal const string ShortcutName = "Game Session Overlay.lnk";
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");

    internal static bool TryEnsure(string executable, string? programsDirectory = null)
    {
        object? instance = null;
        string? temporary = null;
        try
        {
            if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
                return false;
            var programs = programsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (string.IsNullOrEmpty(programs))
                return false;
            Directory.CreateDirectory(programs);
            var shortcut = Path.Combine(programs, ShortcutName);
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass)!);
            if (instance is not IShellLink link || instance is not IPersistFile file)
                return false;
            if (File.Exists(shortcut))
            {
                file.Load(shortcut, 0);
                var target = new StringBuilder(32768);
                Check(link.GetPath(target, target.Capacity, 0, 4));
                if (!MatchesInstallation(target.ToString(), executable))
                    return false;
                var arguments = new StringBuilder(1024);
                Check(link.GetArguments(arguments, arguments.Capacity));
                if (string.Equals(target.ToString(), executable, StringComparison.OrdinalIgnoreCase) && arguments.ToString() == "--show")
                    return true;
            }
            Check(link.SetPath(executable));
            Check(link.SetArguments("--show"));
            Check(link.SetWorkingDirectory(Path.GetDirectoryName(executable)!));
            Check(link.SetDescription("Open Game Session Overlay"));
            Check(link.SetIconLocation(executable, 0));
            Check(link.SetShowCmd(1));
            temporary = Path.Combine(programs, ".GameSessionOverlay-" + Guid.NewGuid().ToString("N") + ".tmp");
            file.Save(temporary, true);
            var existing = File.Exists(shortcut);
            File.Move(temporary, shortcut, true);
            temporary = null;
            SHChangeNotify(existing ? 0x2000u : 2u, 0x2005, shortcut, 0);
            SHChangeNotify(0x1000, 0x2005, programs, 0);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
                Marshal.FinalReleaseComObject(instance);
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    internal static bool MatchesInstallation(string target, string executable)
    {
        if (!Path.IsPathFullyQualified(target) || !Path.IsPathFullyQualified(executable))
            return false;
        var root = Path.GetDirectoryName(executable)!;
        return string.Equals(target, executable, StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, Path.Combine(root, "current", "ApolloOverlay.exe"), StringComparison.OrdinalIgnoreCase);
    }

    internal static void TryRemoveMatching(string executable)
    {
        object? instance = null;
        try
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var shortcut = Path.Combine(programs, ShortcutName);
            if (!File.Exists(shortcut))
                return;
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass)!);
            if (instance is not IShellLink link || instance is not IPersistFile file)
                return;
            file.Load(shortcut, 0);
            var target = new StringBuilder(32768);
            Check(link.GetPath(target, target.Capacity, 0, 4));
            if (MatchesInstallation(target.ToString(), executable))
            {
                File.Delete(shortcut);
                SHChangeNotify(4, 0x2005, shortcut, 0);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException)
        {
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
                Marshal.FinalReleaseComObject(instance);
        }
    }

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint change, uint flags, string item, nint other);

    // IShellLinkW requires its complete native method order, including unused slots.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, nint data, uint flags);
        [PreserveSig] int GetIDList(out nint item);
        [PreserveSig] int SetIDList(nint item);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        [PreserveSig] int GetHotkey(out short key);
        [PreserveSig] int SetHotkey(short key);
        [PreserveSig] int GetShowCmd(out int command);
        [PreserveSig] int SetShowCmd(int command);
        [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(nint window, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}

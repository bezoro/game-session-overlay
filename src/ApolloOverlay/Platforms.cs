using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ApolloOverlay;

public readonly record struct ShortcutDispatch(long Revision, int Expected, uint? Accepted, int CleanupExpected, uint? CleanupAccepted, Automation.SteamSequenceEvidence? Sequence = null);
public interface IPlatform : IDisposable
{
    bool IsPreview
    {
        get;
    }

    WindowRef? Foreground();
    IReadOnlyList<WindowRef> Windows();
    bool Valid(WindowRef window);
    ImageSource? Icon(WindowRef window) => null;
    IReadOnlyList<DisplayRef> Displays();
    ModuleEvidence SteamEvidence(WindowRef? window);
    bool HasSteamLaunchContext(WindowRef? window) => false;
    Task<ActionResult> Switch(WindowRef window);
    Task<ActionResult> Close(WindowRef window, CancellationToken cancellation);
    Task<ActionResult> Minimize(WindowRef window, CancellationToken cancellation) => Task.FromResult(new ActionResult(ResultKind.Unavailable, "Minimização indisponível nesta plataforma."));
    Task<ActionResult> Shortcut(WindowRef window, string keys);
    Task<ActionResult> Shortcut(WindowRef window, string keys, CancellationToken cancellation, Func<bool> continuing) => cancellation.IsCancellationRequested || !continuing() ? Task.FromResult(new ActionResult(ResultKind.Cancelled, "Cancelado antes do envio.")) : IsPreview ? Shortcut(window, keys) : Task.FromResult(new ActionResult(ResultKind.Unavailable, "Esta plataforma não oferece atalho cancelável."));
    async Task<ActionResult> Focus(WindowRef window, CancellationToken cancellation, Func<bool> continuing)
    {
        if (cancellation.IsCancellationRequested || !continuing())
            return new(ResultKind.Cancelled, "Cancelado antes do foco.");
        var result = await Switch(window);
        return cancellation.IsCancellationRequested || !continuing() ? new(ResultKind.Unknown, "Foco solicitado; observação cancelada.") : result;
    }

    ShortcutDispatch? LastShortcutDispatch => null;

    int SteamOwnedKeys => 0;

    Task<ActionResult> Shortcut(WindowRef window, string keys, CancellationToken cancellation, Func<bool> continuing, Action<ShortcutDispatch> progress) => Shortcut(window, keys, cancellation, continuing);
    Task<ActionResult> TaskView() => Task.FromResult(new ActionResult(ResultKind.Unavailable, "Visão de tarefas indisponível nesta plataforma."));
    bool CanSteamMenu(WindowRef? window) => false;
    Task<ActionResult> SteamMenu(WindowRef window) => Task.FromResult(new ActionResult(ResultKind.Unavailable, "Menu Big Picture não confirmado."));
    Task<ActionResult> SteamMenu(WindowRef window, CancellationToken cancellation, Func<bool> continuing) => cancellation.IsCancellationRequested || !continuing() ? Task.FromResult(new ActionResult(ResultKind.Cancelled, "Cancelado antes do envio.")) : IsPreview ? SteamMenu(window) : Task.FromResult(new ActionResult(ResultKind.Unavailable, "Esta plataforma não oferece menu cancelável."));
    ActionResult Launch(bool playnite, string? path);
}

public sealed class PreviewPlatform : IPlatform
{
    public bool IsPreview => true;

    private readonly List<WindowRef> windows = [];
    public PreviewPlatform(WindowRef? ownedFixture = null)
    {
        if (ownedFixture is not null)
            windows.Add(ownedFixture);
    }

    public WindowRef? Foreground() => windows.FirstOrDefault();
    public IReadOnlyList<WindowRef> Windows() => windows.ToArray();
    public bool Valid(WindowRef window) => windows.Any(window.Same);
    public IReadOnlyList<DisplayRef> Displays() => [new("TELA DE EXEMPLO", 0, 0, 1920, 1080, true), new("SEGUNDA TELA DE EXEMPLO", -1280, 0, 1280, 720, false)];
    public ModuleEvidence SteamEvidence(WindowRef? window) => ModuleEvidence.Unknown;
    public Task<ActionResult> Switch(WindowRef window) => Task.FromResult(new ActionResult(ResultKind.Simulated, $"Voltaria para {window.Title}."));
    public Task<ActionResult> Close(WindowRef window, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return Task.FromResult(new ActionResult(ResultKind.Cancelled, "Nada foi enviado."));
        windows.RemoveAll(window.Same);
        return Task.FromResult(new ActionResult(ResultKind.Simulated, $"Fecharia somente {window.Title}."));
    }

    public Task<ActionResult> Shortcut(WindowRef window, string keys) => Task.FromResult(new ActionResult(ResultKind.Simulated, $"Solicitaria {keys}; nenhum comando externo foi enviado."));
    public Task<ActionResult> Shortcut(WindowRef window, string keys, CancellationToken cancellation, Func<bool> continuing) => Task.FromResult(cancellation.IsCancellationRequested || !continuing() ? new ActionResult(ResultKind.Cancelled, "Cancelado sem envio.") : new ActionResult(ResultKind.Simulated, "Prévia: nenhum atalho externo enviado."));
    public Task<ActionResult> Minimize(WindowRef window, CancellationToken cancellation) => Task.FromResult(cancellation.IsCancellationRequested ? new ActionResult(ResultKind.Cancelled, "Nada foi enviado.") : !Valid(window) || window.Protected ? new ActionResult(ResultKind.Unavailable, "Janela indisponível para minimizar.") : new ActionResult(ResultKind.Simulated, $"Minimizaria {window.Title}; nenhum comando externo foi enviado."));
    public ActionResult Launch(bool playnite, string? path) => new(ResultKind.Simulated, playnite ? "Solicitaria o Playnite em tela cheia." : "Solicitaria Steam Big Picture.");
    public void Dispose()
    {
    }
}

public interface IWindowIconNotifications
{
    // Raised off the UI thread only after the window identity was revalidated.
    event Action<WindowRef>? IconAvailable;
}

public sealed class WindowsPlatform : IPlatform, IWindowEffects, IWindowIconNotifications
{
    public ShortcutDispatch? LastShortcutDispatch
    {
        get; private set;
    }
    public int SteamOwnedKeys
    {
        get; private set;
    }

    private long shortcutRevision;
    private bool disposed;
    private readonly Func<Native.Input[], uint> keySender;
    private readonly WindowIconCache icons;
    public event Action<WindowRef>? IconAvailable { add => icons.Available += value; remove => icons.Available -= value; }

    public bool IsPreview => false;

    private readonly Dictionary<nint, long> generations = [];
    private long sequence;
    private readonly Native.WinEvent destroyCallback;
    private readonly nint hook;
    private readonly bool allowOwnedFixture;
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "dwm",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "ApplicationFrameHost",
        "GameBar",
        "GameBarFTServer",
        "GameBarPresenceWriter",
        "gameoverlayui",
        "XboxGameBarWidgets",
        "steamwebhelper",
        "winlogon",
        "csrss"
    };
    public WindowsPlatform(bool allowOwnedFixture = false) : this(allowOwnedFixture, inputs => Native.SendInput((uint)inputs.Length, inputs, 40))
    {
    }

    internal WindowsPlatform(bool allowOwnedFixture, Func<Native.Input[], uint> keySender)
    {
        this.keySender = keySender;
        this.allowOwnedFixture = allowOwnedFixture;
        icons = new WindowIconCache(LoadIcon);
        destroyCallback = (_, _, hwnd, obj, child, _, _) =>
        {
            lock (generations)
            {
                if (hwnd != 0 && obj == 0 && child == 0 && generations.ContainsKey(hwnd))
                    generations[hwnd] = ++sequence;
            }
        };
        hook = Native.SetWinEventHook(0x8001, 0x8001, 0, destroyCallback, 0, 0, 0); // EVENT_OBJECT_DESTROY, OUTOFCONTEXT on this dispatcher.
    }

    private WindowRef? Read(nint hwnd)
    {
        lock (generations)
            return ReadCore(hwnd);
    }

    private WindowRef? ReadCore(nint hwnd)
    {
        if (hwnd == 0 || !Native.IsWindow(hwnd))
            return null;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var title = new StringBuilder(513);
        Native.GetWindowText(hwnd, title, title.Capacity);
        if (title.Length == 0)
            return null;
        var className = new StringBuilder(128);
        Native.GetClassName(hwnd, className, className.Capacity);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            var protectedTarget = pid == Environment.ProcessId && !allowOwnedFixture || ProtectedProcesses.Contains(name) || className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd";
            if (!generations.ContainsKey(hwnd))
            {
                if (generations.Count >= 256)
                {
                    foreach (var stale in generations.Keys.Where(h => !Native.IsWindow(h)).ToArray())
                        generations.Remove(stale);
                    if (generations.Count >= 256)
                        return null;
                }

                generations[hwnd] = ++sequence;
            }

            return new(hwnd, (int)pid, process.StartTime.ToUniversalTime().Ticks, title.ToString(), name, protectedTarget, generations.GetValueOrDefault(hwnd));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException)
        {
            return new(hwnd, (int)pid, 0, title.ToString(), "identidade indisponível", true);
        }
    }

    public WindowRef? Foreground() => Read(Native.GetForegroundWindow());
    public IReadOnlyList<WindowRef> Windows()
    {
        var windows = new List<WindowRef>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (Relevant(hwnd) && Read(hwnd) is { } window && (window.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) || !window.Protected))
                windows.Add(window);
            return true;
        }, 0);
        return windows;
    }

    private bool Relevant(nint hwnd)
    {
        if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, 4) != 0 || (Native.GetWindowLongPtr(hwnd, -20).ToInt64() & 0x80) != 0)
            return false;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId && !allowOwnedFixture)
            return false;
        var type = new StringBuilder(128);
        Native.GetClassName(hwnd, type, type.Capacity);
        if (type.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;
        return Native.DwmGetWindowAttribute(hwnd, 14, out var cloaked, 4) != 0 || cloaked == 0;
    }

    public bool Valid(WindowRef window)
    {
        if (Volatile.Read(ref disposed))
            return false;
        var current = Read(window.Handle);
        return current is not null && window.Same(current) && Relevant(window.Handle) && (!current.Protected || current.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) && hook != 0;
    }

    public IReadOnlyList<DisplayRef> Displays()
    {
        var displays = new List<DisplayRef>();
        Native.EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new Native.MonitorInfo
            {
                Size = Marshal.SizeOf<Native.MonitorInfo>(),
                Device = ""
            };
            if (Native.GetMonitorInfo(monitor, ref info))
                displays.Add(new(info.Device, info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top, (info.Flags & 1) != 0));
            return true;
        }, 0);
        return displays;
    }

    public ModuleEvidence SteamEvidence(WindowRef? window)
    {
        if (window is null || !Valid(window))
            return ModuleEvidence.Unknown;
        try
        {
            using var process = Process.GetProcessById(window.Pid);
            foreach (ProcessModule module in process.Modules)
            {
                var loaded = module.ModuleName.Equals("gameoverlayrenderer.dll", StringComparison.OrdinalIgnoreCase) || module.ModuleName.Equals("gameoverlayrenderer64.dll", StringComparison.OrdinalIgnoreCase);
                if (loaded)
                    return Valid(window) ? ModuleEvidence.Loaded : ModuleEvidence.Unknown;
            }

            return ModuleEvidence.Absent;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException)
        {
            return ModuleEvidence.Denied;
        }
    }

    public bool HasSteamLaunchContext(WindowRef? window) => window is not null && SteamLaunchContext.Observe(window, Valid);
    public Task<ActionResult> Switch(WindowRef window) => new GuardedActions(this).Switch(window);
    public ImageSource? Icon(WindowRef window) => icons.Get(window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CopyIcon(nint icon);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
    private ImageSource? LoadIcon(WindowRef window)
    {
        if (!Valid(window))
            return null;
        nint owned = 0;
        try
        {
            Native.SendMessageTimeout(window.Handle, 0x7F, 2, 0, 2, 100, out var icon);
            var borrowed = icon == 0 ? Native.GetClassLongPtr(window.Handle, -14) : (nint)icon;
            if (borrowed == 0 || !Valid(window))
                return null;
            owned = CopyIcon(borrowed);
            if (owned == 0)
                return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(owned, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(48, 48));
            image.Freeze();
            return Valid(window) ? image : null;
        }
        catch (Exception e) when (e is Win32Exception or ArgumentException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (owned != 0)
                DestroyIcon(owned);
        }
    }

    public Task<ActionResult> Close(WindowRef window, CancellationToken cancellation) => new GuardedActions(this).Close(window, cancellation);
    public Task<ActionResult> Minimize(WindowRef window, CancellationToken cancellation) => new GuardedActions(this).Minimize(window, cancellation);
    public bool SupportsMinimize => true;

    public bool IsMinimized(WindowRef window) => Valid(window) && Native.IsIconic(window.Handle);
    public bool RequestMinimize(WindowRef window) => !disposed && !window.Protected && Valid(window) && Native.ShowWindowAsync(window.Handle, 6);
    public Task<ActionResult> Shortcut(WindowRef window, string keys) => keys is "Win+G" or "Shift+Tab" or "Ctrl+Tab" ? new GuardedActions(this).Shortcut(window, keys) : Task.FromResult(new ActionResult(ResultKind.Unavailable, "Atalho não permitido neste caminho."));
    public Task<ActionResult> Shortcut(WindowRef window, string keys, CancellationToken cancellation, Func<bool> continuing) => keys is "Win+G" or "Shift+Tab" or "Ctrl+Tab" ? new GuardedActions(this).Shortcut(window, keys, cancellation, continuing) : Task.FromResult(new ActionResult(ResultKind.Unavailable, "Atalho não permitido."));
    public Task<ActionResult> Shortcut(WindowRef window, string keys, CancellationToken cancellation, Func<bool> continuing, Action<ShortcutDispatch> progress) => keys is "Shift+Tab" or "Ctrl+Tab" ? new GuardedActions(this, _ => progress(LastShortcutDispatch!.Value)).Shortcut(window, keys, cancellation, continuing) : Task.FromResult(new ActionResult(ResultKind.Unavailable, "Atalho não permitido."));
    public Task<ActionResult> Focus(WindowRef window, CancellationToken cancellation, Func<bool> continuing) => new GuardedActions(this).FocusStable(window, cancellation, continuing);
    public WindowObservation ObserveWindow(WindowRef window) => ClosureObservation.Classify(window, Probe(window.Handle));
    private WindowProbe Probe(nint hwnd)
    {
        long generation;
        lock (generations)
            generation = generations.GetValueOrDefault(hwnd);
        if (!Native.IsWindow(hwnd))
            return new(false, null, null, generation, "");
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var caption = new StringBuilder(513);
        Native.GetWindowText(hwnd, caption, caption.Capacity);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return new(true, (int)pid, process.StartTime.ToUniversalTime().Ticks, generation, caption.ToString());
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException)
        {
            return new(Native.IsWindow(hwnd), pid == 0 ? null : (int)pid, null, generation, caption.ToString());
        }
    }

    public bool IsForeground(WindowRef window) => !disposed && Native.GetForegroundWindow() == window.Handle;
    public bool RequestFocus(WindowRef window)
    {
        if (disposed)
            return false;
        if (Native.IsIconic(window.Handle))
            Native.ShowWindow(window.Handle, 9);
        return !disposed && Native.SetForegroundWindow(window.Handle);
    }

    public bool PostClose(WindowRef window)
    {
        if (disposed)
            return false;
        var current = Read(window.Handle);
        return current is not null && current.CanClose && window.Same(current) && Valid(current) && Native.PostMessage(window.Handle, 0x10, 0, 0);
    }

    public Task Wait(int milliseconds) => Task.Delay(milliseconds);
    public SteamKeyReceipt SendSteamEvent(ushort key, bool up)
    {
        if (key is not (16 or 9) || disposed && !up)
            return new(false, 0);
        var bit = key == 16 ? 1 : 2;
        if (up && (SteamOwnedKeys & bit) == 0)
            return new(false, 0);
        var input = Native.KeyInputs("Shift+Tab")[key == 16 ? (up ? 3 : 0) : (up ? 2 : 1)];
        var previous = SteamOwnedKeys;
        if (!up)
            SteamOwnedKeys |= bit;
        try
        {
            var count = keySender([input]);
            if (!up && count == 0)
                SteamOwnedKeys = previous;
            return new(true, count);
        }
        catch (Exception)
        {
            return new(true, null);
        }
    }

    public bool SteamKeysReleased(int mask) => ((mask & 1) == 0 || (Native.GetAsyncKeyState(16) & 0x8000) == 0 && (Native.GetAsyncKeyState(0xA0) & 0x8000) == 0 && (Native.GetAsyncKeyState(0xA1) & 0x8000) == 0) && ((mask & 2) == 0 || (Native.GetAsyncKeyState(9) & 0x8000) == 0);
    public void SteamOwnership(int mask) => SteamOwnedKeys = mask;
    public void RecordSteamSequence(Automation.SteamSequenceEvidence evidence)
    {
        var normal = evidence.Events.Where(e => !e.Cleanup).ToArray();
        var cleanup = evidence.Events.Where(e => e.Cleanup).ToArray();
        uint? Count(Automation.SteamKeyEvent[] entries) => entries.Any(e => e.Accepted is null || e.Accepted > 1) ? null : (uint)entries.Sum(e => (long)e.Accepted!.Value);
        LastShortcutDispatch = new(++shortcutRevision, 4, Count(normal), cleanup.Length, Count(cleanup), evidence);
    }

    public uint SendKeys(string keys)
    {
        if (disposed)
            return 0;
        var inputs = Native.KeyInputs(keys);
        if (inputs.Length == 0)
            return 0;
        var sent = keySender(inputs);
        LastShortcutDispatch = new(++shortcutRevision, inputs.Length, sent, 0, 0);
        // Cleanup only key-up events after a partial insertion; never retry the action/key-down.
        if (sent != inputs.Length && !disposed)
        {
            var release = inputs.Where(i => i.Flags == 2).ToArray();
            var cleanup = keySender(release);
            LastShortcutDispatch = new(shortcutRevision, inputs.Length, sent, release.Length, cleanup);
        }

        return sent;
    }

    public Task<ActionResult> TaskView() => Task.FromResult(disposed ? new ActionResult(ResultKind.Cancelled, "Overlay encerrada; nenhum atalho enviado.") : Outcome.Keys(SendKeys("Win+Tab"), 4));
    internal static bool BigPictureIdentity(WindowRef window) => window.Started > 0 && window.ProcessName.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase) && window.Title.Equals("Steam Big Picture Mode", StringComparison.OrdinalIgnoreCase);
    public bool CanSteamMenu(WindowRef? window)
    {
        if (disposed || window is null || hook == 0 || !BigPictureIdentity(window))
            return false;
        var current = Read(window.Handle);
        if (current is null || !window.Same(current) || !BigPictureIdentity(current) || !Relevant(window.Handle))
            return false;
        try
        {
            using var process = Process.GetProcessById(window.Pid);
            var path = process.MainModule?.FileName;
            if (path is null || !Path.GetFileName(path).Equals("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase))
                return false;
            var folder = Directory.GetParent(path);
            while (folder is not null)
            {
                if (File.Exists(Path.Combine(folder.FullName, "steam.exe")))
                {
                    var relative = Path.GetRelativePath(folder.FullName, path);
                    return relative.StartsWith("bin" + Path.DirectorySeparatorChar + "cef" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                }

                folder = folder.Parent;
            }

            return false;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public Task<ActionResult> SteamMenu(WindowRef window) => SteamMenu(window, CancellationToken.None, () => true);
    public Task<ActionResult> SteamMenu(WindowRef window, CancellationToken cancellation, Func<bool> continuing) => new GuardedActions(this).SteamMenu(window, cancellation, continuing, () => !disposed && CanSteamMenu(window));
    public ActionResult Launch(bool playnite, string? path)
    {
        if (disposed)
            return new(ResultKind.Cancelled, "Overlay encerrada; nenhuma inicialização enviada.");
        try
        {
            ProcessStartInfo info;
            if (playnite)
            {
                if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path) || !Path.GetFileName(path).Equals("Playnite.FullscreenApp.exe", StringComparison.OrdinalIgnoreCase))
                    return new(ResultKind.Unavailable, "Escolha Playnite.FullscreenApp.exe em Ajustes.");
                info = new(path)
                {
                    UseShellExecute = false
                };
                info.ArgumentList.Add("--startfullscreen");
            }
            else
                info = new("steam://open/bigpicture")
                {
                    UseShellExecute = true
                };
            using var process = Process.Start(info);
            return new(ResultKind.Requested, playnite ? "Inicialização do Playnite solicitada; modo e foco ainda não confirmados." : "URI do Steam enviada; Big Picture ainda não confirmado.");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return new(ResultKind.Failed, "O Windows não aceitou a inicialização. Verifique a instalação em Ajustes.");
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        Volatile.Write(ref disposed, true);
        icons.Dispose();
        if (hook != 0)
            Native.UnhookWinEvent(hook);
        GC.KeepAlive(destroyCallback);
    }
}

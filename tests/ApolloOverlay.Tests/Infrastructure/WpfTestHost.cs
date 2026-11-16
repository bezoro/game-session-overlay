using System.IO;
using System.Windows.Threading;
using ApolloOverlay;

internal enum TestFocusMode
{
    Synthetic, Native
}

/// <summary>Owns the window, temporary journal and dispatcher context; native focus is always an explicit choice.</summary>
internal sealed class WpfTestHost : IDisposable
{
    private readonly IDisposable context;
    private bool disposed, disposing, closed;
    internal MainWindow Window
    {
        get;
    }
    internal LocalDiagnostics Diagnostics
    {
        get;
    }

    private WpfTestHost(MainWindow window, LocalDiagnostics diagnostics, IDisposable context)
    {
        Window = window;
        Diagnostics = diagnostics;
        this.context = context;
        window.Closed += Closed;
    }

    private void Closed(object? sender, EventArgs args) => closed = true;

    internal static WpfTestHost Create(Func<LocalDiagnostics, MainWindow> createWindow, TestFocusMode focusMode,
        Action<MainWindow>? initialize = null, bool? light = false)
    {
        var context = WpfTestDispatcher.UseContext();
        var diagnostics = new LocalDiagnostics(Path.Combine(Path.GetTempPath(), "OverlayWpfTests", Guid.NewGuid().ToString("N")));
        WpfTestHost? host = null;
        try
        {
            var app = WpfTestDispatcher.EnsureApplication();
            if (light is { } theme)
                ThemeSupport.ForceForValidation(app, theme);
            var window = createWindow(diagnostics);
            host = new(window, diagnostics, context);
            if (focusMode == TestFocusMode.Synthetic)
            {
                window.ShowActivated = false;
                window.InputContextReader = () => new(true, true, true, true);
                window.PhysicalKeyReader = _ => 0;
            }
            initialize?.Invoke(window);
            return host;
        }
        catch (Exception error)
        {
            try
            {
                if (host is not null)
                    host.Dispose();
                else
                {
                    try
                    {
                        ReleaseDiagnostics(diagnostics);
                    }
                    finally { context.Dispose(); }
                }
            }
            catch (Exception cleanup)
            {
                throw new AggregateException("WPF fixture initialization and cleanup failed.", error, cleanup);
            }
            throw;
        }
    }

    internal static MainWindow CreatePreviewWindow(TestFocusMode focusMode, bool activateOnShow) =>
        CreatePreviewWindow(new PreviewPlatform(new(101, 42, 100, "Test window", "fixture")), focusMode, activateOnShow);

    /// <summary>Window-only fixtures inherit the caller's theme; activation on Show is a separate, explicit choice.</summary>
    internal static MainWindow CreatePreviewWindow(IPlatform platform, TestFocusMode focusMode, bool activateOnShow)
    {
        var host = Create(diagnostics => new MainWindow(renderOnly: true, platform: platform, diagnostics: diagnostics), focusMode, light: null);
        try
        {
            host.Window.ShowActivated = activateOnShow;
            host.Refresh();
            host.context.Dispose();
            host.Window.Closed += (_, _) => host.Dispose();
            return host.Window;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    internal void Refresh()
    {
        Window.Controller.CaptureReturn();
        WpfTestDispatcher.Complete(Window.Controller.Refresh());
        Window.RefreshRows();
    }

    public void Dispose()
    {
        if (disposed || disposing)
            return;
        Window.Dispatcher.VerifyAccess();
        disposing = true;
        try
        {
            using (WpfTestDispatcher.UseContext())
            {
                if (!closed)
                {
                    try
                    {
                        Window.Close();
                        if (!closed)
                        {
                            WpfTestDispatcher.Complete(Window.ClosingTask);
                            WpfTestDispatcher.Complete(Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task);
                        }
                    }
                    catch (Exception error) when (!closed)
                    {
                        throw new WpfTestCleanupException(this, "The fixture window did not finish closing. Its resources remain owned; retry disposal after resolving the pending operation.", error);
                    }
                    if (!closed)
                        throw new WpfTestCleanupException(this, "Closing was cancelled. The live fixture retains its resources; remove the cancellation and retry disposal.");
                }
                ReleaseDiagnostics(Diagnostics);
                Window.Closed -= Closed;
                disposed = true;
            }
        }
        finally
        {
            disposing = false;
            context.Dispose();
        }
    }

    private static void ReleaseDiagnostics(LocalDiagnostics diagnostics)
    {
        diagnostics.Dispose();
        if (Directory.Exists(diagnostics.Root))
            Directory.Delete(diagnostics.Root, true);
    }
}

/// <summary>A failed close retains an accessible owner rather than releasing resources beneath a live window.</summary>
internal sealed class WpfTestCleanupException(WpfTestHost owner, string message, Exception? inner = null) : InvalidOperationException(message, inner)
{
    internal WpfTestHost Owner { get; } = owner;
}

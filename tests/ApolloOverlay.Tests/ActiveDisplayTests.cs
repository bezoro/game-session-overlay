using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ApolloOverlay;

internal static class ActiveDisplayTests
{
    internal static void Run(Action<string, Action> test)
    {
        foreach (var (process, protectedTarget) in new[] { ("steamwebhelper", true), ("explorer", true), ("browser", false), ("Playnite.FullscreenApp", false), ("game", false) })
            test(process == "steamwebhelper" ? "protected foreground display wins over the historical session anchor" : "active display follows " + process, () =>
                WithWindow((window, platform) =>
                {
                    var foreground = platform.Window! with
                    {
                        ProcessName = process,
                        Protected = protectedTarget
                    };
                    window.Controller.Select(foreground with
                    {
                        Handle = 202,
                        ProcessName = "old-game",
                        Protected = false
                    });
                    platform.Window = foreground;
                    nint observed = 0;
                    window.WindowDisplayReader = handle => { observed = handle; return "fixture"; };
                    window.FocusForeground = () => foreground.Handle;
                    window.OpenPanel();
                    Require(observed == foreground.Handle, $"Expected foreground HWND {foreground.Handle}, got historical HWND {observed}");
                    Require(window.Controller.ReturnTarget!.Protected == protectedTarget, "Display selection changed action protection");
                }));

        test("layout changes retain the monitor captured before overlay activation", () =>
            WithWindow((window, platform) =>
            {
                var reads = 0;
                window.WindowDisplayReader = _ => { reads++; return "fixture"; };
                window.CursorDisplayReader = () => throw new Exception("Usable foreground should not read the cursor");
                window.FocusForeground = () => platform.Window!.Handle;
                window.OpenPanel();
                window.FocusForeground = () => new WindowInteropHelper(window).Handle;
                window.Place();
                window.Controller.Change(window.Controller.Settings with
                {
                    UiScalePercent = 115
                });
                window.Place();
                Require(reads == 1, "Layout followed the overlay's foreground or reread its previous window");
            }));

        foreach (var externalReopen in new[] { true, false })
            test(externalReopen ? "external-focus fade reopen captures and places the new monitor before reacquisition" : "owned fade reversal retains its captured monitor and return target", () =>
                WithWindow((window, platform) =>
                {
                    nint observed = 0, placedFor = 0;
                    var reads = 0;
                    var acquisition = new TaskCompletionSource<bool>();
                    window.WindowDisplayReader = handle => { observed = handle; reads++; return "fixture"; };
                    window.DpiReader = _ => { placedFor = observed; return 96; };
                    window.FocusForeground = () => platform.Window!.Handle;
                    window.FocusAcquisitionRequest = (_, _, token) => acquisition.Task.WaitAsync(token);
                    window.OpenPanel();
                    Require(window.IsVisible && observed == 101, "Initial opening did not capture monitor A");
                    window.Opacity = 1;
                    window.ForceFadeFixture = true;
                    window.BeginFadeFixture();
                    Require(window.FadePending && window.IsVisible, "Controlled closing fade was not active");
                    var previous = window.Controller.ReturnTarget;
                    platform.Window = platform.Window! with
                    {
                        Handle = 202,
                        Title = "Monitor B"
                    };
                    window.FocusForeground = () => externalReopen ? 202 : new WindowInteropHelper(window).Handle;
                    window.FocusAcquisitionRequest = (_, _, token) =>
                    {
                        Require(observed == 202 && placedFor == 202, "Monitor B was not captured and placed before focus reacquisition");
                        return acquisition.Task.WaitAsync(token);
                    };
                    window.OpenPanel();
                    if (externalReopen)
                    {
                        Require(observed == 202 && reads == 2 && placedFor == 202, "External fade reopen retained monitor A");
                        Require(window.Controller.ReturnTarget?.Handle == 202, "External activation retained the previous return target");
                    }
                    else
                    {
                        Require(reads == 1 && observed == 101, "Owned reversal recaptured the overlay's monitor");
                        Require(window.Controller.ReturnTarget == previous, "Owned reversal changed the return target");
                    }
                    Require(!window.FadePending, "Reopening did not cancel the old fade");
                }));

        foreach (var ownForeground in new[] { false, true })
            test("cursor monitor is captured once for " + (ownForeground ? "overlay foreground" : "no foreground"), () =>
                WithWindow((window, platform) =>
                {
                    platform.Window = null;
                    var cursorReads = 0;
                    window.FocusForeground = () => ownForeground ? new WindowInteropHelper(window).Handle : 0;
                    window.WindowDisplayReader = _ => throw new Exception("No external foreground should be queried");
                    window.CursorDisplayReader = () => { cursorReads++; return "fixture"; };
                    window.OpenPanel();
                    window.CursorDisplayReader = () => throw new Exception("Cursor was reread after activation");
                    window.Place();
                    Require(cursorReads == 1, "The activation did not capture the cursor monitor once");
                }));

        test("desktop tray and unavailable native monitor use cursor fallback", () =>
        {
            foreach (var className in new[] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
                Require(ActiveDisplay.IsShellSurface(className), "Shell surface was treated as an application: " + className);
            foreach (var className in new[] { "CabinetWClass", "Chrome_WidgetWin_1", "SDL_app" })
                Require(!ActiveDisplay.IsShellSurface(className), "Application class was treated as desktop: " + className);
            var reads = 0;
            Require(ActiveDisplay.Capture(101, 303, _ => null, () => { reads++; return "cursor"; }) == "cursor" && reads == 1,
                "Unavailable foreground monitor did not fall back to cursor");
            Require(ActiveDisplay.Capture(101, 303, _ => null, () => null) is null,
                "Failed foreground and cursor queries invented a monitor identity");
        });

        test("invalid and owned native windows cannot select a display", () =>
            WithWindow((window, _) =>
            {
                Require(ActiveDisplay.ReadWindow(0) is null && ActiveDisplay.ReadWindow(-1) is null,
                    "Invalid native window produced a display identity");
                Require(ActiveDisplay.ReadWindow(new WindowInteropHelper(window).Handle) is null,
                    "The overlay's own native window selected a display");
            }));

        test("explicit display wins and disconnected selections fall back safely", () =>
        {
            var primary = new DisplayRef("primary", 0, 0, 1920, 1080, true);
            var active = new DisplayRef("apollo", -1920, 0, 1920, 1080, false);
            DisplayRef[] displays = [primary, active];
            Require(DisplayRef.Choose(displays, "primary", "apollo") == primary, "Explicit selection lost precedence");
            Require(DisplayRef.Choose(displays, null, "apollo") == active, "Automatic selection ignored captured display");
            Require(DisplayRef.Choose(displays, "disconnected", "apollo") == active, "Disconnected override ignored available captured display");
            Require(DisplayRef.Choose(displays, null, "disconnected") == primary, "Disconnected captured monitor did not fall back to primary");
            Require(DisplayRef.Choose(displays, null, null) == primary, "Unavailable native reads did not fall back to primary");
            Require(DisplayRef.Choose([active], "disconnected", null) == active, "No-primary configuration did not use first available display");
        });

        test("placement tolerates no connected displays", () =>
            WithWindow((window, platform) =>
            {
                platform.Screens = [];
                window.DpiReader = _ => throw new Exception("No-display placement attempted native layout");
                window.Place();
            }));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void WithWindow(Action<MainWindow, PlacementPlatform> test)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.TryFindResource(ThemeSupport.Card) is null)
            ThemeSupport.ForceForValidation(app, false);
        using var platform = new PlacementPlatform();
        var window = new MainWindow(renderOnly: true, platform: platform,
            diagnostics: new(Path.Combine(Path.GetTempPath(), "ActiveDisplay", Guid.NewGuid().ToString("N"))));
        window.PhysicalKeyReader = _ => 0;
        window.DpiReader = _ => 96;
        window.ShowActivated = false;
        window.FocusAcquisitionRequest = (_, _, _) => Task.FromResult(false);
        try
        {
            test(window, platform);
        }
        finally
        {
            window.Close();
            ReviewFixTests.Complete(window.OpeningTask);
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private sealed class PlacementPlatform : IPlatform
    {
        internal WindowRef? Window = new(101, 42, 100, "Steam Big Picture", "steamwebhelper", true);
        internal IReadOnlyList<DisplayRef> Screens = [new("fixture", 0, 0, 1280, 720, true)];
        public bool IsPreview => true;
        public WindowRef? Foreground() => Window;
        public IReadOnlyList<WindowRef> Windows() => [];
        public bool Valid(WindowRef window) => window.Handle is 101 or 202;
        public IReadOnlyList<DisplayRef> Displays() => Screens;
        public ModuleEvidence SteamEvidence(WindowRef? window) => ModuleEvidence.Unknown;
        private static Task<ActionResult> Refused() => Task.FromResult(new ActionResult(ResultKind.Unavailable, "Placement fixture never dispatches actions."));
        public Task<ActionResult> Switch(WindowRef window) => Refused();
        public Task<ActionResult> Close(WindowRef window, CancellationToken cancellation) => Refused();
        public Task<ActionResult> Shortcut(WindowRef window, string keys) => Refused();
        public ActionResult Launch(bool playnite, string? path) => new(ResultKind.Unavailable, "Placement fixture never launches applications.");
        public void Dispose()
        {
        }
    }
}

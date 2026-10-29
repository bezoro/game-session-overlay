using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ApolloOverlay;

internal static class TvUsabilityTests
{
    private static void Require(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    private static MainWindow Window()
    {
        var target = new WindowRef(101, 42, 100, "Test window", "fixture");
        var window = new MainWindow(renderOnly: true, platform: new PreviewPlatform(target))
        {
            Width = 360,
            Height = 480,
            Left = -12000,
            Top = -12000,
            InputContextReader = () => new(true, true, true, true),
            PhysicalKeyReader = _ => 0
        };
        window.RefreshRows();
        window.ShowDiagnostic();
        window.UpdateLayout();
        return window;
    }

    private static void UseController(MainWindow window)
    {
        var frames = new PadFrame[4];
        frames[0] = new(true, PadButtons.None);
        window.ReviewTick(frames);
        frames[0] = new(true, PadButtons.Down);
        window.ReviewTick(frames);
    }

    private static void PressX(MainWindow window)
    {
        var frames = new PadFrame[4];
        frames[0] = new(true, PadButtons.None);
        window.ReviewTick(frames);
        frames[0] = new(true, PadButtons.X);
        window.ReviewTick(frames);
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    internal static void Run(Action<string, Action> test)
    {
        test("TV fixture refresh completes while the Dispatcher synchronization context is installed", () =>
        {
            var previous = SynchronizationContext.Current;
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                window = ReviewFixTests.Window();
                Require(window.Controller.Apps.Count == 1, "Dispatcher fixture did not finish refreshing applications");
            }
            finally
            {
                try
                {
                    window?.Close();
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }
        });
        test("TV direction and A together move without activating the newly selected action", () =>
        {
            var window = Window();
            try
            {
                window.Controller.CaptureReturn();
                window.RefreshRows();
                window.UpdateLayout();
                Require(window.SelectedAction == "session-card", "Fixture did not start on the session card");
                var frames = new PadFrame[4];
                frames[0] = new(true, PadButtons.None);
                window.ReviewTick(frames);
                frames[0] = new(true, PadButtons.Down | PadButtons.A);
                window.ReviewTick(frames);
                Require(window.SelectedAction != "session-card", "Simultaneous input did not move selection");
                Require(((TextBlock)window.FindName("PageTitle")).Text == "Session controls", "Simultaneous input activated the next item");
                Require(!window.TransitionPending, "Simultaneous input queued an external action");
            }
            finally
            {
                window.Close();
            }
        });
        foreach (var when in new[]
        {
            "during",
            "at-end"
        }

        )
            test("TV fade does not cede focus after controller disconnect " + when, () =>
            {
                var previous = SynchronizationContext.Current;
                MainWindow? window = null;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                    window = Window();
                    var frames = new PadFrame[]
                    {
                        new(true, PadButtons.None),
                        default,
                        default,
                        default
                    };
                    window.ForceFadeFixture = true;
                    window.FadeFramesFixture = () => frames;
                    window.ReviewTick(frames);
                    Require(Native.GetForegroundWindow() == new WindowInteropHelper(window).Handle, "PRECONDITION: fixture has no foreground focus");
                    window.BeginFadeFixture();
                    Require(window.FadePending, "Fixture did not start fade");
                    frames = new PadFrame[4];
                    if (when == "during")
                    {
                        window.ReviewTick(frames);
                        frames = new PadFrame[]
                        {
                            new(true, PadButtons.None),
                            default,
                            default,
                            default
                        };
                    }

                    var timeout = System.Diagnostics.Stopwatch.StartNew();
                    while (window.FadePending && timeout.ElapsedMilliseconds < 5000)
                    {
                        PumpDispatcher();
                        Thread.Sleep(1);
                    }

                    Require(!window.FadePending && window.IsVisible, "Disconnect allowed the fade to hand off focus");
                    Require(Native.GetForegroundWindow() == new WindowInteropHelper(window).Handle, "Disconnect ceded foreground focus");
                }
                finally
                {
                    try
                    {
                        window?.Close();
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(previous);
                    }
                }
            });
        test("TV unavailable session card opens window picker", () =>
        {
            var platform = new PremiumInterfaceTests.FixturePlatform();
            var window = PremiumInterfaceTests.FixtureWindow(platform);
            try
            {
                platform.ValidIdentity = false;
                window.RefreshRows();
                window.ActivateEntry();
                Require(window.ActionIds.Contains("refresh-apps"), "Unavailable session card did not offer the window picker");
                Require(platform.Calls == 0, "Unavailable session card attempted to switch to an invalid window");
            }
            finally
            {
                window.Close();
            }
        });
        test("TV X opens options only from relevant pages", () =>
        {
            var window = Window();
            try
            {
                window.Controller.CaptureReturn();
                window.RefreshRows();
                window.Page(2);
                PressX(window);
                Require(((TextBlock)window.FindName("PageTitle")).Text == "System", "X changed system page to unrelated options");
                ReviewFixTests.Button(window, "configure-playnite").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                PressX(window);
                Require(((TextBlock)window.FindName("PageTitle")).Text == "Find Playnite", "X interrupted Playnite folder selection");
                window.Back();
                window.Page(0);
                PressX(window);
                Require(((TextBlock)window.FindName("PageTitle")).Text == "Window options", "X no longer opens session options");
                window.Back();
                window.Page(1);
                PressX(window);
                Require(((TextBlock)window.FindName("PageTitle")).Text == "Window options", "X no longer opens application options");
            }
            finally
            {
                window.Close();
            }
        });
        test("TV session hint reflects whether a window is selected", () =>
        {
            var window = Window();
            try
            {
                var hint = (TextBlock)window.FindName("SessionHint");
                Require(window.Controller.Session.Anchor is null, "The initial fixture unexpectedly selected a window");
                Require(hint.Text.Contains("Choose", StringComparison.OrdinalIgnoreCase), "Empty session advertises resuming a window");
                UseController(window);
                Require(InputPrompt.Read(hint).Contains("A", StringComparison.Ordinal) && InputPrompt.Read(hint).Contains("Choose", StringComparison.OrdinalIgnoreCase), "Controller hint does not explain the empty session action");
                window.Controller.CaptureReturn();
                window.RefreshRows();
                Require(hint.Text.Contains("Resume", StringComparison.OrdinalIgnoreCase), "Selected session does not advertise resuming");
            }
            finally
            {
                window.Close();
            }
        });
        test("TV compact hints do not advertise contextual options on the system page", () =>
        {
            var window = Window();
            try
            {
                UseController(window);
                window.Page(2);
                var hint = InputPrompt.Read((TextBlock)window.FindName("ModeLabel"));
                Require(!hint.Contains("X options", StringComparison.Ordinal), "System page misleadingly advertises contextual options");
                window.Page(1);
                hint = InputPrompt.Read((TextBlock)window.FindName("ModeLabel"));
                Require(hint.Contains("X options", StringComparison.Ordinal), "Apps page lost its contextual options hint");
            }
            finally
            {
                window.Close();
            }
        });
    }
}

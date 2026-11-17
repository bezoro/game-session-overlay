using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ApolloOverlay;

internal static class InputSafetyTests
{
    internal static void Run(Action<string, Action> test)
    {
        foreach (var mouseKey in new[] { 0x01, 0x02, 0x04, 0x05, 0x06 })
        {
            var button = mouseKey;
            test("controller dismissal waits for held mouse VK " + button, () =>
            {
                using var host = new Host();
                var window = host.Window;
                host.HeldMouse = button;
                if (button == 0x02)
                {
                    var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    {
                        RoutedEvent = Mouse.PreviewMouseDownEvent
                    };
                    ((UIElement)window.FindName("HeaderArea")).RaiseEvent(press);
                    Require(!press.Handled && Mouse.Captured is null, "PRECONDITION: blank-header press was captured or rejected");
                }
                window.ReviewTick(Pads(PadButtons.B));
                Require(window.TransitionPending, "Controller B did not queue dismissal");
                var now = Environment.TickCount64;
                Advance(window, now);
                Advance(window, now + 1000);
                Require(window.IsVisible && window.TransitionPending && host.Platform.Calls == 0, "Held mouse allowed controller handoff or focus return");
                host.HeldMouse = 0;
                Advance(window, now + 1001);
                Advance(window, now + 1100);
                Require(window.TransitionPending && host.Platform.Calls == 0, "Mouse release skipped the continuous neutral interval");
                Advance(window, now + 1101);
                Require(!window.IsVisible && !window.TransitionPending && host.Platform.Calls == 1, "Released mixed-device input did not restore the captured app exactly once");
            });
        }
        foreach (var stage in new[] { "during", "final-recheck" })
        {
            var scenario = stage;
            test("mouse input cancels focus return at fade " + scenario, () =>
            {
                using var host = new Host();
                var window = host.Window;
                window.ForceFadeFixture = true;
                window.FadeFramesFixture = () => Pads();
                window.Back();
                var now = Environment.TickCount64;
                Advance(window, now);
                Advance(window, now + 100);
                Require(window.FadePending && window.IsVisible, "PRECONDITION: dismissal did not reach its fade");
                host.HeldMouse = 0x02;
                if (scenario == "during")
                {
                    window.ReviewTick(Pads());
                    host.HeldMouse = 0;
                }
                PumpUntil(() => !window.FadePending);
                WpfTestDispatcher.Complete(window.OpeningTask);
                Require(window.IsVisible && host.Platform.Calls == 0, "Fade returned focus after detecting mouse input");
            });
        }
        test("mouse state is rechecked after hiding immediately before app focus return", () =>
        {
            using var host = new Host();
            var window = host.Window;
            window.IsVisibleChanged += (_, args) =>
            {
                if (args.NewValue is false)
                    host.HeldMouse = 0x02;
            };
            window.Back();
            var now = Environment.TickCount64;
            Advance(window, now);
            Advance(window, now + 100);
            Require(!window.IsVisible && host.HeldMouse == 0x02 && host.Platform.Calls == 0, "Late mouse input reached a restored target");
        });
        InputSettingsMigrationTests.Run(test);
    }

    private static PadFrame[] Pads(PadButtons buttons = 0) => [new(true, buttons), default, default, default];
    private static void Advance(MainWindow window, long now) => window.AdvanceHandoff(new(Pads(), now), now);
    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!complete() && timeout.ElapsedMilliseconds < 5000)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
        Require(complete(), "Owned WPF operation exceeded five seconds");
    }

    private sealed class Host : IDisposable
    {
        private readonly SynchronizationContext? previousContext = SynchronizationContext.Current;
        private readonly string root = Path.Combine(Path.GetTempPath(), "OverlayInputSafety", Guid.NewGuid().ToString("N"));
        internal readonly TestPlatform Platform = new()
        {
            Next = Task.FromResult(new ActionResult(ResultKind.Completed, "Owned focus fixture accepted"))
        };
        internal MainWindow Window
        {
            get;
        }
        internal int HeldMouse;

        internal Host()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (app.TryFindResource(ThemeSupport.Card) is null)
                ThemeSupport.ForceForValidation(app, false);
            Window = new MainWindow(renderOnly: true, platform: Platform, diagnostics: new LocalDiagnostics(root));
            Window.PhysicalKeyReader = key => key == HeldMouse && HeldMouse != 0 ? unchecked((short)0x8000) : (short)0;
            Window.KeyboardModifiersReader = () => ModifierKeys.None;
            Window.DpiReader = _ => 96;
            Window.RefreshFocusFixture = () => Task.CompletedTask;
            Window.Controller.CaptureReturn();
            WpfTestDispatcher.Complete(Window.Controller.Refresh());
            Window.OpenPanel();
            WpfTestDispatcher.Complete(Window.OpeningTask);
            Require(Window.IsVisible && Window.IsActive && Native.GetForegroundWindow() == new WindowInteropHelper(Window).Handle,
                "PRECONDITION: owned overlay does not have confirmed actual foreground");
            Require(Window.Controller.ReturnTarget?.Same(Platform.Window) == true, "PRECONDITION: owned return target was not captured");
            Window.ReviewTick(Pads());
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            try
            {
                Window.Close();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ApolloOverlay;

internal static class SettingsFocusTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("Guide fallback activation still requires real WPF and native foreground", () =>
        {
            using var host = new Host(); var w = host.Window; host.Open(); var calls = 0;
            w.FocusRequest = _ => { host.Other.Show(); host.Other.Activate(); Native.SetForegroundWindow(host.OtherHandle); return false; };
            w.FocusFallbackRequest = (handle, _, token) => { calls++; token.ThrowIfCancellationRequested(); return Task.FromResult(Native.SetForegroundWindow(handle)); };
            w.OpenPanel(); Complete(w.OpeningTask); host.RequireOwnedFocus();
            Neutral(w); var before = w.SelectedAction; Frame(w, PadButtons.Down);
            Require(calls == 1 && w.SelectedAction != before, "Fallback did not restore actual foreground navigation");
        });
        test("Guide fallback success cannot admit a genuinely foreign foreground", () =>
        {
            using var host = new Host(); var w = host.Window; host.Open(); var calls = 0;
            w.FocusRequest = _ => { host.Other.Show(); host.Other.Activate(); Native.SetForegroundWindow(host.OtherHandle); return false; };
            w.FocusFallbackRequest = (_, _, _) => { calls++; return Task.FromResult(true); };
            w.OpenPanel(); Complete(w.OpeningTask); var before = w.SelectedAction; Neutral(w); Frame(w, PadButtons.Down);
            Require(calls == 1 && Native.GetForegroundWindow() == host.OtherHandle && w.SelectedAction == before, "Fallback return fabricated input ownership");
        });
        test("Guide fallback cancellation prevents a late result from confirming a newer cycle", () =>
        {
            using var host = new Host(); var w = host.Window; host.Open();
            var delayed = new TaskCompletionSource<bool>(); CancellationToken token = default;
            w.FocusRequest = _ => { host.Other.Show(); host.Other.Activate(); Native.SetForegroundWindow(host.OtherHandle); return false; };
            w.FocusFallbackRequest = (_, _, cancellation) => { token = cancellation; return delayed.Task; };
            w.OpenPanel(); var old = w.OpeningTask;
            w.FocusFallbackRequest = null; w.OpenPanel(); Complete(w.OpeningTask);
            Require(token.IsCancellationRequested, "New explicit opening did not cancel old activation");
            delayed.SetResult(true); Complete(old); var before = w.SelectedAction; Neutral(w); Frame(w, PadButtons.Down);
            Require(w.SelectedAction == before && w.Feedback.Contains("não confirmou"), "Late fallback overwrote denied focus");
        });
        test("settings focus accepts delayed foreground after one false native request", () =>
        {
            using var host = new Host(); var w = host.Window; host.Open();
            var observed = false; var requests = 0;
            w.FocusForeground = () => observed ? Native.GetForegroundWindow() : host.OtherHandle;
            w.FocusRequest = _ => { requests++; w.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => observed = true); return false; };
            w.OpenPanel(); Complete(w.OpeningTask); Pump(70); host.RequireOwnedFocus();
            Require(observed && requests == 1, "Activation was not observed or native focus was requested repeatedly");
            Neutral(w); var before = w.SelectedAction; Frame(w, PadButtons.Down);
            Require(w.SelectedAction != before, "Confirmed native foreground remained blocked by a false focus latch");
        });
        test("settings focus refuses a genuinely different foreground window", () =>
        {
            using var host = new Host(); var w = host.Window; host.Open(); var requests = 0;
            w.FocusRequest = _ => { requests++; host.Other.Show(); host.Other.Activate(); Native.SetForegroundWindow(host.OtherHandle); return false; };
            w.OpenPanel(); Complete(w.OpeningTask);
            Require(Native.GetForegroundWindow() == host.OtherHandle && w.IsVisible, "PRECONDITION: other owned window is not the real foreground");
            var selected = w.SelectedAction; Neutral(w); Frame(w, PadButtons.Down); Frame(w, PadButtons.A);
            Require(requests == 1 && w.SelectedAction == selected && !w.TransitionPending, "Another foreground was admitted for controller actions");
        });
        test("settings focus Guide retries a visible unconfirmed panel without hiding", () =>
        {
            using var host = new Host(); var w = host.Window; host.Open(); host.DenyConfirmation();
            var requests = 0; w.FocusForeground = Native.GetForegroundWindow;
            w.FocusRequest = handle => { requests++; return Native.SetForegroundWindow(handle); };
            w.Toggle(); Complete(w.OpeningTask); host.RequireOwnedFocus();
            Require(w.IsVisible && !w.TransitionPending && !w.FadePending && requests == 1, "Guide hid the unconfirmed panel or failed to retry activation");
            Neutral(w); var before = w.SelectedAction; Frame(w, PadButtons.Down);
            Require(w.SelectedAction != before, "Guide activation retry did not recover controller navigation");
        });
        test("settings focus B and Escape can dismiss a visible unconfirmed panel", () =>
        {
            foreach (var keyboard in new[] { false, true })
            {
                using var host = new Host(); var w = host.Window; host.Open(); host.DenyConfirmation();
                if (keyboard)
                {
                    w.FocusForeground = Native.GetForegroundWindow;
                    SendKey(w, Key.Escape, true); SendKey(w, Key.Escape, false);
                }
                else { Neutral(w); Frame(w, PadButtons.B); }
                Require(w.TransitionPending, "Dismissal did not remain available without confirmed focus");
                w.AdvanceHandoff(new(NeutralFrames(), 0, GuideValid: true), 0);
                w.AdvanceHandoff(new(NeutralFrames(), 100, GuideValid: true), 100);
                Complete(w.OperationTask);
                Require(!w.IsVisible && !w.TransitionPending, "Unconfirmed panel trapped the user instead of dismissing");
            }
        });
        foreach (var haptics in new[] { false, true })
        {
            var both = haptics;
            test("settings focus controller survives " + (both ? "sound and vibration" : "sound") + " then footer Save", () =>
            {
                using var host = new Host(); var w = host.Window; host.Open(); w.Page(2);
                Pick(w, "side"); Tap(w, PadButtons.A); Neutral(w); Pump(40); w.UpdateLayout();
                Require(w.Controller.Settings.PanelSide == "Right", "PRECONDITION: right-side placement was not applied");
                ToggleFeedback(w, host, "sound-feedback");
                if (both) ToggleFeedback(w, host, "haptic-feedback");
                var content = PickFooter(w, "save-pending");
                Require(Keyboard.FocusedElement is FrameworkElement focused && Equals(focused.Tag, "save-pending"), "PRECONDITION: real keyboard focus is not on the Save footer");
                Tap(w, PadButtons.A); w.UpdateLayout(); Pump(20); host.RequireOwnedFocus();
                Require(!w.Controller.SettingsDirty && w.Controller.Operation is { State: "Concluído" }, "Save did not complete as a real settings operation");
                Require(w.SelectedAction == content && w.SelectedAction != "exit" && Keyboard.FocusedElement is FrameworkElement live && live.IsVisible && Equals(live.Tag, content), "Save left focus on the collapsed dirty strip, a result button or Exit");
                var saved = UserSettings.Load(Path.Combine(host.Root, "settings.json"), out _);
                Require(saved.SoundFeedbackEnabled && saved.HapticFeedbackEnabled == both, "Feedback settings were not persisted");
                Neutral(w); var before = w.SelectedAction; Frame(w, PadButtons.Up);
                Require(w.SelectedAction != before, "Controller stopped navigating after footer Save");
                Neutral(w); Frame(w, PadButtons.B); Neutral(w);
                Require(w.ActionIds.Contains("session-card"), "Controller Back did not reach the Hub after Save");
                Pick(w, "session-card"); before = w.SelectedAction; Frame(w, PadButtons.Down);
                Require(w.SelectedAction != before && !w.Feedback.Contains("não confirmou"), "Hub controller navigation remained blocked after feedback settings");
            });
        }
    }

    private static void ToggleFeedback(MainWindow window, Host host, string id)
    {
        Pick(window, id); Neutral(window); window.UpdateLayout();
        var moves = 0; EventHandler changed = (_, _) => moves++;
        window.LocationChanged += changed;
        Require(NativeCapture.GetWindowRect(host.Handle, out var before), "PRECONDITION: window bounds unavailable");
        try
        {
            Frame(window, PadButtons.A); window.UpdateLayout();
            var selected = window.SelectedAction;
            // Release A directly into a direction, without an all-neutral frame. A pure
            // feedback preference must not recreate the coordinator and suppress this input.
            Frame(window, PadButtons.Up); window.UpdateLayout();
            Require(window.SelectedAction != selected, "Feedback toggle reset controller ownership or input arming");
            host.RequireOwnedFocus();
            Require(NativeCapture.GetWindowRect(host.Handle, out var after) && before.Left == after.Left && before.Top == after.Top && before.Right == after.Right && before.Bottom == after.Bottom && moves == 0,
                "Feedback-only preference moved the native overlay window");
        }
        finally { window.LocationChanged -= changed; }
        Neutral(window); Pick(window, id);
    }
    private static string PickFooter(MainWindow window, string id)
    {
        var content = window.SelectedAction;
        for (var count = 0; count <= window.ActionIds.Count && window.SelectedAction != id; count++)
        {
            window.Sequential(1);
            if (window.SelectedAction is not ("save-pending" or "discard-pending" or "operation-details" or "dismiss-operation" or "exit")) content = window.SelectedAction;
        }
        Require(window.SelectedAction == id, "Save footer was not reachable"); return content;
    }
    private static void Pick(MainWindow window, string id)
    {
        for (var count = 0; count <= window.ActionIds.Count && window.SelectedAction != id; count++) window.Sequential(1);
        Require(window.SelectedAction == id, "Controller action unreachable: " + id);
    }
    private static PadFrame[] NeutralFrames() => [new(true, PadButtons.None), default, default, default];
    private static void Frame(MainWindow window, PadButtons buttons) => window.ReviewTick([new(true, buttons), default, default, default]);
    private static void Neutral(MainWindow window) { Frame(window, PadButtons.None); Frame(window, PadButtons.None); }
    private static void Tap(MainWindow window, PadButtons buttons) { Neutral(window); Frame(window, buttons); }
    private static void SendKey(MainWindow window, Key key, bool down)
    {
        var presentation = PresentationSource.FromVisual(window) ?? throw new Exception("PRECONDITION: window has no presentation source");
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, key)
        { RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent });
    }
    private static void Complete(Task task) => ReviewFixTests.Complete(task);
    private static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private sealed class Host : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "OverlaySettingsFocus", Guid.NewGuid().ToString("N"));
        internal MainWindow Window { get; }
        internal Window Other { get; } = new() { Title = "Owned focus refusal fixture", Width = 260, Height = 140, Left = 80, Top = 80, Topmost = true, ShowInTaskbar = false };
        internal nint Handle => new WindowInteropHelper(Window).Handle;
        internal nint OtherHandle => new WindowInteropHelper(Other).EnsureHandle();
        internal Host()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (app.TryFindResource(ThemeSupport.Card) is null) ThemeSupport.ForceForValidation(app, false);
            Window = new MainWindow(renderOnly: true, platform: new PremiumInterfaceTests.FixturePlatform(preview: false), diagnostics: new LocalDiagnostics(Root));
            Window.PhysicalKeyReader = _ => 0; Window.DpiReader = _ => 96;
            Window.Controller.CaptureReturn(); Complete(Window.Controller.Refresh());
            Window.RefreshRows();
        }
        internal void Open() { Window.OpenPanel(); Complete(Window.OpeningTask); Window.UpdateLayout(); RequireOwnedFocus(); }
        internal void DenyConfirmation()
        {
            Window.FocusForeground = () => OtherHandle; Window.FocusRequest = _ => false;
            Window.OpenPanel(); Complete(Window.OpeningTask);
            Require(Window.IsVisible && Window.Feedback.Contains("não confirmou"), "PRECONDITION: focus refusal was not established");
        }
        internal void RequireOwnedFocus() => Require(Window.IsVisible && Window.IsActive && Native.GetForegroundWindow() == Handle, "PRECONDITION: real owned overlay foreground not established");
        public void Dispose()
        {
            try { Window.Close(); Other.Close(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        }
    }
}

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
        foreach (var nativeAccepted in new[] { false, true })
        {
            test("Guide fallback verifies real WPF/native foreground after request return " + nativeAccepted, () =>
            {
                using var host = new Host();
                var w = host.Window;
                host.Open();
                host.FocusOther();
                var calls = 0;
                w.FocusRequest = _ =>
                {
                    host.Other.Show();
                    host.Other.Activate();
                    Native.SetForegroundWindow(host.OtherHandle);
                    return nativeAccepted;
                };
                w.FocusFallbackRequest = (handle, previous, token) =>
                {
                    calls++;
                    Require(previous == host.OtherHandle, "Opening lost its original foreground before showing the overlay");
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(Native.SetForegroundWindow(handle));
                };
                w.OpenPanel();
                Complete(w.OpeningTask);
                host.RequireOwnedFocus();
                Neutral(w);
                var before = w.SelectedAction;
                Frame(w, PadButtons.Down);
                Require(calls == 1 && w.SelectedAction != before, "Fallback did not restore actual foreground navigation");
            });
            test("Guide fallback cannot fabricate real foreground after request return " + nativeAccepted, () =>
            {
                using var host = new Host();
                var w = host.Window;
                host.Open();
                host.FocusOther();
                var calls = 0;
                w.FocusRequest = _ =>
                {
                    host.Other.Show();
                    host.Other.Activate();
                    Native.SetForegroundWindow(host.OtherHandle);
                    return nativeAccepted;
                };
                w.FocusFallbackRequest = (_, _, _) =>
                {
                    calls++;
                    return Task.FromResult(true);
                };
                w.OpenPanel();
                Complete(w.OpeningTask);
                var before = w.SelectedAction;
                Neutral(w);
                Frame(w, PadButtons.Down);
                Require(calls == 1 && !w.IsVisible && Native.GetForegroundWindow() == host.OtherHandle && w.SelectedAction == before, "Fallback return fabricated a visible, usable panel without input ownership");
            });
        }
        test("Guide fallback cancellation prevents a late result from confirming a newer cycle", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            host.FocusOther();
            var delayed = new TaskCompletionSource<bool>();
            CancellationToken token = default;
            w.FocusRequest = _ =>
            {
                host.Other.Show();
                host.Other.Activate();
                Native.SetForegroundWindow(host.OtherHandle);
                return false;
            };
            w.FocusFallbackRequest = (_, _, cancellation) =>
            {
                token = cancellation;
                return delayed.Task;
            };
            w.OpenPanel();
            var old = w.OpeningTask;
            w.FocusFallbackRequest = null;
            w.OpenPanel();
            Complete(w.OpeningTask);
            Require(token.IsCancellationRequested, "New explicit opening did not cancel old activation");
            delayed.SetResult(true);
            Complete(old);
            var before = w.SelectedAction;
            Neutral(w);
            Frame(w, PadButtons.Down);
            Require(!w.IsVisible && w.SelectedAction == before && w.Feedback.Contains("did not confirm"), "Late fallback overwrote denied focus or revealed the failed panel");
        });
        test("pending foreground acquisition remains visually hidden and cannot dispatch actions", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.BeginPendingConfirmation();
            var selected = w.SelectedAction;
            // Observe beyond the normal opening fade. A provisional HWND must not
            // become a visible panel merely because the animation has elapsed.
            Pump(200);
            Require(w.IsVisible && w.Opacity == 0 && !w.OpeningTask.IsCompleted, "Pending native activation revealed the panel before confirmation");
            Neutral(w);
            Frame(w, PadButtons.Down);
            Frame(w, PadButtons.A);
            Require(w.SelectedAction == selected && !w.TransitionPending
                && ((PremiumInterfaceTests.FixturePlatform)w.Controller.Platform).Calls == 0
                && Native.GetForegroundWindow() == host.OtherHandle, "Pending activation admitted navigation or an external action");
        });
        foreach (var lateResult in new[] { false, true })
            test("stale activation completion preserves the newer confirmed panel " + lateResult, () =>
            {
                using var host = new Host();
                var w = host.Window;
                host.FocusOther();
                var delayed = new TaskCompletionSource<bool>();
                CancellationToken token = default;
                w.FocusRequest = _ =>
                {
                    host.Other.Activate();
                    Native.SetForegroundWindow(host.OtherHandle);
                    return false;
                };
                w.FocusFallbackRequest = (_, _, cancellation) => { token = cancellation; return delayed.Task; };
                w.OpenPanel();
                var old = w.OpeningTask;
                Require(!old.IsCompleted && w.Opacity == 0, "PRECONDITION: first activation was not held provisionally");
                w.FocusRequest = Native.SetForegroundWindow;
                w.FocusFallbackRequest = null;
                w.OpenPanel();
                Complete(w.OpeningTask);
                Pump(180);
                host.RequireOwnedFocus();
                Require(token.IsCancellationRequested && w.Opacity == 1, "The new confirmed panel did not replace its pending predecessor");
                Neutral(w);
                Frame(w, PadButtons.Down);
                var selected = w.SelectedAction;
                var feedback = w.Feedback;
                delayed.SetResult(lateResult);
                Complete(old);
                Require(w.IsVisible && w.Opacity == 1 && w.SelectedAction == selected && w.Feedback == feedback, "Stale activation completion changed or hid the confirmed panel");
                host.RequireOwnedFocus();
            });
        test("a failed foreground acquisition can recover on the next explicit opening", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.FocusOther();
            w.FocusRequest = _ =>
            {
                host.Other.Activate();
                Native.SetForegroundWindow(host.OtherHandle);
                return false;
            };
            w.OpenPanel();
            Complete(w.OpeningTask);
            Require(!w.IsVisible && Native.GetForegroundWindow() == host.OtherHandle, "Refused focus left a blocked panel visible");
            w.FocusRequest = Native.SetForegroundWindow;
            w.OpenPanel();
            Complete(w.OpeningTask);
            Pump(180);
            host.RequireOwnedFocus();
            Require(w.Opacity == 1, "The recovered panel stayed transparent");
            Neutral(w);
            var selected = w.SelectedAction;
            Frame(w, PadButtons.Down);
            Require(w.SelectedAction != selected, "A previous refusal poisoned subsequent foreground navigation");
        });
        test("settings focus accepts delayed foreground after one false native request", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            var observed = false;
            var requests = 0;
            w.FocusForeground = () => observed ? Native.GetForegroundWindow() : host.OtherHandle;
            w.FocusRequest = _ =>
            {
                requests++;
                w.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => observed = true);
                return false;
            };
            w.OpenPanel();
            Complete(w.OpeningTask);
            Pump(70);
            host.RequireOwnedFocus();
            Require(observed && requests == 1, "Activation was not observed or native focus was requested repeatedly");
            Neutral(w);
            var before = w.SelectedAction;
            Frame(w, PadButtons.Down);
            Require(w.SelectedAction != before, "Confirmed native foreground remained blocked by a false focus latch");
        });
        test("settings focus refuses a genuinely different foreground window", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            var requests = 0;
            w.FocusFallbackRequest = (_, _, _) => throw new Exception("Activation followed a new foreground instead of the opening's original window");
            w.FocusRequest = _ =>
            {
                requests++;
                host.Other.Show();
                host.Other.Activate();
                Native.SetForegroundWindow(host.OtherHandle);
                return false;
            };
            w.OpenPanel();
            Complete(w.OpeningTask);
            Require(Native.GetForegroundWindow() == host.OtherHandle && !w.IsVisible, "Refused activation did not leave the other owned window in foreground and hide the panel");
            var selected = w.SelectedAction;
            Neutral(w);
            Frame(w, PadButtons.Down);
            Frame(w, PadButtons.A);
            Require(requests == 1 && w.SelectedAction == selected && !w.TransitionPending, "Another foreground was admitted for controller actions");
        });
        test("Guide cancels a pending unpresented panel without issuing another focus request", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            host.BeginPendingConfirmation();
            var requests = 0;
            w.FocusRequest = _ => { requests++; return false; };
            w.RequestGuideToggle();
            Frame(w, PadButtons.None);
            Require(w.IsVisible && w.Opacity == 0 && w.TransitionPending && requests == 0, "Guide retried or revealed activation rather than queuing dismissal");
            w.AdvanceHandoff(new(NeutralFrames(), 0, GuideValid: true), 0);
            w.AdvanceHandoff(new(NeutralFrames(), 100, GuideValid: true), 100);
            Complete(w.OperationTask);
            Complete(w.OpeningTask);
            Require(!w.IsVisible && !w.TransitionPending && requests == 0, "Guide trapped an unfocused visible overlay");
        });
        test("Guide dismissal cancels pending focus acquisition and ignores a late broker result", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            host.FocusOther();
            var delayed = new TaskCompletionSource<bool>();
            CancellationToken token = default;
            w.FocusRequest = _ =>
            {
                host.Other.Activate();
                Native.SetForegroundWindow(host.OtherHandle);
                return false;
            };
            w.FocusFallbackRequest = (_, _, cancellation) => { token = cancellation; return delayed.Task; };
            w.OpenPanel();
            var opening = w.OpeningTask;
            Require(!opening.IsCompleted, "PRECONDITION: acquisition did not await the broker");
            w.RequestGuideToggle();
            Frame(w, PadButtons.None);
            Require(token.IsCancellationRequested && w.TransitionPending, "Guide left a pending activation alive");
            w.AdvanceHandoff(new(NeutralFrames(), 0, GuideValid: true), 0);
            w.AdvanceHandoff(new(NeutralFrames(), 100, GuideValid: true), 100);
            delayed.SetResult(true);
            Complete(opening);
            Require(!w.IsVisible && !w.TransitionPending, "A late activation result reopened the dismissed panel");
        });
        test("settings focus B and Escape can cancel an unpresented pending panel", () =>
        {
            foreach (var keyboard in new[]
            {
                false,
                true
            }

            )
            {
                using var host = new Host();
                var w = host.Window;
                host.Open();
                host.BeginPendingConfirmation();
                if (keyboard)
                {
                    SendKey(w, Key.Escape, true);
                    SendKey(w, Key.Escape, false);
                }
                else
                {
                    Neutral(w);
                    Frame(w, PadButtons.B);
                }

                Require(w.TransitionPending && w.Opacity == 0, "Dismissal revealed the pending panel or required confirmed focus");
                w.AdvanceHandoff(new(NeutralFrames(), 0, GuideValid: true), 0);
                w.AdvanceHandoff(new(NeutralFrames(), 100, GuideValid: true), 100);
                Complete(w.OperationTask);
                Complete(w.OpeningTask);
                Require(!w.IsVisible && !w.TransitionPending, "Unconfirmed panel trapped the user instead of dismissing");
            }
        });
        foreach (var confirmed in new[] { false, true })
            test("owned dialog " + (confirmed ? "restarts media observation after verified focus" : "keeps media suspended when focus is refused"), () =>
            {
                var reads = 0;
                using var media = new SessionMedia(_ =>
                {
                    reads++;
                    return Task.FromResult(new SessionMediaSnapshot(reads,
                        [new("source", "Media observation " + reads, "", "Fixture media", false, true, false, false)], "source", "Fixture observation"));
                });
                using var host = new Host(media);
                var w = host.Window;
                host.Open();
                Require(reads == 1 && media.Current.Items.Single().Title == "Media observation 1", "PRECONDITION: initial confirmed opening did not observe media");
                w.Page(2);
                var dialog = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                w.PlayniteDialogFixture = () => dialog.Task;
                ReviewFixTests.Button(w, "configure-playnite").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Require(!w.DialogTask.IsCompleted && media.Current.Items.Count == 0 && reads == 1, "The owned dialog retained active media observation");
                // Even an explicit refresh cannot observe playback while ownership
                // belongs to the dialog; reopening must request a fresh generation.
                Complete(media.RefreshAsync());
                Require(reads == 1, "The suspended media service accepted a refresh during the dialog");
                if (!confirmed)
                    w.FocusRequest = _ =>
                    {
                        host.Other.Show();
                        host.Other.Activate();
                        Native.SetForegroundWindow(host.OtherHandle);
                        return false;
                    };
                dialog.SetResult(null);
                Complete(w.DialogTask);
                if (confirmed)
                {
                    host.RequireOwnedFocus();
                    Require(reads == 2 && media.Current.Items.Single().Title == "Media observation 2", "Verified dialog return did not resume a fresh media observation");
                }
                else
                    Require(!w.IsVisible && reads == 1 && media.Current.Items.Count == 0
                        && Native.GetForegroundWindow() == host.OtherHandle, "Refused dialog return restarted hidden media observation");
            });
        foreach (var haptics in new[]
        {
            false,
            true
        }

        )
        {
            var both = haptics;
            test("settings focus controller survives " + (both ? "sound and vibration" : "sound") + " then footer Save", () =>
            {
                using var host = new Host();
                var w = host.Window;
                host.Open();
                w.Page(2);
                Pick(w, "side");
                Tap(w, PadButtons.A);
                Neutral(w);
                Pump(40);
                w.UpdateLayout();
                Require(w.Controller.Settings.PanelSide == "Right", "PRECONDITION: right-side placement was not applied");
                ToggleFeedback(w, host, "sound-feedback");
                if (both)
                    ToggleFeedback(w, host, "haptic-feedback");
                var content = PickFooter(w, "save-pending");
                Require(Keyboard.FocusedElement is FrameworkElement focused && Equals(focused.Tag, "save-pending"), "PRECONDITION: real keyboard focus is not on the Save footer");
                Tap(w, PadButtons.A);
                w.UpdateLayout();
                Pump(20);
                host.RequireOwnedFocus();
                Require(!w.Controller.SettingsDirty && w.Controller.Operation is { State: "Completed" }, "Save did not complete as a real settings operation");
                Require(w.SelectedAction == content && w.SelectedAction != "exit" && Keyboard.FocusedElement is FrameworkElement live && live.IsVisible && Equals(live.Tag, content), "Save left focus on the collapsed dirty strip, a result button or Exit");
                var saved = UserSettings.Load(Path.Combine(host.Root, "settings.json"), out _);
                Require(saved.SoundFeedbackEnabled && saved.HapticFeedbackEnabled == both, "Feedback settings were not persisted");
                Neutral(w);
                var before = w.SelectedAction;
                Frame(w, PadButtons.Up);
                Require(w.SelectedAction != before, "Controller stopped navigating after footer Save");
                Neutral(w);
                Frame(w, PadButtons.B);
                Neutral(w);
                Require(w.ActionIds.Contains("session-card"), "Controller Back did not reach the Hub after Save");
                Pick(w, "session-card");
                before = w.SelectedAction;
                Frame(w, PadButtons.Down);
                Require(w.SelectedAction != before && !w.Feedback.Contains("did not confirm"), "Hub controller navigation remained blocked after feedback settings");
            });
        }
    }

    private static void ToggleFeedback(MainWindow window, Host host, string id)
    {
        Pick(window, id);
        Neutral(window);
        window.UpdateLayout();
        var moves = 0;
        EventHandler changed = (_, _) => moves++;
        window.LocationChanged += changed;
        Require(NativeCapture.GetWindowRect(host.Handle, out var before), "PRECONDITION: window bounds unavailable");
        try
        {
            Frame(window, PadButtons.A);
            window.UpdateLayout();
            var selected = window.SelectedAction;
            // Release A directly into a direction, without an all-neutral frame. A pure
            // feedback preference must not recreate the coordinator and suppress this input.
            Frame(window, PadButtons.Up);
            window.UpdateLayout();
            Require(window.SelectedAction != selected, "Feedback toggle reset controller ownership or input arming");
            host.RequireOwnedFocus();
            Require(NativeCapture.GetWindowRect(host.Handle, out var after)
                && before.Left == after.Left
                && before.Top == after.Top
                && before.Right == after.Right
                && before.Bottom == after.Bottom
                && moves == 0, "Feedback-only preference moved the native overlay window");
        }
        finally
        {
            window.LocationChanged -= changed;
        }

        Neutral(window);
        Pick(window, id);
    }

    private static string PickFooter(MainWindow window, string id)
    {
        var content = window.SelectedAction;
        for (var count = 0; count <= window.ActionIds.Count && window.SelectedAction != id; count++)
        {
            window.Sequential(1);
            if (window.SelectedAction is not ("save-pending" or "discard-pending" or "operation-details" or "dismiss-operation" or "exit"))
                content = window.SelectedAction;
        }

        Require(window.SelectedAction == id, "Save footer was not reachable");
        return content;
    }

    private static void Pick(MainWindow window, string id)
    {
        for (var count = 0; count <= window.ActionIds.Count && window.SelectedAction != id; count++)
            window.Sequential(1);
        Require(window.SelectedAction == id, "Controller action unreachable: " + id);
    }

    private static PadFrame[] NeutralFrames() => [new(true, PadButtons.None), default, default, default];
    private static void Frame(MainWindow window, PadButtons buttons) => window.ReviewTick([new(true, buttons), default, default, default]);
    private static void Neutral(MainWindow window)
    {
        Frame(window, PadButtons.None);
        Frame(window, PadButtons.None);
    }

    private static void Tap(MainWindow window, PadButtons buttons)
    {
        Neutral(window);
        Frame(window, buttons);
    }

    private static void SendKey(MainWindow window, Key key, bool down)
    {
        var presentation = PresentationSource.FromVisual(window) ?? throw new Exception("PRECONDITION: window has no presentation source");
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, key) { RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent });
    }

    private static void Complete(Task task) => ReviewFixTests.Complete(task);
    private static void Require(bool value, string reason)
    {
        if (!value)
            throw new Exception(reason);
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class Host : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "OverlaySettingsFocus", Guid.NewGuid().ToString("N"));
        internal MainWindow Window
        {
            get;
        }
        internal Window Other
        {
            get;
        } = new()
        {
            Title = "Owned focus refusal fixture",
            Width = 260,
            Height = 140,
            Left = 80,
            Top = 80,
            Topmost = true,
            ShowInTaskbar = false
        };
        internal nint Handle => new WindowInteropHelper(Window).Handle;
        internal nint OtherHandle => new WindowInteropHelper(Other).EnsureHandle();

        internal Host(ISessionMedia? media = null)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = Application.Current ?? new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            if (app.TryFindResource(ThemeSupport.Card) is null)
                ThemeSupport.ForceForValidation(app, false);
            Window = new MainWindow(renderOnly: true, platform: new PremiumInterfaceTests.FixturePlatform(preview: false), diagnostics: new LocalDiagnostics(Root), media: media);
            Window.PhysicalKeyReader = _ => 0;
            Window.DpiReader = _ => 96;
            Window.Controller.CaptureReturn();
            Complete(Window.Controller.Refresh());
            Window.RefreshRows();
        }

        internal void Open()
        {
            Window.OpenPanel();
            Complete(Window.OpeningTask);
            Window.UpdateLayout();
            RequireOwnedFocus();
        }

        internal void FocusOther()
        {
            Window.Hide();
            Other.Show();
            Other.Activate();
            Native.SetForegroundWindow(OtherHandle);
            Require(Native.GetForegroundWindow() == OtherHandle, "PRECONDITION: owned competing window is not foreground");
        }

        internal void BeginPendingConfirmation()
        {
            FocusOther();
            Window.FocusRequest = _ =>
            {
                Other.Activate();
                Native.SetForegroundWindow(OtherHandle);
                return false;
            };
            Window.FocusFallbackRequest = async (_, _, cancellation) =>
            {
                await Task.Delay(Timeout.Infinite, cancellation);
                return false;
            };
            Window.OpenPanel();
            Require(Window.IsVisible && Window.Opacity == 0 && !Window.OpeningTask.IsCompleted, "PRECONDITION: cancellable provisional acquisition was not established");
        }

        internal void RequireOwnedFocus() => Require(Window.IsVisible && Window.IsActive && Native.GetForegroundWindow() == Handle, "PRECONDITION: real owned overlay foreground not established");
        public void Dispose()
        {
            try
            {
                Window.Close();
                Other.Close();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }
        }
    }
}

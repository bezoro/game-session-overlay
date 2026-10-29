using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ApolloOverlay;

internal static class MouseKeyboardTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("local mouse activation recovers confirmed focus without activating its recovery click", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            w.Page(2);
            Pick(w, "sound-feedback");
            host.RevokeAdmission();
            host.RestoreNativeFocus();
            var before = w.Controller.Settings.SoundFeedbackEnabled;
            var press = SendMouse(ReviewFixTests.Button(w, "sound-feedback"), true);
            Require(press.Handled, "The activation click was not consumed");
            Complete(w.OpeningTask);
            Require(SendMouse(w, false).Handled, "Release of the activation click reached the button");
            host.RequireOwnedFocus();
            Require(host.Requests == 1 && w.Controller.Settings.SoundFeedbackEnabled == before, "Recovery repeated its native request or activated the underlying setting");
            Click(w, "sound-feedback");
            Require(w.Controller.Settings.SoundFeedbackEnabled != before, "A fresh pointer click was still blocked after recovery");
        });
        test("local keyboard recovery consumes Enter until release before allowing a fresh activation", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            w.Page(2);
            Pick(w, "sound-feedback");
            host.RevokeAdmission();
            host.RestoreNativeFocus();
            var before = w.Controller.Settings.SoundFeedbackEnabled;
            Require(SendKey(w, Key.Enter, true).Handled, "Recovery Enter escaped the overlay");
            Complete(w.OpeningTask);
            SendKey(w, Key.Enter, true);
            Require(host.Requests == 1 && w.Controller.Settings.SoundFeedbackEnabled == before, "A held recovery key activated a setting or requested focus twice");
            SendKey(w, Key.Enter, false);
            Tap(w, Key.Enter);
            host.RequireOwnedFocus();
            Require(w.Controller.Settings.SoundFeedbackEnabled != before, "Released keyboard did not rearm after recovery");
        });
        test("local mouse and keyboard do not steal focus from a genuine foreign foreground", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            w.Page(2);
            Pick(w, "sound-feedback");
            var requests = 0;
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
            Require(!w.IsVisible && Native.GetForegroundWindow() == host.OtherHandle, "PRECONDITION: refused acquisition did not hide the panel and preserve foreign foreground");
            var before = w.Controller.Settings;
            void Refused(string gesture)
            {
                Require(requests == 1, gesture + " requested native focus again: " + requests);
                Require(SessionHubSettings.Equivalent(w.Controller.Settings, before), gesture + " changed settings without admitted focus");
                Require(Native.GetForegroundWindow() == host.OtherHandle, gesture + " stole native foreground: " + FocusState(w));
                Require(!w.TransitionPending, gesture + " queued an outgoing action without admitted focus");
            }

            Require(SendMouse(ReviewFixTests.Button(w, "sound-feedback"), true).Handled, "Unadmitted pointer down was allowed to focus its button");
            Refused("Pointer down");
            Require(SendMouse(w, false).Handled, "Unadmitted pointer release reached its button");
            Refused("Pointer up");
            Click(w, "sound-feedback");
            Refused("Click");
            Tap(w, Key.Enter);
            Refused("Enter");
        });
        test("local keyboard Tab Shift Tab arrows Enter and Space navigate and open nested pages", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            Pick(w, "session-card");
            var first = w.SelectedAction;
            Tap(w, Key.Tab);
            Require(w.SelectedAction != first, "Tab did not advance the selection");
            host.Modifiers = ModifierKeys.Shift;
            Tap(w, Key.Tab);
            host.Modifiers = ModifierKeys.None;
            Require(w.SelectedAction == first, "Shift Tab did not return to the preceding action");
            Tap(w, Key.X);
            Require(w.ActionIds.Contains("application-mute"), "X did not open session options");
            Tap(w, Key.Escape);
            Require(w.SelectedAction == "session-card", "Options did not return to the session card");
            Pick(w, "audio");
            Tap(w, Key.Enter);
            Require(w.ActionIds.Contains("volume") && w.ActionIds.Contains("back"), "Enter did not open Audio");
            Pick(w, "back");
            Tap(w, Key.Space);
            Require(w.ActionIds.Contains("session-card"), "Space did not operate the nested Back action");
            host.RequireOwnedFocus();
        });
        test("session right-click opens the game options and refuses unconfirmed focus", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            var card = (Button)w.FindName("SessionCardButton");
            card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseUpEvent });
            Require(w.ActionIds.Contains("application-mute"), "Right-click did not open the session window options");
            Tap(w, Key.Escape);
            Require(w.SelectedAction == "session-card" && !w.ActionIds.Contains("session-options"), "Options did not return to the single full-width game card");
            host.RevokeAdmission();
            card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseUpEvent });
            Require(!w.ActionIds.Contains("application-mute") && !w.TransitionPending, "Unconfirmed focus admitted a pointer context action");
        });
        test("local keyboard and pointer remain usable after footer Save Discard and animated acknowledgement", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            w.Page(2);
            Pick(w, "sound-feedback");
            Tap(w, Key.Space);
            Pick(w, "haptic-feedback");
            Tap(w, Key.Enter);
            Require(w.Controller.Settings.SoundFeedbackEnabled && w.Controller.Settings.HapticFeedbackEnabled && w.Controller.SettingsDirty, "Keyboard did not change feedback preferences");
            Pick(w, "save-pending");
            Tap(w, Key.Enter);
            w.UpdateLayout();
            Require(!w.Controller.SettingsDirty && w.SelectedAction != "exit", "Save failed or selected Exit");
            RequireLiveSelection(w);
            host.RequireOwnedFocus();
            var saved = UserSettings.Load(Path.Combine(host.Root, "settings.json"), out _);
            Require(saved.SoundFeedbackEnabled && saved.HapticFeedbackEnabled, "Save did not persist keyboard changes");
            Click(w, "sound-feedback");
            Require(w.Controller.SettingsDirty && !w.Controller.Settings.SoundFeedbackEnabled, "Pointer did not edit after Save");
            Click(w, "discard-pending");
            Require(!w.Controller.SettingsDirty && w.Controller.Settings.SoundFeedbackEnabled, "Pointer Discard did not restore the saved preferences");
            Pick(w, "side");
            Tap(w, Key.Enter);
            Require(w.Controller.Settings.PanelSide == "Right", "Keyboard could not change placement");
            var button = ReviewFixTests.Button(w, "side");
            button.ApplyTemplate();
            var flash = (Border)button.Template.FindName("ActivationFeedback", button);
            Require(flash.Opacity > 0 && flash.HasAnimatedProperties == InteractionMotion.Allowed, "Keyboard acknowledgement did not follow the motion policy");
            Tap(w, Key.Up);
            Require(w.SelectedAction != "side", "Keyboard navigation stopped after changing placement");
            RequireLiveSelection(w);
        });
        test("local keyboard can describe disabled choices and enter adjust and leave the Guide slider", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            w.Page(2);
            Pick(w, "guide");
            var enabled = w.Controller.Settings.GuideXInputEnabled;
            Tap(w, Key.Enter);
            Require(w.Controller.Settings.GuideXInputEnabled == enabled && w.Feedback.StartsWith("Unavailable"), "Disabled Guide was activated or lacked an explanation");
            Pick(w, "guide-limit");
            var before = w.Controller.Settings.GuideOpenMaxHoldMs;
            Tap(w, Key.Right);
            Require(w.Controller.Settings.GuideOpenMaxHoldMs == Math.Min(3000, before + 100), "Arrow did not adjust the selected Guide limit");
            Tap(w, Key.Tab);
            Require(Keyboard.FocusedElement is Slider, "Tab did not enter the editable slider: " + SliderState(w));
            var slider = (Slider)Keyboard.FocusedElement;
            before = w.Controller.Settings.GuideOpenMaxHoldMs;
            Tap(slider, Key.Right);
            Require(w.Controller.Settings.GuideOpenMaxHoldMs == Math.Min(3000, before + 100), "The focused slider did not receive its own arrow adjustment");
            host.Modifiers = ModifierKeys.Shift;
            Tap(slider, Key.Tab);
            host.Modifiers = ModifierKeys.None;
            Require(w.SelectedAction == "guide-limit" && Keyboard.FocusedElement is Button, "Shift Tab trapped focus in the slider");
            Tap(w, Key.Tab);
            Require(Keyboard.FocusedElement is Slider, "Slider could not be reentered: " + SliderState(w));
            Tap((Slider)Keyboard.FocusedElement, Key.Tab);
            Require(w.SelectedAction != "guide-limit" && Keyboard.FocusedElement is not Slider, "Forward Tab did not leave the slider for the next overlay action");
            RequireLiveSelection(w);
        });
        test("local keyboard can leave readonly window and operation readers without mutating their text", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            w.OpenOptions(host.Platform.Window);
            Click(w, "details");
            Pick(w, "window-information");
            Tap(w, Key.Enter);
            var reader = RequireReader();
            var original = reader.Text;
            host.Modifiers = ModifierKeys.Control;
            Require(!SendKey(reader, Key.A, true).Handled, "Read-only text selection shortcut was swallowed");
            SendKey(reader, Key.A, false);
            host.Modifiers = ModifierKeys.None;
            Tap(reader, Key.Tab);
            Require(w.SelectedAction != "window-information" && Keyboard.FocusedElement is not TextBox, "Tab trapped focus in window information");
            host.Modifiers = ModifierKeys.Shift;
            Tap(w, Key.Tab);
            host.Modifiers = ModifierKeys.None;
            Require(w.SelectedAction == "window-information" && reader.Text == original, "Shift Tab did not return to the reader action or changed its text");
            Tap(w, Key.Enter);
            Tap(RequireReader(), Key.Right);
            Require(w.SelectedAction == "switch-detail" && Keyboard.FocusedElement is not TextBox, "Right did not leave the window reader");
            w.Controller.SetOperation("Unconfirmed", "A long result message for reading and selection.", "audio");
            w.RefreshRows();
            Click(w, "operation-details");
            Pick(w, "operation-text");
            Tap(w, Key.Enter);
            var operation = RequireReader();
            var message = operation.Text;
            Tap(operation, Key.Tab);
            Require(w.SelectedAction != "operation-text" && Keyboard.FocusedElement is not TextBox && operation.Text == message, "Tab trapped focus in the result reader or modified the result");
            host.RequireOwnedFocus();
        });
        test("local keyboard can leave readonly release notes and return with Shift Tab", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            w.Page(2);
            Click(w, "updates");
            Click(w, "check-updates");
            Complete(w.UpdateTask);
            Click(w, "release:" + Source.Next.BuildId);
            Pick(w, "update-notes");
            Tap(w, Key.Enter);
            var reader = RequireReader();
            var notes = reader.Text;
            Tap(reader, Key.Tab);
            Require(w.SelectedAction == "back" && Keyboard.FocusedElement is not TextBox, "Tab trapped focus in release notes or skipped the next visible action");
            host.Modifiers = ModifierKeys.Shift;
            Tap(w, Key.Tab);
            host.Modifiers = ModifierKeys.None;
            Require(w.SelectedAction == "update-notes" && reader.Text == notes, "Shift Tab lost the notes action or modified release notes");
            Tap(w, Key.Enter);
            Tap(RequireReader(), Key.Right);
            Require(w.SelectedAction == "prepare-update" && host.Source.Stages == 0, "Reading notes triggered a download or prevented leaving the reader");
            host.RequireOwnedFocus();
        });
        test("local held Escape leaves one nested page per fresh press", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            w.OpenOptions(host.Platform.Window);
            Click(w, "details");
            SendKey(w, Key.Escape, true);
            Require(w.ActionIds.Contains("details") && !w.ActionIds.Contains("window-information"), "Escape did not leave Details");
            SendKey(w, Key.Escape, true);
            Require(w.ActionIds.Contains("details") && !w.TransitionPending, "Held Escape unwound another page or dismissed the overlay");
            SendKey(w, Key.Escape, false);
            Tap(w, Key.Escape);
            Require(w.ActionIds.Contains("session-card") && !w.TransitionPending && w.IsVisible, "A fresh Escape could not return to the Hub");
        });
    }

    private static TextBox RequireReader()
    {
        Require(Keyboard.FocusedElement is TextBox { IsReadOnly: true }, "PRECONDITION: a real read-only reader is not focused");
        return (TextBox)Keyboard.FocusedElement;
    }

    private static void RequireLiveSelection(MainWindow window) => Require(Keyboard.FocusedElement is FrameworkElement live && live.IsVisible && Equals(live.Tag, window.SelectedAction), "Keyboard focus and live overlay selection diverged");
    private static string FocusState(MainWindow window) => $"selected={window.SelectedAction}; focused={Keyboard.FocusedElement?.GetType().Name}/{(Keyboard.FocusedElement as FrameworkElement)?.Tag}; active={window.IsActive}; nativeOwn={Native.GetForegroundWindow() == new WindowInteropHelper(window).Handle}";
    private static string SliderState(MainWindow window)
    {
        var slider = PremiumInterfaceTests.Descendants<Slider>(ReviewFixTests.Button(window, "guide-limit")).Single();
        return FocusState(window) + $"; sliderFocusable={slider.Focusable}; sliderVisible={slider.IsVisible}; sliderEnabled={slider.IsEnabled}; sliderLoaded={slider.IsLoaded}; sliderSize={slider.ActualWidth}x{slider.ActualHeight}";
    }

    private static void Pick(MainWindow window, string id)
    {
        for (var count = 0; count <= window.ActionIds.Count && window.SelectedAction != id; count++)
            window.Sequential(1);
        Require(window.SelectedAction == id, "Action is not reachable: " + id);
        // Routed test events are synchronous. Render the new route/control template
        // before the next gesture, as Windows does between user interactions.
        window.UpdateLayout();
    }

    private static void Click(MainWindow window, string id) => ReviewFixTests.Button(window, id).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Tap(UIElement target, Key key)
    {
        SendKey(target, key, true);
        SendKey(target, key, false);
    }

    private static KeyEventArgs SendKey(UIElement target, Key key, bool down)
    {
        var source = PresentationSource.FromVisual(target) ?? throw new Exception("PRECONDITION: key target has no native presentation source");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent
        };
        target.RaiseEvent(args);
        return args;
    }

    private static MouseButtonEventArgs SendMouse(UIElement target, bool down)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = down ? Mouse.PreviewMouseDownEvent : Mouse.PreviewMouseUpEvent
        };
        target.RaiseEvent(args);
        return args;
    }

    private static void Complete(Task task) => ReviewFixTests.Complete(task);
    private static void Require(bool value, string reason)
    {
        if (!value)
            throw new Exception(reason);
    }

    private sealed class Host : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "OverlayMouseKeyboard", Guid.NewGuid().ToString("N"));
        internal readonly PremiumInterfaceTests.FixturePlatform Platform = new(preview: false);
        internal readonly Source Source = new();
        internal MainWindow Window
        {
            get;
        }
        internal Window Other
        {
            get;
        } = new()
        {
            Title = "Owned local input refusal fixture",
            Width = 260,
            Height = 140,
            Left = 80,
            Top = 80,
            Topmost = true,
            ShowInTaskbar = false
        };
        internal nint Handle => new WindowInteropHelper(Window).Handle;
        internal nint OtherHandle => new WindowInteropHelper(Other).EnsureHandle();

        internal ModifierKeys Modifiers;
        internal int Requests;
        internal Host()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = Application.Current ?? new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            if (app.TryFindResource(ThemeSupport.Card) is null)
                ThemeSupport.ForceForValidation(app, false);
            Window = new MainWindow(renderOnly: true, platform: Platform, diagnostics: new LocalDiagnostics(Root), updates: Source);
            Window.PhysicalKeyReader = _ => 0;
            Window.DpiReader = _ => 96;
            Window.KeyboardModifiersReader = () => Modifiers;
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

        internal void RevokeAdmission()
        {
            // Model a transient ownership observation without changing the actual
            // active HWND. The next local gesture must reconfirm admission and
            // consume its own press, rather than activating the selected setting.
            Window.FocusForeground = () => OtherHandle;
            Window.Move(NavDirection.Down);
            Require(Window.IsVisible && Window.Feedback.Contains("focus lost", StringComparison.OrdinalIgnoreCase), "PRECONDITION: navigation admission was not revoked");
            Require(Native.GetForegroundWindow() == Handle && Window.IsActive, "PRECONDITION: transient observation changed the actual HWND");
        }

        internal void RestoreNativeFocus()
        {
            Window.FocusForeground = Native.GetForegroundWindow;
            Window.FocusRequest = handle =>
            {
                Requests++;
                return Native.SetForegroundWindow(handle);
            };
            RequireOwnedFocus();
        }

        internal void RequireOwnedFocus() => Require(Window.IsVisible && Window.IsActive && Native.GetForegroundWindow() == Handle, "PRECONDITION: overlay does not own the actual active native foreground");
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

    private sealed class Source : IUpdateSource
    {
        internal static readonly UpdateCandidate Next = new(42, "0.2.0-preview.42.1+1111111", "mouse-keyboard-fixture", new string('1', 40), "testing", DateTimeOffset.Parse("2026-10-28T12:00:00Z"), "Literal notes for reading and text selection.", 201, "fixture.nupkg", 1234, new string('a', 64));
        internal int Stages;
        public Task<IReadOnlyList<UpdateCandidate>> CheckAsync(string channel, CancellationToken token) => Task.FromResult<IReadOnlyList<UpdateCandidate>>([Next]);
        public Task<PreparedUpdate> StageAsync(UpdateCandidate candidate, IProgress<UpdateProgress>? progress, CancellationToken token)
        {
            Stages++;
            throw new Exception("Reader test must not download or install a release");
        }

        public void Dispose()
        {
        }
    }
}

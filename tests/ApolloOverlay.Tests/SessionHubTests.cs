using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApolloOverlay;

internal static class SessionHubTests
{
    private static void Check(bool value, string reason)
    {
        if (!value)
            throw new Exception(reason);
    }

    internal static void Run(Action<string, Action> test)
    {
        SelectionAudioRegressions(test);
        test("hub settings v1 v2 migrate and optional fields recover independently", () =>
        {
            foreach (var version in new[]
            {
                1,
                2,
                3
            }

            )
            {
                var value = UserSettings.Parse("{\"Version\":" + version + ",\"GuideXInputEnabled\":true,\"UiScalePercent\":\"bad\",\"PanelSide\":4,\"FavoriteQuickActions\":false,\"HapticFeedbackEnabled\":{},\"SoundFeedbackEnabled\":true}");
                Check(value.Version == 3
                    && value.GuideXInputEnabled
                    && value.SoundFeedbackEnabled
                    && !value.HapticFeedbackEnabled
                    && value.UiScalePercent == 100
                    && value.PanelSide == "Left"
                    && SessionHubSettings.Favorites(value).Count() == 6, "Field-local recovery lost valid settings");
            }
        });
        test("hub favorites missing defaults versus empty and stable sanitized order", () =>
        {
            Check(SessionHubSettings.Favorites(UserSettings.Parse("{}")).SequenceEqual(SessionHubSettings.Catalog), "Missing is not defaults");
            Check(SessionHubSettings.Favorites(UserSettings.Parse("{\"FavoriteQuickActions\":[]}")).Length == 0, "Explicit empty lost");
            Check(SessionHubSettings.Favorites(UserSettings.Parse("{\"FavoriteQuickActions\":[\"audio\",\"bad\",\"playnite\",\"audio\"]}")).SequenceEqual(new[] { "audio", "playnite" }), "Order/dedup invalid");
        });
        test("hub structural dirty equality and snapshot isolation", () =>
        {
            using var c = new OverlayController(new PreviewPlatform());
            var original = c.Settings;
            var copy = original.FavoriteQuickActions!.ToArray();
            c.Change(original with
            {
                FavoriteQuickActions = copy
            });
            Check(!c.SettingsDirty, "Equivalent reconstructed array dirty");
            copy[0] = "audio";
            var exposed = c.Settings;
            exposed.FavoriteQuickActions![0] = "taskview";
            Check(!c.SettingsDirty && c.Settings.FavoriteQuickActions![0] == "playnite", "Caller array aliased current/saved");
            c.Change(SessionHubSettings.Move(c.Settings, "steam", -1));
            Check(c.SettingsDirty, "Reorder clean");
            c.Change(original);
            Check(!c.SettingsDirty, "Exact revert dirty");
        });
        test("hub settings v3 roundtrip all hidden and explicit optins", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ApolloHubTests", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(folder, "settings.json");
            try
            {
                var value = new UserSettings(FavoriteQuickActions: [], UiScalePercent: 150, PanelSide: "Right", SoundFeedbackEnabled: true, HapticFeedbackEnabled: true, SetupSeen: true);
                UserSettings.Save(path, value);
                Check(SessionHubSettings.Equivalent(value, UserSettings.Load(path, out _)), "v3 roundtrip lost fields");
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        });
        test("hub favorites all hide restore reorder selected identity", () =>
        {
            var value = new UserSettings();
            foreach (var id in SessionHubSettings.Catalog)
                value = SessionHubSettings.Toggle(value, id);
            Check(SessionHubSettings.Favorites(value).Length == 0, "All-hidden impossible");
            value = SessionHubSettings.Toggle(value, "audio");
            value = SessionHubSettings.Toggle(value, "playnite");
            value = SessionHubSettings.Move(value, "playnite", -1);
            Check(SessionHubSettings.Favorites(value).SequenceEqual(new[] { "playnite", "audio" }), "Restore/reorder changed identity");
        });
        test("hub bounded nested contexts and tab memories", () =>
        {
            var state = new OverlayViewState();
            state.Remember("session-options", 72);
            state.Open(new(OverlayRoute.TargetOptions, new(1, 1, 1, "Title", "app"), "minimize", 30));
            state.Open(new(OverlayRoute.Details, SelectedId: "back"));
            Check(state.Back() && state.Current.SelectedId == "minimize" && state.Current.ScrollOffset == 30, "Details origin lost");
            Check(state.Back() && state.Current.SelectedId == "session-options" && state.Current.ScrollOffset == 72, "Options origin lost");
            state.SwitchTab(OverlayTab.System);
            state.Remember("customize", 35);
            state.SwitchTab(OverlayTab.Hub);
            Check(state.Current.SelectedId == "session-options", "Hub tab memory lost");
            state.SwitchTab(OverlayTab.System);
            Check(state.Current.SelectedId == "customize", "System tab memory lost");
            for (var i = 0; i < 20; i++)
                state.Open(new(OverlayRoute.OperationResult));
            Check(state.Depth == 12, "Unbounded parent stack");
        });
        test("hub recent identities only confirmed observations and previous toggle", () =>
        {
            var recent = new RecentWindows();
            WindowRef W(int i) => new(i, i, i, "Title" + i, "app", Generation: i);
            var a = W(1);
            var b = W(2);
            recent.Open(a, _ => true);
            recent.ConfirmSwitch(b, new(ResultKind.Requested, "request"), b, _ => true);
            Check(recent.Items.Count == 1, "Optimistic request promoted");
            recent.ConfirmSwitch(b, new(ResultKind.Completed, "ok"), a, _ => true);
            Check(recent.Items.Count == 1, "Wrong foreground promoted");
            recent.ConfirmSwitch(b, new(ResultKind.Completed, "ok"), b, _ => true);
            Check(recent.Previous(_ => true)?.Same(a) == true, "Previous not A");
            recent.Open(b, _ => true);
            recent.ConfirmSwitch(a, new(ResultKind.Completed, "ok"), a, _ => true);
            Check(recent.Previous(_ => true)?.Same(b) == true, "Toggle not B");
            for (var i = 3; i < 10; i++)
                recent.Observe(W(i), _ => true);
            Check(recent.Items.Count == 5, "Capacity exceeded");
            recent.Prune(w => w.Pid != 9);
            Check(!recent.Items.Any(w => w.Pid == 9), "Stale identity retained");
        });
        foreach (var dpi in new[]
        {
            96,
            144,
            192
        }

        )
            foreach (var percent in new[]
            {
                100,
                115,
                130,
                150
            }

            )
                test($"hub physical placement negative origin dpi{dpi} scale{percent}", () =>
                {
                    var display = new DisplayRef("negative", -2560, -180, 2560, 1400, false);
                    var left = SidebarLayout.Place(display, dpi, percent, "Left");
                    var right = SidebarLayout.Place(display, dpi, percent, "Right");
                    Check(left.X >= display.X && left.Y >= display.Y && right.X + right.Width <= display.X + display.Width && left.Height <= display.Height, "Outside usable work area");
                    Check(right.X > left.X && right.Width == left.Width, "Side placement broken");
                    Check(SidebarLayout.EffectiveScale(display, dpi, percent) >= percent / 100d, "Chosen scale was reduced");
                });
        BusyControllerChecks(test);
        TelemetryFailures(test);
        RunViews(test);
    }

    internal static void BusyControllerChecks(Action<string, Action> test)
    {
        foreach (var buttons in new[]
        {
            PadButtons.A,
            PadButtons.X,
            PadButtons.LB,
            PadButtons.RB,
            PadButtons.Up,
            PadButtons.Down,
            PadButtons.Left,
            PadButtons.Right
        }

        )
            test("hub busy Tick blocks " + buttons, () => BusyController(buttons, false, ResultKind.Partial));
        var mixed = PadButtons.B | PadButtons.A | PadButtons.X | PadButtons.LB | PadButtons.RB | PadButtons.Down;
        foreach (var buttons in new[]
        {
            PadButtons.B,
            mixed
        }

        )
            foreach (var outcome in new[]
            {
                ResultKind.Partial,
                ResultKind.Failed,
                ResultKind.Unknown
            }

            )
                test("hub busy Tick cancels only " + buttons + " then " + outcome, () => BusyController(buttons, true, outcome));
    }

    private static void BusyController(PadButtons buttons, bool cancel, ResultKind outcome)
    {
        var w = Fixture(out var p, out var audio, out _, out _);
        var completion = new TaskCompletionSource<ApplicationAudioChange>();
        audio.Next = completion.Task;
        try
        {
            w.Left = -12000;
            w.Top = -12000;
            w.ShowDiagnostic();
            w.OpenOptions(p.A);
            Pick(w, "application-mute");
            Click(w, "application-mute");
            Release(w);
            Check(audio.Writes == 1 && w.Controller.Operation is { State: "Pending", Dispatched: true }, "Fake audio command was not dispatched once");
            var selected = w.SelectedAction;
            var entries = w.ActionIds.ToArray();
            var title = ((TextBlock)w.FindName("PageTitle")).Text;
            var frames = new[]
            {
                new PadFrame(true, PadButtons.None),
                default,
                default,
                default
            };
            w.ReviewTick(frames);
            frames[0] = new(true, buttons);
            w.ReviewTick(frames);
            Check(audio.Token.IsCancellationRequested == cancel, "Tick swallowed B or cancelled for another button");
            Check(w.IsVisible && w.SelectedAction == selected && w.ActionIds.SequenceEqual(entries) && ((TextBlock)w.FindName("PageTitle")).Text == title, "Busy input navigated, activated or hid the panel");
            Check(audio.Writes == 1 && p.Switches == 0 && p.Minimizes == 0 && !w.TransitionPending, "Busy input dispatched another operation");
            if (cancel)
                Check(w.Controller.Operation is { State: "Unconfirmed", Dispatched: true } operation && operation.Message.Contains("cannot be undone"), "Cancellation claimed undo or confirmed success");
            else
                Check(w.Controller.Operation is { State: "Pending", Dispatched: true }, "Non-B input changed pending outcome");
            if (outcome == ResultKind.Unknown)
                completion.SetCanceled(audio.Token);
            else
                completion.SetResult(new(new(outcome, "Fixture late outcome; verify the application."), audio.Snapshot(), 2, 1, 1, true));
            Complete(w.OperationTask);
            var expected = outcome switch
            {
                ResultKind.Partial => "Partial",
                ResultKind.Failed => "Failed",
                _ => "Unconfirmed"
            };
            Check(audio.Writes == 1 && w.Controller.Operation is { Dispatched: true } result && result.State == expected, "Late outcome was lost, retried or reported as success");
            Check(w.IsVisible && w.SelectedAction == selected && p.Switches == 0 && p.Minimizes == 0, "Late outcome navigated or hid the panel");
            frames[0] = new(true, PadButtons.None);
            w.ReviewTick(frames);
            Check(audio.Writes == 1 && !w.TransitionPending, "Neutral recovery retried the command");
        }
        finally
        {
            w.Close();
            completion.TrySetCanceled();
            Complete(w.OperationTask);
        }
    }

    internal static void TelemetryFailures(Action<string, Action> test)
    {
        test("hub failed input observation stays unknown and suspends feedback without battery polling", () =>
        {
            var w = Fixture(out _, out _, out var devices, out var feedback);
            try
            {
                w.UpdateControllerTelemetry(Enumerable.Repeat(new PadFrame(false, PadButtons.None, ReadFailed: true), 4).ToArray(), null, 0);
                var text = ((TextBlock)w.FindName("ControllerStatusLabel")).Text;
                Console.WriteLine("TELEMETRY-FAILURE status=" + text + "; batteryReads=" + devices.Reads + "; feedbackSuspends=" + feedback.Suspends);
                Check(text.Contains("unavailable") && !text.Contains("No"), "Failed/unknown connection was reported as disconnected");
                Check(devices.Reads == 0 && feedback.Suspends > 0, "Unknown input queried battery or retained feedback context");
            }
            finally
            {
                w.Close();
            }
        });
        foreach (var route in new[]
        {
            "system",
            "setup"
        }

        )
            test("hub live controller paragraph follows disconnect and owner change " + route, () =>
            {
                var w = Fixture(out var p, out _, out _, out _);
                try
                {
                    w.Left = -12000;
                    w.Top = -12000;
                    w.ShowDiagnostic();
                    w.UpdateControllerTelemetry(new PadFrame[] { new(true, 0), default, default, default }, 0, 0);
                    Navigate(w, route, p);
                    Arrange(w, 572, 940);
                    var paragraph = PremiumInterfaceTests.Descendants<TextBlock>((DependencyObject)w.FindName("Rows")).Single(t => t.Text.Contains("Controller 1") && t.Text.Contains("Battery medium"));
                    var selected = w.SelectedAction;
                    var selectedButton = ReviewFixTests.Button(w, selected);
                    w.UpdateControllerTelemetry(new PadFrame[4], null, 1);
                    Console.WriteLine("TELEMETRY-LIVE route=" + route + "; afterDisconnect=" + paragraph.Text);
                    Check(!paragraph.Text.Contains("Battery medium") && paragraph.Text.Contains("No"), "Visible paragraph retained disconnected controller battery");
                    w.UpdateControllerTelemetry(new PadFrame[] { default, new(true, 0), default, default }, 1, 2);
                    Check(paragraph.Text.Contains("Controller 2") && !paragraph.Text.Contains("Controller 1"), "Visible paragraph retained prior controller owner");
                    Check(System.Windows.Automation.AutomationProperties.GetName(paragraph) == paragraph.Text, "Controller paragraph UIA name stale");
                    Check(ReferenceEquals(selectedButton, ReviewFixTests.Button(w, selected)) && w.SelectedAction == selected, "Telemetry rebuilt view/selection");
                    RequireFocus(w);
                }
                finally
                {
                    w.Close();
                }
            });
    }

    private static void Pick(MainWindow window, string id)
    {
        for (var n = 0; n <= window.ActionIds.Count && window.SelectedAction != id; n++)
            window.Sequential(1);
        Check(window.SelectedAction == id, "Cannot reach " + id);
    }

    private static void Click(MainWindow window, string id) => ReviewFixTests.Button(window, id).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    private static void Release(MainWindow window)
    {
        window.AdvanceHandoff(new(new PadFrame[4], 0, GuideValid: true), 0);
        window.AdvanceHandoff(new(new PadFrame[4], 100, GuideValid: true), 100);
    }

    private static void Complete(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }

        Check(task.IsCompleted, "Task deadline");
        task.GetAwaiter().GetResult();
    }

    private static MainWindow Fixture(out HubPlatform platform, out HubAudio audio, out HubDevices devices, out HubFeedback feedback)
    {
        var app = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        if (app.TryFindResource(ThemeSupport.Card) is null)
            ThemeSupport.ForceForValidation(app, false);
        platform = new();
        audio = new();
        devices = new();
        feedback = new();
        var window = new MainWindow(renderOnly: true, platform: platform, appAudioService: audio, devices: devices, feedback: feedback);
        window.InputContextReader = () => new(true, true, true, true);
        window.PhysicalKeyReader = _ => 0;
        window.Controller.CaptureReturn();
        Complete(window.Controller.Refresh());
        window.RefreshRows();
        return window;
    }

    private static void Arrange(MainWindow window, int width, int height)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static FrameworkElement ActualFocus(MainWindow window) => Keyboard.FocusedElement as FrameworkElement ?? throw new Exception("No actual keyboard focus");
    private static void RequireFocus(MainWindow window)
    {
        var focus = ActualFocus(window);
        var id = focus.Tag as string;
        Check(id == window.SelectedAction
            || id == "description:" + window.SelectedAction
            || focus.Name == "SessionCardButton"
            && window.SelectedAction == "session-card"
            || focus.Name == "SettingsGear"
            && window.SelectedAction == "settings", "Actual focus differs from selected " + window.SelectedAction + ": " + id);
    }

    private static void RequireRevealed(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        var focus = ActualFocus(window);
        var scroll = (ScrollViewer)window.FindName("ContentScroll");
        if (!scroll.IsAncestorOf(focus))
            return;
        var content = focus.TransformToAncestor(root).TransformBounds(new Rect(new Size(focus.ActualWidth, focus.ActualHeight)));
        var viewport = scroll.TransformToAncestor(root).TransformBounds(new Rect(new Size(scroll.ActualWidth, scroll.ActualHeight)));
        Check(content.Top >= viewport.Top - 2 && content.Bottom <= viewport.Bottom + 2, "Selected control clipped: " + window.SelectedAction + " bounds=" + content + " viewport=" + viewport);
    }

    private static void RunViews(Action<string, Action> test)
    {
        test("hub compact selected card remains fully visible with dirty and retained result", () =>
        {
            var w = Fixture(out _, out _, out _, out _);
            try
            {
                w.Left = -12000;
                w.Top = -12000;
                w.Width = 360;
                w.Height = 480;
                w.ShowDiagnostic();
                w.Controller.Change(w.Controller.Settings with
                {
                    ReducedMotion = true
                });
                w.Controller.SetOperation("Partial", "The operation was not confirmed. Check the window and read the full result before trying again. No additional command was sent.");
                w.RefreshRows();
                Arrange(w, 360, 480);
                Pick(w, "session-card");
                w.RevealSelection();
                ((FrameworkElement)w.Content).UpdateLayout();
                var scroll = (ScrollViewer)w.FindName("ContentScroll");
                var card = (Button)w.FindName("SessionCardButton");
                Console.WriteLine($"COMPACT-DIRTY-RESULT viewport={scroll.ViewportHeight}; cardHeight={card.ActualHeight}; offset={scroll.VerticalOffset}");
                Check(scroll.ViewportHeight >= card.ActualHeight, "Persistent chrome clips primary card even after revealing selection");
                RequireFocus(w);
            }
            finally
            {
                w.Close();
            }
        });
        test("hub card body and sibling options focus resume exact target", () =>
        {
            var w = Fixture(out var p, out _, out _, out _);
            try
            {
                Check(w.SelectedAction == "session-card" && !w.ActionIds.Contains("return"), "Old separate resume row remains");
                w.Left = -12000;
                w.Top = -12000;
                w.ShowDiagnostic();
                Arrange(w, 572, 940);
                RequireFocus(w);
                w.Move(NavDirection.Right);
                Check(w.SelectedAction == "session-options", "Right misses sibling options");
                RequireFocus(w);
                w.ActivateEntry();
                Check(w.ActionIds.Contains("application-mute") && p.Switches == 0, "Opening options switched window");
                w.Back();
                Check(w.SelectedAction == "session-options", "Options Back lost origin");
                Pick(w, "session-card");
                w.ActivateEntry();
                Release(w);
                Complete(w.OperationTask);
                Check(p.Switches == 1 && p.LastSwitch?.Same(p.A) == true, "Card resumed wrong target");
            }
            finally
            {
                w.Close();
            }
        });
        test("hub app mute explicit desired and minimize stay in place", () =>
        {
            var w = Fixture(out var p, out var audio, out _, out _);
            try
            {
                w.Left = -12000;
                w.Top = -12000;
                w.ShowDiagnostic();
                Arrange(w, 572, 940);
                w.OpenOptions(p.A);
                Click(w, "application-mute");
                Check(audio.Writes == 0 && w.TransitionPending, "App mute bypassed release");
                Release(w);
                Complete(w.OperationTask);
                Check(audio.Writes == 1 && audio.LastDesired && w.IsVisible && p.Switches == 0, "App mute hid or switched/master action");
                audio.Mute = ApplicationMuteState.Muted;
                audio.Emit();
                Click(w, "application-mute");
                Release(w);
                Complete(w.OperationTask);
                Check(audio.Writes == 2 && !audio.LastDesired, "Unmute was not explicit desired false");
                Click(w, "minimize");
                Release(w);
                Complete(w.OperationTask);
                Check(p.Minimizes == 1 && w.IsVisible && w.Controller.Recent.Items.Count == 1, "Minimize hid panel or promoted history");
            }
            finally
            {
                w.Close();
            }
        });
        test("hub stale target options never silently retarget", () =>
        {
            var w = Fixture(out var p, out _, out _, out _);
            try
            {
                w.OpenOptions(p.A);
                p.Stale = true;
                w.Controller.Select(p.B);
                w.RefreshRows();
                Check(!ReviewFixTests.Button(w, "minimize").IsEnabled && !ReviewFixTests.Button(w, "application-mute").IsEnabled, "Stale options retargeted");
                Pick(w, "minimize");
                w.ActivateEntry();
                Release(w);
                Check(p.Minimizes == 0, "Stale target dispatched");
                Click(w, "choose-session");
                Check(((TextBlock)w.FindName("PageTitle")).Text == "Choose window", "No explicit stale recovery");
            }
            finally
            {
                w.Close();
            }
        });
        test("hub chooser does not switch or promote history and nested Back restores", () =>
        {
            var w = Fixture(out var p, out _, out _, out _);
            try
            {
                w.OpenOptions(p.A);
                Pick(w, "details");
                w.ActivateEntry();
                Pick(w, "back");
                w.ActivateEntry();
                Check(w.SelectedAction == "details", "Details Back origin lost");
                Click(w, "choose-session");
                var id = w.ActionIds.First(id => id.StartsWith("window:") && id.Contains(":52:"));
                Click(w, id);
                Check(p.Switches == 0 && w.Controller.Session.Anchor?.Same(p.B) == true && w.Controller.Recent.Items.Count == 1, "Chooser performed optimistic switch/history");
                w.Back();
                Check(w.ActionIds.Contains("session-card"), "Nested chooser did not return hub");
            }
            finally
            {
                w.Close();
            }
        });
        test("hub async audio snapshot rejects stale generation without rebuilding focus", () =>
        {
            var w = Fixture(out var p, out var audio, out _, out _);
            try
            {
                w.OpenOptions(p.A);
                var button = ReviewFixTests.Button(w, "application-mute");
                var old = audio.Snapshot();
                w.Controller.Select(p.B);
                w.Back();
                w.RefreshRows();
                var badge = ((TextBlock)w.FindName("SessionAudioLabel")).Text;
                audio.Raise(old with
                {
                    Revision = 99,
                    MuteState = ApplicationMuteState.Muted
                });
                Check(((TextBlock)w.FindName("SessionAudioLabel")).Text == badge, "Stale target callback changed badge");
                w.OpenOptions(p.B);
                button = ReviewFixTests.Button(w, "application-mute");
                audio.Emit();
                Check(ReferenceEquals(button, ReviewFixTests.Button(w, "application-mute")), "Status rebuilt focused action");
            }
            finally
            {
                w.Close();
            }
        });
        test("hub battery cadence hidden zero polling and deliberate feedback context", () =>
        {
            var w = Fixture(out _, out _, out var devices, out var feedback);
            try
            {
                var frames = new PadFrame[]
                {
                    new(true, 0),
                    default,
                    default,
                    default
                };
                w.UpdateControllerTelemetry(frames, 0, 0);
                w.UpdateControllerTelemetry(frames, 0, 4999);
                Check(devices.Reads == 1, "Battery overpolled");
                w.UpdateControllerTelemetry(frames, 0, 5000);
                Check(devices.Reads == 2, "Battery cadence did not refresh");
                w.InputContextReader = () => new(false, false, false, false);
                w.UpdateControllerTelemetry(frames, 0, 10000);
                Check(devices.Reads == 2 && feedback.Suspends > 0, "Hidden battery/cues active");
                w.InputContextReader = () => new(true, true, true, true);
                w.UpdateControllerTelemetry(new PadFrame[] { default, default, default, default }, null, 10001);
                w.UpdateControllerTelemetry(frames, 0, 10002);
                Check(devices.Reads == 3, "Reconnect kept stale battery epoch");
                var requests = feedback.Requests;
                w.RefreshRows();
                Check(feedback.Requests == requests, "Refresh emitted cue");
                w.Sequential(1);
                Check(feedback.Requests == requests + 1, "Deliberate navigation missed cue");
            }
            finally
            {
                w.Close();
            }
        });
        test("hub setup Skip Finish Back preserve drafts without opting into feedback", () =>
        {
            var w = Fixture(out _, out _, out _, out _);
            try
            {
                w.Controller.Change(w.Controller.Settings with
                {
                    UiScalePercent = 130
                });
                w.Page(2);
                Click(w, "setup");
                Check(w.ActionIds.Contains("setup-next") && !w.ActionIds.Contains("setup-back"), "Step1 navigation wrong");
                Click(w, "setup-next");
                Check(w.ActionIds.Contains("ui-scale") && w.ActionIds.Contains("setup-back"), "Step2 absent");
                Click(w, "setup-next");
                Check(w.ActionIds.Contains("sound-feedback") && w.ActionIds.Contains("configure-playnite"), "Step3 absent");
                Click(w, "setup-back");
                Check(w.ActionIds.Contains("ui-scale"), "Step Back lost route");
                w.Back();
                Check(w.Controller.Settings.UiScalePercent == 130 && w.Controller.SettingsDirty && !w.Controller.Settings.SetupSeen, "Esc dropped draft or persisted invitation");
                Click(w, "setup");
                Click(w, "setup-skip");
                Check(w.Controller.Settings.SetupSeen
                    && w.Controller.SettingsDirty
                    && !w.Controller.Settings.SoundFeedbackEnabled
                    && !w.Controller.Settings.HapticFeedbackEnabled
                    && w.Controller.Settings.GuideXInputEnabled, "Skip changed Guide or saved feedback settings");
                Click(w, "setup");
                Click(w, "setup-next");
                Click(w, "setup-next");
                Click(w, "setup-next");
                Check(w.Controller.Settings.SetupSeen && w.ActionIds.Contains("setup"), "Finish did not restore origin");
            }
            finally
            {
                w.Close();
            }
        });
        test("hub favorite sibling arrows reorder and all-hidden remains recoverable", () =>
        {
            var w = Fixture(out _, out _, out _, out _);
            try
            {
                w.Left = -12000;
                w.Top = -12000;
                w.ShowDiagnostic();
                w.Page(2);
                Click(w, "customize");
                Click(w, "later:playnite");
                Check(w.Controller.Settings.FavoriteQuickActions![1] == "playnite" && w.SelectedAction == "favorite:playnite", "Moved favorite lost row identity");
                RequireFocus(w);
                foreach (var id in SessionHubSettings.Catalog)
                    Click(w, "favorite:" + id);
                Check(w.Controller.Settings.FavoriteQuickActions!.Length == 0 && w.ActionIds.Contains("favorite:playnite"), "Hidden rows disappeared");
                w.Page(0);
                Check(w.ActionIds.Contains("session-card") && w.ActionIds.Contains("apps") && !w.ActionIds.Contains("playnite"), "All-hidden removed recovery or left empty tiles");
                w.Page(2);
                Click(w, "customize");
                Click(w, "restore-favorites");
                Check(w.Controller.Settings.FavoriteQuickActions!.SequenceEqual(SessionHubSettings.Catalog), "Restore default failed");
            }
            finally
            {
                w.Close();
            }
        });
        test("hub pending in-place cancellation preserves sent outcome and blocks duplicate action", () =>
        {
            var w = Fixture(out var p, out var audio, out _, out _);
            var completion = new TaskCompletionSource<ApplicationAudioChange>();
            audio.Next = completion.Task;
            try
            {
                w.OpenOptions(p.A);
                Click(w, "application-mute");
                Release(w);
                Check(audio.Writes == 1 && w.Controller.Operation is { State: "Pending", Dispatched: true }, "Queued versus dispatched confused");
                w.ActivateEntry();
                Check(audio.Writes == 1, "Duplicate mutation dispatched");
                w.Back();
                Check(audio.Token.IsCancellationRequested && w.Controller.Operation?.State == "Unconfirmed", "Post-dispatch Back claimed undo");
                completion.SetResult(new(new(ResultKind.Partial, "One output was not confirmed; check the app."), audio.Snapshot(), 2, 1, 1, true));
                Complete(w.OperationTask);
                Check(audio.Writes == 1 && w.Controller.Operation is { State: "Partial" }, "Late partial outcome lost or retried");
            }
            finally
            {
                w.Close();
            }
        });
        test("hub close modal exposes only hold cancel and restores options actual focus", () =>
        {
            var w = Fixture(out var p, out _, out _, out _);
            try
            {
                w.Left = -12000;
                w.Top = -12000;
                w.ShowDiagnostic();
                w.OpenOptions(p.A);
                Click(w, "close");
                Check(w.ActionIds.SequenceEqual(new[] { "confirm-hold", "confirm-cancel" }), "Modal leaked underlying entries");
                w.Sequential(1);
                Check(w.SelectedAction == "confirm-cancel" && ActualFocus(w).Name == "CancelCloseButton", "Tab did not reach actual cancel");
                w.Back();
                Check(!w.Confirming && w.SelectedAction == "close" && ActualFocus(w).Tag as string == "close", "Cancel lost options control focus");
            }
            finally
            {
                w.Close();
            }
        });
        test("hub UI scale side and Discard preserve context focus and reapply once", () =>
        {
            var w = Fixture(out _, out _, out _, out var feedback);
            try
            {
                w.DpiReader = _ => 96;
                w.Left = -12000;
                w.Top = -12000;
                w.ShowDiagnostic();
                w.Page(2);
                foreach (var percent in new[]
                {
                    115,
                    130,
                    150,
                    100
                }

                )
                {
                    Click(w, "ui-scale");
                    Check(w.Controller.Settings.UiScalePercent == percent && w.SelectedAction == "ui-scale" && ((ScaleTransform)((FrameworkElement)w.Content).LayoutTransform).ScaleX == percent / 100d, "Scale did not apply while preserving entry");
                    RequireFocus(w);
                }

                Click(w, "side");
                Check(w.Controller.Settings.PanelSide == "Right" && w.SelectedAction == "side", "Side changed context");
                RequireFocus(w);
                w.Controller.Change(w.Controller.Settings with
                {
                    SoundFeedbackEnabled = true,
                    HapticFeedbackEnabled = true,
                    FavoriteQuickActions = []
                });
                w.RefreshRows();
                var before = feedback.Configures;
                Click(w, "discard-pending");
                Check(feedback.Configures == before + 1 && !feedback.Sound && !feedback.Haptics && !w.Controller.SettingsDirty && SessionHubSettings.Favorites(w.Controller.Settings).Length == 6, "Discard did not reapply complete snapshot exactly once");
                Check(((TextBlock)w.FindName("PageTitle")).Text == "System" && w.ActionIds.Contains(w.SelectedAction), "Discard reset view or invalidated selection");
                RequireFocus(w);
            }
            finally
            {
                w.Close();
            }
        });
        test("hub explicit clear leaves empty actionable card without history promotion", () =>
        {
            var w = Fixture(out var p, out _, out _, out _);
            try
            {
                w.OpenOptions(p.A);
                Click(w, "choose-session");
                Click(w, "clear-session");
                Check(w.Controller.Session.Anchor is null && w.SelectedAction == "session-card" && p.Switches == 0 && w.Controller.Recent.Items.Count == 1, "Clear switched/promoted or kept anchor");
                Click(w, "session-card");
                Check(((TextBlock)w.FindName("PageTitle")).Text == "Choose window", "Empty card lost chooser action");
            }
            finally
            {
                w.Close();
            }
        });
        test("hub cleanup suppresses delayed in-place result callback and notification", () =>
        {
            var w = Fixture(out var p, out var audio, out _, out _);
            var completion = new TaskCompletionSource<ApplicationAudioChange>();
            audio.Next = completion.Task;
            var notifications = 0;
            w.ResultNotificationRequest = _ => notifications++;
            w.OpenOptions(p.A);
            Click(w, "application-mute");
            Release(w);
            Check(audio.Writes == 1, "Precondition not dispatched");
            w.Close();
            var previous = w.Controller.Operation;
            completion.SetResult(new(new(ResultKind.Partial, "late fixture outcome"), audio.Snapshot(), 2, 1, 1, true));
            Complete(w.OperationTask);
            Check(w.Controller.Operation == previous && notifications == 0 && audio.Writes == 1, "Cleanup allowed late notification/result or retry");
        });
        foreach (var name in new[]
        {
            "home",
            "apps",
            "system",
            "options",
            "details",
            "audio",
            "integrations",
            "customize",
            "setup"
        }

        )
            foreach (var narrow in new[]
            {
                false,
                true
            }

            )
                foreach (var retained in new[]
                {
                    false,
                    true
                }

                )
                    test("hub actual focus/context matrix " + name + (narrow ? " narrow" : " nominal") + (retained ? " dirty-result" : " clean"), () =>
                    {
                        var w = Fixture(out var p, out _, out _, out _);
                        try
                        {
                            w.Left = -12000;
                            w.Top = -12000;
                            w.Width = narrow ? 360 : 572;
                            w.Height = narrow ? 480 : 940;
                            w.ShowDiagnostic();
                            Navigate(w, name, p);
                            if (retained)
                            {
                                w.Controller.Change(w.Controller.Settings with
                                {
                                    ReducedMotion = true
                                });
                                w.Controller.SetOperation("Partial", "Fixture retained result. No additional command was sent.");
                            }

                            w.RefreshRows();
                            Arrange(w, narrow ? 360 : 572, narrow ? 480 : 940);
                            var count = w.ActionIds.Count;
                            for (var n = 0; n < count; n++)
                            {
                                w.Sequential(1);
                                Check(w.ActionIds.Contains(w.SelectedAction), "Tab absent ID");
                                RequireFocus(w);
                                RequireRevealed(w);
                            }

                            if (retained)
                            {
                                Pick(w, "operation-details");
                                w.ActivateEntry();
                                w.Back();
                                Check(w.SelectedAction == "operation-details", "Nested result Back lost context");
                                Pick(w, "save-pending");
                                w.Move(NavDirection.Up);
                                Check(w.ActionIds.Contains(w.SelectedAction), "Dirty Up absent");
                                RequireFocus(w);
                                Click(w, "dismiss-operation");
                                Check(w.Controller.Operation is null && w.ActionIds.Contains(w.SelectedAction), "Direct dismiss changed origin");
                            }
                        }
                        finally
                        {
                            w.Close();
                        }
                    });
    }

    private static void Navigate(MainWindow w, string state, HubPlatform p)
    {
        switch (state)
        {
            case "apps":
                w.Page(1);
                break;
            case "system":
                w.Page(2);
                break;
            case "options":
                w.OpenOptions(p.A);
                break;
            case "details":
                w.OpenOptions(p.A);
                Click(w, "details");
                break;
            case "audio":
                w.SetRenderState("audio");
                break;
            case "integrations":
                w.SetRenderState("integrations");
                break;
            case "customize":
                w.Page(2);
                Click(w, "customize");
                break;
            case "setup":
                w.Page(2);
                Click(w, "setup");
                break;
        }
    }

    internal static void Capture(string output, bool focused = false)
    {
        Directory.CreateDirectory(output);
        var app = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        var cases = new List<(string Theme, int Width, int Height, string State, int Scale, string Side)>();
        foreach (var theme in new[]
        {
            "dark",
            "light"
        }

        )
        {
            foreach (var size in new[]
            {
                (572, 940),
                (420, 600),
                (360, 480)
            }

            )
                foreach (var state in new[]
                {
                    "home",
                    "options",
                    "system",
                    "customize",
                    "setup",
                    "setup2",
                    "setup3",
                    "apps",
                    "details",
                    "audio",
                    "integrations",
                    "recent",
                    "empty",
                    "stale",
                    "mixed",
                    "partial",
                    "all-hidden",
                    "dirty",
                    "confirmation",
                    "progress",
                    "pending",
                    "result"
                }

                )
                    cases.Add((theme, size.Item1, size.Item2, state, 100, "Left"));
            foreach (var state in new[]
            {
                "card-options",
                "muted",
                "unknown",
                "no-audio",
                "customize-reordered",
                "customize-all-hidden",
                "dirty-result",
                "close-cancel",
                "hover-template-simulation",
                "pressed-template-simulation",
                "focus-template-simulation",
                "controller-known"
            }

            )
                cases.Add((theme, 572, 940, state, 100, "Left"));
            cases.Add((theme, 360, 480, "dirty-result", 100, "Left"));
            foreach (var scale in new[]
            {
                130,
                150
            }

            )
                foreach (var side in new[]
                {
                    "Left",
                    "Right"
                }

                )
                    cases.Add((theme, 572, 940, "tv", scale, side));
        }

        foreach (var state in new[]
        {
            "home",
            "options",
            "system",
            "customize",
            "setup",
            "confirmation",
            "result",
            "dirty-result"
        }

        )
            cases.Add(("contrast-palette", 420, 600, state, 100, "Left"));
        if (focused)
            cases = cases.Where(c => c.Theme != "contrast-palette" && c.Scale == 100 && c.Width != 420 && c.State is "home" or "options" or "customize" or "dirty-result").ToList();
        var metadata = new List<SessionCaptureInfo>();
        foreach (var item in cases)
        {
            var (theme, width, height, state, scale, side) = item;
            ThemeSupport.ForceForValidation(app, theme == "light");
            ResourceDictionary? palette = null;
            if (theme == "contrast-palette")
            {
                palette = new();
                foreach (var key in new[]
                {
                    ThemeSupport.Surface,
                    ThemeSupport.Card,
                    ThemeSupport.Control,
                    ThemeSupport.SuccessBackground,
                    ThemeSupport.CriticalBackground
                }

                )
                    palette[key] = Brushes.Black;
                foreach (var key in new[]
                {
                    ThemeSupport.Text,
                    ThemeSupport.SecondaryText,
                    ThemeSupport.DisabledText
                }

                )
                    palette[key] = Brushes.White;
                palette[ThemeSupport.Stroke] = Brushes.White;
                palette[ThemeSupport.Success] = Brushes.Yellow;
                palette[ThemeSupport.Focus] = Brushes.Yellow;
                palette[ThemeSupport.Accent] = Brushes.White;
                palette[ThemeSupport.Critical] = Brushes.Cyan;
                app.Resources.MergedDictionaries.Add(palette);
            }

            var w = Fixture(out var p, out var audio, out _, out _);
            TaskCompletionSource<ApplicationAudioChange>? pendingCompletion = null;
            try
            {
                if (state == "empty")
                {
                    p.Empty = true;
                    w.Controller.ClearSelection();
                    w.Controller.CaptureReturn();
                    Complete(w.Controller.Refresh());
                }
                else if (state == "recent")
                {
                    w.Controller.Recent.Observe(p.B, p.Valid);
                    w.Controller.Recent.Open(p.A, p.Valid);
                }
                else if (state == "stale")
                {
                    w.OpenOptions(p.A);
                    p.Stale = true;
                }
                else if (state == "all-hidden")
                    w.Controller.Change(w.Controller.Settings with
                    {
                        FavoriteQuickActions = []
                    });
                else if (state is "mixed" or "muted")
                {
                    audio.Mute = state == "mixed" ? ApplicationMuteState.Mixed : ApplicationMuteState.Muted;
                    audio.Emit();
                }
                else if (state is "partial" or "unknown" or "no-audio")
                {
                    audio.Availability = state == "partial" ? ApplicationAudioAvailability.Partial : state == "unknown" ? ApplicationAudioAvailability.Unknown : ApplicationAudioAvailability.Unavailable;
                    if (state == "no-audio")
                        audio.Identified = 0;
                    audio.Emit();
                    w.OpenOptions(p.A);
                }
                else if (state is "setup2" or "setup3")
                {
                    Navigate(w, "setup", p);
                    Click(w, "setup-next");
                    if (state == "setup3")
                        Click(w, "setup-next");
                }
                else if (state == "dirty")
                    w.Controller.Change(w.Controller.Settings with
                    {
                        PanelSide = "Right",
                        UiScalePercent = 130
                    });
                else if (state is "customize-reordered" or "customize-all-hidden")
                {
                    Navigate(w, "customize", p);
                    if (state == "customize-reordered")
                        Click(w, "later:playnite");
                    else
                        foreach (var id in SessionHubSettings.Catalog)
                            Click(w, "favorite:" + id);
                }
                else if (state is "confirmation" or "progress" or "close-cancel")
                {
                    w.OpenOptions(p.A);
                    Click(w, "close");
                    if (state == "progress")
                    {
                        w.AdvancePointerHold(false, true, true, 0);
                        w.AdvancePointerHold(true, true, true, 1);
                        w.AdvancePointerHold(true, true, true, 751);
                    }

                    if (state == "close-cancel")
                        w.Back();
                }
                else if (state == "pending")
                {
                    w.OpenOptions(p.A);
                    pendingCompletion = new();
                    audio.Next = pendingCompletion.Task;
                    Click(w, "application-mute");
                    Release(w);
                }
                else if (state is "result" or "dirty-result")
                {
                    w.Controller.SetOperation("Partial", "The window identity changed during the request. Open Apps, select the window again, and check for a save dialog before trying again. No additional command was sent.", "fixture", p.A, true);
                    if (state == "result")
                    {
                        w.RefreshRows();
                        Click(w, "operation-details");
                    }
                    else
                        w.Controller.Change(w.Controller.Settings with
                        {
                            ReducedMotion = true
                        });
                }
                else if (state == "controller-known")
                    w.UpdateControllerTelemetry(new PadFrame[] { new(true, 0), default, default, default }, 0, 0);
                else
                    Navigate(w, state, p);
                if (state == "tv")
                    w.Controller.Change(w.Controller.Settings with
                    {
                        UiScalePercent = scale,
                        PanelSide = side
                    });
                w.RefreshRows();
                if (state == "card-options")
                    Pick(w, "session-options");
                var root = (FrameworkElement)w.Content;
                w.Content = null;
                root.Resources.MergedDictionaries.Add(w.Resources);
                root.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, ThemeSupport.Text);
                root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, w.FontFamily);
                root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, w.FontSize);
                var effective = w.Controller.Settings.UiScalePercent / 100d;
                root.LayoutTransform = new ScaleTransform(effective, effective);
                var host = new Border
                {
                    Child = root
                };
                host.Measure(new Size(width, height));
                host.Arrange(new Rect(0, 0, width, height));
                host.UpdateLayout();
                w.RevealSelection();
                host.UpdateLayout();
                if (state == "system")
                {
                    ((ScrollViewer)w.FindName("ContentScroll")).ScrollToBottom();
                    host.UpdateLayout();
                }

                if (state.EndsWith("template-simulation"))
                {
                    var button = (Button)w.FindName("SessionCardButton");
                    button.ApplyTemplate();
                    if (state == "focus-template-simulation")
                        ((Border)button.Template.FindName("FocusRing", button)).Visibility = Visibility.Visible;
                    else
                        ((Border)button.Template.FindName("Interaction", button)).Opacity = state == "hover-template-simulation" ? .06 : .13;
                }

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var name = $"{theme}-{width}x{height}-{state}" + (state == "tv" ? $"-{scale}-{side.ToLowerInvariant()}" : "") + ".png";
                using (var stream = File.Create(Path.Combine(output, name)))
                    encoder.Save(stream);
                var scroll = (ScrollViewer)w.FindName("ContentScroll");
                metadata.Add(new(name, theme, width, height, state, w.Controller.Settings, w.SelectedAction, ((TextBlock)w.FindName("PageTitle")).Text, scroll.VerticalOffset, scroll.ViewportHeight, scroll.ExtentHeight, true, true, state.EndsWith("template-simulation"), SidebarLayout.Place(p.Displays()[0], 96, w.Controller.Settings.UiScalePercent, w.Controller.Settings.PanelSide)));
                Console.WriteLine("CAPTURE " + name + "; offscreen=True; fixture=True; viewport=" + scroll.ViewportHeight + "; offset=" + scroll.VerticalOffset);
            }
            finally
            {
                w.Close();
                if (pendingCompletion is not null)
                {
                    pendingCompletion.TrySetCanceled();
                    Complete(w.OperationTask);
                }

                if (palette is not null)
                    app.Resources.MergedDictionaries.Remove(palette);
                ThemeSupport.FollowSystem(app);
            }
        }

        File.WriteAllText(Path.Combine(output, "capture-report.json"), JsonSerializer.Serialize(metadata, SessionCaptureJson.Default.ListSessionCaptureInfo));
    }

    private static void SelectionAudioRegressions(Action<string, Action> test)
    {
        foreach (var width in new[]
        {
            360,
            572
        }

        )
            foreach (var theme in new[]
            {
                "dark",
                "light",
                "contrast-palette"
            }

            )
                test($"hub row typography and geometry stable across selection {width} {theme}", () =>
                {
                    var app = Application.Current ?? new Application
                    {
                        ShutdownMode = ShutdownMode.OnExplicitShutdown
                    };
                    ThemeSupport.ForceForValidation(app, theme == "light");
                    ResourceDictionary? palette = null;
                    if (theme == "contrast-palette")
                    {
                        palette = new();
                        foreach (var key in new[]
                        {
                            ThemeSupport.Surface,
                            ThemeSupport.Card,
                            ThemeSupport.Control,
                            ThemeSupport.SuccessBackground,
                            ThemeSupport.FocusBackground
                        }

                        )
                            palette[key] = Brushes.Black;
                        foreach (var key in new[]
                        {
                            ThemeSupport.Text,
                            ThemeSupport.SecondaryText,
                            ThemeSupport.Stroke
                        }

                        )
                            palette[key] = Brushes.White;
                        palette[ThemeSupport.Success] = Brushes.Yellow;
                        palette[ThemeSupport.Focus] = Brushes.Yellow;
                        palette[ThemeSupport.Accent] = Brushes.White;
                        app.Resources.MergedDictionaries.Add(palette);
                    }

                    var w = Fixture(out var p, out _, out _, out _);
                    try
                    {
                        w.Left = -12000;
                        w.Top = -12000;
                        w.Width = width;
                        w.Height = width == 360 ? 480 : 940;
                        w.ShowDiagnostic();
                        w.OpenOptions(p.A);
                        var root = (FrameworkElement)w.Content;
                        var button = ReviewFixTests.Button(w, "application-mute");
                        var label = ((StackPanel)((Grid)button.Content).Children[1]).Children.OfType<TextBlock>().Last();
                        // Keep a real multiline description close to wrapping boundaries.
                        label.Text = "This app's identified audio sessions can be muted and restored in this window.";
                        void Layout()
                        {
                            Arrange(w, width, width == 360 ? 480 : 940);
                            root.UpdateLayout();
                        }

                        string Geometry() => string.Join("|", PremiumInterfaceTests.Descendants<TextBlock>(button).Select(t => $"{t.Text}:{t.FontFamily.Source}:{t.FontSize}:{t.FontWeight}:{t.FontStyle}:{t.TextWrapping}:{t.TranslatePoint(new Point(), button)}:{t.ActualWidth}:{t.ActualHeight}"));
                        void Capture(string state)
                        {
                            var output = Environment.GetEnvironmentVariable("BEZORO_POLISH_CAPTURES");
                            if (output is null)
                                return;
                            Directory.CreateDirectory(output);
                            var image = new System.Windows.Media.Imaging.RenderTargetBitmap(width, width == 360 ? 480 : 940, 96, 96, PixelFormats.Pbgra32);
                            image.Render(root);
                            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                            using var file = File.Create(Path.Combine(output, $"{theme}-{width}-{state}.png"));
                            png.Save(file);
                        }

                        Pick(w, "minimize");
                        Layout();
                        var before = Geometry();
                        Capture("unselected");
                        typeof(MainWindow).GetField("inputMethod", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(w, "controller");
                        Pick(w, "application-mute");
                        Layout();
                        var after = Geometry();
                        Capture("selected");
                        Console.WriteLine($"SELECTION {width} {theme} before={before}; after={after}");
                        Check(before == after, "Selection changed rendered text geometry or typography");
                        Check(((Border)button.Resources["ABadge"]).Visibility == Visibility.Visible, "Controller feedback disappeared");
                        Check(Equals(button.BorderBrush, app.FindResource(ThemeSupport.Focus)), "Selection outline missing");
                        Pick(w, "minimize");
                        button.Focus();
                        Layout();
                        Check(button.IsKeyboardFocused && before == Geometry(), "Keyboard focus changed text");
                        // Same existing template overlays used for hover/pressed capture; no global pointer input.
                        var interaction = (Border)button.Template.FindName("Interaction", button);
                        interaction.Opacity = .06;
                        Layout();
                        Check(before == Geometry(), "Hover overlay changed text");
                        interaction.Opacity = .13;
                        Layout();
                        Check(before == Geometry(), "Pressed overlay changed text");
                    }
                    finally
                    {
                        w.Close();
                        if (palette is not null)
                            app.Resources.MergedDictionaries.Remove(palette);
                        ThemeSupport.FollowSystem(app);
                    }
                });
        foreach (var partial in new[]
        {
            false,
            true
        }

        )
            test("hub confirmed app mute restore cycle " + (partial ? "partial" : "complete"), () =>
            {
                var w = Fixture(out var p, out var audio, out _, out _);
                try
                {
                    w.Left = -12000;
                    w.Top = -12000;
                    w.ShowDiagnostic();
                    w.OpenOptions(p.A);
                    audio.Availability = partial ? ApplicationAudioAvailability.Partial : ApplicationAudioAvailability.Available;
                    audio.Emit();
                    var button = ReviewFixTests.Button(w, "application-mute");
                    string Title() => System.Windows.Automation.AutomationProperties.GetName(button);
                    Check(Title().Contains("Mute"), "Initial action must clearly offer muting");
                    var pending = new TaskCompletionSource<ApplicationAudioChange>();
                    audio.Next = pending.Task;
                    Click(w, "application-mute");
                    Release(w);
                    Check(audio.Writes == 1 && audio.LastDesired && Title().Contains("Mute"), "Mute was optimistic or not explicit");
                    pending.SetResult(new(new(partial ? ResultKind.Partial : ResultKind.Completed, "Fixture observed mute"), audio.Snapshot(), 2, 2, 2, true));
                    Complete(w.OperationTask);
                    audio.Next = null;
                    var mutedTitle = Title();
                    Check(ReferenceEquals(button, ReviewFixTests.Button(w, "application-mute")) && button.IsEnabled, "Confirmed mute rebuilt or disabled action");
                    Click(w, "application-mute");
                    Release(w);
                    Complete(w.OperationTask);
                    Check(audio.Writes == 2 && !audio.LastDesired && audio.LastTarget?.Same(p.A) == true && audio.LastGeneration == audio.Generation, "Restore did not target same validated session with desired=false");
                    Check(mutedTitle.Contains("Unmute app audio") && Title().Contains("Mute app audio"), "Toggle labels do not expose mute and restore");
                    audio.Mute = ApplicationMuteState.Muted;
                    audio.Emit();
                    Check(Title().Contains("Unmute app audio"), "External mute not reflected");
                    audio.Next = Task.FromResult(new ApplicationAudioChange(new(ResultKind.Unknown, "Fixture read failure"), audio.Snapshot() with
                    {
                        MuteState = ApplicationMuteState.Unknown,
                        Availability = ApplicationAudioAvailability.Partial
                    }, 1, 1, 0, true));
                    Click(w, "application-mute");
                    Release(w);
                    Complete(w.OperationTask);
                    Check(!Title().Contains("Unmute app audio") && w.Controller.Operation?.State == "Unconfirmed", "Unknown result claimed known mute state");
                    audio.Next = null;
                    foreach (var state in new[]
                    {
                        ApplicationMuteState.Mixed,
                        ApplicationMuteState.Unknown
                    }

                    )
                    {
                        audio.Mute = state;
                        audio.Emit();
                        Check(!Title().Contains("Unmute app audio"), "Mixed/unknown state guessed restoration");
                        Click(w, "application-mute");
                        Release(w);
                        Complete(w.OperationTask);
                        Check(audio.LastDesired, "Mixed/unknown action must explicitly mute");
                    }

                    audio.Identified = 0;
                    audio.Availability = ApplicationAudioAvailability.Unavailable;
                    audio.Emit();
                    Check(!button.IsEnabled, "No-session action enabled");
                    var writes = audio.Writes;
                    Click(w, "application-mute");
                    Release(w);
                    Check(audio.Writes == writes, "No-session action wrote audio");
                }
                finally
                {
                    w.Close();
                }
            });
    }

    private sealed class HubPlatform : IPlatform
    {
        internal readonly WindowRef A = new(101, 42, 100, "Project document · demonstration window with a complete title", "fixture");
        internal readonly WindowRef B = new(102, 52, 200, "Session notes", "fixture-notes");
        internal bool Stale, Empty;
        internal int Switches, Minimizes;
        internal WindowRef? LastSwitch, ForegroundWindow;
        public bool IsPreview => true;

        public WindowRef? Foreground() => Empty ? null : ForegroundWindow ?? A;
        public IReadOnlyList<WindowRef> Windows() => Empty ? [] : [A, B];
        public bool Valid(WindowRef target) => !Empty && !(Stale && target.Same(A)) && (target.Same(A) || target.Same(B));
        public IReadOnlyList<DisplayRef> Displays() => [new("fixture", -12000, -12000, 1920, 1080, true)];
        public ModuleEvidence SteamEvidence(WindowRef? window) => ModuleEvidence.Unknown;
        public Task<ActionResult> Switch(WindowRef target)
        {
            Switches++;
            LastSwitch = target;
            ForegroundWindow = target;
            return Task.FromResult(new ActionResult(ResultKind.Completed, "Fixture focus observed"));
        }

        public Task<ActionResult> Minimize(WindowRef target, CancellationToken cancellation)
        {
            Minimizes++;
            return Task.FromResult(new ActionResult(ResultKind.Completed, "Fixture minimized observed"));
        }

        public Task<ActionResult> Close(WindowRef target, CancellationToken cancellation) => Task.FromResult(new ActionResult(ResultKind.Simulated, "Fixture close"));
        public Task<ActionResult> Shortcut(WindowRef target, string keys) => Task.FromResult(new ActionResult(ResultKind.Simulated, "Fixture shortcut"));
        public ActionResult Launch(bool playnite, string? path) => new(ResultKind.Simulated, "Fixture launch");
        public void Dispose()
        {
        }
    }

    private sealed class HubAudio : IApplicationAudio
    {
        public event Action<ApplicationAudioSnapshot>? Changed;
        internal WindowRef? Target, LastTarget;
        internal long Generation, Revision, LastGeneration;
        internal ApplicationMuteState Mute = ApplicationMuteState.Audible;
        internal ApplicationAudioAvailability Availability = ApplicationAudioAvailability.Available;
        internal int Writes, Suspends, Identified = 2;
        internal bool LastDesired;
        internal Task<ApplicationAudioChange>? Next;
        internal CancellationToken Token;
        internal ApplicationAudioSnapshot Snapshot() => new(Target, Generation, ++Revision, Availability, Mute, Identified, Availability == ApplicationAudioAvailability.Partial ? 1 : 0, "Fixture · sessions of the identified process");
        internal void Emit() => Changed?.Invoke(Snapshot());
        internal void Raise(ApplicationAudioSnapshot value) => Changed?.Invoke(value);
        public void Track(WindowRef? target, bool visible, long generation)
        {
            Target = target;
            Generation = generation;
            Revision = 0;
            if (visible)
                Emit();
        }

        public Task<ApplicationAudioSnapshot> RefreshAsync(WindowRef target, long generation, CancellationToken cancellation = default) => Task.FromResult(Snapshot());
        public Task<ApplicationAudioChange> SetMutedAsync(WindowRef target, bool desiredMute, long generation, CancellationToken cancellation = default)
        {
            Writes++;
            Token = cancellation;
            LastDesired = desiredMute;
            LastTarget = target;
            LastGeneration = generation;
            Mute = desiredMute ? ApplicationMuteState.Muted : ApplicationMuteState.Audible;
            return Next ?? Task.FromResult(new ApplicationAudioChange(new(ResultKind.Completed, "Fixture explicit desired readback"), Snapshot(), 2, 2, 2, true));
        }

        public void Suspend()
        {
            Suspends++;
        }

        public void Dispose()
        {
        }
    }

    private sealed class HubDevices : IControllerDevices
    {
        internal int Reads;
        private readonly bool[] connected = new bool[4];
        private readonly long[] epochs = new long[4];
        public void UpdateConnections(IReadOnlyList<bool> slots)
        {
            for (var i = 0; i < 4; i++)
            {
                var next = i < slots.Count && slots[i];
                if (next != connected[i])
                {
                    epochs[i]++;
                    connected[i] = next;
                }
            }
        }

        public ControllerDeviceSnapshot Read(uint slot)
        {
            Reads++;
            return new(slot, epochs[slot], connected[slot], ControllerBatteryType.Alkaline, ControllerBatteryLevel.Medium, ControllerVibrationCapability.Available, 0, "fixture");
        }

        public ControllerVibrationResult SetVibration(uint slot, long connectionEpoch, ushort leftMotor, ushort rightMotor) => throw new Exception("UI must not write motors directly");
        public void Dispose()
        {
        }
    }

    private sealed class HubFeedback : IFeedbackService
    {
        internal int Requests, Suspends, Configures;
        internal bool Sound, Haptics;
        public event Action<FeedbackStatus>? Changed
        {
            add
            {
            }
            remove
            {
            }
        }

        public FeedbackStatus Status => new(true, true, "Fixture feedback");

        public void Configure(bool sound, bool haptics)
        {
            Configures++;
            Sound = sound;
            Haptics = haptics;
        }

        public void SetContext(FeedbackContext context)
        {
        }

        public FeedbackDispatch Request(FeedbackCue cue, FeedbackSource source)
        {
            Requests++;
            return new(false, false, "fixture");
        }

        public void Suspend()
        {
            Suspends++;
        }

        public void Dispose()
        {
        }
    }
}

internal sealed record SessionCaptureInfo(
    string Image,
    string Theme,
    int Width,
    int Height,
    string State,
    UserSettings Settings,
    string Selected,
    string Route,
    double Offset,
    double Viewport,
    double Extent,
    bool Offscreen,
    bool Fixture,
    bool TemplateSimulation,
    SidebarBounds PhysicalPlacement
);
[JsonSerializable(typeof(List<SessionCaptureInfo>))]
internal partial class SessionCaptureJson : JsonSerializerContext;

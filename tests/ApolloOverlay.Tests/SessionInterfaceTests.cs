using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApolloOverlay;

internal static class SessionInterfaceTests
{
    internal static void Run(Action<string, Action> test)
    {
        foreach (var height in new[] { 480, 600 })
            test("compact keyboard keeps target buffer and explicit insertion visible " + height, () =>
            {
                using var host = new Host();
                var w = host.Window;
                host.Open(360, height);
                Navigate(w, "keyboard");
                w.UpdateLayout();
                var viewport = (ScrollViewer)w.FindName("ContentScroll");
                Require(viewport.ScrollableHeight <= 1, "Keyboard must scroll its keys without scrolling or fading its pinned commands");
                foreach (var id in new[] { "keyboard-target", "keyboard-editor", "keyboard-insert", "back" })
                {
                    var button = ReviewFixTests.Button(w, id);
                    var bounds = button.TransformToAncestor(viewport).TransformBounds(new Rect(new Size(button.ActualWidth, button.ActualHeight)));
                    Require(bounds.Top >= -1 && bounds.Bottom <= viewport.ViewportHeight + 1, "Pinned keyboard control is outside the viewport: " + id + " " + bounds);
                }
                Require(((PremiumInterfaceTests.FixturePlatform)w.Controller.Platform).Calls == 0, "Rendering the keyboard sent an external command");
            });
        foreach (var size in new[]
        {
            (572, 940),
            (360, 480)
        }

        )
            foreach (var route in new[]
            {
                "home",
                "system",
                "apps",
                "mixer",
                "devices",
                "profiles",
                "edit",
                "media",
                "keyboard",
                "gallery"
            }

            )
            {
                var dimensions = size;
                var page = route;
                test($"session interface real focus and visible navigation {page} {dimensions.Item1}", () =>
                {
                    using var host = new Host();
                    var w = host.Window;
                    host.Open(dimensions.Item1, dimensions.Item2);
                    Navigate(w, page);
                    if (page == "system")
                    {
                        w.UpdateLayout();
                        var scroll = (ScrollViewer)w.FindName("ContentScroll");
                        var selected = ReviewFixTests.Button(w, w.SelectedAction);
                        if (scroll.IsAncestorOf(selected) && scroll.ScrollableHeight - scroll.VerticalOffset > 1)
                        {
                            var bounds = selected.TransformToAncestor(scroll).TransformBounds(new Rect(new Size(selected.ActualWidth, selected.ActualHeight)));
                            Require(bounds.Bottom <= scroll.ViewportHeight - 24, "Focused system action is covered by the continuation fade");
                        }
                    }
                    for (var index = 0; index < w.ActionIds.Count; index++)
                    {
                        w.Sequential(1);
                        w.UpdateLayout();
                        RequireFocus(w);
                        if (Keyboard.FocusedElement is FrameworkElement focused && ((ScrollViewer)w.FindName("ContentScroll")).IsAncestorOf(focused))
                        {
                            var scroll = (ScrollViewer)w.FindName("ContentScroll");
                            var bounds = focused.TransformToAncestor(scroll).TransformBounds(new Rect(new Size(focused.ActualWidth, focused.ActualHeight)));
                            Require(bounds.Top >= -3 && bounds.Bottom <= scroll.ActualHeight + 3, "Focused action was clipped: " + w.SelectedAction + " bounds=" + bounds + " viewport=" + scroll.ViewportHeight);
                        }
                    }

                    var version = (TextBlock)w.FindName("HeaderIdentity");
                    Pick(w, w.ActionIds[0]);
                    w.UpdateLayout();
                    Require(version.Visibility == Visibility.Visible && version.Text.StartsWith("v"), "Compact page hid build version");
                });
            }

        foreach (var clear in new[]
        {
            false,
            true
        }

        )
        {
            var clearing = clear;
            test("session artwork immediately clears on " + (clearing ? "clearing" : "changing") + " the selected game", () =>
            {
                using var host = new Host(root =>
                {
                    var captures = new ScreenCaptures(root, new CaptureFixture());
                    var saved = captures.CaptureAsync(new("fixture", 0, 0, 120, 80, true), "Fixture game", "fixture");
                    Complete(saved);
                    Require(saved.Result.Item is not null, "Fixture capture was not saved");
                });
                var w = host.Window;
                host.Open();
                Navigate(w, "gallery");
                Complete(w.ArtworkTask);
                var artwork = (Border)w.FindName("SessionArtwork");
                Require(artwork.Background is not null, "Fixture session artwork did not load");
                if (clearing)
                    w.Controller.ClearSelection();
                else
                {
                    var platform = (PremiumInterfaceTests.FixturePlatform)w.Controller.Platform;
                    platform.Window = new(202, 84, 200, "Other game", "other-game");
                    w.Controller.Select(platform.Window);
                }

                w.RefreshRows();
                Require(artwork.Background is null && artwork.Visibility == Visibility.Collapsed, "The previous game's artwork survived a session change");
            });
        }

        test("session directional audio navigation reaches microphone without changing volume", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            Pick(w, "session-volume");
            w.Move(NavDirection.Down);
            Require(w.SelectedAction == "windows-volume", "Down skipped the output volume");
            w.Move(NavDirection.Down);
            if (w.SelectedAction == "session-mixer")
                w.Move(NavDirection.Left);
            Require(w.SelectedAction == "microphone" && host.Audio.VolumeWrites == 0, "Directional navigation could not reach the microphone without editing volume");
            w.Move(NavDirection.Right);
            Require(w.SelectedAction == "session-mixer", "Microphone did not connect to its sibling mixer action");
        });
        test("session volume slider keeps native focus while keyboard edits commit exact app volume", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            Pick(w, "session-volume");
            var button = ReviewFixTests.Button(w, "session-volume");
            TapKey(w, Key.Tab);
            Require(Keyboard.FocusedElement is Slider, "Tab did not enter the game volume slider");
            var slider = (Slider)Keyboard.FocusedElement;
            slider.Value = .5;
            Complete(w.OperationTask);
            Require(host.Audio.VolumeWrites == 1 && Math.Abs(host.Audio.Volume - .5f) < .001 && slider.IsKeyboardFocusWithin, "Volume did not commit exactly once or lost child focus");
            TapKey(slider, Key.Tab);
            Require(w.SelectedAction != "session-volume" && Keyboard.FocusedElement is not Slider, "Tab trapped the user in the volume slider");
        });
        test("session mixer refresh retains focused slider and applies current mute observation", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            Navigate(w, "mixer");
            Pick(w, w.ActionIds.First(id => id.StartsWith("mix:")));
            w.ActivateEntry();
            Complete(w.FeatureTask);
            Pick(w, "mixer-volume");
            TapKey(w, Key.Tab);
            Require(Keyboard.FocusedElement is Slider, "Mixer slider unavailable");
            var slider = (Slider)Keyboard.FocusedElement;
            host.Mixer.Volume = .35f;
            host.Mixer.Muted = true;
            w.RefreshSessionFeatures();
            Complete(w.FeatureTask);
            Require(ReferenceEquals(slider, Keyboard.FocusedElement) && Math.Abs(slider.Value - .35) < .001, "Background observation rebuilt the focused mixer slider");
            slider.Value = .4;
            Complete(w.OperationTask);
            Require(ReferenceEquals(slider, Keyboard.FocusedElement) && host.Mixer.VolumeWrites == 1, "Volume edit rebuilt its focused mixer slider");
            TapKey(slider, Key.Tab);
            Require(w.SelectedAction == "mixer-mute" && Keyboard.FocusedElement is not Slider, "Tab did not leave the slider after editing its volume");
            Pick(w, "mixer-mute");
            w.ActivateEntry();
            Release(w);
            Complete(w.OperationTask);
            Require(host.Mixer.MuteWrites == 1 && !host.Mixer.Muted, "Mute action used the state captured before the latest observation");
        });
        test("session keyboard edits buffer locally and returns from child editor using Tab", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            Navigate(w, "keyboard");
            Pick(w, "key:q");
            w.ActivateEntry();
            Pick(w, "keyboard-editor");
            w.ActivateEntry();
            Require(Keyboard.FocusedElement is TextBox { IsReadOnly: false }, "Editable local buffer did not receive focus");
            var editor = (TextBox)Keyboard.FocusedElement;
            editor.Text = "Olá 👩‍🚀";
            TapKey(editor, Key.Tab);
            Pick(w, "keyboard-delete");
            w.ActivateEntry();
            var buffer = PremiumInterfaceTests.Descendants<TextBox>((DependencyObject)w.FindName("Rows")).Single();
            Require(buffer.Text == "Olá ", "Virtual backspace split a visible character");
            Pick(w, "keyboard-insert");
            w.ActivateEntry();
            Require(w.Controller.Operation?.State == "Simulated", "Diagnostic keyboard attempted native input");
        });
        test("session profile editor keeps unsaved name through keyboard back navigation", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            Navigate(w, "edit");
            Pick(w, "profile-name");
            w.ActivateEntry();
            Pick(w, "keyboard-editor");
            w.ActivateEntry();
            ((TextBox)Keyboard.FocusedElement).Text = "Game room";
            TapKey(w, Key.Tab);
            Pick(w, "keyboard-insert");
            w.ActivateEntry();
            Require(w.SelectedAction == "profile-name" && AutomationProperties.GetName(ReviewFixTests.Button(w, "profile-name")).Contains("Game room"), "Profile name or editing origin was lost");
            Require(!File.Exists(Path.Combine(host.Root, "session-profiles.json")), "Editing persisted a profile before Save");
        });
        test("session cancelled profile undo preserves layout and its undo record", () =>
        {
            using var backend = new ProfileAudioFixture();
            using var host = new Host(root =>
            {
                var profiles = new SessionProfiles(root);
                profiles.Save(profiles.Items[0] with
                {
                    OutputId = "second",
                    OutputName = "Second output"
                });
            }, backend, false);
            var w = host.Window;
            host.Open();
            Navigate(w, "profiles");
            Pick(w, "profile:tv");
            w.ActivateEntry();
            Release(w);
            Complete(w.OperationTask);
            Require(w.Controller.Settings.UiScalePercent == 115 && !w.Controller.SettingsDirty && w.ActionIds.Contains("profile-undo"), $"Profile was not applied and saved: scale={w.Controller.Settings.UiScalePercent}; dirty={w.Controller.SettingsDirty}; pending={w.TransitionPending}; operation={w.Controller.Operation}; feedback={w.Feedback}; selected={w.SelectedAction}");
            Pick(w, "profile-undo");
            w.ActivateEntry();
            Release(w);
            try
            {
                var entered = Task.Run(() => backend.RestoreEntered.Wait(2000));
                Complete(entered);
                Require(entered.Result, "Undo did not enter the output restoration");
                w.Back();
                Complete(w.OperationTask);
                Require(w.Controller.Settings.UiScalePercent == 115 && !w.Controller.SettingsDirty && w.ActionIds.Contains("profile-undo") && new SessionProfiles(host.Root).ActiveId == "tv", "Cancelled undo persisted layout or discarded its restore record");
            }
            finally
            {
                backend.ReleaseRestore.Set();
            }
        });
        test("session media invokes only the identified source and observes its result", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Open();
            Navigate(w, "media");
            var initialIcon = (VectorIcon)((ActionButtonContent)ReviewFixTests.Button(w, "media-toggle").Content).Icon;
            Require(ReferenceEquals(initialIcon.Data, w.FindResource("Icon.pause")), "Playing media has no pause icon");
            Pick(w, "media-toggle");
            w.ActivateEntry();
            Release(w);
            Complete(w.OperationTask);
            Require(host.Media.Commands == 1 && host.Media.LastId == "music" && !host.Media.Current.Items[0].Playing, "Media command missed its explicit source");
            Require(AutomationProperties.GetName(ReviewFixTests.Button(w, "media-toggle")).Contains("Play"), "Paused state was not reflected visually");

            var pausedIcon = (VectorIcon)((ActionButtonContent)ReviewFixTests.Button(w, "media-toggle").Content).Icon;
            Require(ReferenceEquals(pausedIcon.Data, w.FindResource("Icon.play")), "Paused media kept the pause icon");
        });
    }

    internal static void Capture(string output)
    {
        Directory.CreateDirectory(output);
        var app = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        foreach (var light in new[]
        {
            false,
            true
        }

        )
            foreach (var width in new[]
            {
                572,
                360
            }

            )
                foreach (var route in new[]
                {
                    "home",
                    "system",
                    "system-controller",
                    "system-feedback",
                    "apps",
                    "mixer",
                    "devices",
                    "profiles",
                    "edit",
                    "media",
                    "keyboard",
                    "gallery"
                }

                )
                {
                    ThemeSupport.ForceForValidation(app, light);
                    using var host = new Host(gameTitle: "Elden Ring");
                    var w = host.Window;
                    var height = width == 572 ? 940 : 600;
                    host.Open(width, height, forCapture: true);
                    Navigate(w, route);
                    w.RefreshOperationNotice(Environment.TickCount64 + 200);
                    w.UpdateLayout();
                    var bitmap = RenderCapture(w, width, height, "keyboard");
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, $"{(light ? "light" : "dark")}-{width}-{route}.png"));
                    png.Save(file);
                }
        CaptureAcknowledgements(output, app);
    }

    private static void CaptureAcknowledgements(string output, Application app)
    {
        foreach (var light in new[] { false, true })
            foreach (var width in new[] { 572, 360 })
                foreach (var state in new[] { "controller", "mouse", "success", "warning", "focus", "unknown-controller", "wired-controller", "unavailable-action", "selected-audio", "breathing" })
                {
                    ThemeSupport.ForceForValidation(app, light);
                    using var host = new Host(gameTitle: "Elden Ring", identities: state switch
                    {
                        "unknown-controller" => [],
                        "wired-controller" => [new("virtual-xbox", "Xbox 360", ControllerFamily.Xbox, false, null, false)],
                        _ => null
                    });
                    var window = host.Window;
                    var height = width == 572 ? 940 : 600;
                    host.Open(width, height, forCapture: true);
                    AdaptiveInterfaceTests.UseController(window);
                    if (state == "unavailable-action")
                        Pick(window, "steam-overlay");
                    else if (state is "selected-audio" or "breathing")
                    {
                        Pick(window, "audio");
                        if (state == "breathing")
                            Complete(Task.Delay(TimeSpan.FromMilliseconds(1550)));
                    }
                    else if (state == "mouse")
                    {
                        window.ObservePointerPosition(new(100, 100));
                        window.ObservePointerPosition(new(120, 100));
                    }
                    else if (state == "focus")
                    {
                        // Simulate refused ownership while retaining a real, attached WPF
                        // window for visual review; this does not certify native activation.
                        window.FocusForeground = () => -1;
                        window.FocusRequest = _ => false;
                        window.OpenPanel();
                        Complete(window.OpeningTask);
                    }
                    else if (state is "success" or "warning")
                    {
                        window.Controller.SetOperation(state == "success" ? "Completed" : "Unconfirmed", state == "success" ? "Settings saved." : "The window changed. Select it again before trying another command.", summary: state == "warning" ? "Select window again." : null);
                        window.RefreshRows();
                    }
                    window.RefreshOperationNotice(Environment.TickCount64 + 200);
                    var bitmap = RenderCapture(window, width, height, state == "focus" ? "controller-unfocused" : state == "mouse" ? "mouse" : "controller");
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, $"{(light ? "light" : "dark")}-{width}-notice-{state}.png"));
                    png.Save(file);
                }
    }

    private static RenderTargetBitmap RenderCapture(MainWindow window, int requestedWidth, int requestedHeight, string expectedInput)
    {
        var root = (FrameworkElement)window.Content;
        // Keep the native, loaded tree attached. Detaching it loses inherited
        // implicit templates and can render empty buttons with otherwise valid geometry.
        Require(root.IsLoaded && PresentationSource.FromVisual(root) is not null, "Capture tree is detached from its real WPF presentation source");
        foreach (var button in PremiumInterfaceTests.Descendants<Button>(root))
            button.ApplyTemplate();
        window.UpdateLayout();
        root.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
        // Inspect the settled visual tree, including text created by ContentPresenter.
        // The logical tree does not contain template-generated labels for string content.
        var buttons = CaptureVisuals<Button>(root)
            .Where(button => button.IsVisible && button.ActualWidth > 0 && button.ActualHeight > 0)
            .ToArray();
        Require(buttons.Length >= 4, "Capture has no rendered command buttons");
        var rows = (FrameworkElement)window.FindName("Rows");
        Require(buttons.Any(button => rows.IsAncestorOf(button)), "Capture has chrome but no rendered page actions");
        foreach (var button in buttons)
        {
            Require(button.Template is not null && VisualTreeHelper.GetChildrenCount(button) > 0, "Capture button has no template visuals: " + button.Tag);
            if (button.Content is FrameworkElement content)
                Require(content.IsVisible && content.ActualWidth > 0 && content.ActualHeight > 0 && PresentationSource.FromVisual(content) is not null, "Capture button content is disconnected or empty: " + button.Tag);
            else if (button.Content is string text && text.Length > 0)
                Require(CaptureVisuals<TextBlock>(button).Any(label => label.IsVisible && label.ActualWidth > 0 && label.ActualHeight > 0 && label.Text.Length > 0), "Capture button text is not rendered: " + button.Tag);
        }
        if (((Border)window.FindName("ContextCard")).Visibility == Visibility.Visible)
        {
            var title = (TextBlock)window.FindName("TargetLabel");
            Require(title.IsVisible && title.ActualWidth > 0 && title.ActualHeight > 0 && title.Text.Length > 0, "Capture game title is not rendered");
        }

        var footer = InputPrompt.Read((TextBlock)window.FindName("ModeLabel"));
        var expectedPrompt = expectedInput switch
        {
            "controller" => "LB/RB",
            "controller-unfocused" => "Guide / B",
            "mouse" => "Left click",
            _ => "Enter"
        };
        Require(footer.Contains(expectedPrompt, StringComparison.Ordinal), $"Capture input mismatch: expected {expectedInput}; footer={footer}");
        Require(window.IsVisible && window.IsActive && Native.GetForegroundWindow() == new WindowInteropHelper(window).Handle, "Capture lost its actual native foreground window");

        // If a runner still constrains the native window, capture its actual viewport.
        // Never label black padding as additional rendered screen space.
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        Console.WriteLine($"CAPTURE GEOMETRY requested={requestedWidth}x{requestedHeight}; actual={width}x{height}; root={root.ActualWidth:F1}x{root.ActualHeight:F1}; buttons={buttons.Length}; input={expectedInput}; footer={footer.Replace("\n", " | ", StringComparison.Ordinal)}");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        return bitmap;
    }

    private static IEnumerable<T> CaptureVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var nested in CaptureVisuals<T>(child))
                yield return nested;
        }
    }

    private static void Navigate(MainWindow w, string page)
    {
        switch (page)
        {
            case "system":
                w.Page(2);
                break;
            case "system-controller":
                w.Page(2);
                Pick(w, "guide-limit");
                break;
            case "system-feedback":
                w.Page(2);
                Pick(w, "sound-feedback");
                break;
            case "apps":
                w.Page(1);
                break;
            case "mixer":
                Choose(w, "session-mixer");
                Complete(w.FeatureTask);
                break;
            case "devices":
                w.SetRenderState("audio");
                Choose(w, "audio-devices");
                Complete(w.FeatureTask);
                break;
            case "profiles":
                Choose(w, "session-profiles");
                break;
            case "edit":
                Choose(w, "session-profiles");
                Choose(w, "profile-edit:tv");
                break;
            case "media":
                Choose(w, "session-media");
                break;
            case "keyboard":
                Choose(w, "session-keyboard");
                break;
            case "gallery":
                Choose(w, "session-gallery");
                Complete(w.FeatureTask);
                break;
        }

        w.UpdateLayout();
    }

    private static void Choose(MainWindow w, string id)
    {
        Pick(w, id);
        w.ActivateEntry();
        w.UpdateLayout();
    }

    private static void Pick(MainWindow w, string id)
    {
        for (var i = 0; i <= w.ActionIds.Count && w.SelectedAction != id; i++)
            w.Sequential(1);
        Require(w.SelectedAction == id, "Action unreachable: " + id);
        w.UpdateLayout();
    }

    private static void TapKey(UIElement element, Key key)
    {
        var source = PresentationSource.FromVisual(element) ?? throw new Exception("Window has no source");
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
    }

    private static void Release(MainWindow w)
    {
        var pads = new PadFrame[]
        {
            new(true, 0),
            default,
            default,
            default
        };
        w.AdvanceHandoff(new(pads, 0, GuideValid: true), 0);
        w.AdvanceHandoff(new(pads, 100, GuideValid: true), 100);
    }

    private static void RequireFocus(MainWindow w)
    {
        Require(w.IsActive && Native.GetForegroundWindow() == new WindowInteropHelper(w).Handle, "Overlay does not own real foreground");
        Require(Keyboard.FocusedElement is FrameworkElement focused && (Equals(focused.Tag, w.SelectedAction) || Equals(focused.Tag, "description:" + w.SelectedAction)), "Actual keyboard focus differs from " + w.SelectedAction);
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    private static void Complete(Task task) => ReviewFixTests.Complete(task);
    private sealed class Host : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "SessionInterface", Guid.NewGuid().ToString("N"));
        internal readonly AudioFixture Audio = new(), Mixer = new();
        internal readonly MediaFixture Media = new();
        internal MainWindow Window
        {
            get;
        }

        internal Host(Action<string>? initialize = null, ISystemAudioBackend? backend = null, bool preview = true, string? gameTitle = null, ControllerIdentity[]? identities = null)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = Application.Current ?? new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            if (app.TryFindResource(ThemeSupport.Card) is null)
                ThemeSupport.ForceForValidation(app, false);
            initialize?.Invoke(Root);
            var platform = new PremiumInterfaceTests.FixturePlatform(preview);
            if (gameTitle is not null)
                platform.Window = platform.Window with
                {
                    Title = gameTitle,
                    ProcessName = "eldenring"
                };
            Window = new(renderOnly: true, platform: platform, diagnostics: new(Root), appAudioService: Audio, media: Media, systemAudioBackend: backend ?? new PreviewSystemAudioBackend(), mixerAudioService: Mixer, controllerIdentitySource: new PreviewControllerIdentitySource { Items = identities ?? [new("fixture-xbox", "Xbox One", ControllerFamily.Xbox, true, 76, false)] });
            Window.PhysicalKeyReader = _ => 0;
            Window.DpiReader = _ => 96;
            Window.Controller.CaptureReturn();
            Complete(Window.Controller.Refresh());
            Window.RefreshRows();
        }

        internal void Open(int width = 572, int height = 940, bool forCapture = false)
        {
            Window.Left = -12000;
            Window.Top = -12000;
            if (forCapture)
            {
                // Capture-only native tracking bounds keep tall reference layouts
                // independent of the hosted runner's desktop resolution.
                Window.MinWidth = Window.MaxWidth = width;
                Window.MinHeight = Window.MaxHeight = height;
            }
            Window.Width = width;
            Window.Height = height;
            Window.ShowDiagnostic();
            Window.RefreshRows();
            Window.UpdateControllerTelemetry([new(true, 0), default, default, default], null, 0);
            Complete(Window.ControllerBadgeTask);
            Window.RefreshSessionFeatures();
            Complete(Window.FeatureTask);
            Window.UpdateLayout();
            RequireFocus(Window);
        }

        public void Dispose()
        {
            try
            {
                Window.Close();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }
        }
    }

    private sealed class AudioFixture : IApplicationAudio
    {
        private WindowRef? target;
        private long generation, revision;
        internal float Volume = .65f;
        internal bool Muted;
        internal int VolumeWrites, MuteWrites;
        public event Action<ApplicationAudioSnapshot>? Changed;
        private ApplicationAudioSnapshot Snapshot() => new(target, generation, ++revision, ApplicationAudioAvailability.Available, Muted ? ApplicationMuteState.Muted : ApplicationMuteState.Audible, 1, 0, "Fixture readback")
        {
            VolumeMinimum = Volume,
            VolumeMaximum = Volume,
            VolumeSessions = 1
        };
        public void Track(WindowRef? value, bool visible, long epoch)
        {
            target = value;
            generation = epoch;
            if (visible)
                Changed?.Invoke(Snapshot());
        }

        public Task<ApplicationAudioSnapshot> RefreshAsync(WindowRef value, long epoch, CancellationToken cancellation = default)
        {
            target = value;
            generation = epoch;
            return Task.FromResult(Snapshot());
        }

        public Task<ApplicationAudioChange> SetVolumeAsync(WindowRef value, float volume, long epoch, CancellationToken cancellation = default)
        {
            VolumeWrites++;
            target = value;
            generation = epoch;
            Volume = volume;
            return Task.FromResult(new ApplicationAudioChange(new(ResultKind.Simulated, "Fixture volume"), Snapshot(), 1, 1, 1, true));
        }

        public Task<ApplicationAudioChange> SetMutedAsync(WindowRef value, bool muted, long epoch, CancellationToken cancellation = default)
        {
            MuteWrites++;
            target = value;
            generation = epoch;
            Muted = muted;
            return Task.FromResult(new ApplicationAudioChange(new(ResultKind.Simulated, "Fixture mute"), Snapshot(), 1, 1, 1, true));
        }

        public void Suspend()
        {
        }

        public void Dispose()
        {
            Changed = null;
        }
    }

    private sealed class CaptureFixture : IScreenCaptureBackend
    {
        public CapturePixels Capture(DisplayRef display)
        {
            var pixels = new byte[120 * 80 * 4];
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
            return new(120, 80, pixels);
        }
    }

    private sealed class ProfileAudioFixture : ISystemAudioBackend, IDisposable
    {
        internal readonly ManualResetEventSlim RestoreEntered = new(), ReleaseRestore = new();
        private SystemAudioSnapshot state = new([new("first", "First output"), new("second", "Second output")], "first", "first", "mic", "Microphone", false, "fixture");
        public SystemAudioSnapshot Read() => state;
        public ActionResult SetMicrophoneMuted(string expectedId, bool muted, Func<bool> continuing) => new(ResultKind.Unavailable, "fixture");
        public ActionResult SetOutputs(string expectedConsole, string expectedMedia, string console, string media, Func<bool> continuing)
        {
            if (console == "first")
            {
                RestoreEntered.Set();
                ReleaseRestore.Wait();
            }

            if (!continuing())
                return new(ResultKind.Unknown, "Cancelled native fixture");
            state = state with
            {
                ConsoleOutputId = console,
                MediaOutputId = media
            };
            return new(ResultKind.Simulated, "fixture readback");
        }

        public void Dispose()
        {
            ReleaseRestore.Set();
        }
    }

    private sealed class MediaFixture : ISessionMedia
    {
        public SessionMediaSnapshot Current
        {
            get;
            private set;
        } = new(1, [new("music", "A moment between worlds", "Session soundtrack", "Fixture music", true, true, false, true)], "music", "Windows media controls.");

        public event Action<SessionMediaSnapshot>? Changed;
        internal int Commands;
        internal string LastId = "";
        public void SetVisible(bool visible)
        {
        }

        public Task RefreshAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<ActionResult> CommandAsync(string id, SessionMediaCommand command, CancellationToken cancellation = default)
        {
            Commands++;
            LastId = id;
            Current = Current with
            {
                Revision = Current.Revision + 1,
                Items = [Current.Items[0] with
                {
                    Playing = false
                }

                ]
            };
            Changed?.Invoke(Current);
            return Task.FromResult(new ActionResult(ResultKind.Simulated, "Fixture media"));
        }

        public void Dispose()
        {
            Changed = null;
        }
    }
}

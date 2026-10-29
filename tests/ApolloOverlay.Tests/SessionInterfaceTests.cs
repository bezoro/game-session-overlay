using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    private static readonly string[] Routes =
    [
        "home", "apps", "system", "options", "details", "audio", "integrations", "customize",
        "setup", "operation", "confirmation", "browser", "updates", "update-review", "mixer", "devices",
        "profiles", "edit", "media", "keyboard", "gallery", "capture-detail", "appearance", "color-editor"
    ];
    private static readonly UpdateCandidate NextUpdate = new(42, "0.49.0-preview.42.1+1111111", "ui-fixture-next", new string('1', 40), "testing",
        DateTimeOffset.Parse("2026-10-28T12:00:00Z"), "Interface fixture release. No update is installed.\n• Clearer session navigation\n• Local capture previews\n• Custom appearance", 201, "fixture-only.nupkg", 8_388_608, new string('a', 64));
    internal static void Run(Action<string, Action> test)
    {
        test("activation glyph belongs only to the selected Resume action", () =>
        {
            using var host = new Host();
            var window = host.Window;
            host.Open();
            AdaptiveInterfaceTests.UseController(window);
            Pick(window, "session-card");
            var hint = (TextBlock)window.FindName("SessionHint");
            Require(InputPrompt.Read(hint).StartsWith("A ", StringComparison.Ordinal), "Selected Resume has no controller prompt");
            Pick(window, "audio");
            Require(InputPrompt.Read(hint) == "Resume", "Unselected Resume still advertises the selected action's input");
        });
        test("high contrast slider uses the actual system-mapped solid thumb", () =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ThemeSupport.ForceHighContrastForValidation(app);
            try
            {
                using var host = new Host();
                host.Open();
                var slider = CaptureVisuals<Slider>((DependencyObject)host.Window.FindName("Rows")).First();
                slider.ApplyTemplate();
                var thumb = (System.Windows.Controls.Primitives.Thumb)slider.Template.FindName("VolumeThumb", slider);
                thumb.ApplyTemplate();
                var surface = (Border)thumb.Template.FindName("ThumbSurface", thumb);
                Require(surface.Background is SolidColorBrush brush
                    && brush.Color == ((SolidColorBrush)host.Window.FindResource(ThemeSupport.SliderThumb)).Color,
                    "The slider inherited a theme gradient instead of its system-mapped palette");
                Require(((ScrollViewer)host.Window.FindName("ContentScroll")).OpacityMask is null,
                    "A viewport fade changes the high contrast system colors");
            }
            finally { ThemeSupport.ForceForValidation(app, false); }
        });
        foreach (var compact in new[] { false, true })
            test("scroll gutter and reorder commands fit their actual bounds compact=" + compact, () =>
            {
                using var host = new Host();
                var window = host.Window;
                host.Open(compact ? 360 : 572, compact ? 480 : 940);
                Navigate(window, "customize", host);
                window.UpdateLayout();
                var scroll = (ScrollViewer)window.FindName("ContentScroll");
                scroll.ApplyTemplate();
                var bar = (FrameworkElement)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
                Require(bar.ActualWidth is >= 7.9 and <= 8.1, "A theme minimum expanded the reserved scrollbar lane");
                var buttons = CaptureVisuals<Button>((DependencyObject)window.FindName("Rows"))
                    .Where(button => button.Tag is string id && (id.StartsWith("earlier:", StringComparison.Ordinal) || id.StartsWith("later:", StringComparison.Ordinal))).ToArray();
                Require(buttons.Length == 12, "The customization fixture has no complete reorder controls");
                foreach (var button in buttons)
                {
                    Pick(window, (string)button.Tag);
                    var holder = (Border)button.Resources["DescriptionTarget"];
                    var bounds = button.TransformToAncestor(holder).TransformBounds(new Rect(button.RenderSize));
                    Require(bounds.Left >= holder.BorderThickness.Left - .1 && bounds.Top >= holder.BorderThickness.Top - .1
                        && bounds.Right <= holder.ActualWidth - holder.BorderThickness.Right + .1
                        && bounds.Bottom <= holder.ActualHeight - holder.BorderThickness.Bottom + .1,
                        "A reorder command is clipped by its focus holder: " + button.Tag);
                }
            });
        foreach (var compact in new[] { false, true })
            test("result reader keeps recovery actions pinned with one scroll context compact=" + compact, () =>
            {
                using var host = new Host();
                var window = host.Window;
                host.Open(compact ? 360 : 572, compact ? 480 : 940);
                Navigate(window, "operation", host);
                window.UpdateLayout();
                var viewport = (ScrollViewer)window.FindName("ContentScroll");
                Require(viewport.ScrollableHeight <= 1, "Result page adds a second scrolling context");
                var reader = CaptureVisuals<TextBox>((DependencyObject)window.FindName("Rows")).Single();
                Require(reader.ActualHeight >= 80, "The pinned commands leave no useful message area");
                foreach (var id in new[] { "operation-text", "dismiss-operation", "back" })
                {
                    Pick(window, id);
                    window.UpdateLayout();
                    var button = ReviewFixTests.Button(window, id);
                    var bounds = button.TransformToAncestor(viewport).TransformBounds(new Rect(button.RenderSize));
                    Require(bounds.Top >= -1 && bounds.Bottom <= viewport.ViewportHeight + 1,
                        "A result recovery action is clipped: " + id);
                }
            });
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
            foreach (var route in Routes)
            {
                var dimensions = size;
                var page = route;
                test($"session interface real focus and visible navigation {page} {dimensions.Item1}", () =>
                {
                    using var host = new Host(populateCaptures: page == "capture-detail");
                    var w = host.Window;
                    host.Open(dimensions.Item1, dimensions.Item2);
                    Navigate(w, page, host);
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
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var manifest = new List<object>();
        var scenarios = Routes.Concat(new[]
        {
            "system-controller", "system-feedback", "setup-display", "setup-finish", "gallery-empty", "capture-missing",
            "updates-available", "update-ready", "update-downloading", "update-error", "update-rollback",
            "no-session", "apps-empty", "audio-empty", "long-device", "media-empty", "long-title",
            "system-save", "system-discard", "appearance-save", "appearance-discard", "color-keep", "color-cancel",
            "update-download-action", "update-install-action", "color-hex-keyboard", "color-unsafe", "gallery-loading"
        }).ToArray();
        foreach (var light in new[] { false, true })
            foreach (var width in new[] { 572, 360 })
                foreach (var route in scenarios)
                {
                    Console.WriteLine($"CAPTURE START theme={(light ? "light" : "dark")}; requested={width}x{(width == 572 ? 940 : 480)}; route={route}");
                    ThemeSupport.ForceForValidation(app, light);
                    using var host = CaptureHost(route);
                    var window = host.Window;
                    var height = width == 572 ? 940 : 480;
                    host.Open(width, height, forCapture: true);
                    Navigate(window, route, host);
                    if (route == "gallery")
                    {
                        var previews = CaptureVisuals<CapturePreview>((DependencyObject)window.FindName("Rows")).ToArray();
                        Require(previews.Length == 6 && previews.All(preview => preview.Source is { IsFrozen: true }),
                            "The populated gallery capture has not published every actual thumbnail");
                    }
                    window.RefreshOperationNotice(Environment.TickCount64 + 200);
                    window.UpdateLayout();
                    var bitmap = RenderCapture(window, width, height, "keyboard");
                    var name = $"{(light ? "light" : "dark")}-{width}-{route}.png";
                    WritePng(bitmap, Path.Combine(output, name));
                    manifest.Add(new
                    {
                        file = name,
                        route,
                        theme = light ? "light" : "dark",
                        requestedWidth = width,
                        requestedHeight = height,
                        actualWidth = window.ActualWidth,
                        actualHeight = window.ActualHeight,
                        selected = window.SelectedAction,
                        fixture = true,
                        attachedNativeWpfTree = true,
                        actualForegroundConfirmed = true
                    });
                }
        manifest.AddRange(CaptureAcknowledgements(output, app));
        manifest.AddRange(CaptureHighContrast(output, app));
        File.WriteAllText(Path.Combine(output, "capture-manifest.json"), JsonSerializer.Serialize(new
        {
            status = "completed",
            scope = "Actual attached WPF rendering using deterministic service fixtures. These images do not certify games, physical input, TVs or streaming.",
            environment = EnvironmentFacts(),
            routes = Routes,
            captures = manifest
        }, new JsonSerializerOptions { WriteIndented = true }));
        CapturePerformance(output, app);
    }

    private static IEnumerable<object> CaptureHighContrast(string output, Application app)
    {
        var results = new List<object>();
        foreach (var width in new[] { 572, 360 })
            foreach (var route in new[] { "home", "devices", "edit", "confirmation", "keyboard", "appearance", "color-editor", "operation", "media-empty" })
            {
                Console.WriteLine($"CAPTURE START theme=high-contrast-palette; requested={width}x{(width == 572 ? 940 : 480)}; route={route}");
                ThemeSupport.ForceHighContrastForValidation(app);
                using var host = CaptureHost(route);
                var height = width == 572 ? 940 : 480;
                host.Open(width, height, forCapture: true);
                Navigate(host.Window, route, host);
                host.Window.RefreshOperationNotice(Environment.TickCount64 + 200);
                var name = $"contrast-{width}-{route}.png";
                WritePng(RenderCapture(host.Window, width, height, "keyboard"), Path.Combine(output, name));
                results.Add(new
                {
                    file = name,
                    route,
                    theme = "high-contrast-palette",
                    requestedWidth = width,
                    requestedHeight = height,
                    actualWidth = host.Window.ActualWidth,
                    actualHeight = host.Window.ActualHeight,
                    selected = host.Window.SelectedAction,
                    fixture = true,
                    attachedNativeWpfTree = true,
                    actualForegroundConfirmed = true,
                    limitation = "Windows SystemColors resource simulation; the operating system high-contrast preference was not changed."
                });
            }
        ThemeSupport.ForceForValidation(app, false);
        return results;
    }

    private static Host CaptureHost(string route)
    {
        ISystemAudioBackend? backend = route switch
        {
            "audio-empty" => new SnapshotAudioFixture(SystemAudioSnapshot.Empty),
            "long-device" => new SnapshotAudioFixture(new([new("long-output", "Living room television — NVIDIA High Definition Audio with a deliberately long output name")],
                "long-output", "long-output", "long-mic", "USB microphone — a deliberately long manufacturer and product name", false, "Fixture devices")),
            _ => null
        };
        return new Host(backend: backend,
            gameTitle: route == "long-title" ? "A deliberately long game title — Complete Definitive Edition with additional session context" : "Elden Ring",
            populateCaptures: route is "gallery" or "gallery-loading" or "capture-detail" or "capture-missing",
            emptyApps: route == "apps-empty", emptyMedia: route == "media-empty");
    }

    private static void WritePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static object EnvironmentFacts() => new
    {
        framework = RuntimeInformation.FrameworkDescription,
        operatingSystem = RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        processors = Environment.ProcessorCount,
        renderTier = RenderCapability.Tier >> 16,
        primaryScreenDip = new
        {
            width = SystemParameters.PrimaryScreenWidth,
            height = SystemParameters.PrimaryScreenHeight
        },
        clientAreaAnimation = SystemParameters.ClientAreaAnimation,
        actualOsHighContrast = SystemParameters.HighContrast,
        graphicsDevice = "Not queried; WPF render tier is not a GPU capability or frame-rate guarantee."
    };

    private static void CapturePerformance(string output, Application app)
    {
        ThemeSupport.ForceForValidation(app, false);
        var samples = new List<object>();
        using var process = Process.GetCurrentProcess();
        using var host = new Host(populateCaptures: true, gameTitle: "Performance fixture");
        var watch = Stopwatch.StartNew();
        host.Open(572, 940, forCapture: true);
        host.Window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
        var opening = watch.Elapsed.TotalMilliseconds;
        var initialCpu = process.TotalProcessorTime;
        process.Refresh();
        var initialBytes = process.PrivateMemorySize64;
        var total = Stopwatch.StartNew();
        // Measure route construction through the loaded WPF tree. This deliberately
        // excludes external services, input transport and a physical display scanout.
        for (var cycle = 0; cycle < 8; cycle++)
        {
            foreach (var route in new[] { "home", "apps", "system", "profiles", "media", "gallery" })
            {
                host.Window.Page(0);
                watch.Restart();
                Navigate(host.Window, route, host);
                host.Window.UpdateLayout();
                host.Window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
                var elapsed = watch.Elapsed.TotalMilliseconds;
                var scroll = (ScrollViewer)host.Window.FindName("ContentScroll");
                scroll.ScrollToEnd();
                host.Window.UpdateLayout();
                scroll.ScrollToTop();
                host.Window.UpdateLayout();
                process.Refresh();
                samples.Add(new
                {
                    cycle,
                    route,
                    dispatchToRenderMs = elapsed,
                    privateBytes = process.PrivateMemorySize64
                });
            }
        }
        process.Refresh();
        File.WriteAllText(Path.Combine(output, "presentation-cost.json"), JsonSerializer.Serialize(new
        {
            environment = EnvironmentFacts(),
            scope = "Loaded WPF fixture on the declared runner. No physical gamepad, Apollo, TV or scanout latency measurement.",
            itemCount = 8,
            cycles = 8,
            openingToRenderMs = opening,
            totalMs = total.Elapsed.TotalMilliseconds,
            cpuMs = (process.TotalProcessorTime - initialCpu).TotalMilliseconds,
            privateStart = initialBytes,
            privateEnd = process.PrivateMemorySize64,
            samples,
            limitations = new[] { "No before/after comparison on the same physical machine.", "No GPU frame-time or dropped-frame measurement.", "Memory samples are observations, not proof of leak freedom." }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<object> CaptureAcknowledgements(string output, Application app)
    {
        var results = new List<object>();
        foreach (var light in new[] { false, true })
            foreach (var width in new[] { 572, 360 })
                foreach (var state in new[] { "controller", "mouse", "success", "warning", "unknown-controller", "wired-controller", "controller-lost", "unavailable-action", "selected-audio", "focused-action" })
                {
                    Console.WriteLine($"CAPTURE START theme={(light ? "light" : "dark")}; requested={width}x{(width == 572 ? 940 : 480)}; notice={state}");
                    ThemeSupport.ForceForValidation(app, light);
                    using var host = new Host(gameTitle: "Elden Ring", identities: state switch
                    {
                        "unknown-controller" or "controller-lost" => [],
                        "wired-controller" => [new("virtual-xbox", "Xbox 360", ControllerFamily.Xbox, false, null, false)],
                        _ => null
                    });
                    var window = host.Window;
                    var height = width == 572 ? 940 : 480;
                    host.Open(width, height, forCapture: true);
                    AdaptiveInterfaceTests.UseController(window);
                    if (state == "controller-lost")
                    {
                        window.UpdateControllerTelemetry([default, default, default, default], null, Environment.TickCount64);
                        Complete(window.ControllerBadgeTask);
                        Require(((FrameworkElement)window.FindName("ControllerInfoBar")).IsVisible
                            && ((TextBlock)window.FindName("ControllerModelLabel")).Text == "Controller disconnected"
                            && !((TextBlock)window.FindName("ControllerBatteryLabel")).IsVisible,
                            "Disconnected evidence lacks recovery or retained a charge claim");
                    }
                    if (state == "unavailable-action")
                        Pick(window, "steam-overlay");
                    else if (state is "selected-audio" or "focused-action")
                    {
                        Pick(window, "audio");
                    }
                    else if (state == "mouse")
                    {
                        window.ObservePointerPosition(new(100, 100));
                        window.ObservePointerPosition(new(120, 100));
                    }
                    else if (state is "success" or "warning")
                    {
                        window.Controller.SetOperation(state == "success" ? "Completed" : "Unconfirmed", state == "success" ? "Settings saved." : "The window changed. Select it again before trying another command.", summary: state == "warning" ? "Select window again." : null);
                        window.RefreshRows();
                    }
                    window.RefreshOperationNotice(Environment.TickCount64 + 200);
                    var bitmap = RenderCapture(window, width, height, state == "controller-lost" ? "keyboard" : state == "mouse" ? "mouse" : "controller");
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(bitmap));
                    var name = $"{(light ? "light" : "dark")}-{width}-notice-{state}.png";
                    using var file = File.Create(Path.Combine(output, name));
                    png.Save(file);
                    results.Add(new
                    {
                        file = name,
                        route = "notice-" + state,
                        theme = light ? "light" : "dark",
                        requestedWidth = width,
                        requestedHeight = height,
                        actualWidth = window.ActualWidth,
                        actualHeight = window.ActualHeight,
                        selected = window.SelectedAction,
                        fixture = true,
                        attachedNativeWpfTree = true,
                        actualForegroundConfirmed = true
                    });
                }
        return results;
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
        var confirmation = ((FrameworkElement)window.FindName("ConfirmPanel")).IsVisible;
        if (confirmation)
            Require(buttons.Any(button => Equals(button.Tag, "confirm-hold"))
                && buttons.Any(button => Equals(button.Tag, "confirm-cancel")),
                "Confirmation capture is missing its actual hold or cancel command");
        else
            Require(buttons.Length >= 4, "Capture has no rendered command buttons: selected=" + window.SelectedAction + "; visible=" + buttons.Length
                + "; all=" + string.Join(", ", CaptureVisuals<Button>(root).Select(button => $"{button.Tag}:{button.Visibility}/{button.IsVisible}/{button.ActualWidth:F1}x{button.ActualHeight:F1}")));
        var rows = (FrameworkElement)window.FindName("Rows");
        Require(buttons.Any(button => rows.IsAncestorOf(button)) || confirmation,
            "Capture has chrome but no rendered page actions");
        foreach (var button in buttons)
        {
            Require(button.Template is not null && VisualTreeHelper.GetChildrenCount(button) > 0, "Capture button has no template visuals: " + button.Tag);
            if (button.Content is FrameworkElement content)
                Require(content.IsVisible && content.ActualWidth > 0 && content.ActualHeight > 0 && PresentationSource.FromVisual(content) is not null, "Capture button content is disconnected or empty: " + button.Tag);
            else if (button.Content is string text && text.Length > 0)
                Require(CaptureVisuals<TextBlock>(button).Any(label => label.IsVisible && label.ActualWidth > 0 && label.ActualHeight > 0 && label.Text.Length > 0), "Capture button text is not rendered: " + button.Tag);
        }
        if (((Border)window.FindName("ContextCard")).IsVisible)
        {
            var title = (TextBlock)window.FindName("TargetLabel");
            Require(title.IsVisible && title.ActualWidth > 0 && title.ActualHeight > 0 && title.Text.Length > 0, "Capture game title is not rendered");
        }
        if (confirmation)
        {
            var target = (TextBlock)window.FindName("ConfirmTarget");
            Require(target.IsVisible && target.ActualWidth > 0 && target.ActualHeight > 0 && target.Text.Length > 0,
                "Confirmation capture does not identify its actual close target");
        }

        var footer = confirmation && ((Button)window.FindName("HoldCloseButton")).Content is IconLabel hold
            ? InputPrompt.Read(hold.Label)
            : InputPrompt.Read((TextBlock)window.FindName("ModeLabel"));
        var expectedPrompt = expectedInput switch
        {
            "controller" => confirmation ? "A" : "LB/RB",
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

    private static void Navigate(MainWindow w, string page, Host? host = null)
    {
        switch (page)
        {
            case "system":
                w.Page(2);
                break;
            case "system-save":
            case "system-discard":
                w.Page(2);
                Choose(w, "sound-feedback");
                Pick(w, page == "system-save" ? "save-pending" : "discard-pending");
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
            case "apps-empty":
                w.Page(1);
                break;
            case "options":
                w.SetRenderState("options");
                break;
            case "details":
                w.SetRenderState("details");
                break;
            case "audio":
                w.SetRenderState("audio");
                break;
            case "integrations":
                w.SetRenderState("integrations");
                break;
            case "customize":
                w.Page(2);
                Choose(w, "customize");
                break;
            case "setup":
                w.Page(2);
                Choose(w, "setup");
                break;
            case "setup-display":
            case "setup-finish":
                w.Page(2);
                Choose(w, "setup");
                Choose(w, "setup-next");
                if (page == "setup-finish")
                    Choose(w, "setup-next");
                break;
            case "operation":
                w.Controller.SetOperation("Unconfirmed", "The selected window changed before the command could be confirmed. Select it again in Apps. No additional command was sent.", "fixture", summary: "Select the window again.");
                w.RefreshRows();
                Choose(w, "operation-details");
                break;
            case "confirmation":
                w.SetRenderState("confirmation");
                break;
            case "browser":
                if (host is null)
                    throw new InvalidOperationException("Browser fixture requires its owned temporary folder.");
                var folder = Path.Combine(host.Root, "Playnite");
                Directory.CreateDirectory(Path.Combine(folder, "Themes"));
                Directory.CreateDirectory(Path.Combine(folder, "Extensions"));
                File.WriteAllText(Path.Combine(folder, "Playnite.FullscreenApp.exe"), "This is an inert UI fixture, never executed.");
                w.ShowPlayniteFolderForValidation(folder);
                WaitUntil(() => w.ActionIds.Contains("playnite-use"), "Playnite folder did not finish its real directory scan");
                break;
            case "updates":
                w.Page(2);
                Choose(w, "updates");
                break;
            case "updates-available":
            case "update-review":
            case "update-ready":
            case "update-downloading":
            case "update-download-action":
            case "update-install-action":
                w.Page(2);
                Choose(w, "updates");
                Choose(w, "check-updates");
                Complete(w.UpdateTask);
                if (page == "updates-available")
                    Pick(w, "release:" + NextUpdate.BuildId);
                else
                {
                    Choose(w, "release:" + NextUpdate.BuildId);
                    if (page is "update-ready" or "update-install-action")
                    {
                        Choose(w, "prepare-update");
                        Complete(w.UpdateTask);
                        if (page == "update-install-action")
                            Pick(w, "apply-update");
                    }
                    else if (page == "update-downloading")
                    {
                        if (host is null)
                            throw new InvalidOperationException("Pending update fixture has no source owner.");
                        host.Updates.Pending = true;
                        Choose(w, "prepare-update");
                        WaitUntil(() => !w.UpdateTask.IsCompleted, "Download fixture did not enter its pending state");
                        Complete(Task.Delay(30));
                    }
                    else if (page == "update-download-action")
                        Pick(w, "prepare-update");
                }
                break;
            case "update-error":
                if (host is null)
                    throw new InvalidOperationException("Update error fixture has no source owner.");
                host.Updates.Fail = true;
                w.Page(2);
                Choose(w, "updates");
                Choose(w, "check-updates");
                Complete(w.UpdateTask);
                break;
            case "update-rollback":
                w.ShowRollbackForValidation(new("fixture-previous", "0.47.1+2222222", new string('2', 40), DateTimeOffset.Parse("2026-10-27T12:00:00Z")));
                break;
            case "mixer":
                Choose(w, "session-mixer");
                Complete(w.FeatureTask);
                break;
            case "devices":
            case "audio-empty":
            case "long-device":
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
            case "media-empty":
                Choose(w, "session-media");
                break;
            case "keyboard":
                Choose(w, "session-keyboard");
                break;
            case "gallery":
            case "gallery-loading":
            case "gallery-empty":
            case "capture-detail":
            case "capture-missing":
                if (page == "gallery-loading")
                {
                    // Isolate the gallery's delayed work from session artwork,
                    // which deliberately shares the production decoder budget.
                    w.Controller.ClearSelection();
                    w.RefreshRows();
                    var pending = new TaskCompletionSource<BitmapSource>();
                    w.CaptureImageReaderFixture = (_, _, cancellation) => pending.Task.WaitAsync(cancellation);
                }
                Choose(w, "session-gallery");
                Complete(w.FeatureTask);
                if (page != "gallery-loading")
                    Complete(w.CapturePresentationTask);
                if (page is "capture-detail" or "capture-missing")
                {
                    if (page == "capture-missing" && host is not null)
                        foreach (var path in Directory.EnumerateFiles(new ScreenCaptures(host.Root).DirectoryPath, "*.png"))
                            File.Delete(path);
                    Choose(w, w.ActionIds.First(id => id.StartsWith("capture:", StringComparison.Ordinal)));
                    Complete(w.CapturePresentationTask);
                }
                break;
            case "appearance":
                w.OpenAppearance();
                break;
            case "appearance-save":
            case "appearance-discard":
                w.OpenAppearance();
                Choose(w, "appearance-preset:Graphite");
                Pick(w, page);
                break;
            case "color-editor":
            case "color-keep":
            case "color-cancel":
            case "color-hex-keyboard":
            case "color-unsafe":
                w.OpenAppearance();
                Choose(w, "appearance-color:" + PaletteRole.Focus);
                if (page is "color-keep" or "color-cancel")
                    Pick(w, page == "color-keep" ? "color-apply" : "color-cancel");
                else if (page is "color-hex-keyboard" or "color-unsafe")
                {
                    Choose(w, "color-hex");
                    if (page == "color-unsafe")
                    {
                        Choose(w, "keyboard-editor");
                        Require(Keyboard.FocusedElement is TextBox, "The local hexadecimal editor did not receive focus");
                        ((TextBox)Keyboard.FocusedElement).Text = ((SolidColorBrush)w.FindResource(ThemeSupport.FocusBackground)).Color.ToString()[3..];
                        TapKey((UIElement)Keyboard.FocusedElement, Key.Tab);
                        Choose(w, "keyboard-insert");
                    }
                }
                break;
            case "no-session":
                w.Controller.ClearSelection();
                w.RefreshRows();
                break;
        }
        w.UpdateLayout();
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(5))
            Complete(Task.Delay(10));
        Require(condition(), message);
    }

    internal static void Choose(MainWindow w, string id)
    {
        Pick(w, id);
        w.ActivateEntry();
        w.UpdateLayout();
    }

    internal static void Pick(MainWindow w, string id)
    {
        for (var i = 0; i <= w.ActionIds.Count && w.SelectedAction != id; i++)
            w.Sequential(1);
        Require(w.SelectedAction == id, "Action unreachable: " + id);
        w.UpdateLayout();
    }

    internal static void TapKey(UIElement element, Key key)
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

    internal static void Complete(Task task) => ReviewFixTests.Complete(task);
    internal sealed class Host : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "SessionInterface", Guid.NewGuid().ToString("N"));
        internal readonly AudioFixture Audio = new(), Mixer = new();
        internal readonly MediaFixture Media = new();
        internal readonly UpdateFixture Updates = new();
        internal MainWindow Window
        {
            get;
        }

        internal Host(Action<string>? initialize = null, ISystemAudioBackend? backend = null, bool preview = true, string? gameTitle = null, ControllerIdentity[]? identities = null,
            bool populateCaptures = false, bool emptyApps = false, bool emptyMedia = false)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = Application.Current ?? new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            if (app.TryFindResource(ThemeSupport.Card) is null)
                ThemeSupport.ForceForValidation(app, false);
            initialize?.Invoke(Root);
            if (populateCaptures)
            {
                var captures = new ScreenCaptures(Root, new CaptureFixture());
                for (var index = 0; index < 8; index++)
                    Complete(captures.CaptureAsync(new("fixture", 0, 0, 120, 80, true), "Local screenshot " + (index + 1).ToString("00"), "eldenring"));
            }
            if (emptyMedia)
                Media.Clear();
            var platform = new PremiumInterfaceTests.FixturePlatform(preview) { Empty = emptyApps };
            if (gameTitle is not null)
                platform.Window = platform.Window with
                {
                    Title = gameTitle,
                    ProcessName = "eldenring"
                };
            Window = new(renderOnly: true, platform: platform, diagnostics: new(Root), appAudioService: Audio, media: Media, systemAudioBackend: backend ?? new PreviewSystemAudioBackend(), mixerAudioService: Mixer, updates: Updates, controllerIdentitySource: new PreviewControllerIdentitySource { Items = identities ?? [new("fixture-xbox", "Xbox One", ControllerFamily.Xbox, true, 76, false)] });
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

    internal sealed class AudioFixture : IApplicationAudio
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
            for (var y = 0; y < 80; y++)
                for (var x = 0; x < 120; x++)
                {
                    var pixel = (y * 120 + x) * 4;
                    pixels[pixel] = (byte)(34 + y / 2);
                    pixels[pixel + 1] = (byte)(54 + x / 3);
                    pixels[pixel + 2] = (byte)(116 + (x + y) / 3);
                    pixels[pixel + 3] = 255;
                }
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

    internal sealed class MediaFixture : ISessionMedia
    {
        public SessionMediaSnapshot Current
        {
            get;
            private set;
        } = new(1, [new("music", "A moment between worlds", "Session soundtrack", "Fixture music", true, true, false, true)], "music", "Windows media controls.");

        public event Action<SessionMediaSnapshot>? Changed;
        internal int Commands;
        internal string LastId = "";
        internal void Clear() => Current = new(1, [], "", "No media is available.");
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
    internal sealed class UpdateFixture : IUpdateSource
    {
        internal bool Pending, Fail;
        public Task<IReadOnlyList<UpdateCandidate>> CheckAsync(string channel, CancellationToken cancellation) => Fail
            ? Task.FromException<IReadOnlyList<UpdateCandidate>>(new HttpRequestException("Fixture query failure"))
            : Task.FromResult<IReadOnlyList<UpdateCandidate>>([NextUpdate]);
        public async Task<PreparedUpdate> StageAsync(UpdateCandidate candidate, IProgress<UpdateProgress>? progress, CancellationToken cancellation)
        {
            progress?.Report(new("Downloading update", candidate.Size / 3, candidate.Size));
            if (Pending)
                await Task.Delay(Timeout.Infinite, cancellation);
            return new("fixture-only-never-installed", new(candidate.Version, candidate.BuildId, candidate.Commit, candidate.Channel));
        }
        public void Dispose()
        {
        }
    }

    private sealed class SnapshotAudioFixture(SystemAudioSnapshot state) : ISystemAudioBackend
    {
        public SystemAudioSnapshot Read() => state;
        public ActionResult SetMicrophoneMuted(string expectedId, bool muted, Func<bool> continuing) => new(ResultKind.Unavailable, "Read-only presentation fixture");
        public ActionResult SetOutputs(string expectedConsole, string expectedMedia, string console, string media, Func<bool> continuing) => new(ResultKind.Unavailable, "Read-only presentation fixture");
    }

}

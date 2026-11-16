using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ApolloOverlay;

internal static class ReviewFixTests
{
    internal static void Require(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    internal static void Capture(string output, bool focused = false, bool closeConfirmation = false)
    {
        Directory.CreateDirectory(output);
        var app = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        var matrix = new List<(string Theme, int Width, int Height, string State)>();
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
                (572, 600),
                (420, 600),
                (360, 480)
            }

            )
                matrix.Add((theme, size.Item1, size.Item2, "home"));
            foreach (var state in new[]
            {
                "system-dirty-top",
                "system-dirty",
                "apps",
                "details",
                "audio",
                "integrations",
                "confirmation",
                "cancelled",
                "empty",
                "long-error",
                "hover-template-simulation",
                "pressed-template-simulation",
                "focus-template-simulation"
            }

            )
                matrix.Add((theme, 572, 940, state));
            foreach (var state in new[]
            {
                "system-dirty",
                "confirmation",
                "audio",
                "apps",
                "empty",
                "long-error"
            }

            )
                matrix.Add((theme, 360, 480, state));
        }

        foreach (var state in new[]
        {
            "home",
            "system-dirty",
            "confirmation",
            "audio"
        }

        )
            matrix.Add(("contrast-palette", 420, 600, state));
        if (closeConfirmation)
            matrix = matrix.Where(c => c.Theme == "dark" && c.State == "confirmation" && c.Width is 572 or 360).ToList();
        if (focused)
        {
            matrix.Clear();
            foreach (var theme in new[]
            {
                "dark",
                "light"
            }

            )
            {
                matrix.Add((theme, 360, 480, "long-error"));
                matrix.Add((theme, 360, 480, "operation"));
                matrix.Add((theme, 572, 940, "integrations"));
            }
        }

        foreach (var item in matrix)
        {
            var (theme, width, height, state) = item;
            ThemeSupport.ForceForValidation(app, theme == "light");
            var palette = new ResourceDictionary();
            if (theme == "contrast-palette")
            {
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
                palette[ThemeSupport.Critical] = Brushes.Cyan;
                app.Resources.MergedDictionaries.Add(palette);
            }

            var platform = new TestPlatform
            {
                Empty = state == "empty"
            };
            var w = WpfTestHost.CreatePreviewWindow(platform, TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                if (state.StartsWith("system-dirty"))
                {
                    w.Controller.Change(w.Controller.Settings with
                    {
                        ReducedMotion = true,
                        GuideOpenMaxHoldMs = 1500
                    });
                    w.Page(2);
                }
                else if (state is "confirmation" or "cancelled")
                {
                    PremiumInterfaceTests.OpenCloseConfirmation(w);
                    if (state == "cancelled")
                        w.Back();
                }
                else if (state is "long-error" or "operation")
                {
                    w.Controller.SetOperation("Unconfirmed", "The window identity changed during the request. Open Apps, select the window again, and check for a save dialog before trying again. No additional command was sent.", "fixture", dispatched: true);
                    w.RefreshRows();
                    if (state == "operation")
                        ((Button)w.FindName("OperationDetailsButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }
                else if (state is not ("home" or "empty") && !state.EndsWith("template-simulation"))
                    w.SetRenderState(state);
                var root = (FrameworkElement)w.Content;
                w.Content = null;
                root.Resources.MergedDictionaries.Add(w.Resources);
                root.SetResourceReference(TextElement.ForegroundProperty, ThemeSupport.Text);
                root.SetValue(TextElement.FontFamilyProperty, w.FontFamily);
                root.SetValue(TextElement.FontSizeProperty, w.FontSize);
                root.Measure(new Size(width, height));
                root.Arrange(new Rect(0, 0, width, height));
                root.UpdateLayout();
                var scroll = (ScrollViewer)w.FindName("ContentScroll");
                var mode = (TextBlock)w.FindName("ModeLabel");
                var footer = mode.TransformToAncestor(root).TransformBounds(new Rect(new Size(mode.ActualWidth, mode.ActualHeight)));
                Require(footer.Bottom <= height + 1 && footer.Top >= 0, "Footer clipped: " + item);
                Require(!w.IsVisible, "Capture acquired foreground");
                foreach (var controlName in state.StartsWith("system-dirty") ? new[]
                {
                    "SaveSettingsButton",
                    "DiscardSettingsButton"
                }

                : state == "confirmation" ? new[]
                {
                    "ConfirmCloseButton",
                    "CancelCloseButton"
                }

                : Array.Empty<string>())
                {
                    var control = (FrameworkElement)w.FindName(controlName);
                    var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(new Size(control.ActualWidth, control.ActualHeight)));
                    Require(bounds.Top >= 0 && bounds.Bottom <= height + 1 && bounds.Width >= 40 && bounds.Height >= 40, "Essential control clipped: " + controlName + item);
                }

                if (scroll.Visibility == Visibility.Visible)
                {
                    Require(scroll.ViewportHeight > 0, "No scroll viewport: " + item);
                    scroll.ScrollToEnd();
                    root.UpdateLayout();
                    Require(scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 1, "Scroll end unreachable");
                    if (state != "system-dirty")
                        scroll.ScrollToTop();
                    root.UpdateLayout();
                }

                if (state.EndsWith("template-simulation"))
                {
                    var button = WpfTestElements.Descendants<Button>(root).First(b => Equals(b.Tag, "session-card"));
                    button.ApplyTemplate();
                    if (state == "focus-template-simulation")
                        InteractionMotion.SetIsSelected(button, true);
                    else
                        ((Border)button.Template.FindName("Interaction", button)).Opacity = state == "hover-template-simulation" ? .06 : .13;
                }

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var name = $"{theme}-{width}x{height}-{state}";
                using (var stream = File.Create(Path.Combine(output, name + ".png")))
                    encoder.Save(stream);
                Console.WriteLine($"CAPTURE {name}; footer={footer}; viewport={scroll.ViewportHeight}; extent={scroll.ExtentHeight}; offset={scroll.VerticalOffset}; offscreen={!w.IsVisible}; templateSimulation={state.EndsWith("template-simulation")}");
            }
            finally
            {
                w.Close();
                if (theme == "contrast-palette")
                    app.Resources.MergedDictionaries.Remove(palette);
                ThemeSupport.FollowSystem(app);
            }
        }
    }

    internal static void Run(Action<string, Action> test)
    {
        var app = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        ThemeSupport.FollowSystem(app);
        test("RF keyboard pre-held W without overlay key event blocks neutrality", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.PhysicalKeyReader = k => k == 0x57 ? unchecked((short)0x8000) : (short)0;
                Require(!w.PhysicalKeysNeutral(), "Omitted pre-held W was reported neutral by shortcut whitelist");
            }
            finally
            {
                w.Close();
            }
        });
        foreach (var key in new[]
        {
            0x03,
            0x08,
            0x30,
            0x57,
            0x70,
            0xA0,
            0xA1,
            0xA2,
            0xA3,
            0xA4,
            0xA5,
            0xBA,
            0xFE
        }

        )
            test("RF keyboard high-bit pre-held key " + key, () =>
            {
                var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
                try
                {
                    w.PhysicalKeyReader = k => k == key ? unchecked((short)0x8000) : (short)0;
                    Require(!w.PhysicalKeysNeutral(), "Pre-held VK ignored: " + key);
                }
                finally
                {
                    w.Close();
                }
            });
        test("RF physical input coverage includes mouse buttons and ignores the low bit", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                var read = new HashSet<int>();
                w.PhysicalKeyReader = k =>
                {
                    read.Add(k);
                    return 1;
                };
                Require(w.PhysicalKeysNeutral(), "Low bit must not mean held");
                Require(read.SetEquals(Enumerable.Range(1, 6).Concat(Enumerable.Range(8, 0xFE - 8 + 1))),
                    "Keyboard or mouse-button VK coverage differs");
            }
            finally
            {
                w.Close();
            }
        });
        foreach (var context in new[]
        {
            new OverlayInputContext(true, true, true, true),
            new(false, true, true, true),
            new(true, false, true, true),
            new(true, true, false, true),
            new(true, true, true, false),
            new(true, true, true, true, true),
            new(true, true, true, true, false, true)
        }

        )
            test("RF navigation same predicate " + context, () =>
            {
                var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
                try
                {
                    w.InputContextReader = () => context;
                    var before = w.SelectedAction;
                    w.Move(NavDirection.Down);
                    Require((w.SelectedAction != before) == (context.Confirmed && context.Visible && context.Active && context.OwnForeground && !context.OwnedDialog && !context.Exiting), "Focus predicate changed");
                }
                finally
                {
                    w.Close();
                }
            });
        foreach (var degraded in new[]
        {
            false,
            true
        }

        )
            test("RF pre-held W blocks " + (degraded ? "degraded dismissal" : "normal handoff") + " and renewed input resets 100ms", () =>
            {
                var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
                try
                {
                    var held = true;
                    w.PhysicalKeyReader = k => held && k == 0x57 ? unchecked((short)0x8000) : (short)0;
                    if (degraded)
                        w.Back();
                    else
                        WpfTestElements.Button(w, "taskview").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Require(w.TransitionPending, "Handoff not queued");
                    ReleaseSample Sample(long t) => new(new PadFrame[4], t, GuideValid: !degraded);
                    w.AdvanceHandoff(Sample(0), 0);
                    w.AdvanceHandoff(Sample(1000), 1000);
                    Require(w.TransitionPending, "Pre-held W allowed dispatch");
                    held = false;
                    w.AdvanceHandoff(Sample(1001), 1001);
                    w.AdvanceHandoff(Sample(1100), 1100);
                    Require(w.TransitionPending, "Committed before100ms");
                    held = true;
                    w.AdvanceHandoff(Sample(1101), 1101);
                    held = false;
                    w.AdvanceHandoff(Sample(1102), 1102);
                    w.AdvanceHandoff(Sample(1201), 1201);
                    Require(w.TransitionPending, "Renewed held key did not restart interval");
                    w.AdvanceHandoff(Sample(1202), 1202);
                    Require(!w.TransitionPending, "100ms release did not commit");
                    w.AdvanceHandoff(Sample(1302), 1302);
                    Require(!w.TransitionPending, "Commit did not remain final");
                }
                finally
                {
                    w.Close();
                }
            });
        foreach (var state in new[]
        {
            "home",
            "apps",
            "system",
            "details",
            "audio",
            "integrations"
        }

        )
            test("RF UIA all page actions " + state, () =>
            {
                var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
                try
                {
                    w.SetRenderState(state);
                    var gear = (Button)w.FindName("SettingsGear");
                    Require(new ButtonAutomationPeer(gear).GetName() == "Open System", "Gear name missing");
                    Require(new ButtonAutomationPeer(gear).GetHelpText().Length > 0, "Gear help missing");
                    var buttons = ((Grid)w.FindName("Rows")).Children.OfType<Button>().ToArray();
                    Require(buttons.Length > 0, "Empty page fixture");
                    foreach (var button in buttons)
                    {
                        var peer = new ButtonAutomationPeer(button);
                        Require(peer.GetHelpText().Length > 0, "Action help missing: " + button.Tag);
                        if (button.Content is ActionButtonContent content)
                        {
                            Require(peer.GetName() == content.Title.Text, "Action name differs: " + button.Tag);
                            var guidance = InputPrompt.Read(content.Description).Trim();
                            if (content.Description.Visibility == Visibility.Visible && guidance.Length > 0)
                                Require(peer.GetHelpText().Contains(guidance, StringComparison.Ordinal),
                                    "Accessible help omits the rendered guidance: " + button.Tag);
                        }
                        else
                            Require(peer.GetName().StartsWith("Options for "), "Sibling options missing full target name");
                    }

                    Require(!w.IsVisible, "Accessibility probe must stay offscreen");
                }
                finally
                {
                    w.Close();
                }
            });
        test("RF Audio tab exposes current volume and mute state to accessibility", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                var state = new AudioSnapshot("fixture", .35f, true, "fixture");
                var audio = typeof(MainWindow).GetField("audio", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(w);
                typeof(WindowsAudio).GetProperty(nameof(WindowsAudio.Current))!.SetValue(audio, state);
                w.SetRenderState("audio");
                var mute = new ButtonAutomationPeer(WpfTestElements.Button(w, "mute"));
                var volume = new ButtonAutomationPeer(WpfTestElements.Button(w, "volume"));
                Require(mute.GetName() == "Unmute", "Initial mute state stale");
                w.UpdateAudioEntries(state with
                {
                    Volume = .55f,
                    Muted = false
                });
                Require(mute.GetName() == "Mute", "Unmute state stale");
                Require(volume.GetName() == "Volume · " + .55f.ToString("P0"), "Volume state stale");
            }
            finally
            {
                w.Close();
            }
        });
        test("RF Guide status reason and limit title update existing peers", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.SetRenderState("integrations");
                var button = WpfTestElements.Button(w, "guide");
                var peer = new ButtonAutomationPeer(button);
                w.UpdateGuideStatus("Controller unavailable · fixture");
                Require(peer.GetHelpText() == "Controller unavailable · fixture", "Guide help stale");
                Require(ReferenceEquals(button, WpfTestElements.Button(w, "guide")), "Guide rebuilt button");
                w.Page(2);
                var limit = WpfTestElements.Button(w, "guide-limit");
                var limitPeer = new ButtonAutomationPeer(limit);
                var texts = (StackPanel)((Grid)limit.Content).Children[1];
                texts.Children.OfType<Slider>().Single().Value = 1500;
                Require(limitPeer.GetName() == ((TextBlock)texts.Children[0]).Text && limitPeer.GetName().Contains("1.5"), "Guide limit name stale");
            }
            finally
            {
                w.Close();
            }
        });
        test("RF UIA card exposes full target title and resume action without Show", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                var button = WpfTestElements.Button(w, "session-card");
                var peer = new ButtonAutomationPeer(button);
                Require(peer.GetName().Contains("Test window") && peer.GetName().Contains("Resume window"), "Missing card target/action name: '" + peer.GetName() + "'");
                Require(peer.GetHelpText().Contains("resumes") && peer.GetHelpText().Contains("options"), "Missing card help: '" + peer.GetHelpText() + "'");
                Require(!w.IsVisible, "Probe must stay offscreen");
            }
            finally
            {
                w.Close();
            }
        });
    }
}

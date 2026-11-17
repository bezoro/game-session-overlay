using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ApolloOverlay;

internal static class PremiumInterfaceTests
{
    private static void Require(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    internal static void Run(Action<string, Action> test)
    {
        _ = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        test("shared controller notice is visible and accessible", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                var notice = (TextBlock)w.FindName("SharedControlNotice");
                Require(notice.Visibility == Visibility.Visible && notice.Text.Contains("may also reach the game"), "Missing or misleading controller sharing notice");
                var peer = new TextBlockAutomationPeer(notice);
                Require(peer.GetName().Contains("while this panel is open"), "Screen reader cannot discover the complete notice");
            }
            finally
            {
                w.Close();
            }
        });
        test("premium neutral anchor wording", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                var card = WpfTestElements.Button(w, "session-card");
                Require(new ButtonAutomationPeer(card).GetName().Contains("Resume window") && !new ButtonAutomationPeer(card).GetName().Contains("game"), "Ordinary application is called a game");
            }
            finally
            {
                w.Close();
            }
        });
        test("premium unavailable action has disabled UIA semantics", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                var card = new ButtonAutomationPeer(WpfTestElements.Button(w, "session-card"));
                Require(!w.ActionIds.Contains("steam-overlay") && card.GetHelpText().Contains("Steam Overlay unavailable", StringComparison.Ordinal),
                    "The unavailable Steam gesture has an independent action or missing accessibility status");
            }
            finally
            {
                w.Close();
            }
        });
        test("premium mouse app options affordance", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.Page(1);
                Require(WpfTestElements.Descendants<Button>((DependencyObject)w.Content).Any(b => System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("Options for ")), "Mouse options absent");
            }
            finally
            {
                w.Close();
            }
        });
        test("premium audio decrease path", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.SetRenderState("audio");
                Require(w.ActionIds.Contains("volume-down"), "Mouse can only increase audio");
            }
            finally
            {
                w.Close();
            }
        });
        test("premium mouse close controls", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.SetRenderState("confirmation");
                Require(w.FindName("ConfirmCloseButton") is Button && w.FindName("CancelCloseButton") is Button, "Mouse confirmation controls absent");
            }
            finally
            {
                w.Close();
            }
        });
        test("premium durable dirty affordance after refresh and navigation", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.Controller.Change(w.Controller.Settings with
                {
                    ReducedMotion = true
                });
                w.Page(1);
                w.RefreshRows();
                Require(w.FindName("DirtyStrip") is FrameworkElement strip && strip.Visibility == Visibility.Visible, "Unsaved state is only transient feedback");
            }
            finally
            {
                w.Close();
            }
        });
        test("premium disabled Guide omitted from opening hints", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                var mode = (TextBlock)w.FindName("ModeLabel");
                Require(!InputPrompt.Read(mode).Contains("Guide ·"), "Disabled Guide is advertised");
            }
            finally
            {
                w.Close();
            }
        });
        test("premium normal disabled Guide hint is honest", () =>
        {
            var folder = Temporary();
            var w = new MainWindow(renderOnly: true, platform: new TestPlatform(false), diagnostics: new(folder));
            try
            {
                Require(!InputPrompt.Read((TextBlock)w.FindName("ModeLabel")).Contains("Guide"), "Disabled Guide advertised on normal path");
            }
            finally
            {
                w.Close();
            }
        });
        test("premium settings persistence discard and exact dirty roundtrip", () =>
        {
            var folder = Temporary();
            using var c = new OverlayController(new TestPlatform(false), new(folder));
            var original = c.Settings;
            Require(!c.SettingsDirty, "Initial state dirty");
            c.Change(original with
            {
                ReducedMotion = true,
                GuideOpenMaxHoldMs = 1500
            });
            Require(c.SettingsDirty, "Change lost");
            c.Discard();
            Require(SessionHubSettings.Equivalent(c.Settings, original) && !c.SettingsDirty, "Discard did not restore snapshot");
            c.Change(original with
            {
                ReducedMotion = true
            });
            c.Change(original);
            Require(!c.SettingsDirty, "Revert all fields stayed dirty");
            c.Change(original with
            {
                GuideOpenMaxHoldMs = 1100
            });
            c.Save();
            Require(!c.SettingsDirty && UserSettings.Load(c.Diagnostics.SettingsPath, out _).GuideOpenMaxHoldMs == 1100, "Successful save did not advance snapshot");
        });
        test("premium failed save preserves prior bytes and dirty state", () =>
        {
            var folder = Temporary();
            using var c = new OverlayController(new TestPlatform(false), new(folder));
            c.Save();
            var before = File.ReadAllBytes(c.Diagnostics.SettingsPath);
            c.Change(c.Settings with
            {
                ReducedMotion = true
            });
            Directory.CreateDirectory(c.Diagnostics.SettingsPath + ".pending");
            try
            {
                c.Save();
                throw new Exception("Expected persistence failure");
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }

            Require(c.SettingsDirty && before.SequenceEqual(File.ReadAllBytes(c.Diagnostics.SettingsPath)), "Failure advanced snapshot or modified prior file");
        });
        test("premium preview save is explicitly nonpersistent", () =>
        {
            using var c = new OverlayController(new PreviewPlatform(), new(Temporary()));
            c.Change(c.Settings with
            {
                ReducedMotion = true
            });
            c.Save();
            Require(c.SettingsDirty && !File.Exists(c.Diagnostics.SettingsPath) && c.Status.Contains("Preview"), "Preview falsely persisted settings");
        });
        foreach (var kind in new[]
        {
            ResultKind.Failed,
            ResultKind.Partial,
            ResultKind.Unknown,
            ResultKind.Requested,
            ResultKind.Completed,
            ResultKind.Simulated,
            ResultKind.Unavailable,
            ResultKind.Cancelled
        }

        )
            test("premium retained operation outcome " + kind, () =>
            {
                var p = new TestPlatform();
                p.Next = Task.FromResult(new ActionResult(kind, "fixture outcome"));
                var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
                try
                {
                    Dispatch(w, "taskview");
                    WpfTestDispatcher.Complete(w.OperationTask);
                    Require(p.Calls == 1 && w.Controller.Operation is { Dispatched: true } op && op.Message == "fixture outcome", "Operation lost or duplicated");
                    var previous = w.Controller.Operation;
                    w.Page(1);
                    w.RefreshRows();
                    Require(w.Controller.Operation == previous, "Navigation overwrote result");
                    Require(!w.IsVisible, "Fixture acquired foreground");
                }
                finally
                {
                    w.Close();
                }
            });
        test("premium delayed failure busy cancellation and explicit recovery", () =>
        {
            var p = new TestPlatform();
            var completion = new TaskCompletionSource<ActionResult>();
            p.Next = completion.Task;
            var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                Dispatch(w, "taskview");
                Require(w.Controller.Operation is { State: "Pending", Dispatched: true }, "Dispatch confused with completion");
                w.Page(1);
                w.ActivateEntry();
                Require(p.Calls == 1, "Duplicate dispatch while busy");
                w.Back();
                Require(w.Controller.Operation is { State: "Unconfirmed" }, "Post-dispatch cancellation claims undo");
                completion.SetResult(new(ResultKind.Failed, "delayed refusal"));
                WpfTestDispatcher.Complete(w.OperationTask);
                Require(w.Controller.Operation is { State: "Failed", Message: "delayed refusal", Action: "taskview" }, "Delayed result or original action lost");
                Require(p.Calls == 1, "Implicit retry occurred");
            }
            finally
            {
                w.Close();
            }
        });
        test("premium cleanup suppresses late outcome callbacks", () =>
        {
            var p = new TestPlatform();
            var completion = new TaskCompletionSource<ActionResult>();
            p.Next = completion.Task;
            var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
            Dispatch(w, "taskview");
            w.Close();
            var before = w.Controller.Operation;
            completion.SetResult(new(ResultKind.Failed, "late"));
            WpfTestDispatcher.Complete(w.OperationTask);
            Require(w.Controller.Operation == before && p.Calls == 1, "Late callback after disposal");
        });
        test("premium cancellation before dispatch sends no effect", () =>
        {
            var p = new TestPlatform();
            var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                WpfTestElements.Button(w, "taskview").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                w.Back();
                Require(p.Calls == 0 && w.Controller.Operation?.State == "Cancelled", "Queued cancellation dispatched");
            }
            finally
            {
                w.Close();
            }
        });
        test("premium Guide slider named focusable stable and dirty", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.Page(2);
                var slider = WpfTestElements.Descendants<Slider>((DependencyObject)w.Content).Single();
                Require(slider.Focusable && System.Windows.Automation.AutomationProperties.GetName(slider).Length > 10, "Guide slider inaccessible");
                slider.Value = 1500;
                Require(w.Controller.SettingsDirty && ReferenceEquals(slider, WpfTestElements.Descendants<Slider>((DependencyObject)w.Content).Single()), "Value edit rebuilt focused slider");
                w.Page(1);
                Require(((FrameworkElement)w.FindName("DirtyStrip")).Visibility == Visibility.Visible, "Navigation lost dirty strip");
            }
            finally
            {
                w.Close();
            }
        });
        test("premium controller reaches sticky discard from home", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.Controller.Change(w.Controller.Settings with
                {
                    ReducedMotion = true
                });
                w.RefreshRows();
                Layout((FrameworkElement)w.Content, 572, 940);
                var visited = new List<string>();
                for (var n = 0; n < w.ActionIds.Count * 2 && w.SelectedAction != "save-pending"; n++)
                {
                    visited.Add(w.SelectedAction);
                    w.Move(NavDirection.Down);
                    ((FrameworkElement)w.Content).UpdateLayout();
                }

                Require(w.SelectedAction == "save-pending", "Sticky save unreachable: " + string.Join(" -> ", visited));
                w.Move(NavDirection.Right);
                Require(w.SelectedAction == "discard-pending", "Sticky discard unreachable");
                w.ActivateEntry();
                Require(!w.Controller.SettingsDirty, "Controller discard failed");
            }
            finally
            {
                w.Close();
            }
        });
        foreach (var light in new[]
        {
            false,
            true
        }

        )
            test("premium rendered tab typography " + light, () =>
            {
                var app = Application.Current!;
                ThemeSupport.ForceForValidation(app, light);
                var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
                try
                {
                    var root = Detach(w);
                    foreach (var page in new[]
                    {
                        0,
                        1,
                        2
                    }

                    )
                    {
                        w.Page(page);
                        Layout(root, 572, 940);
                        foreach (var name in new[]
                        {
                            "HomeTab",
                            "AppsTab",
                            "SettingsTab"
                        }

                        )
                        {
                            var label = ((IconLabel)((Button)w.FindName(name)).Content).Label;
                            Require(label.FontFamily.Source == "Segoe UI Variable Text, Segoe UI" && label.FontSize == 16 && label.FontStyle == FontStyles.Normal, "Tab typography differs from the interface font: " + name);
                            GlyphTypeface? glyph = null;
                            foreach (var family in label.FontFamily.Source.Split(','))
                            {
                                var candidate = new Typeface(new FontFamily(family.Trim()), label.FontStyle, label.FontWeight, label.FontStretch);
                                if (candidate.TryGetGlyphTypeface(out var resolved) && label.Text.All(character => resolved.CharacterToGlyphMap.ContainsKey(character)))
                                {
                                    glyph = resolved;
                                    break;
                                }
                            }

                            Require(glyph is not null, "Neither the preferred font nor the Windows fallback can render the tab label");
                            var formatted = new FormattedText(label.Text, System.Globalization.CultureInfo.GetCultureInfo("en-US"), label.FlowDirection, new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize, Brushes.White, 1);
                            var bounds = formatted.BuildGeometry(new Point()).Bounds;
                            Require(!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0, "Tab label did not produce visible glyphs");
                            Console.WriteLine($"TAB-FONT light={light}; selectedPage={page}; name={name}; family={label.FontFamily.Source}; size={label.FontSize}; weight={label.FontWeight}; resolved={string.Join(",", glyph!.FamilyNames.Values.Distinct())}");
                        }
                    }
                }
                finally
                {
                    w.Close();
                    ThemeSupport.FollowSystem(app);
                }
            });
        RunCloseConfirmation(test);
        RunSafety(test);
    }

    internal static void RunCloseConfirmation(Action<string, Action> test)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ThemeSupport.FollowSystem(Application.Current!);
        void ConfirmCase(string name, Action run) => test(name, () =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                run();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
        ConfirmCase("close confirmation shows the named target, defaults to cancel and restores its origin", () =>
        {
            var p = new TestPlatform();
            var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.OpenOptions(p.Window);
                OpenCloseConfirmation(w);
                var target = (TextBlock)w.FindName("ConfirmTarget");
                Require(target.Text == p.Window.Title && w.CloseTarget!.Same(p.Window), "Confirmation omitted or changed the exact target");
                Require(w.ActionIds.SequenceEqual(["confirm-cancel", "confirm-close"]) && w.SelectedAction == "confirm-cancel", "Wrong confirmation commands or default");
                Require(w.AutomationSnapshot().Actions.Any(a => a.Id == "confirm-hold" && a.Capability == "RequiresGesture"), "CLI close capability changed");
                WpfTestElements.ClickNamed(w, "CancelCloseButton");
                Require(!w.Confirming && w.SelectedAction == "close" && p.Closes == 0, "Cancel did not restore its origin");
                OpenCloseConfirmation(w);
                WpfTestElements.ClickNamed(w, "ConfirmCloseButton");
                WpfTestElements.ClickNamed(w, "ConfirmCloseButton");
                WpfTestDispatcher.Complete(w.OperationTask);
                Require(!w.Confirming && p.Closes == 1 && w.Controller.Operation is { Action: "close", State: "Simulated" }, "One Close click did not dispatch exactly once");
            }
            finally
            {
                w.Close();
            }
        });
        ConfirmCase("close confirmation draws the complete decision at compact panel size", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(new TestPlatform(), TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                OpenCloseConfirmation(w);
                var root = Detach(w);
                Layout(root, 360, 480);
                var scroll = (ScrollViewer)w.FindName("ConfirmScroll");
                foreach (var name in new[] { "ConfirmationHeading", "ConfirmTarget", "ConfirmationWarning", "CancelCloseButton", "ConfirmCloseButton" })
                {
                    var element = (FrameworkElement)w.FindName(name);
                    var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(new Size(element.ActualWidth, element.ActualHeight)));
                    Require(bounds.Top >= 0 && bounds.Bottom <= 480 && bounds.Height > 0, "Compact confirmation clipped: " + name);
                    if (name is "ConfirmationHeading" or "ConfirmTarget" or "ConfirmationWarning")
                    {
                        var viewport = scroll.TransformToAncestor(root).TransformBounds(new Rect(new Size(scroll.ActualWidth, scroll.ViewportHeight)));
                        Require(bounds.Top >= viewport.Top && bounds.Bottom <= viewport.Bottom + 1, "Decision text clipped inside scroller: " + name);
                    }
                    Console.WriteLine("COMPACT-CONFIRM " + name + "=" + bounds);
                }
            }
            finally
            {
                w.Close();
            }
        });
        ConfirmCase("keyboard opening press and repeat cannot close; a fresh key dispatches once", () =>
        {
            var p = new TestPlatform();
            var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                var source = HwndSource.FromHwnd(new WindowInteropHelper(w).EnsureHandle());
                w.OpenOptions(p.Window);
                for (var i = 0; i < w.ActionIds.Count && w.SelectedAction != "close"; i++)
                    w.Sequential(1);
                Require(w.SelectedAction == "close", "Keyboard fixture could not reach Close");
                ConfirmKey(w, source, Key.Enter, true);
                Require(w.Confirming && w.SelectedAction == "confirm-cancel", "Opening Enter did not stop at Cancel");
                ConfirmKey(w, source, Key.Tab, true);
                ConfirmKey(w, source, Key.Tab, false);
                Require(w.SelectedAction == "confirm-close", "Tab did not reach Close");
                ConfirmKey(w, source, Key.Enter, true);
                ConfirmKey(w, source, Key.Space, true);
                ConfirmKey(w, source, Key.Space, false);
                Require(p.Closes == 0 && w.Confirming, "Held opening key or overlapping activation closed the window");
                ConfirmKey(w, source, Key.Enter, false);
                Require(p.Closes == 0, "Opening release sent a close request");
                ConfirmKey(w, source, Key.Space, true);
                ConfirmKey(w, source, Key.Space, true);
                ConfirmKey(w, source, Key.Space, false);
                WpfTestDispatcher.Complete(w.OperationTask);
                Require(p.Closes == 1 && !w.Confirming, "Fresh normal key activation did not close exactly once");
            }
            finally
            {
                w.Close();
            }
        });
        ConfirmCase("controller opening press and repeat cannot close; a fresh A dispatches once", () =>
        {
            var p = new TestPlatform();
            var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                AdmitFixtureWindow(w);
                w.OpenOptions(p.Window);
                for (var i = 0; i < w.ActionIds.Count && w.SelectedAction != "close"; i++)
                    w.Sequential(1);
                Require(w.SelectedAction == "close", "Controller fixture could not reach Close");
                CloseFrame(w, 0);
                CloseFrame(w, PadButtons.A);
                Require(w.Confirming && w.SelectedAction == "confirm-cancel", "Opening A did not stop at Cancel");
                CloseFrame(w, PadButtons.A | PadButtons.Right);
                CloseFrame(w, PadButtons.A);
                Require(w.SelectedAction == "confirm-close" && p.Closes == 0, "Moving with A held sent a close");
                CloseFrame(w, 0);
                Require(p.Closes == 0 && w.Confirming, "Opening A release sent a close");
                CloseFrame(w, PadButtons.A);
                CloseFrame(w, PadButtons.A);
                WpfTestDispatcher.Complete(w.OperationTask);
                Require(p.Closes == 1 && !w.Confirming, "Fresh A did not close exactly once");
            }
            finally
            {
                w.Close();
            }
        });
        foreach (var cause in new[] { "focus loss", "stale identity", "disconnection", "read failure" })
            ConfirmCase("close confirmation blocks " + cause, () =>
            {
                var p = new TestPlatform();
                var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
                try
                {
                    AdmitFixtureWindow(w);
                    w.OpenOptions(p.Window);
                    CloseFrame(w, 0);
                    OpenCloseConfirmation(w);
                    CloseFrame(w, PadButtons.Right);
                    CloseFrame(w, 0);
                    Require(w.SelectedAction == "confirm-close", "Confirmation fixture failed to select Close");
                    if (cause == "focus loss")
                        w.InputContextReader = () => new(true, true, true, false);
                    if (cause == "stale identity")
                        p.Window = p.Window with
                        {
                            Generation = p.Window.Generation + 1
                        };
                    if (cause is "disconnection" or "read failure")
                        w.ReviewTick([new(false, 0, ReadFailed: cause == "read failure"), default, default, default]);
                    else
                        CloseFrame(w, PadButtons.A);
                    WpfTestDispatcher.Complete(w.OperationTask);
                    Require(p.Closes == 0 && !w.Confirming, "Unsafe condition allowed confirmation or left it armed");
                }
                finally
                {
                    w.Close();
                }
            });
        ConfirmCase("protected close and stale targets are blocked before confirmation", () =>
        {
            var p = new TestPlatform();
            var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.Confirm(p.Window with
                {
                    Protected = true
                });
                Require(!w.Confirming && p.Closes == 0, "Protected close opened");
                p.ValidIdentity = false;
                w.Confirm(p.Window);
                Require(!w.Confirming && p.Closes == 0, "Stale close opened");
            }
            finally
            {
                w.Close();
            }
        });
    }

    private static void ConfirmKey(MainWindow w, HwndSource source, Key key, bool down) =>
        w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent
        });

    private static void AdmitFixtureWindow(MainWindow w)
    {
        w.ShowActivated = false;
        w.Left = -12000;
        w.Top = -12000;
        w.Show();
    }

    private static void CloseFrame(MainWindow w, PadButtons buttons) => w.ReviewTick([new(true, buttons), default, default, default]);

    internal static void RunSafety(Action<string, Action> test)
    {
        _ = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        ThemeSupport.FollowSystem(Application.Current!);
        test("premium full outcome and dismiss reachable by keyboard and controller", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                const string message = "The window identity changed during the request. Open Apps, select the window again, and check for a save dialog before trying again. No additional command was sent.";
                w.Controller.SetOperation("Unconfirmed", message);
                w.RefreshRows();
                Require(w.ActionIds.Contains("operation-details") && w.ActionIds.Contains("dismiss-operation"), "Full result or dismiss is missing from navigation");
                for (var n = 0; n < 30 && w.SelectedAction != "operation-details"; n++)
                    w.Sequential(1);
                Require(w.SelectedAction == "operation-details", "Keyboard cannot reach full outcome");
                w.ActivateEntry();
                var full = WpfTestElements.Descendants<TextBox>((DependencyObject)w.FindName("Rows")).Single();
                var peer = new TextBoxAutomationPeer(full);
                var value = (System.Windows.Automation.Provider.IValueProvider)peer.GetPattern(PatternInterface.Value);
                Require(full.Text == message && full.IsReadOnly && full.TextWrapping == TextWrapping.Wrap && full.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && value.Value == message, "Complete recovery text is not accessible and scrollable");
                w.Back();
                Require(w.SelectedAction == "operation-details", "Back lost result action");
                Layout((FrameworkElement)w.Content, 572, 940);
                w.Move(NavDirection.Right);
                Require(w.SelectedAction == "dismiss-operation", "Controller cannot reach dismiss");
                w.ActivateEntry();
                Require(w.Controller.Operation is null, "Reachable dismissal did not clear result");
            }
            finally
            {
                w.Close();
            }
        });
        test("premium integration Guide description agrees with disabled preview state", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.SetRenderState("integrations");
                var texts = (StackPanel)((Grid)WpfTestElements.Button(w, "guide").Content).Children[1];
                var description = ((TextBlock)texts.Children[1]).Text;
                Require(description.Contains("Preview") && !description.Contains("short press to open"), "Disabled preview Guide still advertises opening: " + description);
            }
            finally
            {
                w.Close();
            }
        });
        LocalDismissalRegression(test);
        test("premium deactivation terminalizes undispatched action", () =>
        {
            var p = new TestPlatform();
            var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
            var other = OwnedWindow();
            try
            {
                w.ShowDiagnostic();
                RequireOwnedFocus(w);
                Queue(w, "taskview");
                TransferOwnedFocus(other);
                Console.WriteLine($"DEACTIVATE visible={w.IsVisible}; active={w.IsActive}; queued={w.TransitionPending}; foregroundIsOther={Native.GetForegroundWindow() == new WindowInteropHelper(other).Handle}; operation={w.Controller.Operation}");
                Require(!w.IsVisible && !w.TransitionPending && p.Calls == 0, "Deactivation did not safely cancel queued action");
                Require(w.Controller.Operation is { State: "Cancelled", Dispatched: false }, "Deactivated undispatched operation remains " + w.Controller.Operation?.State);
            }
            finally
            {
                other.Close();
                w.Close();
            }
        });
        foreach (var kind in new[]
        {
            ResultKind.Failed,
            ResultKind.Partial
        }

        )
            test("premium synchronous foreground transfer hides on " + kind, () =>
            {
                var p = new TestPlatform();
                var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
                var other = OwnedWindow();
                var deactivations = 0;
                try
                {
                    w.Deactivated += (_, _) => deactivations++;
                    w.ShowDiagnostic();
                    RequireOwnedFocus(w);
                    p.OnTaskView = () =>
                    {
                        TransferOwnedFocus(other);
                        Console.WriteLine($"SYNC-EFFECT kind={kind}; insideVisible={w.IsVisible}; active={w.IsActive}; deactivations={deactivations}; foregroundIsOther={Native.GetForegroundWindow() == new WindowInteropHelper(other).Handle}");
                        Require(!w.IsActive && Native.GetForegroundWindow() == new WindowInteropHelper(other).Handle && deactivations > 0, "PRECONDITION: synchronous owned foreground transfer not observed");
                        return Task.FromResult(new ActionResult(kind, "owned synchronous refusal"));
                    };
                    Dispatch(w, "taskview");
                    WpfTestDispatcher.Complete(w.OperationTask);
                    Console.WriteLine($"SYNC-RESULT kind={kind}; visible={w.IsVisible}; active={w.IsActive}; foregroundIsOther={Native.GetForegroundWindow() == new WindowInteropHelper(other).Handle}; operation={w.Controller.Operation}");
                    Require(w.Controller.Operation is { Dispatched: true } op && op.Message == "owned synchronous refusal" && p.Calls == 1, "Outcome was lost or effect repeated");
                    Require(!w.IsVisible, "Inactive topmost overlay remains visible after synchronous foreground transfer");
                }
                finally
                {
                    other.Close();
                    w.Close();
                }
            });
        test("premium immediate refusal without foreground transfer stays visible", () =>
        {
            var p = new TestPlatform();
            var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.ShowDiagnostic();
                RequireOwnedFocus(w);
                Dispatch(w, "session-card");
                WpfTestDispatcher.Complete(w.OperationTask);
                Require(w.IsVisible && w.IsActive && p.Calls == 1 && w.Controller.Operation?.State == "Failed", "Visible immediate refusal or feedback lost");
            }
            finally
            {
                w.Close();
            }
        });
        foreach (var kind in new[]
        {
            ResultKind.Failed,
            ResultKind.Partial,
            ResultKind.Unknown,
            ResultKind.Unavailable,
            ResultKind.Cancelled,
            ResultKind.Completed,
            ResultKind.Requested,
            ResultKind.Simulated
        }

        )
            test("premium hidden notification request policy " + kind, () =>
            {
                var p = new TestPlatform
                {
                    Next = Task.FromResult(new ActionResult(kind, "notification fixture"))
                };
                var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
                var requests = 0;
                w.ResultNotificationRequest = _ => requests++;
                try
                {
                    Dispatch(w, "taskview");
                    WpfTestDispatcher.Complete(w.OperationTask);
                    var expected = kind is ResultKind.Completed or ResultKind.Requested or ResultKind.Simulated ? 0 : 1;
                    Require(requests == expected && p.Calls == 1, "Wrong notification count " + requests);
                    Require(!w.IsVisible, "Notification stole focus");
                }
                finally
                {
                    w.Close();
                }
            });
        test("premium visible immediate refusal does not request notification", () =>
        {
            var p = new TestPlatform();
            var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
            var requests = 0;
            w.ResultNotificationRequest = _ => requests++;
            try
            {
                w.ShowDiagnostic();
                RequireOwnedFocus(w);
                Dispatch(w, "session-card");
                WpfTestDispatcher.Complete(w.OperationTask);
                Require(w.IsVisible && requests == 0, "Visible refusal requested balloon or hid feedback");
            }
            finally
            {
                w.Close();
            }
        });
        test("premium delayed hidden failure requests once without refocus", () =>
        {
            var completion = new TaskCompletionSource<ActionResult>();
            var p = new TestPlatform
            {
                Next = completion.Task
            };
            var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
            var other = OwnedWindow();
            var requests = 0;
            w.ResultNotificationRequest = _ => requests++;
            try
            {
                w.ShowDiagnostic();
                RequireOwnedFocus(w);
                Dispatch(w, "taskview");
                TransferOwnedFocus(other);
                Require(!w.IsVisible && requests == 0, "Pending operation notified or remained visible after deactivation");
                completion.SetResult(new(ResultKind.Failed, "delayed owned refusal"));
                WpfTestDispatcher.Complete(w.OperationTask);
                Require(requests == 1 && p.Calls == 1 && Native.GetForegroundWindow() == new WindowInteropHelper(other).Handle && !w.IsVisible && w.Controller.Operation?.State == "Failed", "Delayed request retried, refocused or lost result");
            }
            finally
            {
                other.Close();
                w.Close();
            }
        });
        test("premium cleanup prevents late notification request", () =>
        {
            var completion = new TaskCompletionSource<ActionResult>();
            var p = new TestPlatform
            {
                Next = completion.Task
            };
            var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
            var requests = 0;
            w.ResultNotificationRequest = _ => requests++;
            Dispatch(w, "taskview");
            w.Close();
            var before = w.Controller.Operation;
            completion.SetResult(new(ResultKind.Failed, "late failure"));
            WpfTestDispatcher.Complete(w.OperationTask);
            Require(requests == 0 && w.Controller.Operation == before && p.Calls == 1, "Cleanup allowed late notification or outcome mutation");
        });
        test("premium cancelled app transition immediately enables outcome recovery", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.Page(1);
                var app = w.ActionIds.First(id => id.StartsWith("window:", StringComparison.Ordinal));
                Queue(w, app);
                Require(w.TransitionPending, "App switch not queued");
                w.Back();
                Require(!w.TransitionPending && w.Controller.Operation?.State == "Cancelled", "App cancellation was lost");
                Require(((Button)w.FindName("OperationDetailsButton")).IsEnabled && ((Button)w.FindName("DismissOperationButton")).IsEnabled, "Cancelled transition left recovery actions disabled");
            }
            finally
            {
                w.Close();
            }
        });
        RunNavigation(test);
    }

    internal static void RunNavigation(Action<string, Action> test)
    {
        _ = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        ThemeSupport.FollowSystem(Application.Current!);
        test("premium tab destinations remembered selections and subpage return remain stable", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.Page(2);
                Require(w.SelectedAction == "display", "System destination overwritten: " + w.SelectedAction);
                Choose(w, "side");
                w.Page(1);
                Require(w.SelectedAction == "refresh-apps", "Apps destination overwritten: " + w.SelectedAction);
                var app = w.ActionIds.First(id => id.StartsWith("window:", StringComparison.Ordinal));
                Choose(w, app);
                w.Page(0);
                Require(w.SelectedAction == "session-card", "Home destination overwritten");
                Choose(w, "apps");
                w.Page(2);
                Require(w.SelectedAction == "side", "Remembered system selection lost");
                w.Page(1);
                Require(w.SelectedAction == app, "Remembered app selection lost");
                w.Details();
                w.Back();
                Require(w.SelectedAction == app && w.ActionIds.Contains(app), "Details Back lost app selection");
                w.Page(0);
                Require(w.SelectedAction == "apps", "Remembered home selection lost");
                w.SetRenderState("audio");
                w.Back();
                Require(w.SelectedAction == "apps", "Audio Back lost home selection");
                w.Page(2);
                Require(w.SelectedAction == "side", "System selection lost after subpage");
                w.SetRenderState("integrations");
                w.Back();
                Require(w.SelectedAction == "side", "Integrations Back lost system selection");
            }
            finally
            {
                w.Close();
            }
        });
        foreach (var view in new[]
        {
            "home",
            "apps",
            "system",
            "audio",
            "details",
            "integrations"
        }

        )
        {
            test("premium visible entry navigation stays valid " + view, () =>
            {
                var w = NavigationWindow(view);
                try
                {
                    var count = w.ActionIds.Count;
                    for (var n = 0; n < count * 2; n++)
                    {
                        w.Move(NavDirection.Up);
                        Require(w.ActionIds.Contains(w.SelectedAction), "Up selected absent " + w.SelectedAction);
                        w.Move(NavDirection.Down);
                        Require(w.ActionIds.Contains(w.SelectedAction), "Down selected absent " + w.SelectedAction);
                        w.Sequential(1);
                        Require(w.ActionIds.Contains(w.SelectedAction), "Tab selected absent " + w.SelectedAction);
                    }
                }
                finally
                {
                    w.Close();
                }
            });
            test("premium result Back restores originating view " + view, () =>
            {
                var w = NavigationWindow(view);
                try
                {
                    var title = ((TextBlock)w.FindName("PageTitle")).Text;
                    Choose(w, "operation-details");
                    w.ActivateEntry();
                    w.Back();
                    Require(((TextBlock)w.FindName("PageTitle")).Text == title && w.SelectedAction == "operation-details" && w.ActionIds.Contains(w.SelectedAction), "Result Back lost originating context or selection");
                }
                finally
                {
                    w.Close();
                }
            });
            test("premium direct dismissal preserves visible context " + view, () =>
            {
                var w = NavigationWindow(view);
                try
                {
                    var title = ((TextBlock)w.FindName("PageTitle")).Text;
                    ((Button)w.FindName("DismissOperationButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Require(w.Controller.Operation is null && ((TextBlock)w.FindName("PageTitle")).Text == title && w.ActionIds.Contains(w.SelectedAction), "Direct dismissal selected absent " + w.SelectedAction + " or lost context");
                }
                finally
                {
                    w.Close();
                }
            });
            test("premium dirty footer Up uses actual visible entries " + view, () =>
            {
                var w = NavigationWindow(view);
                try
                {
                    w.Controller.DismissOperation();
                    w.RefreshRows();
                    Choose(w, "save-pending");
                    w.Move(NavDirection.Up);
                    Require(w.ActionIds.Contains(w.SelectedAction), "Dirty footer selected absent " + w.SelectedAction);
                }
                finally
                {
                    w.Close();
                }
            });
        }
    }

    private static MainWindow NavigationWindow(string view)
    {
        var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
        w.SetRenderState(view);
        w.Controller.Change(w.Controller.Settings with
        {
            ReducedMotion = true
        });
        w.Controller.SetOperation("Failed", "Navigation fixture");
        w.RefreshRows();
        return w;
    }

    private static void Choose(MainWindow w, string id)
    {
        for (var n = 0; n < w.ActionIds.Count && w.SelectedAction != id; n++)
            w.Sequential(1);
        Require(w.SelectedAction == id, "Cannot select " + id);
    }

    internal static void LocalDismissalRegression(Action<string, Action> test)
    {
        _ = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        ThemeSupport.FollowSystem(Application.Current!);
        test("premium local dismissal terminalizes replaced undispatched action", () =>
        {
            var p = new TestPlatform();
            var w = WpfTestHost.CreatePreviewWindow(p, TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.ShowDiagnostic();
                RequireOwnedFocus(w);
                Queue(w, "taskview");
                w.Toggle();
                Console.WriteLine($"LOCAL-DISMISS visible={w.IsVisible}; queued={w.TransitionPending}; calls={p.Calls}; operation={w.Controller.Operation}");
                Require(p.Calls == 0 && w.TransitionPending, "Local dismissal handoff was lost or effect dispatched");
                Require(w.Controller.Operation is { State: "Cancelled", Dispatched: false }, "Replaced undispatched operation remains " + w.Controller.Operation?.State);
            }
            finally
            {
                w.Close();
            }
        });
    }

    internal static void ReaderEvidence(Action<string, Action> test, string output)
    {
        var app = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        ThemeSupport.ForceForValidation(app, false);
        Directory.CreateDirectory(output);
        test("premium actual reader route exposes final recovery sentence and dismissal", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                const string ending = "No additional command was sent.";
                var paragraph = "The window identity changed during the request. Open Apps, select the window again, and check for a save dialog before trying again. ";
                w.Controller.SetOperation("Unconfirmed", string.Concat(Enumerable.Repeat(paragraph, 8)) + ending);
                w.RefreshRows();
                w.Width = 360;
                w.Height = 480;
                w.Left = -12000;
                w.Top = -12000;
                w.ShowDiagnostic();
                RequireOwnedFocus(w);
                Choose(w, "operation-details");
                w.ActivateEntry();
                Require(w.SelectedAction == "operation-text", "Actual result entry did not open reader route");
                var activatedReader = WpfTestElements.Descendants<TextBox>((DependencyObject)w.FindName("Rows")).Single();
                w.ActivateEntry();
                Console.WriteLine($"READER-ACTIVATED focus={activatedReader.IsKeyboardFocusWithin}; selected={w.SelectedAction}; actualType={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name}");
                w.UpdateLayout();
                Console.WriteLine($"READER-AFTER-LAYOUT focus={activatedReader.IsKeyboardFocusWithin}; sameControl={ReferenceEquals(activatedReader, WpfTestElements.Descendants<TextBox>((DependencyObject)w.FindName("Rows")).Single())}; selected={w.SelectedAction}; actualType={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name}");
                var reader = WpfTestElements.Descendants<TextBox>((DependencyObject)w.FindName("Rows")).Single();
                Require(reader.IsKeyboardFocusWithin, "Actual reader activation did not focus its text control");
                var initial = reader.VerticalOffset;
                var moves = 0;
                for (; moves < 600 && reader.VerticalOffset + reader.ViewportHeight < reader.ExtentHeight - .5; moves++)
                {
                    w.Move(NavDirection.Down);
                    w.UpdateLayout();
                }

                Require(moves > 0 && moves < 600 && reader.VerticalOffset > initial, "Actual controller route did not scroll to reader end");
                var root = (FrameworkElement)w.Content;
                var start = reader.GetRectFromCharacterIndex(reader.Text.LastIndexOf(ending, StringComparison.Ordinal));
                var end = reader.GetRectFromCharacterIndex(reader.Text.Length - 1, true);
                Require(!start.IsEmpty && !end.IsEmpty, "Ending text has no visible geometry");
                var sentence = reader.TransformToAncestor(root).TransformBounds(Rect.Union(start, end));
                var inner = (ScrollViewer)reader.Template.FindName("PART_ContentHost", reader);
                var innerBounds = inner.TransformToAncestor(root).TransformBounds(new Rect(new Size(inner.ActualWidth, inner.ActualHeight)));
                var outer = (ScrollViewer)w.FindName("ContentScroll");
                var outerBounds = outer.TransformToAncestor(root).TransformBounds(new Rect(new Size(outer.ActualWidth, outer.ActualHeight)));
                Require(sentence.Top >= innerBounds.Top - .5 && sentence.Bottom <= innerBounds.Bottom + .5 && sentence.Left >= innerBounds.Left - .5 && sentence.Right <= innerBounds.Right + .5, "Final sentence clipped inside reader: " + sentence + " / " + innerBounds);
                Require(sentence.Top >= outerBounds.Top - .5 && sentence.Bottom <= outerBounds.Bottom + .5 && sentence.Left >= outerBounds.Left - .5 && sentence.Right <= outerBounds.Right + .5, "Final sentence clipped by outer viewport: " + sentence + " / " + outerBounds);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(output, "reader-ending-visible.png")))
                    encoder.Save(stream);
                Console.WriteLine($"READER-EVIDENCE focused={reader.IsKeyboardFocusWithin}; ownedForeground={Native.GetForegroundWindow() == new WindowInteropHelper(w).Handle}; windowLeft={w.Left}; windowTop={w.Top}; moves={moves}; initialOffset={initial}; finalOffset={reader.VerticalOffset}; viewport={reader.ViewportHeight}; extent={reader.ExtentHeight}; sentence={sentence}; inner={innerBounds}; outer={outerBounds}; ending={ending}");
                w.Move(NavDirection.Right);
                Require(w.SelectedAction == "dismiss-operation" && w.ActionIds.Contains(w.SelectedAction), "Controller could not reach dismissal from focused reader");
                w.ActivateEntry();
                Require(w.Controller.Operation is null && w.ActionIds.Contains(w.SelectedAction), "Reader dismissal failed or lost valid origin selection");
                Console.WriteLine("READER-DISMISS reached=True; cleared=True; selected=" + w.SelectedAction);
            }
            finally
            {
                w.Close();
                ThemeSupport.FollowSystem(app);
            }
        });
        test("premium actual reader keyboard arrows preserve reading and exit to actions", () =>
        {
            var w = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic, activateOnShow: true);
            try
            {
                w.Controller.SetOperation("Partial", string.Concat(Enumerable.Repeat("A long recovery message for reading. ", 20)));
                w.RefreshRows();
                w.Width = 360;
                w.Height = 480;
                w.Left = -12000;
                w.Top = -12000;
                w.ShowDiagnostic();
                RequireOwnedFocus(w);
                Choose(w, "operation-details");
                w.ActivateEntry();
                w.UpdateLayout();
                w.ActivateEntry();
                var reader = WpfTestElements.Descendants<TextBox>((DependencyObject)w.FindName("Rows")).Single();
                Require(reader.IsKeyboardFocusWithin, "PRECONDITION: reader not focused after settled layout");
                void Key(System.Windows.Input.Key key)
                {
                    var source = PresentationSource.FromVisual(reader);
                    var preview = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                    {
                        RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
                    };
                    reader.RaiseEvent(preview);
                    if (!preview.Handled)
                        reader.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
                }

                Key(System.Windows.Input.Key.Down);
                Key(System.Windows.Input.Key.Up);
                Require(reader.IsKeyboardFocusWithin && w.SelectedAction == "operation-text", "Up/Down stole reader focus");
                Key(System.Windows.Input.Key.Right);
                Console.WriteLine($"READER-KEYBOARD rightSelected={w.SelectedAction}; readerFocused={reader.IsKeyboardFocusWithin}");
                Require(w.SelectedAction == "dismiss-operation" && !reader.IsKeyboardFocusWithin, "Keyboard Right did not exit reader to dismissal");
                Choose(w, "operation-text");
                w.ActivateEntry();
                Require(reader.IsKeyboardFocusWithin, "Reader could not reenter");
                Key(System.Windows.Input.Key.Left);
                Require(w.SelectedAction == "operation-text" && !reader.IsKeyboardFocusWithin, "Keyboard Left did not exit reader to its action");
            }
            finally
            {
                w.Close();
                ThemeSupport.FollowSystem(app);
            }
        });
    }

    private static Window OwnedWindow() => new()
    {
        Title = "Bezoro owned premium safety fixture",
        Width = 260,
        Height = 140,
        Left = 80,
        Top = 80,
        ShowInTaskbar = false
    };
    private static void RequireOwnedFocus(Window w) => Require(w.IsVisible && w.IsActive && Native.GetForegroundWindow() == new WindowInteropHelper(w).Handle, "PRECONDITION: owned overlay focus was not acquired");
    private static void TransferOwnedFocus(Window other)
    {
        other.Show();
        other.Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(other).Handle);
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Queue(MainWindow w, string id) => WpfTestElements.Button(w, id).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    private static string Temporary()
    {
        var p = Path.Combine(Path.GetTempPath(), "Bezoro-premium-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static FrameworkElement Detach(MainWindow w)
    {
        var root = (FrameworkElement)w.Content;
        w.Content = null;
        root.Resources.MergedDictionaries.Add(w.Resources);
        root.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, ThemeSupport.Text);
        return root;
    }

    private static void Layout(FrameworkElement root, int width, int height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static void Dispatch(MainWindow w, string id)
    {
        WpfTestElements.Button(w, id).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        w.AdvanceHandoff(new(new PadFrame[4], 0, GuideValid: true), 0);
        w.AdvanceHandoff(new(new PadFrame[4], 100, GuideValid: true), 100);
    }

    internal static void OpenCloseConfirmation(MainWindow window)
    {
        if (!window.ActionIds.Contains("close") && window.Controller.Session.Anchor is { } target)
            window.OpenOptions(target);
        WpfTestElements.Button(window, "close").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }
}

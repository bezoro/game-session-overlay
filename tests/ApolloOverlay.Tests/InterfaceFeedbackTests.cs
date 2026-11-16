using System.Buffers.Binary;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using ApolloOverlay;
using ApolloOverlay.Automation;

internal static class InterfaceFeedbackTests
{
    private static void Require(bool value, string reason)
    {
        if (!value)
            throw new Exception(reason);
    }

    internal static void Run(Action<string, Action> test)
    {
        test("interface deliberate tab actions acknowledge activation once while focus movement stays navigation", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                Click((Button)w.FindName("AppsTab"));
                Require(feedback.Cues.SequenceEqual(new[] { FeedbackCue.Confirmation }), "Tab activation cue missing or duplicated");
                Click((Button)w.FindName("AppsTab"));
                Require(feedback.Cues.Count == 1, "Already selected tab repeated a cue");
                typeof(MainWindow).GetField("dispatchSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(w, 1);
                typeof(MainWindow).GetMethod("NavigatePrimaryTab", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(w, new object[] { 1 });
                Require(feedback.Cues.SequenceEqual(new[] { FeedbackCue.Confirmation, FeedbackCue.Confirmation }) && feedback.Sources.Last().Kind == FeedbackSourceKind.Controller,
                    "Shoulder activation emitted the wrong cue or input source");
                w.Sequential(1);
                Require(feedback.Cues.Last() == FeedbackCue.Navigation, "Focus movement emitted an activation cue");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface header settings button remains actionable with acknowledgement", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                Click((Button)w.FindName("SettingsGear"));
                Require(w.ActionIds.Contains("sound-feedback") && feedback.Cues.SequenceEqual(new[] { FeedbackCue.Confirmation }), "Header settings lost its action or emitted multiple cues");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface active tab returns from a nested page with one activation cue", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.SetRenderState("audio");
                feedback.Cues.Clear();
                Click((Button)w.FindName("HomeTab"));
                Require(w.ActionIds.Contains("session-card") && !w.ActionIds.Contains("volume"), "Active tab trapped the user on a nested page");
                Require(feedback.Cues.SequenceEqual(new[] { FeedbackCue.Confirmation }), "Return to tab has missing or duplicate feedback");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface Back from a primary tab keeps the cancellation cue", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                Click((Button)w.FindName("AppsTab"));
                feedback.Cues.Clear();
                w.Back();
                Require(w.ActionIds.Contains("session-card") && feedback.Cues.SequenceEqual(new[] { FeedbackCue.Cancellation }),
                    "Returning to Session interrupted the cancellation cue");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface Audio tab click retains pointer source and visibly acknowledges activation", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                typeof(MainWindow).GetField("inputMethod", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(w, "controller");
                var tab = (Button)w.FindName("SettingsTab");
                Click(tab);
                Require(w.ActionIds.Contains("volume") && feedback.Cues.SequenceEqual(new[] { FeedbackCue.Confirmation }) && feedback.Sources.Single().Kind == FeedbackSourceKind.Pointer,
                    "Audio tab did not keep its pointer source or emitted the wrong activation cue");
                tab.ApplyTemplate();
                Require(tab.Template.FindName("ActivationFeedback", tab) is Border { Opacity: > 0 }, "Audio tab had no visible acknowledgement");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface Audio automation route remains available without a Session shortcut", () =>
        {
            var w = Fixture(out _);
            try
            {
                Require(!w.ActionIds.Contains("audio") && w.ActionIds.Contains("windows-volume"), "Session audio shortcuts or controls are incorrect");
                var request = new AutomationRequest { ActionId = "route:audio", Revision = w.AutomationSnapshot().Revision };
                var reply = w.AutomationExecute(request, CancellationToken.None, _ => { }, () => true).GetAwaiter().GetResult();
                Require(reply.Outcome == "Completed" && w.ActionIds.Contains("volume") && w.ActionIds.Contains("mute"), "Audio automation route lost its controls");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface keyboard keycaps center visible ink at multiple pixel densities", () =>
        {
            var resources = new Border();
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
                foreach (var key in new[] { "Enter", "Esc", "KeyX", "KeyY", "PgUp", "PgDn", "Up", "Down", "Left", "Right" })
                {
                    var cap = (Border)InputPrompt.CreateGlyph(resources, key, 20);
                    cap.Background = Brushes.White;
                    TextElement.SetForeground(cap, Brushes.Black);
                    TextElement.SetFontFamily(cap, new FontFamily("Segoe UI Variable Text, Segoe UI"));
                    cap.UseLayoutRounding = true;
                    TextOptions.SetTextFormattingMode(cap, TextFormattingMode.Display);
                    VisualTreeHelper.SetRootDpi(cap, new DpiScale(scale, scale));
                    cap.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    cap.Arrange(new Rect(cap.DesiredSize));

                    var width = (int)Math.Ceiling(cap.RenderSize.Width * scale);
                    var height = (int)Math.Ceiling(cap.RenderSize.Height * scale);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(cap);
                    var pixels = new byte[width * height * 4];
                    bitmap.CopyPixels(pixels, width * 4, 0);
                    var left = width;
                    var right = -1;
                    var top = height;
                    var bottom = -1;
                    for (var y = 0; y < height; y++)
                        for (var x = 0; x < width; x++)
                        {
                            var offset = (y * width + x) * 4;
                            if (pixels[offset + 3] != 255 || pixels[offset + 1] >= 180)
                                continue;
                            left = Math.Min(left, x);
                            right = Math.Max(right, x);
                            top = Math.Min(top, y);
                            bottom = Math.Max(bottom, y);
                        }

                    Require(right >= left && bottom >= top, $"{key} rendered no label at {scale:P0}");
                    var offsetX = (left + right + 1 - cap.RenderSize.Width * scale) / 2;
                    var offsetY = (top + bottom + 1 - cap.RenderSize.Height * scale) / 2;
                    Require(Math.Abs(offsetX) <= 1 && Math.Abs(offsetY) <= 1, $"{key} at {scale:P0} is off-center by ({offsetX:F2}, {offsetY:F2}) physical pixels");
                }
        });
        test("interface header Back cancels a pending handoff before dispatch", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.OpenOptions(w.Controller.Session.Anchor!);
                Click(WpfTestElements.Button(w, "minimize"));
                Require(w.TransitionPending, "Fixture did not queue handoff");
                feedback.Cues.Clear();
                Click((Button)w.FindName("BackButton"));
                Require(!w.TransitionPending && w.Controller.Operation is { State: "Cancelled", Dispatched: false }, "Header Back did not cancel before dispatch");
                Require(feedback.Cues.SequenceEqual(new[] { FeedbackCue.Cancellation }), "Header cancellation feedback missing or duplicated");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface self test emits only its sample and explains the request", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.Controller.Change(w.Controller.Settings with
                {
                    SoundFeedbackEnabled = true
                });
                w.Page(2);
                feedback.Cues.Clear();
                Click(WpfTestElements.Button(w, "test-feedback"));
                Require(feedback.Cues.SequenceEqual(new[] { FeedbackCue.Test }), "Confirmation interrupted or duplicated the sample");
                Require(w.Feedback.Contains("Test requested"), "Test has no visible acknowledgement");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface haptics-only pointer test explains that the controller is required", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.Controller.Change(w.Controller.Settings with
                {
                    HapticFeedbackEnabled = true
                });
                w.Page(2);
                feedback.Response = new(false, false, "Prior status must not be presented as a new pulse");
                Click(WpfTestElements.Button(w, "test-feedback"));
                Require(w.Feedback == "Press A on the active controller to test vibration.", "Test falsely claimed a pulse or kept an unrelated status");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface fixed footer actions use the same acknowledgement as rows", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.Page(2);
                w.Controller.Change(w.Controller.Settings with
                {
                    UiScalePercent = 130
                });
                w.RefreshRows();
                typeof(MainWindow).GetField("inputMethod", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(w, "controller");
                feedback.Cues.Clear();
                feedback.Sources.Clear();
                Click((Button)w.FindName("DiscardSettingsButton"));
                Require(!w.Controller.SettingsDirty && feedback.Cues.SequenceEqual(new[] { FeedbackCue.Confirmation }), "Footer discard lost its action or cue");
                Require(feedback.Sources.Single().Kind == FeedbackSourceKind.Pointer, "Pointer source misidentified");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface confirmation navigation and cancel use distinct cues", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.Confirm(w.Controller.Session.Anchor!);
                feedback.Cues.Clear();
                w.Sequential(1);
                Click((Button)w.FindName("CancelCloseButton"));
                Require(!w.Confirming, "Pointer cancel did not dismiss confirmation");
                Require(feedback.Cues.SequenceEqual(new[] { FeedbackCue.Navigation, FeedbackCue.Cancellation }), "Confirmation navigation/cancel silent or duplicated");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface close confirmation vibrates only for a deliberate controller cancellation", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.Left = -12000;
                w.Top = -12000;
                w.ShowActivated = false;
                w.Show();
                var anchor = w.Controller.Session.Anchor!;
                void Tick(PadButtons buttons, PadFrame second) => w.ReviewTick([new(true, buttons), second, default, default]);
                var connected = new PadFrame(true, PadButtons.None);
                Tick(PadButtons.None, connected);
                Tick(PadButtons.Down, connected);
                Tick(PadButtons.None, connected);

                foreach (var second in new[] { default(PadFrame), new PadFrame(false, PadButtons.None, ReadFailed: true) })
                {
                    Tick(PadButtons.None, connected);
                    w.Confirm(anchor);
                    Require(w.Confirming, "Fixture did not enter close confirmation");
                    feedback.Cues.Clear();
                    feedback.Sources.Clear();
                    Tick(PadButtons.None, second);
                    Require(!w.Confirming && w.Feedback.Contains("Closing cancelled"), "Lost controller certainty did not abort confirmation");
                    Require(feedback.Cues.SequenceEqual(new[] { FeedbackCue.Cancellation }) && feedback.Sources.Single().Slot is null,
                        "An automatic cancellation vibrated the remaining controller");
                }

                Tick(PadButtons.None, connected);
                w.Confirm(anchor);
                feedback.Cues.Clear();
                feedback.Sources.Clear();
                Tick(PadButtons.B, connected);
                Require(!w.Confirming && feedback.Cues.SequenceEqual(new[] { FeedbackCue.Cancellation })
                    && feedback.Sources.Single() is { Kind: FeedbackSourceKind.Controller, Slot: 0 },
                    "A deliberate B press did not target the controller that pressed it");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface disabled action reports unavailability without confirmation", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.Page(2);
                for (var i = 0; i < w.ActionIds.Count && w.SelectedAction != "test-feedback"; i++)
                    w.Sequential(1);
                Require(w.SelectedAction == "test-feedback", "Disabled test unreachable");
                feedback.Cues.Clear();
                w.ActivateEntry();
                Require(feedback.Cues.SequenceEqual(new[] { FeedbackCue.Unavailable }) && w.Feedback.StartsWith("Unavailable"), "Unavailable action falsely acknowledged");
                feedback.Cues.Clear();
                w.RefreshRows();
                w.RevealSelection();
                Require(feedback.Cues.Count == 0, "Passive layout refresh made a sound");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface controller confirmation visibly acknowledges a press without animation in reduced motion", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.Controller.Change(w.Controller.Settings with
                {
                    ReducedMotion = true
                });
                w.Page(2);
                for (var i = 0; i < w.ActionIds.Count && w.SelectedAction != "side"; i++)
                    w.Sequential(1);
                Require(w.SelectedAction == "side", "Setting unreachable");
                feedback.Cues.Clear();
                w.ActivateEntry();
                var button = WpfTestElements.Button(w, "side");
                button.ApplyTemplate();
                var highlight = (Border)button.Template.FindName("ActivationFeedback", button);
                Require(highlight.Opacity > 0 && !highlight.HasAnimatedProperties, "Reduced motion suppressed acknowledgement or animated its highlight");
                Require(feedback.Cues.SequenceEqual(new[] { FeedbackCue.Confirmation }), "Activation emitted multiple cues");
                Pump(190);
                Require(highlight.Opacity == 0, "Press acknowledgement remained stuck");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface result vectors and semantic tones distinguish confirmed and uncertain outcomes", () =>
        {
            var w = Fixture(out _);
            try
            {
                foreach (var (state, asset, tone) in new[]
                {
                    ("Pending", "timer", ThemeSupport.Information),
                    ("Requested", "info", ThemeSupport.Information),
                    ("Completed", "check", ThemeSupport.Success),
                    ("Unconfirmed", "info", ThemeSupport.Warning),
                    ("Failed", "close", ThemeSupport.Critical),
                    ("Cancelled", "close", ThemeSupport.Information)
                }

                )
                {
                    w.Controller.SetOperation(state, "Fixture result");
                    w.RefreshRows();
                    var glyph = (VectorIcon)w.FindName("OperationGlyph");
                    Require(ReferenceEquals(glyph.Data, w.FindResource("Icon." + asset)), "Misleading result vector for " + state);
                    Require(glyph.Fill is SolidColorBrush actual && w.FindResource(tone) is SolidColorBrush expected && actual.Color == expected.Color,
                        "Result uses the wrong semantic tone for " + state);
                    Require(((TextBlock)w.FindName("OperationLabel")).Text.Contains(state)
                        && AutomationProperties.GetItemStatus((DependencyObject)w.FindName("OperationPanel")) == state,
                        "Result depends solely on color or geometry without visible and accessible state");
                }
            }
            finally
            {
                w.Close();
            }
        });
        test("interface Resume acknowledges the controller before waiting for release and dispatch", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                Require(w.SelectedAction == "session-card" && w.Controller.Session.CardTarget is { } target && w.Controller.CanResumeCard(target),
                    "Fixture has no resumable card");
                var now = Environment.TickCount64;
                var held = new PadFrame[] { new(true, PadButtons.A), default, default, default };
                var neutral = new PadFrame[] { new(true, PadButtons.None), default, default, default };
                w.HandoffSampleFixture = new(held, now);
                typeof(MainWindow).GetField("dispatchSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(w, 1);
                w.ActivateEntry();

                var card = (Button)w.FindName("SessionCardButton");
                card.ApplyTemplate();
                Require(card.Template.FindName("ActivationFeedback", card) is Border { Opacity: > 0 }
                    && feedback.Cues.SequenceEqual(new[] { FeedbackCue.Confirmation }) && feedback.Sources.Single().Kind == FeedbackSourceKind.Controller,
                    "Resume did not acknowledge the controller before the handoff");
                w.AdvanceHandoff(new(held, now + 20), now + 20);
                w.AdvanceHandoff(new(neutral, now + 30), now + 30);
                w.AdvanceHandoff(new(neutral, now + 129), now + 129);
                Require(w.TransitionPending && w.Controller.Operation is { State: "Pending", Dispatched: false } && feedback.Cues.Count == 1,
                    "Resume dispatched before 100 ms of neutral input or emitted a premature result cue");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface only confirmed completion receives the completion sound", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                var record = typeof(MainWindow).GetMethod("RecordResult", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                foreach (var (kind, cue) in new[]
                {
                    (ResultKind.Requested, (FeedbackCue? )null),
                    (ResultKind.Completed, FeedbackCue.Completion),
                    (ResultKind.Unknown, FeedbackCue.Unavailable),
                    (ResultKind.Partial, FeedbackCue.Unavailable),
                    (ResultKind.Failed, FeedbackCue.Unavailable)
                }

                )
                {
                    feedback.Cues.Clear();
                    record.Invoke(w, new object?[] { new ActionResult(kind, "Fixture result"), "audio", null });
                    Require(cue is null ? feedback.Cues.Count == 0 : feedback.Cues.SequenceEqual(new[] { cue.Value }), "Misleading outcome sound for " + kind);
                }
            }
            finally
            {
                w.Close();
            }
        });
        test("interface delayed outcomes do not vibrate the current controller without the original input", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(MainWindow).GetField("inputMethod", flags)!.SetValue(w, "controller");
                typeof(MainWindow).GetField("activeSlot", flags)!.SetValue(w, (uint?)1);
                var record = typeof(MainWindow).GetMethod("RecordResult", flags)!;
                record.Invoke(w, new object?[] { new ActionResult(ResultKind.Completed, "Fixture result"), "audio", null });
                Require(feedback.Cues.SequenceEqual(new[] { FeedbackCue.Completion }) && feedback.Sources.Single().Slot is null,
                    "An unattributed result selected the current controller for vibration");

                typeof(MainWindow).GetField("dispatchSource", flags)!.SetValue(w, 2);
                record.Invoke(w, new object?[] { new ActionResult(ResultKind.Failed, "Fixture result"), "audio", null });
                Require(feedback.Cues.Last() == FeedbackCue.Unavailable && feedback.Sources.Last() is { Kind: FeedbackSourceKind.Controller, Slot: 1 },
                    "A result still attributable to the controller lost its vibration source");
            }
            finally
            {
                w.Close();
            }
        });
        test("interface sounds are distinct bounded PCM with silent boundaries and no clipping", () =>
        {
            var fingerprints = new HashSet<string>();
            foreach (var cue in Enum.GetValues<FeedbackCue>())
            {
                var bytes = FeedbackTone.Wave(cue);
                Require(System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE", "Invalid WAV");
                Require(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) == bytes.Length - 8 && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)) == bytes.Length - 44, "Invalid PCM lengths");
                Require(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)) == FeedbackTone.SampleRate, "Wrong sample rate");
                var pcm = new short[(bytes.Length - 44) / 2];
                for (var i = 0; i < pcm.Length; i++)
                    pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + i * 2));
                var duration = pcm.Length * 1000d / FeedbackTone.SampleRate;
                Require(duration >= 20 && duration <= (cue == FeedbackCue.Test ? 350 : 110), "Cue too long or inaudibly short");
                Require(pcm[0] == 0 && pcm[^1] == 0 && pcm.Max(v => Math.Abs((int)v)) is > 500 and < 2500, "Click boundary, silence or excessive peak");
                Require(fingerprints.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))), "Cues are identical");
            }
        });
    }

    private static MainWindow Fixture(out RecordingFeedback feedback)
    {
        var app = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        if (app.TryFindResource(ThemeSupport.Card) is null)
            ThemeSupport.ForceForValidation(app, false);
        feedback = new();
        var w = new MainWindow(renderOnly: true, platform: new PreviewPlatform(new(101, 42, 100, "Fixture game", "fixture")), feedback: feedback);
        w.InputContextReader = () => new(true, true, true, true);
        w.PhysicalKeyReader = _ => 0;
        w.Controller.CaptureReturn();
        WpfTestDispatcher.Complete(w.Controller.Refresh());
        w.RefreshRows();
        return w;
    }

    private static void Pump(int milliseconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var stop = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        stop.Tick += (_, _) =>
        {
            stop.Stop();
            frame.Continue = false;
        };
        stop.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private sealed class RecordingFeedback : IFeedbackService
    {
        internal readonly List<FeedbackCue> Cues = [];
        internal FeedbackDispatch Response = new(true, false, "Fixture requested");
        internal readonly List<FeedbackSource> Sources = [];
        public event Action<FeedbackStatus>? Changed
        {
            add
            {
            }
            remove
            {
            }
        }

        public FeedbackStatus Status => new(true, false, "Fixture feedback");

        public void Configure(bool sound, bool haptics)
        {
        }

        public void SetContext(FeedbackContext context)
        {
        }

        public FeedbackDispatch Request(FeedbackCue cue, FeedbackSource source)
        {
            Cues.Add(cue);
            Sources.Add(source);
            return Response;
        }

        public void Suspend()
        {
        }

        public void Dispose()
        {
        }
    }
}

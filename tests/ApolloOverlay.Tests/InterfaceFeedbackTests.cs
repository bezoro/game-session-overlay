using System.Buffers.Binary;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ApolloOverlay;

internal static class InterfaceFeedbackTests
{
    private static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }

    internal static void Run(Action<string, Action> test)
    {
        test("interface tab click and shoulder navigation each emit one cue", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                Click((Button)w.FindName("AppsTab"));
                Require(feedback.Cues.Count == 1 && feedback.Cues[0] == FeedbackCue.Navigation, "Tab cue missing or duplicated");
                Click((Button)w.FindName("AppsTab"));
                Require(feedback.Cues.Count == 1, "Already selected tab repeated a cue");
                w.Page(2);
                Require(feedback.Cues.Count == 2 && feedback.Cues[1] == FeedbackCue.Navigation, "Shoulder/key tab cue missing");
            }
            finally { w.Close(); }
        });
        test("interface header settings button remains actionable with acknowledgement", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                Click((Button)w.FindName("SettingsGear"));
                Require(w.ActionIds.Contains("sound-feedback") && feedback.Cues.SequenceEqual(new[] { FeedbackCue.Confirmation }), "Header settings lost its action or emitted multiple cues");
            }
            finally { w.Close(); }
        });
        test("interface self test emits only its sample and explains the request", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.Controller.Change(w.Controller.Settings with { SoundFeedbackEnabled = true });
                w.Page(2); feedback.Cues.Clear();
                Click(ReviewFixTests.Button(w, "test-feedback"));
                Require(feedback.Cues.SequenceEqual(new[] { FeedbackCue.Test }), "Confirmation interrupted or duplicated the sample");
                Require(w.Feedback.Contains("Teste solicitado"), "Test has no visible acknowledgement");
            }
            finally { w.Close(); }
        });
        test("interface haptics-only pointer test explains that the controller is required", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.Controller.Change(w.Controller.Settings with { HapticFeedbackEnabled = true }); w.Page(2);
                feedback.Response = new(false, false, "Prior status must not be presented as a new pulse");
                Click(ReviewFixTests.Button(w, "test-feedback"));
                Require(w.Feedback == "Use A no controle ativo para testar a vibração.", "Test falsely claimed a pulse or kept an unrelated status");
            }
            finally { w.Close(); }
        });
        test("interface fixed footer actions use the same acknowledgement as rows", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.Page(2);
                w.Controller.Change(w.Controller.Settings with { UiScalePercent = 130 }); w.RefreshRows();
                typeof(MainWindow).GetField("inputMethod", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(w, "controller");
                feedback.Cues.Clear(); feedback.Sources.Clear();
                Click((Button)w.FindName("DiscardSettingsButton"));
                Require(!w.Controller.SettingsDirty && feedback.Cues.SequenceEqual(new[] { FeedbackCue.Confirmation }), "Footer discard lost its action or cue");
                Require(feedback.Sources.Single().Kind == FeedbackSourceKind.Pointer, "Pointer source misidentified");
            }
            finally { w.Close(); }
        });
        test("interface confirmation navigation and cancel use distinct cues", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.Confirm(w.Controller.Session.Anchor!); feedback.Cues.Clear();
                w.Sequential(1);
                Click((Button)w.FindName("CancelCloseButton"));
                Require(!w.Confirming, "Pointer cancel did not dismiss confirmation");
                Require(feedback.Cues.SequenceEqual(new[] { FeedbackCue.Navigation, FeedbackCue.Cancellation }), "Confirmation navigation/cancel silent or duplicated");
            }
            finally { w.Close(); }
        });
        test("interface disabled action reports unavailability without confirmation", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.Page(2);
                for (var i = 0; i < w.ActionIds.Count && w.SelectedAction != "test-feedback"; i++) w.Sequential(1);
                Require(w.SelectedAction == "test-feedback", "Disabled test unreachable");
                feedback.Cues.Clear(); w.ActivateEntry();
                Require(feedback.Cues.SequenceEqual(new[] { FeedbackCue.Unavailable }) && w.Feedback.StartsWith("Indisponível"), "Unavailable action falsely acknowledged");
                feedback.Cues.Clear(); w.RefreshRows(); w.RevealSelection();
                Require(feedback.Cues.Count == 0, "Passive layout refresh made a sound");
            }
            finally { w.Close(); }
        });
        test("interface controller confirmation visibly acknowledges a press with reduced motion", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                w.Controller.Change(w.Controller.Settings with { ReducedMotion = true }); w.Page(2);
                for (var i = 0; i < w.ActionIds.Count && w.SelectedAction != "side"; i++) w.Sequential(1);
                Require(w.SelectedAction == "side", "Setting unreachable");
                feedback.Cues.Clear(); w.ActivateEntry();
                var button = ReviewFixTests.Button(w, "side"); button.ApplyTemplate();
                var highlight = (Border)button.Template.FindName("ActivationFeedback", button);
                Require(highlight.Opacity > 0 && !highlight.HasAnimatedProperties, "Reduced-motion press invisible or animated");
                Require(feedback.Cues.SequenceEqual(new[] { FeedbackCue.Confirmation }), "Activation emitted multiple cues");
                Pump(190);
                Require(highlight.Opacity == 0, "Press acknowledgement remained stuck");
            }
            finally { w.Close(); }
        });
        test("interface result symbols distinguish requested completed and uncertain states", () =>
        {
            var w = Fixture(out _);
            try
            {
                foreach (var (state, symbol) in new[] { ("Pendente", "…"), ("Solicitado", "i"), ("Concluído", "✓"), ("Não confirmado", "!"), ("Falhou", "!"), ("Cancelado", "×") })
                {
                    w.Controller.SetOperation(state, "Fixture result"); w.RefreshRows();
                    Require(((TextBlock)w.FindName("OperationGlyph")).Text == symbol, "Misleading result symbol for " + state);
                    Require(((TextBlock)w.FindName("OperationLabel")).Text.Contains(state), "Result depends solely on color/symbol");
                }
            }
            finally { w.Close(); }
        });
        test("interface only confirmed completion receives the completion sound", () =>
        {
            var w = Fixture(out var feedback);
            try
            {
                var record = typeof(MainWindow).GetMethod("RecordResult", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                foreach (var (kind, cue) in new[] { (ResultKind.Requested, (FeedbackCue?)null), (ResultKind.Completed, FeedbackCue.Completion), (ResultKind.Unknown, FeedbackCue.Unavailable), (ResultKind.Partial, FeedbackCue.Unavailable), (ResultKind.Failed, FeedbackCue.Unavailable) })
                {
                    feedback.Cues.Clear();
                    record.Invoke(w, new object?[] { new ActionResult(kind, "Fixture result"), "audio", null });
                    Require(cue is null ? feedback.Cues.Count == 0 : feedback.Cues.SequenceEqual(new[] { cue.Value }), "Misleading outcome sound for " + kind);
                }
            }
            finally { w.Close(); }
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
                for (var i = 0; i < pcm.Length; i++) pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + i * 2));
                var duration = pcm.Length * 1000d / FeedbackTone.SampleRate;
                Require(duration >= 20 && duration <= (cue == FeedbackCue.Test ? 350 : 110), "Cue too long or inaudibly short");
                Require(pcm[0] == 0 && pcm[^1] == 0 && pcm.Max(v => Math.Abs((int)v)) is > 500 and < 2500, "Click boundary, silence or excessive peak");
                Require(fingerprints.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))), "Cues are identical");
            }
        });
    }

    private static MainWindow Fixture(out RecordingFeedback feedback)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.TryFindResource(ThemeSupport.Card) is null) ThemeSupport.ForceForValidation(app, false);
        feedback = new();
        var w = new MainWindow(renderOnly: true, feedback: feedback);
        w.InputContextReader = () => new(true, true, true, true); w.PhysicalKeyReader = _ => 0;
        w.Controller.CaptureReturn(); ReviewFixTests.Complete(w.Controller.Refresh()); w.RefreshRows();
        return w;
    }
    private static void Pump(int milliseconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var stop = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
        stop.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private sealed class RecordingFeedback : IFeedbackService
    {
        internal readonly List<FeedbackCue> Cues = [];
        internal FeedbackDispatch Response = new(true, false, "Fixture requested");
        internal readonly List<FeedbackSource> Sources = [];
        public event Action<FeedbackStatus>? Changed { add { } remove { } }
        public FeedbackStatus Status => new(true, false, "Fixture feedback");
        public void Configure(bool sound, bool haptics) { }
        public void SetContext(FeedbackContext context) { }
        public FeedbackDispatch Request(FeedbackCue cue, FeedbackSource source) { Cues.Add(cue); Sources.Add(source); return Response; }
        public void Suspend() { }
        public void Dispose() { }
    }
}

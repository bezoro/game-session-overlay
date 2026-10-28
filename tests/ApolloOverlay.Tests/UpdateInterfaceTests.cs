using System.Net.Http;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ApolloOverlay;

internal static class UpdateInterfaceTests
{
    private static readonly UpdateCandidate Next = new(42, "0.2.0-preview.42.1+1111111", "ui-fixture-next",
        new string('1', 40), "testing", DateTimeOffset.Parse("2026-10-28T12:00:00Z"),
        "Alterações da versão de teste.\nTexto literal: <script>não executar</script>.\n" + string.Concat(Enumerable.Repeat("Notas completas para leitura e rolagem. ", 30)),
        201, "fixture.zip", 1234, new string('a', 64));

    internal static void Capture(string output)
    {
        Directory.CreateDirectory(output);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var theme in new[] { "dark", "light" })
        foreach (var (width, height) in new[] { (572, 940), (360, 480) })
        foreach (var state in new[] { "overview", "available", "review", "ready", "error" })
        {
            ThemeSupport.ForceForValidation(app, theme == "light");
            using var fixture = new Host();
            var window = fixture.Window;
            try
            {
                switch (state)
                {
                    case "overview": fixture.Open(); break;
                    case "available":
                        fixture.Open(); Click(window, "check-updates"); Complete(window.UpdateTask);
                        Pick(window, "release:" + Next.BuildId); break;
                    case "review": fixture.Review(); break;
                    case "ready":
                        fixture.Review(); Click(window, "prepare-update"); Complete(window.UpdateTask); break;
                    case "error":
                        fixture.Source.CheckFailure = new UnauthorizedAccessException("Fixture server refused");
                        fixture.Open(); Click(window, "check-updates"); Complete(window.UpdateTask); break;
                }
                var root = (FrameworkElement)window.Content;
                window.Content = null;
                root.Resources.MergedDictionaries.Add(window.Resources);
                root.SetResourceReference(TextElement.ForegroundProperty, ThemeSupport.Text);
                root.SetValue(TextElement.FontFamilyProperty, window.FontFamily);
                root.SetValue(TextElement.FontSizeProperty, window.FontSize);
                var host = new Border { Child = root };
                host.Measure(new Size(width, height));
                host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
                var scroll = (ScrollViewer)window.FindName("ContentScroll");
                Require(scroll.ViewportHeight > 0, "Update page has no content viewport");
                scroll.ScrollToEnd(); host.UpdateLayout();
                Require(scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 1, "Update actions cannot be reached by scrolling");
                window.RevealSelection(); host.UpdateLayout();
                var footer = (TextBlock)window.FindName("ModeLabel");
                var bounds = footer.TransformToAncestor(root).TransformBounds(new Rect(new Size(footer.ActualWidth, footer.ActualHeight)));
                Require(bounds.Top >= 0 && bounds.Bottom <= height + 1, "Update page clips essential controller instructions");
                Require(!window.IsVisible, "Capture acquired a real foreground window");
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var name = $"{theme}-{width}x{height}-update-{state}.png";
                using (var file = File.Create(Path.Combine(output, name))) encoder.Save(file);
                Console.WriteLine($"CAPTURE {name}; footer={bounds}; viewport={scroll.ViewportHeight}; extent={scroll.ExtentHeight}; offset={scroll.VerticalOffset}; selected={window.SelectedAction}; fixture=True; offscreen=True");
            }
            finally { ThemeSupport.FollowSystem(app); }
        }
    }

    internal static void Run(Action<string, Action> test)
    {
        test("updates UI identifies its build without making a passive network request", () =>
        {
            using var host = new Host(); var w = host.Window;
            Require(((TextBlock)w.FindName("HeaderIdentity")).Text.Contains(AppBuild.Current.Display), "Header hides compiled version identity");
            Require(((TextBlock)w.FindName("HeaderIdentity")).ToolTip?.ToString()?.Contains(AppBuild.Current.BuildId) == true, "Header has no build identity");
            w.Page(2); w.RefreshRows();
            Require(w.ActionIds.Contains("updates") && host.Source.Checks == 0, "Settings lacks update entry or passively polls network");
            Click(w, "updates"); w.RefreshRows();
            Require(w.ActionIds.Contains("check-updates") && host.Source.Checks == 0, "Entering version page queried the server");
            Require(Text(w).Contains(AppBuild.Current.Display) && Text(w).Contains(AppBuild.Current.BuildId), "Update page lacks installed identity");
        });
        test("updates UI queries explicitly and only downloading can prepare a candidate", () =>
        {
            using var host = new Host(); var w = host.Window;
            host.Open(); Click(w, "check-updates"); Complete(w.UpdateTask);
            Require(host.Source.Checks == 1 && host.Source.Stages == 0 && w.ActionIds.Contains("release:" + Next.BuildId), "Catalog query downloaded or lost the release");
            Require(w.Feedback.Contains("Há versões disponíveis"), "Footer retained an in-progress message after the query completed");
            Click(w, "release:" + Next.BuildId);
            Require(host.Source.Stages == 0 && w.ActionIds.Contains("prepare-update") && !w.ActionIds.Contains("apply-update"), "Review changed the installation or skipped download");
            Require(Text(w).Contains(Next.Version) && Text(w).Contains(Next.Commit[..7]), "Review does not identify the proposed version");
        });
        test("updates UI access failure cannot claim the installed build is current", () =>
        {
            using var host = new Host(); var w = host.Window;
            host.Source.CheckFailure = new UnauthorizedAccessException("Fixture server refused");
            host.Open(); Click(w, "check-updates"); Complete(w.UpdateTask);
            Require(w.Feedback.Contains("Não foi possível buscar atualizações") && !w.Feedback.Contains("mais recente"), "Authentication refusal became an up-to-date result");
            Require(w.Controller.Operation is { State: "Não confirmado", Action: "update" } && !w.ActionIds.Any(id => id.StartsWith("release:")), "Failure has no visible result or offers an unverified release");
            Require(w.IsLoaded == false && host.Source.Stages == 0, "Failed check initiated UI activation or installation");
        });
        test("updates UI distinguishes an empty channel from a confirmed current version", () =>
        {
            using var host = new Host(); var w = host.Window; host.Source.Releases = [];
            host.Open(); Click(w, "check-updates"); Complete(w.UpdateTask);
            Require(Text(w).Contains("Nenhuma versão publicada") && !Text(w).Contains("mais recente"), "Empty release feed falsely confirmed freshness");
            Require(!w.ActionIds.Any(id => id.StartsWith("release:")) && w.ActionIds.Contains("check-updates"), "Empty feed broke recovery action");
        });
        test("updates UI Back cancels a pending query and remains usable", () =>
        {
            using var host = new Host(); var w = host.Window;
            host.Source.CheckCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously); host.Source.ObserveCancellation = true;
            host.Open(); Click(w, "check-updates"); var pending = w.UpdateTask;
            Require(!pending.IsCompleted && host.Source.Checks == 1, "Query fixture did not suspend");
            w.Back(); Require(host.Source.LastCheckCancellation.IsCancellationRequested, "Back did not cancel query");
            Complete(pending);
            Require(Text(w).Contains("Busca cancelada") && w.Feedback.Contains("Busca cancelada") && !w.ActionIds.Any(id => id.StartsWith("release:")), "Cancelled query produced candidates or lost terminal cancellation feedback");
            w.Back(); Require(w.ActionIds.Contains("updates"), "Back could not leave the cancelled query page");
        });
        test("updates UI ignores a late query success after cancellation", () =>
        {
            using var host = new Host(); var w = host.Window;
            host.Source.CheckCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Open(); Click(w, "check-updates"); var pending = w.UpdateTask;
            w.Back(); host.Source.CheckCompletion.SetResult([Next]); Complete(pending);
            Require(!w.ActionIds.Any(id => id.StartsWith("release:")) && w.Feedback.Contains("Busca cancelada"), "Late query replaced terminal cancellation with success or a pending message");
            Require(host.Source.Stages == 0, "Cancelled query started download");
        });
        test("updates UI channel changes clear the old catalog without polling", () =>
        {
            using var host = new Host(); var w = host.Window;
            host.Open(); Click(w, "check-updates"); Complete(w.UpdateTask);
            var first = host.Source.Channels.Single(); Click(w, "update-channel");
            Require(host.Source.Checks == 1 && !w.ActionIds.Any(id => id.StartsWith("release:")), "Changing channel retained foreign candidates or polled automatically");
            Click(w, "check-updates"); Complete(w.UpdateTask);
            Require(host.Source.Channels.Count == 2 && host.Source.Channels[1] != first, "Next explicit query used the previous channel");
        });
        test("updates UI release notes are read only and keyboard arrows return to actions", () =>
        {
            using var host = new Host(); var w = host.Window;
            host.Review(); w.Left = -12000; w.Top = -12000; w.ShowDiagnostic(); w.UpdateLayout();
            var notes = Notes(w);
            Require(notes.IsReadOnly && notes.Text == Next.Notes, "Release notes are editable or interpreted as markup");
            Click(w, "update-notes");
            Require(notes.IsKeyboardFocusWithin, "PRECONDITION: notes reader did not receive actual WPF keyboard focus");
            var presentation = PresentationSource.FromVisual(notes) ?? throw new Exception("PRECONDITION: notes reader has no presentation source");
            notes.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Require(w.SelectedAction == "prepare-update" && !notes.IsKeyboardFocusWithin && (Keyboard.FocusedElement as FrameworkElement)?.Tag as string == "prepare-update", "Arrow left the user trapped in the read-only notes reader");
            Require(host.Source.Stages == 0, "Reading notes started download");
        });
        test("updates UI completed download requires a separate install action and diagnostics never install", () =>
        {
            using var host = new Host(); var w = host.Window;
            host.Review(); Click(w, "prepare-update"); Complete(w.UpdateTask);
            Require(host.Source.Stages == 1 && host.Source.Staged?.BuildId == Next.BuildId, "Wrong release downloaded");
            Require(w.ActionIds.Contains("apply-update") && !w.ActionIds.Contains("prepare-update") && Text(w).Contains("Download verificado"), "Verified payload has no distinct ready state");
            Require(w.Feedback.Contains("Download verificado"), "Footer retained an in-progress message after download verification");
            Require(!ReviewFixTests.Button(w, "apply-update").IsEnabled && !w.TransitionPending, "Diagnostic fixture allowed native installation");
            Click(w, "apply-update");
            Require(!w.TransitionPending && w.IsLoaded == false && host.Source.Stages == 1, "Disabled install queued a native operation");
        });
        test("updates UI download cancellation retains the review and cannot expose install", () =>
        {
            using var host = new Host(); var w = host.Window;
            host.Source.StageCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously); host.Source.ObserveCancellation = true;
            host.Review(); Click(w, "prepare-update"); var pending = w.UpdateTask;
            Require(!pending.IsCompleted, "Download fixture did not suspend");
            w.Back(); Require(host.Source.LastStageCancellation.IsCancellationRequested, "Back did not cancel download"); Complete(pending);
            Require(w.ActionIds.Contains("prepare-update") && !w.ActionIds.Contains("apply-update") && Text(w).Contains("Download cancelado") && w.Feedback.Contains("Download cancelado"), "Cancelled download exposed install or lost retry/terminal feedback");
            Require(!w.TransitionPending, "Cancelled download queued a restart");
        });
        test("updates UI download refusal cannot produce a verified or installed state", () =>
        {
            using var host = new Host(); var w = host.Window;
            host.Source.StageFailure = new HttpRequestException("Fixture transfer failed");
            host.Review(); Click(w, "prepare-update"); Complete(w.UpdateTask);
            Require(w.Feedback.Contains("Não foi possível preparar") && w.ActionIds.Contains("prepare-update") && !w.ActionIds.Contains("apply-update"), "Download refusal became a prepared installation");
            Require(w.Controller.Operation is { State: "Não confirmado" } && !w.TransitionPending, "Download failure has no honest result");
        });
        test("updates UI query and download preserve unsaved settings and block restart", () =>
        {
            using var host = new Host(); var w = host.Window;
            w.Controller.Change(w.Controller.Settings with { UiScalePercent = 130 }); w.RefreshRows();
            host.Review(); Click(w, "prepare-update"); Complete(w.UpdateTask);
            Require(w.Controller.SettingsDirty && w.Controller.Settings.UiScalePercent == 130, "Updates silently saved or discarded the settings draft");
            Require(!ReviewFixTests.Button(w, "apply-update").IsEnabled && Text(w).Contains("Salve ou descarte"), "Dirty settings did not block restart or explain recovery");
            Require(w.ActionIds.Contains("save-pending") && w.ActionIds.Contains("discard-pending"), "Settings recovery controls disappeared");
        });
        test("updates UI cleanup fences late completions and disposes its source", () =>
        {
            using var host = new Host(); var w = host.Window;
            host.Source.CheckCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Open(); Click(w, "check-updates"); var pending = w.UpdateTask;
            w.Close(); var text = w.Feedback; var actions = w.ActionIds.ToArray();
            host.Source.CheckCompletion.SetResult([Next]); Complete(pending);
            Require(host.Source.Disposed && host.Source.LastCheckCancellation.IsCancellationRequested, "Cleanup retained source or pending network work");
            Require(w.Feedback == text && w.ActionIds.SequenceEqual(actions) && host.Source.Stages == 0, "Late query changed the closed interface or started download");
        });
        test("updates UI preview exposes version while disabling real download and install", () =>
        {
            using var host = new Host(useDefaultSource: true); var w = host.Window;
            host.Open();
            Require(!ReviewFixTests.Button(w, "check-updates").IsEnabled && Text(w).Contains("Setup"), "Preview enabled downloads or omitted the real installation path");
            Require(!w.ActionIds.Any(id => id is "install-overlay" or "apply-update" or "apply-rollback"), "Preview exposed a native installation action");
            Click(w, "check-updates");
            Require(w.UpdateTask.IsCompleted && !w.TransitionPending, "Disabled preview started work");
        });
        test("updates UI marks the current build as installed without offering to reinstall it", () =>
        {
            using var host = new Host(); var w = host.Window;
            host.Source.Releases = [Next with { BuildId = AppBuild.Current.BuildId, Version = AppBuild.Current.Version, Commit = AppBuild.Current.Commit }];
            host.Open(); Click(w, "check-updates"); Complete(w.UpdateTask);
            Require(Text(w).Contains("mais recente") && w.Feedback.Contains("mais recente") && !ReviewFixTests.Button(w, "release:" + AppBuild.Current.BuildId).IsEnabled, "Current version is offered as an update or footer still claims a pending check");
        });
    }

    private static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Complete(Task task) => ReviewFixTests.Complete(task);
    private static void Pick(MainWindow window, string id)
    {
        for (var n = 0; n <= window.ActionIds.Count && window.SelectedAction != id; n++) window.Sequential(1);
        Require(window.SelectedAction == id, "Capture selection is unreachable: " + id);
    }
    private static void Click(MainWindow window, string id) => ReviewFixTests.Button(window, id).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static string Text(MainWindow window) => string.Join("\n", PremiumInterfaceTests.Descendants<TextBlock>((DependencyObject)window.FindName("Rows")).Select(t => t.Text));
    private static TextBox Notes(MainWindow window) => PremiumInterfaceTests.Descendants<TextBox>((DependencyObject)window.FindName("Rows")).Single(t => AutomationProperties.GetName(t).StartsWith("Alterações da versão", StringComparison.Ordinal));

    private sealed class Host : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        internal readonly SourceFake Source = new();
        internal MainWindow Window { get; }
        internal Host(bool useDefaultSource = false)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (app.TryFindResource(ThemeSupport.Card) is null) ThemeSupport.ForceForValidation(app, false);
            Window = new MainWindow(renderOnly: true, platform: new PreviewPlatform(new(101, 42, 100, "Fixture game", "fixture")), updates: useDefaultSource ? null : Source);
            Window.InputContextReader = () => new(true, true, true, true); Window.PhysicalKeyReader = _ => 0;
            Window.Controller.CaptureReturn(); Complete(Window.Controller.Refresh()); Window.RefreshRows();
        }
        internal void Open() { Window.Page(2); Click(Window, "updates"); }
        internal void Review() { Open(); Click(Window, "check-updates"); Complete(Window.UpdateTask); Click(Window, "release:" + Next.BuildId); }
        public void Dispose()
        {
            try { Window.Close(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
    }

    private sealed class SourceFake : IUpdateSource
    {
        internal int Checks, Stages;
        internal bool Disposed, ObserveCancellation;
        internal readonly List<string> Channels = [];
        internal IReadOnlyList<UpdateCandidate> Releases = [Next];
        internal TaskCompletionSource<IReadOnlyList<UpdateCandidate>>? CheckCompletion;
        internal TaskCompletionSource<PreparedUpdate>? StageCompletion;
        internal Exception? CheckFailure, StageFailure;
        internal CancellationToken LastCheckCancellation, LastStageCancellation;
        internal UpdateCandidate? Staged;
        public bool HasCredential => true;
        public Task ConnectAsync(string token, CancellationToken cancellation) => Task.FromException(new Exception("No credential dialog permitted in fixture"));
        public void Disconnect() => throw new Exception("No credential mutation permitted in fixture");
        public Task<IReadOnlyList<UpdateCandidate>> CheckAsync(string channel, CancellationToken cancellation)
        {
            Checks++; Channels.Add(channel); LastCheckCancellation = cancellation;
            if (CheckFailure is {} failure) return Task.FromException<IReadOnlyList<UpdateCandidate>>(failure);
            if (CheckCompletion is {} completion) return ObserveCancellation ? completion.Task.WaitAsync(cancellation) : completion.Task;
            return Task.FromResult(Releases);
        }
        public Task<PreparedUpdate> StageAsync(UpdateCandidate candidate, IProgress<UpdateProgress>? progress, CancellationToken cancellation)
        {
            Stages++; Staged = candidate; LastStageCancellation = cancellation;
            if (StageFailure is {} failure) return Task.FromException<PreparedUpdate>(failure);
            if (StageCompletion is {} completion) return ObserveCancellation ? completion.Task.WaitAsync(cancellation) : completion.Task;
            progress?.Report(new("Baixando atualização", candidate.Size, candidate.Size));
            return Task.FromResult(new PreparedUpdate("fixture-only-never-installed", new(candidate.Version, candidate.BuildId, candidate.Commit, candidate.Channel)));
        }
        public void Dispose() => Disposed = true;
    }
}

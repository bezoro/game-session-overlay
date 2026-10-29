using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApolloOverlay;

internal static class CapturePresentationTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("capture gallery pages real local thumbnails without creating a control for every saved image", () =>
        {
            using var host = new Host(13);
            host.OpenGallery();
            Complete(host.Window.CapturePresentationTask);
            var first = CaptureActions(host.Window);
            Require(first.Length == 6, "The first page did not bound its capture controls");
            Require(first.All(id => Preview(host.Window, id).Source is { IsFrozen: true, PixelWidth: <= 768 }), "A thumbnail was missing or decoded at full screenshot resolution");
            Choose(host.Window, "capture-next");
            Complete(host.Window.CapturePresentationTask);
            var second = CaptureActions(host.Window);
            Require(second.Length == 6 && !second.Intersect(first).Any(), "Next reused or omitted a page of captures");
            Choose(host.Window, "capture-next");
            Complete(host.Window.CapturePresentationTask);
            Require(CaptureActions(host.Window).Length == 1 && !ReviewFixTests.Button(host.Window, "capture-next").IsEnabled, "The final partial page was not bounded");
            Choose(host.Window, "capture-previous");
            Complete(host.Window.CapturePresentationTask);
            Require(CaptureActions(host.Window).SequenceEqual(second), "Previous did not restore the same capture page");
            Require(host.Platform.Calls == 0 && host.Window.IsActive
                && Native.GetForegroundWindow() == new WindowInteropHelper(host.Window).Handle, "Gallery paging sent an external command or lost native focus");
        });

        test("capture decode budget survives rapid page changes and rejects stale image publication", () =>
        {
            using var host = new Host(8);
            var pending = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = 0;
            var maximum = 0;
            var started = 0;
            host.Window.CaptureImageReaderFixture = async (_, _, _) =>
            {
                var current = Interlocked.Increment(ref active);
                Interlocked.Increment(ref started);
                lock (pending)
                    maximum = Math.Max(maximum, current);
                try
                {
                    return await pending.Task;
                }
                finally { Interlocked.Decrement(ref active); }
            };
            try
            {
                host.OpenGallery();
                PumpUntil(() => Volatile.Read(ref started) == 2, "The first thumbnail batch did not begin");
                var old = host.Window.CapturePresentationTask;
                var oldPreview = Preview(host.Window, CaptureActions(host.Window)[0]);
                Choose(host.Window, "capture-next");
                var currentBatch = host.Window.CapturePresentationTask;
                Pump(50);
                Require(Volatile.Read(ref started) == 2, "Superseding a page exceeded the shared decode budget");
                pending.SetResult(TestImage());
                Complete(old);
                Complete(currentBatch);
                Require(maximum == 2 && oldPreview.Source is null, "An old page published its image or overlapping pages escaped the decode limit");
                Require(CaptureActions(host.Window).All(id => Preview(host.Window, id).Source is not null), "The current page did not recover after cancelled decodes finished");
            }
            finally { pending.TrySetResult(TestImage()); }
        });

        test("late capture failures cannot replace a later route's feedback", () =>
        {
            using var host = new Host(1);
            var pending = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = 0;
            host.Window.CaptureImageReaderFixture = (_, _, _) => { Interlocked.Exchange(ref started, 1); return pending.Task; };
            try
            {
                host.OpenGallery();
                PumpUntil(() => Volatile.Read(ref started) == 1, "The delayed image read did not start");
                var old = host.Window.CapturePresentationTask;
                var oldPreview = Preview(host.Window, CaptureActions(host.Window)[0]);
                host.Window.Page(0);
                var feedback = host.Window.Feedback;
                pending.SetException(new IOException("Fixture image disappeared"));
                Complete(old);
                Require(oldPreview.Source is null && !oldPreview.Unavailable && host.Window.Feedback == feedback,
                    "An obsolete gallery failure changed the current route");
            }
            finally { pending.TrySetResult(TestImage()); }
        });

        test("capture detail keeps folder recovery when a real PNG disappears after listing", () =>
        {
            using var host = new Host(1);
            host.OpenGallery();
            Complete(host.Window.CapturePresentationTask);
            var action = CaptureActions(host.Window).Single();
            File.Delete(Path.Combine(host.Captures.DirectoryPath, action["capture:".Length..] + ".png"));
            Choose(host.Window, action);
            Complete(host.Window.CapturePresentationTask);
            var preview = PremiumInterfaceTests.Descendants<CapturePreview>((DependencyObject)host.Window.Content).Single();
            Require(preview.Unavailable && preview.Source is null && ReviewFixTests.Button(host.Window, "capture-folder").IsEnabled,
                "A missing image left a blank detail or removed its folder recovery");
            Require(host.Window.Feedback.Contains("removed") && host.Platform.Calls == 0, "Missing-image recovery dispatched an action or lost its explanation");
        });

        test("compact capture cards retain the full accessible title and visible controller focus", () =>
        {
            const string title = "Night in the northern mountains with a deliberately long session title that should remain complete to assistive technology";
            using var host = new Host(1, title);
            host.OpenGallery(360, 480);
            Complete(host.Window.CapturePresentationTask);
            var id = CaptureActions(host.Window).Single();
            Select(host.Window, id);
            host.Window.UpdateLayout();
            var button = ReviewFixTests.Button(host.Window, id);
            var viewport = (ScrollViewer)host.Window.FindName("ContentScroll");
            var bounds = button.TransformToAncestor(viewport).TransformBounds(new Rect(new Size(button.ActualWidth, button.ActualHeight)));
            Require(new ButtonAutomationPeer(button).GetName() == title, "Thumbnail layout shortened the accessible capture title");
            Require(button.IsKeyboardFocusWithin && bounds.Top >= -3 && bounds.Bottom <= viewport.ViewportHeight + 3,
                "A focused capture card cannot be fully revealed in the compact viewport");
        });
    }

    private static CapturePreview Preview(MainWindow window, string id) => ((CaptureThumbnail)ReviewFixTests.Button(window, id).Content).Preview;
    private static string[] CaptureActions(MainWindow window) => window.ActionIds.Where(id => id.StartsWith("capture:", StringComparison.Ordinal)).ToArray();
    private static BitmapSource TestImage()
    {
        var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        source.Freeze();
        return source;
    }

    private static void Choose(MainWindow window, string id)
    {
        Select(window, id);
        window.ActivateEntry();
        window.UpdateLayout();
    }

    private static void Select(MainWindow window, string id)
    {
        for (var index = 0; index <= window.ActionIds.Count && window.SelectedAction != id; index++)
            window.Sequential(1);
        Require(window.SelectedAction == id, "Gallery action is unreachable: " + id);
        window.UpdateLayout();
    }

    private static void Complete(Task task) => ReviewFixTests.Complete(task);
    private static void Require(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    private static void PumpUntil(Func<bool> ready, string message)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && elapsed.ElapsedMilliseconds < 3000)
            Pump(10);
        Require(ready(), message);
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class Host : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        private readonly string root = Path.Combine(Path.GetTempPath(), "CapturePresentation", Guid.NewGuid().ToString("N"));
        internal PremiumInterfaceTests.FixturePlatform Platform { get; } = new();
        internal MainWindow Window
        {
            get;
        }
        internal ScreenCaptures Captures
        {
            get;
        }

        internal Host(int count, string? title = null)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (application.TryFindResource(ThemeSupport.Card) is null)
                ThemeSupport.ForceForValidation(application, false);
            Captures = new(root, new CaptureBackend());
            for (var index = 0; index < count; index++)
            {
                var saved = Captures.CaptureAsync(new("fixture", 0, 0, 1280, 720, true), title ?? "Local capture " + index, "gallery-test");
                Complete(saved);
                Require(saved.Result.Result.Kind == ResultKind.Completed, "Fixture PNG could not be saved");
            }
            Window = new(renderOnly: true, platform: Platform, diagnostics: new(root));
            Window.PhysicalKeyReader = _ => 0;
            Window.DpiReader = _ => 96;
            Window.Controller.CaptureReturn();
            Complete(Window.Controller.Refresh());
            Window.RefreshRows();
        }

        internal void OpenGallery(int width = 572, int height = 940)
        {
            Window.Width = width;
            Window.Height = height;
            Window.ShowDiagnostic();
            Window.RefreshRows();
            Choose(Window, "session-gallery");
            Complete(Window.FeatureTask);
            Window.UpdateLayout();
            Require(Window.IsActive && Native.GetForegroundWindow() == new WindowInteropHelper(Window).Handle, "PRECONDITION: gallery does not own real foreground");
        }

        public void Dispose()
        {
            try
            {
                Window.Close();
                Complete(Window.CapturePresentationTask);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }
    }

    private sealed class CaptureBackend : IScreenCaptureBackend
    {
        public CapturePixels Capture(DisplayRef display)
        {
            var pixels = new byte[1280 * 720 * 4];
            for (var index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = 80;
                pixels[index + 1] = 120;
                pixels[index + 2] = 180;
                pixels[index + 3] = 255;
            }
            return new(1280, 720, pixels);
        }
    }
}

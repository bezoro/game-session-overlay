using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ApolloOverlay;

internal static class WindowIconCacheTests
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private sealed class IconPlatform : IPlatform, IWindowIconNotifications
    {
        internal readonly WindowRef Target = new(11, 22, 33, "Window", "fixture", Generation: 1);
        private readonly ImageSource icon;
        private bool ready;
        internal IconPlatform() { var image = new DrawingImage(); image.Freeze(); icon = image; }
        public bool IsPreview => true;
        public event Action<WindowRef>? IconAvailable;
        public WindowRef? Foreground() => Target;
        public IReadOnlyList<WindowRef> Windows() => [Target];
        public bool Valid(WindowRef window) => Target.Same(window);
        public ImageSource? Icon(WindowRef window) => ready && Valid(window) ? icon : null;
        public IReadOnlyList<DisplayRef> Displays() => [new("fixture", 0, 0, 1920, 1080, true)];
        public ModuleEvidence SteamEvidence(WindowRef? window) => ModuleEvidence.Unknown;
        public Task<ActionResult> Switch(WindowRef window) => Task.FromResult(new ActionResult(ResultKind.Simulated, "fixture"));
        public Task<ActionResult> Close(WindowRef window, CancellationToken token) => Task.FromResult(new ActionResult(ResultKind.Simulated, "fixture"));
        public Task<ActionResult> Shortcut(WindowRef window, string keys) => Task.FromResult(new ActionResult(ResultKind.Simulated, "fixture"));
        public ActionResult Launch(bool playnite, string? path) => new(ResultKind.Simulated, "fixture");
        internal void Publish() { ready = true; IconAvailable?.Invoke(Target); }
        public void Dispose() { IconAvailable = null; }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    internal static void Run(Action<string, Action> test)
    {
        test("icon completion updates the row without changing selection or replacing its button", () =>
        {
            var platform = new IconPlatform();
            var window = new MainWindow(renderOnly: true, platform: platform)
            {
                Left = -12000,
                Top = -12000,
                InputContextReader = () => new(true, true, true, true),
                PhysicalKeyReader = _ => 0
            };
            try
            {
                window.Controller.CaptureReturn();
                window.Controller.Refresh().GetAwaiter().GetResult();
                window.ShowDiagnostic();
                window.Page(1);
                var id = window.ActionIds.Single(item => item.StartsWith("window:", StringComparison.Ordinal));
                for (var i = 0; i < window.ActionIds.Count && window.SelectedAction != id; i++) window.Sequential(1);
                Require(window.SelectedAction == id, "Fixture did not select the application row");
                var button = ReviewFixTests.Button(window, id);
                var grid = (Grid)button.Content;
                var selected = window.SelectedAction;
                platform.Publish();
                DrainDispatcher();
                Require(window.SelectedAction == selected, "Icon completion moved the selected action");
                Require(ReferenceEquals(button, ReviewFixTests.Button(window, id)), "Icon completion rebuilt the row");
                Require(grid.Children[0] is Image { Source: not null }, "Icon was not applied to the existing row");
            }
            finally { window.Close(); }
        });

        test("icon request is nonblocking and publishes a cached frozen image", () =>
        {
            var image = new DrawingImage();
            image.Freeze();
            var entered = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var notified = new ManualResetEventSlim();
            var target = new WindowRef(11, 22, 33, "Window", "fixture", Generation: 1);
            using var cache = new WindowIconCache(_ => { entered.Set(); release.Wait(5000); return image; });
            cache.Available += w => { if (w.Same(target)) notified.Set(); };
            var timer = Stopwatch.StartNew();
            var result = cache.Get(target);
            timer.Stop();
            try
            {
                Require(result is null && timer.ElapsedMilliseconds < 500, "Request waited for icon loading");
                Require(entered.Wait(2000), "Background icon request did not start");
                Require(cache.Get(target) is null, "Incomplete request produced an icon");
                release.Set();
                Require(notified.Wait(2000), "Available icon did not notify the observer");
                Require(ReferenceEquals(image, cache.Get(target)), "Loaded icon was not cached");
            }
            finally { release.Set(); }
        });

        test("icon identity generation prevents reuse for a replacement window", () =>
        {
            var first = new DrawingImage(); first.Freeze();
            var second = new DrawingImage(); second.Freeze();
            var target = new WindowRef(11, 22, 33, "Window", "fixture", Generation: 1);
            var replacement = target with { Generation = 2 };
            using var ready = new AutoResetEvent(false);
            using var cache = new WindowIconCache(w => w.Generation == 1 ? first : second);
            cache.Available += _ => ready.Set();
            Require(cache.Get(target) is null && ready.WaitOne(2000), "Original icon did not load");
            Require(ReferenceEquals(first, cache.Get(target)), "Original icon was missing");
            Require(cache.Get(replacement) is null, "Reused the old identity's icon");
            Require(ready.WaitOne(2000) && ReferenceEquals(second, cache.Get(replacement)), "Replacement icon did not load independently");
        });

        test("icon result after disposal is discarded and not published", () =>
        {
            var image = new DrawingImage(); image.Freeze();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var completed = new ManualResetEventSlim();
            var target = new WindowRef(11, 22, 33, "Window", "fixture", Generation: 1);
            var notifications = 0;
            var cache = new WindowIconCache(_ => { entered.Set(); release.Wait(5000); completed.Set(); return image; });
            cache.Available += _ => Interlocked.Increment(ref notifications);
            try
            {
                cache.Get(target);
                Require(entered.Wait(2000), "Background icon request did not start");
                cache.Dispose();
                release.Set();
                Require(completed.Wait(2000), "Background icon request did not finish");
                Require(cache.Get(target) is null && Volatile.Read(ref notifications) == 0, "Disposed cache published an icon");
            }
            finally { release.Set(); cache.Dispose(); }
        });
    }
}

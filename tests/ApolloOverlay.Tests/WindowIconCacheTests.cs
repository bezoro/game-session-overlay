using System.Diagnostics;
using System.Windows.Media;
using ApolloOverlay;

internal static class WindowIconCacheTests
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    internal static void Run(Action<string, Action> test)
    {
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

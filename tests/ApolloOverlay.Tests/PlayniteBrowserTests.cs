using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ApolloOverlay;

internal static class PlayniteBrowserTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("Audio primary navigation discards a pending browser route and can start a fresh scan", () =>
        {
            using var host = new SessionInterfaceTests.Host();
            var window = host.Window;
            host.Open();
            var firstScan = new TaskCompletionSource<MainWindow.PlayniteListing>();
            var reads = 0;
            CancellationToken firstToken = default;
            window.PlayniteFolderReaderFixture = (_, token) =>
            {
                reads++;
                if (reads == 1)
                {
                    firstToken = token;
                    return firstScan.Task;
                }
                return Task.FromResult(new MainWindow.PlayniteListing([], null, null, false));
            };
            window.Page(2);
            SessionInterfaceTests.Choose(window, "configure-playnite");
            var pending = window.PlayniteFolderTask;
            Require(reads == 1 && window.ActionIds.Contains("playnite-loading"), "The first scan was not pending");
            WpfTestElements.ClickNamed(window, "SettingsTab");
            Require(firstToken.IsCancellationRequested && window.ActionIds.Contains("volume"), "Audio retained the browser scan or failed to become a primary destination");
            firstScan.SetResult(new([new("Old result", "C:\\old")], null, null, false));
            WpfTestDispatcher.Complete(pending);
            window.Back();
            Require(window.ActionIds.Contains("session-card") && !window.ActionIds.Contains("playnite-loading"), "Back resurrected a discarded browser frame");
            window.Page(2);
            SessionInterfaceTests.Choose(window, "configure-playnite");
            WpfTestDispatcher.Complete(window.PlayniteFolderTask);
            Require(reads == 2 && !window.ActionIds.Contains("playnite-loading") && !window.ActionIds.Contains("playnite-folder:0"), "A cancelled scan prevented fresh browsing or published its old result");
        });
        test("Playnite browser page navigation selects the first folder on the requested page", () =>
        {
            var previous = SynchronizationContext.Current;
            var root = Path.Combine(Path.GetTempPath(), "ApolloOverlayBrowserTests", Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                Directory.CreateDirectory(root);
                for (var index = 0; index < 25; index++)
                    Directory.CreateDirectory(Path.Combine(root, $"Folder{index:00}"));
                window = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic);
                window.Left = -12000;
                window.Top = -12000;
                window.ShowDiagnostic();
                window.Page(2);
                var open = typeof(MainWindow).GetMethod("OpenView", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new Exception("Browser route not available.");
                open.Invoke(window, [new OverlayViewFrame(OverlayRoute.PlayniteBrowser, SelectedId: "playnite-loading", BrowserFolder: root)]);
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (!window.ActionIds.Contains("playnite-page-next") && deadline.ElapsedMilliseconds < 5000)
                {
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                    Dispatcher.PushFrame(frame);
                    Thread.Sleep(1);
                }

                if (!window.ActionIds.Contains("playnite-page-next"))
                    throw new Exception("Folder results did not arrive.");
                WpfTestElements.Button(window, "playnite-page-next").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if (window.SelectedAction != "playnite-folder:20" || !window.ActionIds.Contains("playnite-page-previous"))
                    throw new Exception("Next page did not select its first folder.");
                WpfTestElements.Button(window, "playnite-page-previous").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if (window.SelectedAction != "playnite-folder:0")
                    throw new Exception("Previous page did not select its first folder.");
            }
            finally
            {
                try
                {
                    window?.Close();
                    if (Directory.Exists(root))
                        Directory.Delete(root, true);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }
        });
        test("Playnite browser opens from settings and Back leaves the setting unchanged", () =>
        {
            var window = WpfTestHost.CreatePreviewWindow(TestFocusMode.Synthetic);
            try
            {
                window.Page(2);
                var before = window.Controller.Settings.PlaynitePath;
                WpfTestElements.Button(window, "configure-playnite").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if (!window.ActionIds.Contains("playnite-browser-back") || !window.ActionIds.Contains("playnite-native") || !window.ActionIds.Contains("playnite-loading"))
                    throw new Exception("Controller browse route did not open.");
                window.Back();
                if (!window.ActionIds.Contains("configure-playnite") || window.Controller.Settings.PlaynitePath != before)
                    throw new Exception("Back did not restore settings without changing Playnite.");
            }
            finally
            {
                window.Close();
            }
        });
        test("Playnite browser lists local direct folders and detects its exact executable", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ApolloOverlayBrowserTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "zeta", "nested"));
            Directory.CreateDirectory(Path.Combine(root, "Alpha"));
            var executable = Path.Combine(root, "Playnite.FullscreenApp.exe");
            File.WriteAllText(executable, "");
            try
            {
                var result = MainWindow.ReadPlayniteFolder(root, CancellationToken.None);
                if (result.Error is not null || result.Executable != executable || !result.Folders.Select(f => f.Name).SequenceEqual(new[] { "Alpha", "zeta" }) || result.Truncated)
                    throw new Exception("Unexpected direct folder listing or executable detection.");
                File.Delete(executable);
                File.WriteAllText(Path.Combine(root, "Playnite.DesktopApp.exe"), "");
                if (MainWindow.ReadPlayniteFolder(root, CancellationToken.None).Executable is not null)
                    throw new Exception("Accepted a different executable.");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        });
        test("Playnite browser reports missing folders without throwing", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ApolloOverlayBrowserTests", Guid.NewGuid().ToString("N"));
            var result = MainWindow.ReadPlayniteFolder(root, CancellationToken.None);
            if (result.Error is null || result.Executable is not null || result.Folders.Length != 0)
                throw new Exception("Missing folder did not produce a recoverable error.");
        });
        test("Playnite browser respects cancellation before filesystem access", () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try
            {
                MainWindow.ReadPlayniteFolder(null, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            if (!cancelled)
                throw new Exception("Cancelled browse still proceeded.");
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}

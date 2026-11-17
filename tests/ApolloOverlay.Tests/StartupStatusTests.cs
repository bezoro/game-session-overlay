using System.Reflection;
using System.Windows.Threading;
using ApolloOverlay;

internal static class StartupStatusTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("a blocked startup reader neither blocks WPF nor queues another native reader", () =>
        {
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            using var host = WpfTestHost.Create(diagnostics => new(renderOnly: true, diagnostics: diagnostics),
                TestFocusMode.Synthetic, initialize: window => window.StartupStatusReadFixture = () =>
                {
                    Interlocked.Increment(ref calls);
                    entered.TrySetResult();
                    release.Wait();
                    return new(true, true, false, "Fixture startup enabled.");
                });
            var window = host.Window;
            try
            {
                window.Left = -12000;
                window.Top = -12000;
                window.Show();
                window.Page(2);
                WpfTestDispatcher.Complete(entered.Task);
                for (var i = 0; i < 20; i++)
                    window.RefreshRows();
                WpfTestDispatcher.Complete(window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task);
                Require(calls == 1 && !window.StartupStatusTask.IsCompleted,
                    "Rendering queued another reader or blocked dispatcher progress");
            }
            finally
            {
                release.Set();
                WpfTestDispatcher.Complete(window.StartupStatusTask);
            }
            Require(WpfTestElements.Button(window, "startup").IsEnabled,
                "The completed startup observation did not enable its action");
        });

        test("uncertain startup mutation rejects a stale native read before displaying the replacement", () =>
        {
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            using var host = WpfTestHost.Create(diagnostics => new(renderOnly: true, diagnostics: diagnostics),
                TestFocusMode.Synthetic, initialize: window => window.StartupStatusReadFixture = () =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        entered.TrySetResult();
                        release.Wait();
                        return new(true, true, true, "Stale fixture startup enabled.");
                    }
                    return new(true, false, false, "Current fixture startup disabled.");
                });
            var window = host.Window;
            try
            {
                window.Left = -12000;
                window.Top = -12000;
                window.Show();
                window.Page(2);
                WpfTestDispatcher.Complete(entered.Task);
                var mutation = (Task<ActionResult>)typeof(MainWindow).GetMethod("ApplyStartupChange", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [(Func<ActionResult>)(() => new(ResultKind.Unknown, "Fixture observation failed after dispatch.")), CancellationToken.None])!;
                WpfTestDispatcher.Complete(mutation);
                Require(mutation.Result.Kind == ResultKind.Unknown, "Mutation uncertainty was lost");
                Require(calls == 1, "Invalidation overlapped the still-owned native read");
            }
            finally
            {
                release.Set();
                WpfTestDispatcher.Complete(window.StartupStatusTask);
                WpfTestDispatcher.Complete(window.StartupStatusTask);
            }
            var button = WpfTestElements.Button(window, "startup");
            Require(calls == 2 && button.Content is ActionButtonContent content && content.Title.Text.EndsWith("Off", StringComparison.Ordinal),
                "A stale observation replaced the current startup registration");
        });
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new Exception(reason);
    }
}

using System.IO;
using System.ComponentModel;
using System.Windows.Threading;
using ApolloOverlay;
using ApolloOverlay.Testing;

internal static class TestInfrastructureTests
{
    internal static void Run(Action<string, Action> test)
    {
        TestInfrastructureContracts.Run(test);
        test("WPF fixture owns its context and temporary journal until disposal", () =>
        {
            var before = SynchronizationContext.Current;
            var host = WpfTestHost.Create(diagnostics => new(renderOnly: true, diagnostics: diagnostics), TestFocusMode.Synthetic);
            var root = host.Diagnostics.Root;
            try
            {
                Require(SynchronizationContext.Current is DispatcherSynchronizationContext, "The fixture did not establish its dispatcher context");
                host.Window.Left = -12000;
                host.Window.Top = -12000;
                host.Window.ShowInTaskbar = false;
                host.Window.Show();
                Require(!host.Window.ShowActivated && !host.Window.IsActive, "A synthetic fixture acquired native activation");
                host.Diagnostics.Commands.Record("fixture-lifetime", ResultKind.Completed);
                WpfTestDispatcher.Complete(host.Diagnostics.Commands.FlushAsync());
                Require(Directory.Exists(root), "The isolated journal was not created");
            }
            finally { host.Dispose(); }
            host.Dispose();
            Require(ReferenceEquals(before, SynchronizationContext.Current) && !Directory.Exists(root), "Disposal leaked thread state or local files");
        });
        test("WPF fixture construction failure restores context and removes partial evidence", () =>
        {
            var before = SynchronizationContext.Current;
            LocalDiagnostics? partial = null;
            MainWindow? window = null;
            var closed = false;
            var failure = new InvalidOperationException("fixture initialization failed");
            try
            {
                using var unexpected = WpfTestHost.Create(diagnostics =>
                {
                    partial = diagnostics;
                    diagnostics.Commands.Record("partial-fixture", ResultKind.Completed);
                    WpfTestDispatcher.Complete(diagnostics.Commands.FlushAsync());
                    return new(renderOnly: true, diagnostics: diagnostics);
                }, TestFocusMode.Native, initialize: created =>
                {
                    window = created;
                    created.Closed += (_, _) => closed = true;
                    throw failure;
                });
                throw new Exception("The initialization failure was swallowed");
            }
            catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { }
            Require(window is not null && closed && partial is not null && partial.Commands.Status.Closed
                && !Directory.Exists(partial.Root) && ReferenceEquals(before, SynchronizationContext.Current),
                "Partial initialization leaked its window, journal or synchronization context");
        });
        test("dispatcher wait delivers continuations and recovers after a deadline", () =>
        {
            using var context = WpfTestDispatcher.UseContext();
            var expected = Dispatcher.CurrentDispatcher;
            async Task Resume()
            {
                await Task.Yield();
                Require(expected.CheckAccess(), "The continuation left its owning dispatcher");
            }
            WpfTestDispatcher.Complete(Resume());
            var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                WpfTestDispatcher.Complete(stalled.Task, TimeSpan.FromMilliseconds(25));
                throw new Exception("A stalled operation had no deadline");
            }
            catch (TimeoutException) { }
            finally { stalled.TrySetResult(); }
            WpfTestDispatcher.Complete(Resume());
        });
        test("fixture disposal waits for deferred closure before releasing the journal", () =>
        {
            var before = SynchronizationContext.Current;
            using var host = WpfTestHost.Create(diagnostics => new ControlledCloseWindow(diagnostics) { DeferNextClose = true }, TestFocusMode.Synthetic);
            var window = (ControlledCloseWindow)host.Window;
            host.Diagnostics.Commands.Record("deferred-close", ResultKind.Completed);
            WpfTestDispatcher.Complete(host.Diagnostics.Commands.FlushAsync());
            var remainedOwned = false;
            window.BeforeDeferredClose = () => remainedOwned = !host.Diagnostics.Commands.Status.Closing && Directory.Exists(host.Diagnostics.Root);
            host.Dispose();
            Require(remainedOwned && host.Diagnostics.Commands.Status.Closed && !Directory.Exists(host.Diagnostics.Root)
                && ReferenceEquals(before, SynchronizationContext.Current), "Deferred closure released live resources or leaked thread state");
        });
        test("cancelled closure retains its owner and permits a later cleanup attempt", () =>
        {
            var before = SynchronizationContext.Current;
            using var host = WpfTestHost.Create(diagnostics => new ControlledCloseWindow(diagnostics) { CancelClose = true }, TestFocusMode.Synthetic);
            var window = (ControlledCloseWindow)host.Window;
            host.Diagnostics.Commands.Record("cancelled-close", ResultKind.Completed);
            WpfTestDispatcher.Complete(host.Diagnostics.Commands.FlushAsync());
            try
            {
                try
                {
                    host.Dispose();
                    throw new Exception("A cancelled close was reported as successful cleanup");
                }
                catch (WpfTestCleanupException error)
                {
                    Require(ReferenceEquals(error.Owner, host), "The failed cleanup lost its retriable owner");
                }
                Require(!host.Diagnostics.Commands.Status.Closing && Directory.Exists(host.Diagnostics.Root)
                    && ReferenceEquals(before, SynchronizationContext.Current), "Cancellation released live resources or leaked the dispatcher context");
            }
            finally
            {
                window.CancelClose = false;
                host.Dispose();
            }
            Require(host.Diagnostics.Commands.Status.Closed && !Directory.Exists(host.Diagnostics.Root), "A later successful close did not release retained resources");
        });
        test("failed initialization preserves both causes and a cancelled close owner for recovery", () =>
        {
            var before = SynchronizationContext.Current;
            ControlledCloseWindow? window = null;
            WpfTestHost? retained = null;
            var original = new InvalidOperationException("initialization failed before cleanup");
            try
            {
                using var unexpected = WpfTestHost.Create(diagnostics =>
                {
                    diagnostics.Commands.Record("failed-initialization", ResultKind.Completed);
                    WpfTestDispatcher.Complete(diagnostics.Commands.FlushAsync());
                    return window = new ControlledCloseWindow(diagnostics) { CancelClose = true };
                }, TestFocusMode.Synthetic, initialize: _ => throw original);
                throw new Exception("Initialization unexpectedly completed");
            }
            catch (AggregateException error)
            {
                retained = error.InnerExceptions.OfType<WpfTestCleanupException>().Single().Owner;
                try
                {
                    Require(error.InnerExceptions.Contains(original) && !retained.Diagnostics.Commands.Status.Closing
                        && Directory.Exists(retained.Diagnostics.Root) && ReferenceEquals(before, SynchronizationContext.Current),
                        "Failed initialization discarded its original cause, live resources or caller context");
                }
                finally
                {
                    window!.CancelClose = false;
                    retained.Dispose();
                }
            }
            Require(retained is not null && retained.Diagnostics.Commands.Status.Closed && !Directory.Exists(retained.Diagnostics.Root)
                && ReferenceEquals(before, SynchronizationContext.Current), "The retained initialization owner could not be recovered");
        });
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new Exception(reason);
    }

    private sealed class ControlledCloseWindow(LocalDiagnostics diagnostics) : MainWindow(renderOnly: true, diagnostics: diagnostics)
    {
        internal bool CancelClose, DeferNextClose;
        internal Action? BeforeDeferredClose;

        protected override void OnClosing(CancelEventArgs args)
        {
            if (CancelClose)
            {
                args.Cancel = true;
                return;
            }
            if (DeferNextClose)
            {
                DeferNextClose = false;
                args.Cancel = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                {
                    BeforeDeferredClose?.Invoke();
                    Close();
                });
                return;
            }
            base.OnClosing(args);
        }
    }
}

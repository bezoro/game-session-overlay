using System.Windows;
using System.Windows.Threading;

/// <summary>Owns a test's dispatcher context and bounded waits without a polling loop.</summary>
internal static class WpfTestDispatcher
{
    internal static IDisposable UseContext() => new ContextScope();

    internal static Application EnsureApplication() => Application.Current ?? new Application
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown
    };

    internal static void Complete(Task task, TimeSpan? timeout = null)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            using var continuation = new CancellationTokenSource();
            var timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
            {
                Interval = timeout ?? TimeSpan.FromSeconds(5)
            };
            timer.Tick += Stop;
            void Stop(object? sender, EventArgs args) => frame.Continue = false;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Background,
                () => frame.Continue = false), continuation.Token, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                continuation.Cancel();
                timer.Stop();
                timer.Tick -= Stop;
            }
            if (!task.IsCompleted)
                throw new TimeoutException("Dispatcher operation exceeded its test deadline.");
        }
        task.GetAwaiter().GetResult();
    }

    private sealed class ContextScope : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        private bool disposed;

        internal ContextScope() => SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

        public void Dispose()
        {
            if (disposed)
                return;
            dispatcher.VerifyAccess();
            disposed = true;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}

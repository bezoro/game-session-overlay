namespace ApolloOverlay;

/// <summary>Observes failures without changing their handling or process termination policy.</summary>
internal sealed class UnhandledExceptionDiagnostics : IDisposable
{
    private readonly CommandLog commands;

    internal UnhandledExceptionDiagnostics(CommandLog commands)
    {
        this.commands = commands;
        AppDomain.CurrentDomain.UnhandledException += Unhandled;
        TaskScheduler.UnobservedTaskException += Unobserved;
    }

    internal void DispatcherFailure(Exception error) => Report(error, "dispatcher-unhandled", true);

    private void Unhandled(object sender, UnhandledExceptionEventArgs args)
    {
        Report(args.ExceptionObject as Exception, args.IsTerminating ? "process-terminating" : "domain-unhandled", false);
        if (args.IsTerminating)
            commands.Dispose();
    }

    private void Unobserved(object? sender, UnobservedTaskExceptionEventArgs args) =>
        Report(args.Exception, "task-unobserved", false);

    private void Report(Exception? error, string reason, bool terminating)
    {
        commands.Record("unhandled-exception", ResultKind.Failed, "system", reason, error);
        if (terminating)
            commands.FlushAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= Unhandled;
        TaskScheduler.UnobservedTaskException -= Unobserved;
    }
}

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Parsing;
using Serilog.Sinks.Async;

namespace ApolloOverlay;

public sealed record CommandLogOptions
{
    public int BufferCapacity { get; init; } = 1024;
    public int MaximumActiveOperations { get; init; } = 256;
    public long FileSizeLimitBytes { get; init; } = 4 * 1024 * 1024;
    public int RetainedFileCount { get; init; } = 8;
    public TimeSpan RetainedFileAge { get; init; } = TimeSpan.FromDays(14);
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

public sealed record CommandLogStatus(int BufferedEvents, long DroppedEvents, long WriteFailures, long UntrackedOperations, int ActiveOperations, bool Closing, bool Closed, bool DrainTimedOut, string? LastErrorType, int? LastErrorHResult);

/// <summary>Bounded, asynchronous command evidence. User payloads and exception messages are excluded.</summary>
public sealed class CommandLog : IDisposable
{
    private static readonly MessageTemplate CommandTemplate = new MessageTemplateParser().Parse("Overlay command {Command}: {Event} ({Outcome})");
    private static readonly MessageTemplate FlushTemplate = new("", []);
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, CommandLogOperation> active = new(StringComparer.Ordinal);
    private readonly CommandLogOptions options;
    private readonly PipelineHealth health = new();
    private readonly Logger logger;
    private Task? closingTask;
    private bool closing, sealedResults, closed;

    public string DirectoryPath
    {
        get;
    }
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public CommandLogStatus Status
    {
        get
        {
            lock (gate)
                return health.Snapshot(active.Count, closing, closed);
        }
    }

    public CommandLog(string root, CommandLogOptions? options = null) : this(root, options, null)
    {
    }

    internal CommandLog(string root, CommandLogOptions? options, ILogEventSink? sink)
    {
        ArgumentNullException.ThrowIfNull(root);
        this.options = options ?? new();
        Validate(this.options);
        DirectoryPath = Path.Combine(root, "logs");
        var build = AppBuild.Current;
        var file = new LoggerConfiguration().MinimumLevel.Verbose()
            .Enrich.WithProperty("SchemaVersion", 1)
            .Enrich.WithProperty("SessionId", SessionId)
            .Enrich.WithProperty("Pid", Environment.ProcessId)
            .Enrich.WithProperty("Version", build.Version)
            .Enrich.WithProperty("BuildId", build.BuildId)
            .Enrich.WithProperty("Commit", build.Commit)
            .Enrich.WithProperty("Channel", build.Channel)
            .WriteTo.Fallible(target =>
            {
                if (sink is not null)
                    target.Sink(sink);
                else
                    target.File(new CompactJsonFormatter(), Path.Combine(DirectoryPath, "commands-.jsonl"),
                        fileSizeLimitBytes: this.options.FileSizeLimitBytes,
                        rollOnFileSizeLimit: true,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: this.options.RetainedFileCount,
                        retainedFileTimeLimit: this.options.RetainedFileAge,
                        shared: true,
                        buffered: false);
            }, health).CreateLogger();
        logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .WriteTo.Fallible(target => target.Async(s => s.Sink(new DrainBarrierSink(file, health)),
                bufferSize: this.options.BufferCapacity, blockWhenFull: false, monitor: health), health)
            .CreateLogger();
    }

    public CommandLogOperation Begin(string command, string source = "system", string? route = null, string? targetId = null, string? requestId = null)
    {
        lock (gate)
        {
            var operation = new CommandLogOperation(this, Identifier(command, "unknown"), Identifier(source, "system"),
                OptionalIdentifier(route), Hash(targetId), Hash(requestId));
            if (closing)
                return operation;
            if (active.Count < options.MaximumActiveOperations)
                active.TryAdd(operation.Id, operation);
            else
                health.TrackingLost();
            Write(operation, "started", "Pending");
            return operation;
        }
    }

    public void Record(string command, ResultKind outcome, string source = "system", string? reason = null, Exception? error = null, string? requestId = null)
    {
        lock (gate)
        {
            if (closing)
                return;
            var operation = new CommandLogOperation(this, Identifier(command, "unknown"), Identifier(source, "system"),
                null, null, Hash(requestId));
            Write(operation, "event", outcome.ToString(), reason, error);
        }
    }

    internal void Complete(CommandLogOperation operation, ResultKind outcome, string? reason, Exception? error)
    {
        lock (gate)
        {
            if (!operation.TryComplete())
                return;
            active.TryRemove(operation.Id, out _);
            if (!sealedResults)
                Write(operation, "finished", outcome.ToString(), reason, error, Stopwatch.GetElapsedTime(operation.Started).TotalMilliseconds);
        }
    }

    private void Write(CommandLogOperation operation, string phase, string outcome, string? reason = null, Exception? error = null, double? elapsed = null)
    {
        var properties = new List<LogEventProperty>
        {
            Property("Event", phase), Property("OperationId", operation.Id), Property("Command", operation.Command),
            Property("Source", operation.Source), Property("Outcome", outcome)
        };
        Add(properties, "RequestId", operation.RequestId);
        Add(properties, "TargetId", operation.TargetId);
        Add(properties, "Route", operation.Route);
        Add(properties, "Reason", OptionalIdentifier(reason));
        if (elapsed is { } duration)
            properties.Add(Property("DurationMs", Math.Round(Math.Max(0, duration), 3)));
        if (error is not null)
        {
            var diagnostic = ErrorDescription(error);
            logger.BindProperty("Error", diagnostic, true, out var property);
            if (property is not null)
                properties.Add(property);
        }
        logger.Write(new(DateTimeOffset.UtcNow, Level(outcome, error), null, CommandTemplate, properties));
    }

    /// <summary>Waits for accepted events preceding the barrier without closing the journal. Check Status for losses.</summary>
    public Task<bool> FlushAsync(CancellationToken cancellation = default) => DrainAsync(cancellation);

    private async Task<bool> DrainAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? closingWork;
        lock (gate)
        {
            if (closed)
                return true;
            closingWork = closingTask;
            if (!closing)
                logger.Write(new(DateTimeOffset.UtcNow, LogEventLevel.Verbose, null, FlushTemplate,
                    [new("CommandLogFlush", new ScalarValue(signal))]));
        }
        try
        {
            await (closingWork ?? signal.Task).WaitAsync(options.DrainTimeout, cancellation).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            health.DrainTimeout();
            return false;
        }
    }

    public void Dispose()
    {
        Task work;
        lock (gate)
        {
            if (closingTask is not null)
                work = closingTask;
            else
            {
                closing = true;
                foreach (var operation in active.Values.ToArray())
                    operation.Complete(ResultKind.Unknown, "logger-shutdown");
                sealedResults = true;
                closingTask = work = Task.Run(() =>
                {
                    try
                    {
                        logger.Dispose();
                    }
                    catch (Exception error)
                    {
                        health.OnLoggingFailed(this, LoggingFailureKind.Final, "shutdown", null, error);
                    }
                    finally
                    {
                        lock (gate)
                            closed = true;
                    }
                });
            }
        }
        try
        {
            if (!work.Wait(options.DrainTimeout))
                health.DrainTimeout();
        }
        catch (AggregateException)
        {
            // Sink failures are reported through the per-pipeline failure listener.
        }
    }

    internal static string Identifier(string value, string fallback)
    {
        if (string.IsNullOrEmpty(value))
            return fallback;
        if (value.StartsWith("key:", StringComparison.Ordinal) || value.StartsWith("key-", StringComparison.Ordinal))
            return "keyboard-edit";
        if (value.Length > 256 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_' or ':' or '/' or '=' or '+')))
            return "redacted-" + Hash(value)![..16];
        return value;
    }

    private static string? OptionalIdentifier(string? value) => value is null ? null : Identifier(value, "unspecified");
    private static string? Hash(string? value) => string.IsNullOrEmpty(value) ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Add(List<LogEventProperty> properties, string name, string? value)
    {
        if (value is not null)
            properties.Add(Property(name, value));
    }
    private static LogEventProperty Property(string name, object value) => new(name, new ScalarValue(value));
    private static LogEventLevel Level(string outcome, Exception? error) => error is not null || outcome == nameof(ResultKind.Failed) ? LogEventLevel.Error
        : outcome is nameof(ResultKind.Unavailable) or nameof(ResultKind.Partial) or nameof(ResultKind.Unknown) ? LogEventLevel.Warning : LogEventLevel.Information;

    private static object ErrorDescription(Exception error, int depth = 0) => new
    {
        Type = error.GetType().FullName,
        error.HResult,
        NativeErrorCode = error is Win32Exception native ? (int?)native.NativeErrorCode : null,
        Frames = new StackTrace(error, false).GetFrames().Take(10).Select(frame => frame.GetMethod()).OfType<System.Reflection.MethodBase>()
            .Select(method => new string(((method.DeclaringType?.FullName ?? "") + "." + method.Name)
                .Take(160).Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-' or '_' ? c : '_').ToArray())).ToArray(),
        Inner = depth < 2 && error.InnerException is { } inner ? ErrorDescription(inner, depth + 1) : null
    };

    private static void Validate(CommandLogOptions options)
    {
        if (options.BufferCapacity is < 1 or > 65536 || options.MaximumActiveOperations is < 1 or > 4096
            || options.FileSizeLimitBytes is < 1024 or > 64 * 1024 * 1024 || options.RetainedFileCount is < 1 or > 64
            || options.RetainedFileAge <= TimeSpan.Zero || options.RetainedFileAge > TimeSpan.FromDays(365)
            || options.DrainTimeout <= TimeSpan.Zero || options.DrainTimeout > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private sealed class DrainBarrierSink(Logger target, PipelineHealth health) : ILogEventSink, IDisposable
    {
        private long reportedDrops, reportedFailures, reportedUntracked;
        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Properties.TryGetValue("CommandLogFlush", out var property) && property is ScalarValue { Value: TaskCompletionSource signal })
            {
                ReportLosses();
                signal.TrySetResult();
            }
            else
            {
                target.Write(logEvent);
                ReportLosses();
            }
        }
        private void ReportLosses()
        {
            var status = health.Snapshot(0, false, false);
            if (status.DroppedEvents == reportedDrops && status.WriteFailures == reportedFailures && status.UntrackedOperations == reportedUntracked)
                return;
            reportedDrops = status.DroppedEvents;
            reportedFailures = status.WriteFailures;
            reportedUntracked = status.UntrackedOperations;
            target.Write(new(DateTimeOffset.UtcNow, LogEventLevel.Warning, null, CommandTemplate,
                [Property("Command", "diagnostics"), Property("Event", "health"), Property("Outcome", "Partial"),
                    Property("Reason", "logging-loss"), Property("DroppedEvents", reportedDrops), Property("WriteFailures", reportedFailures),
                    Property("UntrackedOperations", reportedUntracked)]));
        }
        public void Dispose()
        {
            ReportLosses();
            target.Dispose();
        }
    }

    private sealed class PipelineHealth : IAsyncLogEventSinkMonitor, ILoggingFailureListener
    {
        private IAsyncLogEventSinkInspector? inspector;
        private long drops, failures, untracked;
        private int timedOut;
        private Failure? lastError;
        private sealed record Failure(string? Type, int HResult);
        public void StartMonitoring(IAsyncLogEventSinkInspector value) => Volatile.Write(ref inspector, value);
        public void StopMonitoring(IAsyncLogEventSinkInspector value)
        {
            Interlocked.Exchange(ref drops, value.DroppedMessagesCount);
            Volatile.Write(ref inspector, null);
        }
        public void OnLoggingFailed(object sender, LoggingFailureKind kind, string message, IReadOnlyCollection<LogEvent>? events, Exception? exception)
        {
            Interlocked.Increment(ref failures);
            if (exception is not null)
                Volatile.Write(ref lastError, new(exception.GetType().FullName, exception.HResult));
        }
        public void DrainTimeout() => Volatile.Write(ref timedOut, 1);
        public void TrackingLost() => Interlocked.Increment(ref untracked);
        public CommandLogStatus Snapshot(int active, bool closing, bool closed)
        {
            var current = Volatile.Read(ref inspector);
            var error = Volatile.Read(ref lastError);
            return new(current?.Count ?? 0, current?.DroppedMessagesCount ?? Interlocked.Read(ref drops), Interlocked.Read(ref failures),
                Interlocked.Read(ref untracked), active, closing, closed, Volatile.Read(ref timedOut) != 0, error?.Type, error?.HResult);
        }
    }
}

public sealed class CommandLogOperation : IDisposable
{
    private readonly CommandLog owner;
    private int completed;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    internal string Command
    {
        get;
    }
    internal string Source
    {
        get;
    }
    internal string? Route
    {
        get;
    }
    internal string? TargetId
    {
        get;
    }
    internal string? RequestId
    {
        get;
    }
    internal long Started { get; } = Stopwatch.GetTimestamp();
    internal bool IsCompleted => Volatile.Read(ref completed) != 0;
    internal CommandLogOperation(CommandLog owner, string command, string source, string? route, string? targetId, string? requestId)
    {
        this.owner = owner;
        Command = command;
        Source = source;
        Route = route;
        TargetId = targetId;
        RequestId = requestId;
    }
    public void Complete(ResultKind outcome, string? reason = null, Exception? error = null)
    {
        owner.Complete(this, outcome, reason, error);
    }
    internal bool TryComplete() => Interlocked.Exchange(ref completed, 1) == 0;
    public void Fail(Exception error, string? reason = null) => Complete(ResultKind.Failed, reason, error);
    public void Dispose() => Complete(ResultKind.Unknown, "scope-incomplete");
}

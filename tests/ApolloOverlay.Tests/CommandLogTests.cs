using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApolloOverlay;
using Serilog.Core;
using Serilog.Events;

internal static class CommandLogTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("command journal pairs UTC starts and outcomes with explicit safe correlation", () => WithRoot(root =>
        {
            using var log = new CommandLog(root);
            using var operation = log.Begin("windows-audio", "controller", "Hub", "private-target", "bearer-ticket");
            operation.Complete(ResultKind.Requested, "sent-not-confirmed");
            Require(log.FlushAsync().GetAwaiter().GetResult(), "First drain did not finish");
            log.Record("settings-save", ResultKind.Completed);
            Require(log.FlushAsync().GetAwaiter().GetResult(), "Journal stopped accepting after drain");
            var entries = Read(root);
            Require(entries.Length == 3, "Missing command events");
            var start = entries.Single(e => Field(e, "Event") == "started");
            var finish = entries.Single(e => Field(e, "Event") == "finished");
            Require(Field(start, "OperationId") == operation.Id && Field(finish, "OperationId") == operation.Id, "Command correlation changed");
            Require(Field(start, "RequestId") == Hash("bearer-ticket") && Field(start, "TargetId") == Hash("private-target"), "Private identity was not hashed");
            Require(Field(finish, "Outcome") == "Requested" && finish.GetProperty("DurationMs").GetDouble() >= 0, "Requested outcome became a false completion");
            Require(entries.All(e => Field(e, "SessionId") == log.SessionId && e.GetProperty("Pid").GetInt32() == Environment.ProcessId), "Process identity missing");
            Require(entries.All(e => Field(e, "@t").EndsWith('Z') && Field(e, "Version") == AppBuild.Current.Version && Field(e, "BuildId") == AppBuild.Current.BuildId), "UTC/build provenance missing");
        }));

        test("command journal overlapping drains each cover their preceding events", () => WithRoot(root =>
        {
            using var sink = new TwoStageGateSink();
            using var log = new CommandLog(root, new()
            {
                DrainTimeout = TimeSpan.FromSeconds(5)
            }, sink);
            try
            {
                log.Record("first", ResultKind.Completed);
                Require(sink.FirstStarted.Wait(TimeSpan.FromSeconds(2)), "First event did not reach its gate");
                var firstDrain = log.FlushAsync();
                log.Record("second", ResultKind.Completed);
                var secondDrain = log.FlushAsync();
                sink.FirstRelease.Set();
                Require(firstDrain.GetAwaiter().GetResult(), "First barrier did not complete");
                Require(sink.SecondStarted.Wait(TimeSpan.FromSeconds(2)), "Second event did not reach its gate");
                Require(!secondDrain.IsCompleted, "Later drain reused an earlier barrier before its own event was processed");
                sink.SecondRelease.Set();
                Require(secondDrain.GetAwaiter().GetResult(), "Second barrier did not complete");
                Require(sink.Events.Count == 2, "Later drain completed before both events reached the sink");
            }
            finally
            {
                sink.FirstRelease.Set();
                sink.SecondRelease.Set();
            }
        }));

        test("command journal completion and disposal are idempotent", () => WithRoot(root =>
        {
            using var log = new CommandLog(root);
            var operation = log.Begin("settings-save");
            operation.Complete(ResultKind.Completed);
            operation.Complete(ResultKind.Failed);
            operation.Dispose();
            log.Dispose();
            log.Dispose();
            var entries = Read(root);
            Require(entries.Length == 2 && Field(entries[1], "Outcome") == "Completed", "A duplicate terminal result was written");
            Require(log.Status.Closed && log.Status.ActiveOperations == 0, "Clean close did not finish");
        }));

        test("command journal unfinished scopes and shutdown remain explicitly uncertain", () => WithRoot(root =>
        {
            using var log = new CommandLog(root);
            using (log.Begin("window-switch"))
            {
            }
            var abandoned = log.Begin("update-download");
            log.Dispose();
            abandoned.Dispose();
            var endings = Read(root).Where(e => Field(e, "Event") == "finished").ToArray();
            Require(endings.Length == 2 && endings.All(e => Field(e, "Outcome") == "Unknown"), "Interrupted commands were reported successful or lost");
            Require(endings.Any(e => Field(e, "Reason") == "scope-incomplete") && endings.Any(e => Field(e, "Reason") == "logger-shutdown"), "Interruption reason missing");
        }));

        test("command journal preserves native error evidence without user payloads", () => WithRoot(root =>
        {
            using var log = new CommandLog(root);
            using var operation = log.Begin("key:secret-value", "keyboard", "Keyboard", "C:\\Users\\Private\\title", "private-ticket");
            Exception failure;
            try
            {
                ThrowSensitiveError();
                throw new Exception("Expected native error");
            }
            catch (Win32Exception error)
            {
                failure = error;
            }
            operation.Fail(failure, "C:\\Users\\Private\\message");
            Require(log.FlushAsync().GetAwaiter().GetResult(), "Error drain did not finish");
            var content = string.Join('\n', Read(root).Select(e => e.GetRawText()));
            Require(!content.Contains("secret-value", StringComparison.Ordinal) && !content.Contains("private-ticket", StringComparison.Ordinal)
                && !content.Contains("Users", StringComparison.Ordinal) && !content.Contains("password", StringComparison.Ordinal), "Sensitive payload reached diagnostics");
            var finish = Read(root).Single(e => Field(e, "Event") == "finished");
            var native = finish.GetProperty("Error");
            Require(Field(finish, "Command") == "keyboard-edit" && Field(finish, "Outcome") == "Failed", "Keyboard payload was not normalized");
            Require(native.GetProperty("NativeErrorCode").GetInt32() == 5 && native.GetProperty("HResult").GetInt32() == failure.HResult, "Native failure evidence missing");
            Require(native.GetProperty("Frames").EnumerateArray().Any(f => f.GetString()?.Contains(nameof(ThrowSensitiveError), StringComparison.Ordinal) == true), "Method-only stack evidence missing");
        }));

        test("command journal concurrent producers keep independent operation pairs", () => WithRoot(root =>
        {
            using var log = new CommandLog(root);
            Parallel.For(0, 80, _ =>
            {
                using var operation = log.Begin("audio-adjust", "controller");
                operation.Complete(ResultKind.Completed);
            });
            Require(log.FlushAsync().GetAwaiter().GetResult(), "Concurrent drain did not finish");
            var entries = Read(root);
            Require(entries.Length == 160 && entries.GroupBy(e => Field(e, "OperationId")).All(g => g.Count() == 2), "Concurrent command pairs were mixed or lost");
            Require(log.Status.DroppedEvents == 0 && log.Status.ActiveOperations == 0, "Healthy producers lost evidence");
        }));

        test("command journal completion racing shutdown retains exactly one terminal outcome", () => WithRoot(root =>
        {
            for (var round = 0; round < 4; round++)
            {
                var directory = Path.Combine(root, round.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var log = new CommandLog(directory);
                var operations = Enumerable.Range(0, 128).Select(_ => log.Begin("audio-adjust")).ToArray();
                using var start = new ManualResetEventSlim();
                var finish = Task.Run(() =>
                {
                    start.Wait();
                    Parallel.ForEach(operations, operation => operation.Complete(ResultKind.Completed));
                });
                var close = Task.Run(() =>
                {
                    start.Wait();
                    log.Dispose();
                });
                start.Set();
                Require(Task.WaitAll([finish, close], TimeSpan.FromSeconds(4)), "Concurrent close did not finish");
                var entries = Read(directory);
                Require(entries.Length == 256 && entries.GroupBy(e => Field(e, "OperationId")).All(group => group.Count() == 2
                    && group.Count(e => Field(e, "Event") == "finished") == 1), "Concurrent close lost or duplicated a terminal outcome");
            }
        }));

        test("command journal has no ambient parent across independent commands", () => WithRoot(root =>
        {
            using var log = new CommandLog(root);
            using var earlier = log.Begin("pending-command", requestId: "first");
            using var later = log.Begin("independent-command");
            later.Complete(ResultKind.Completed);
            earlier.Complete(ResultKind.Completed);
            Require(log.FlushAsync().GetAwaiter().GetResult(), "Independent drain did not finish");
            Require(Read(root).Where(e => Field(e, "Command") == "independent-command").All(e => !e.TryGetProperty("RequestId", out _) && !e.TryGetProperty("ParentOperationId", out _)), "Unrelated dispatcher work inherited stale correlation");
        }));

        test("command journal directory failures are observable and repaired sessions recover", () => WithRoot(root =>
        {
            Directory.CreateDirectory(root);
            var blocked = Path.Combine(root, "logs");
            File.WriteAllText(blocked, "not a directory");
            using var log = new CommandLog(root);
            log.Record("settings-save", ResultKind.Completed);
            Require(log.FlushAsync().GetAwaiter().GetResult(), "Unavailable writer stopped its worker");
            Require(log.Status.WriteFailures > 0, "Writer failure remained invisible");
            log.Dispose();
            File.Delete(blocked);
            using var recovered = new CommandLog(root);
            recovered.Record("settings-save", ResultKind.Completed);
            Require(recovered.FlushAsync().GetAwaiter().GetResult(), "Repaired session did not drain");
            var entries = Read(root);
            Require(entries.Any(e => Field(e, "Command") == "settings-save") && recovered.Status.WriteFailures == 0, "New session did not recover its repaired directory");
        }));

        test("command journal sink failures retain a readable logging-gap event", () => WithRoot(root =>
        {
            var sink = new TransientFailureSink();
            using var log = new CommandLog(root, null, sink);
            log.Record("first", ResultKind.Completed);
            log.Record("second", ResultKind.Completed);
            Require(log.FlushAsync().GetAwaiter().GetResult(), "Recovered sink did not drain");
            Require(log.Status.WriteFailures > 0 && log.Status.LastErrorType == typeof(IOException).FullName, "Sink error was swallowed without status");
            Require(sink.Events.Any(e => e.Properties.TryGetValue("Event", out var phase) && phase is ScalarValue { Value: "health" }
                && e.Properties.TryGetValue("WriteFailures", out var failures) && failures is ScalarValue { Value: long count } && count > 0), "Recovered sink lacks explicit loss evidence");
        }));

        test("command journal full queues and stuck sinks do not block commands or shutdown", () => WithRoot(root =>
        {
            using var sink = new GateSink();
            var log = new CommandLog(root, new()
            {
                BufferCapacity = 2,
                DrainTimeout = TimeSpan.FromMilliseconds(100)
            }, sink);
            try
            {
                log.Record("first", ResultKind.Completed);
                Require(sink.Started.Wait(TimeSpan.FromSeconds(2)), "Sink did not reach controlled gate");
                var elapsed = Stopwatch.StartNew();
                for (var i = 0; i < 40; i++)
                    log.Record("audio-adjust", ResultKind.Completed);
                Require(elapsed.Elapsed < TimeSpan.FromSeconds(1), "Bounded queue blocked a producer");
                Require(log.Status.BufferedEvents <= 2 && log.Status.DroppedEvents > 0, "Bounded loss counter missing");
                Require(!log.FlushAsync().GetAwaiter().GetResult(), "Blocked barrier pretended to flush");
                elapsed.Restart();
                log.Dispose();
                Require(elapsed.Elapsed < TimeSpan.FromSeconds(1) && log.Status.DrainTimedOut, "Shutdown ignored its deadline");
            }
            finally
            {
                sink.Release.Set();
                log.Dispose();
            }
            Require(SpinWait.SpinUntil(() => log.Status.Closed, TimeSpan.FromSeconds(2)), "Released sink did not close");
            Require(sink.Events.Any(e => e.Properties.TryGetValue("Event", out var phase) && phase is ScalarValue { Value: "health" }), "Queue loss was not written after recovery");
        }));

        test("command journal rolling retention preserves unrelated files", () => WithRoot(root =>
        {
            var directory = Path.Combine(root, "logs");
            Directory.CreateDirectory(directory);
            var obsolete = Path.Combine(directory, "commands-20000101.jsonl");
            var unrelated = Path.Combine(directory, "user-notes.txt");
            File.WriteAllText(obsolete, "old evidence");
            File.WriteAllText(unrelated, "keep");
            using var log = new CommandLog(root, new()
            {
                FileSizeLimitBytes = 1024,
                RetainedFileCount = 3
            });
            for (var i = 0; i < 30; i++)
                log.Record("audio-adjust", ResultKind.Completed, reason: new string('x', 200));
            log.Record("retention-final", ResultKind.Completed);
            Require(log.FlushAsync().GetAwaiter().GetResult(), "Retention drain did not finish");
            var files = Directory.GetFiles(directory, "commands-*.jsonl");
            Require(files.Length <= 3 && files.Length > 1 && !File.Exists(obsolete), "Size/age retention did not apply");
            Require(File.ReadAllText(unrelated) == "keep" && Read(root).Any(e => Field(e, "Command") == "retention-final"), "Retention removed unrelated or newest evidence");
        }));

        test("command journal shared writers retain separate session provenance", () => WithRoot(root =>
        {
            using var first = new CommandLog(root);
            using var second = new CommandLog(root);
            Parallel.Invoke(() => first.Record("first", ResultKind.Completed), () => second.Record("second", ResultKind.Completed));
            Require(first.FlushAsync().GetAwaiter().GetResult() && second.FlushAsync().GetAwaiter().GetResult(), "Shared writer drain failed");
            var entries = Read(root);
            Require(entries.Length == 2 && entries.Select(e => Field(e, "SessionId")).Distinct().Count() == 2, "Shared lines were corrupt or mixed session identities");
        }));

        test("command journal active tracking is bounded and reported", () => WithRoot(root =>
        {
            using var log = new CommandLog(root, new()
            {
                MaximumActiveOperations = 2
            });
            using var first = log.Begin("first");
            using var second = log.Begin("second");
            using var third = log.Begin("third");
            Require(log.Status.ActiveOperations == 2 && log.Status.UntrackedOperations == 1, "Active tracking grew without a bound");
            third.Complete(ResultKind.Completed);
            first.Complete(ResultKind.Cancelled);
            second.Complete(ResultKind.Unavailable);
        }));
    }

    private static void ThrowSensitiveError() => throw new Win32Exception(5, "C:\\Users\\Private\\password.txt");
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Field(JsonElement value, string property) => value.TryGetProperty(property, out var result) ? result.GetString() ?? "" : "";
    private static JsonElement[] Read(string root) => Directory.GetFiles(Path.Combine(root, "logs"), "commands-*.jsonl")
        .OrderBy(path => path, StringComparer.Ordinal).SelectMany(File.ReadLines).Where(line => line.Length > 0)
        .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
    private static void WithRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "overlay-command-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            action(root);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private sealed class GateSink : ILogEventSink, IDisposable
    {
        internal ManualResetEventSlim Started { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        internal ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent)
        {
            Started.Set();
            Release.Wait();
            Events.Enqueue(logEvent);
        }
        public void Dispose()
        {
            Release.Set();
        }
    }

    private sealed class TwoStageGateSink : ILogEventSink, IDisposable
    {
        private int attempts;
        internal ManualResetEventSlim FirstStarted { get; } = new();
        internal ManualResetEventSlim FirstRelease { get; } = new();
        internal ManualResetEventSlim SecondStarted { get; } = new();
        internal ManualResetEventSlim SecondRelease { get; } = new();
        internal ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent)
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                FirstStarted.Set();
                FirstRelease.Wait();
            }
            else
            {
                SecondStarted.Set();
                SecondRelease.Wait();
            }
            Events.Enqueue(logEvent);
        }
        public void Dispose()
        {
            FirstRelease.Set();
            SecondRelease.Set();
        }
    }

    private sealed class TransientFailureSink : ILogEventSink
    {
        private int attempts;
        internal ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent)
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new IOException("private path must not be retained");
            Events.Enqueue(logEvent);
        }
    }
}

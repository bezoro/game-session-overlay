using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ApolloOverlay;
using ApolloOverlay.Automation;

internal static class CommandDiagnosticsTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("CLI setting execution retains its source and request correlation in semantic results", () =>
        {
            using var host = new Host();
            host.Window.Page(2);
            const string ticket = "sensitive-cli-ticket";
            var request = new AutomationRequest
            {
                Verb = "execute",
                ActionId = "sound-feedback",
                RequestId = ticket,
                Revision = host.Window.AutomationSnapshot().Revision
            };
            var task = (Task<AutomationReply>)Invoke(host.Window, "ExecuteLoggedAutomation", request,
                CancellationToken.None, (Action<AutomationReply>)(_ => { }), (Func<bool>)(() => true))!;
            ReviewFixTests.Complete(task);
            Require(task.Result.Outcome == "Completed", "CLI handler did not complete");
            var changed = host.Read().Single(e => Field(e, "Command") == "setting-sound-feedback");
            Require(Field(changed, "Source") == "cli"
                && Field(changed, "RequestId") == Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ticket))),
                "CLI effect inherited the previous UI input source or lost correlation");
            var before = host.Window.Controller.Settings.SoundFeedbackEnabled;
            var dryRun = (Task<AutomationReply>)Invoke(host.Window, "ExecuteLoggedAutomation", request with
            {
                DryRun = true,
                Revision = host.Window.AutomationSnapshot().Revision
            }, CancellationToken.None,
                (Action<AutomationReply>)(_ => { }), (Func<bool>)(() => true))!;
            ReviewFixTests.Complete(dryRun);
            Require(dryRun.Result.Outcome == "DryRun" && host.Window.Controller.Settings.SoundFeedbackEnabled == before,
                "Dry run changed the setting");
            Require(Field(host.Read().Last(e => Field(e, "Command") == "sound-feedback" && Field(e, "Event") == "finished"), "Outcome") == "Simulated",
                "Dry run was journaled as unavailable");
        });

        foreach (var nativeAccepted in new[] { false, true })
            test("refused overlay opening keeps its actual focus outcome " + nativeAccepted, () =>
            {
                using var host = new Host();
                host.Window.FocusForeground = () => 0;
                host.Window.FocusRequest = _ => nativeAccepted;
                host.Window.FocusFallbackRequest = (_, _, _) => Task.FromResult(nativeAccepted);
                host.Window.OpenPanel();
                ReviewFixTests.Complete(host.Window.OpeningTask);
                var refused = host.Read().Single(e => Field(e, "Command") == "overlay-open" && Field(e, "Event") == "finished");
                Require(Field(refused, "Outcome") == (nativeAccepted ? "Unknown" : "Failed"),
                    "Focus cleanup incorrectly turned a refusal into user cancellation");
            });

        test("interface command journal covers setting results without recording character input", () =>
        {
            using var host = new Host();
            var window = host.Window;
            window.Page(2);
            ReviewFixTests.Button(window, "sound-feedback").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            ReviewFixTests.Button(window, "haptic-feedback").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var key = ReviewFixTests.Button(window, "sound-feedback");
            var previousTag = key.Tag;
            key.Tag = "key:private-typed-value";
            try
            {
                Invoke(window, "RunInterfaceAction", key, (Action)(() => { }), null);
            }
            finally { key.Tag = previousTag; }
            var entries = host.Read();
            Require(entries.Any(e => Field(e, "Command") == "setting-sound-feedback" && Field(e, "Outcome") == "Completed" && Field(e, "Reason") == "enabled"), "Sound draft change was not journaled");
            Require(entries.Any(e => Field(e, "Command") == "setting-haptic-feedback" && Field(e, "Outcome") == "Completed"), "Haptic draft change was not journaled");
            var dispatch = entries.Where(e => Field(e, "Command") == "sound-feedback").ToArray();
            Require(dispatch.Length == 2 && dispatch.Select(e => Field(e, "OperationId")).Distinct().Count() == 1
                && Field(dispatch[1], "Outcome") == "Requested", "UI dispatch was lost or misrepresented as effect confirmation");
            Require(!string.Join("", entries.Select(e => e.GetRawText())).Contains("private-typed-value", StringComparison.Ordinal), "Character input entered the journal");
            Require(window.AutomationSnapshot().Diagnostics is { WriteFailures: 0, DroppedEvents: 0 }, "CLI status lost journal health evidence");
        });

        test("session command journal preserves a real late result after the interface closes", () =>
        {
            using var host = new Host();
            var late = new TaskCompletionSource<ActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = (Task)Invoke(host.Window, "RunSessionAction", "late-fixture-action",
                (Func<CancellationToken, Task<ActionResult>>)(_ => late.Task), null)!;
            host.Window.Close();
            late.SetResult(new(ResultKind.Completed, "A late service confirmed the effect."));
            ReviewFixTests.Complete(operation);
            var finished = host.Read().Single(e => Field(e, "Command") == "late-fixture-action" && Field(e, "Event") == "finished");
            Require(Field(finished, "Outcome") == "Completed", "A UI cleanup guard discarded the real service result");
        });

        test("session command journal retains caught technical errors without their private messages", () =>
        {
            using var host = new Host();
            var operation = (Task)Invoke(host.Window, "RunSessionAction", "failed-fixture-action",
                (Func<CancellationToken, Task<ActionResult>>)(_ => Task.FromException<ActionResult>(new IOException("private-user-path-and-message"))), null)!;
            ReviewFixTests.Complete(operation);
            var entries = host.Read();
            var failed = entries.Single(e => Field(e, "Command") == "failed-fixture-action" && Field(e, "Event") == "finished");
            Require(Field(failed, "Outcome") == "Failed" && failed.GetProperty("Error").GetProperty("Type").GetString() == typeof(IOException).FullName,
                "Handled service exception lost its technical evidence");
            Require(!string.Join("", entries.Select(e => e.GetRawText())).Contains("private-user-path-and-message", StringComparison.Ordinal), "Exception message entered the journal");
        });
    }

    private static object? Invoke(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static string Field(JsonElement entry, string name) => entry.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class Host : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        private readonly LocalDiagnostics diagnostics = new(Path.Combine(Path.GetTempPath(), "BezoroCommandIntegration", Guid.NewGuid().ToString("N")));
        private bool closed;
        internal MainWindow Window
        {
            get;
        }

        internal Host()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ThemeSupport.ForceForValidation(app, false);
            Window = new(renderOnly: true, platform: new PreviewPlatform(), diagnostics: diagnostics);
            Window.CommandLoggingFixture = true;
            Window.InputContextReader = () => new(true, true, true, true);
            Window.PhysicalKeyReader = _ => 0;
            Window.Closed += (_, _) => closed = true;
        }

        internal JsonElement[] Read()
        {
            Require(diagnostics.Commands.FlushAsync().GetAwaiter().GetResult(), "Journal did not drain");
            return Directory.EnumerateFiles(diagnostics.Commands.DirectoryPath, "*.jsonl").Order()
                .SelectMany(JournalFiles.ReadLines).Select(line =>
                {
                    using var document = JsonDocument.Parse(line);
                    return document.RootElement.Clone();
                }).ToArray();
        }

        public void Dispose()
        {
            try
            {
                if (!closed)
                    Window.Close();
                diagnostics.Dispose();
                if (Directory.Exists(diagnostics.Root))
                    Directory.Delete(diagnostics.Root, true);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
    }
}

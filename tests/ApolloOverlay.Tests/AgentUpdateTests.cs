using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ApolloOverlay;
using ApolloOverlay.Automation;

internal static class AgentUpdateTests
{
    private static readonly UpdateCandidate Next = new(42, "9.9.9+agent-fixture", "agent-next", new string('a', 40), AppBuild.Current.Channel, DateTimeOffset.UtcNow, "Fixture release", 24, "fixture.nupkg", 1234, new string('b', 64));
    private static PreparedUpdate Prepared(UpdateCandidate? candidate = null)
    {
        var value = candidate ?? Next;
        return new("fixture-never-installed.nupkg", new(value.Version, value.BuildId, value.Commit, value.Channel));
    }

    private static AutomationRequest Request(string verb, string buildId = "", string jobId = "", bool dryRun = false) => new()
    {
        Verb = verb,
        RequestId = Guid.NewGuid().ToString("N"),
        BuildId = buildId,
        JobId = jobId,
        DryRun = dryRun
    };
    private static void Require(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    private static T Complete<T>(Task<T> task)
    {
        ReviewFixTests.Complete(task);
        return task.GetAwaiter().GetResult();
    }

    private static void Flush() => ReviewFixTests.Complete(Dispatcher.CurrentDispatcher.InvokeAsync(() =>
    {
    }, DispatcherPriority.Background).Task);
    private static void WaitFor(Func<bool> condition)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && elapsed.ElapsedMilliseconds < 3000)
        {
            Flush();
            Thread.Sleep(1);
        }

        Require(condition(), "Fixture did not reach its asynchronous checkpoint");
    }

    internal static void Run(Action<string, Action> test)
    {
        test("agent updates status and dry-run never query, write, show or change focus", () =>
        {
            using var host = new Host();
            var w = host.Window;
            var selection = w.SelectedAction;
            var actions = w.ActionIds.ToArray();
            var focus = Keyboard.FocusedElement;
            var status = host.Status();
            Require(status.Version == AppBuild.Current.Version && status.BuildId == AppBuild.Current.BuildId && status.Commit == AppBuild.Current.Commit && status.Channel == AppBuild.Current.Channel, "Status lost compiled identity");
            Require(status.Phase == "Idle" && !status.Installed && status.Terminal, "Preview falsely claimed a managed installation or pending job");
            Require(host.Send(Request("update-check", dryRun: true)).Outcome == "DryRun", "Dry-run check did not validate");
            Require(host.Source.Checks == 0 && host.Source.Stages == 0 && !File.Exists(host.Receipt), "Read-only request queried network or wrote a receipt");
            Require(!w.IsVisible && selection == w.SelectedAction && actions.SequenceEqual(w.ActionIds) && ReferenceEquals(focus, Keyboard.FocusedElement), "Headless update activated or navigated the overlay");
        });
        test("agent updates reject invalid channels, missing clients and cancelled requests before jobs", () =>
        {
            using var host = new Host();
            Require(host.Send(Request("update-check") with
            {
                Channel = "other"
            }).Outcome == "Invalid", "Invalid channel accepted");
            Require(host.Send(Request("update-check") with
            {
                BuildId = null!
            }).Outcome == "Invalid", "Null identity accepted");
            Require(host.Send(Request("update-check"), continuing: () => false).Outcome == "Cancelled", "Disconnected client started a job");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Require(host.Send(Request("update-check"), cancelled.Token).Outcome == "Cancelled", "Cancelled request started a job");
            Flush();
            Require(host.Source.Checks == 0 && !File.Exists(host.Receipt), "Rejected preflight changed state");
        });
        test("agent update jobs return immediately and survive IPC cancellation without installing", () =>
        {
            using var host = new Host();
            host.Source.CheckCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var ipc = new CancellationTokenSource();
            var request = Request("update-check");
            var ack = host.Send(request, ipc.Token);
            Require(ack.Outcome == "Requested" && !ack.Terminal && ack.Update is { Phase: "Checking", Terminal: false } && ack.Update.JobId == request.RequestId, "Job did not return an immediate identified acknowledgement");
            WaitFor(() => host.Source.Checks == 1);
            ipc.Cancel();
            Require(!host.Source.CheckToken.IsCancellationRequested && !host.Window.UpdateTask.IsCompleted, "IPC disconnect cancelled the independent job");
            host.Source.CheckCompletion.SetResult([Next]);
            host.Finish();
            Require(host.Status() is { Phase: "Catalog", Terminal: true } catalog && catalog.Available.Single().BuildId == Next.BuildId, "Completed check lost its catalog");
            Require(host.Source.Stages == 0 && !host.Window.TransitionPending && !host.Window.IsVisible, "Catalog query installed or opened the overlay");
        });
        test("agent updates selected channel stays separate from running compiled identity", () =>
        {
            var selectedChannel = AppBuild.Current.Channel == "stable" ? "testing" : "stable";
            using var host = new Host();
            host.Source.Releases = [Next with
            {
                Channel = selectedChannel
            }

            ];
            host.Send(Request("update-check") with
            {
                Channel = selectedChannel
            });
            host.Finish();
            var status = host.Status();
            Require(status.SelectedChannel == selectedChannel && status.Channel == AppBuild.Current.Channel && host.Source.Channel == selectedChannel, "Channel preference replaced running build identity");
            Require(host.Send(Request("update-download", Next.BuildId) with
            {
                Channel = AppBuild.Current.Channel
            }).Outcome == "Unavailable", "Catalog from another channel was used");
            Require(host.Send(Request("update-download", "foreign-build")).Outcome == "Unavailable" && host.Source.Stages == 0, "Foreign build bypassed exact catalog selection");
        });
        test("agent updates download dry-run preserves receipt and never stages a package", () =>
        {
            using var host = new Host();
            host.Check();
            var before = File.ReadAllBytes(host.Receipt);
            var reply = host.Send(Request("update-download", Next.BuildId, dryRun: true));
            Require(reply.Outcome == "DryRun" && host.Source.Stages == 0 && before.SequenceEqual(File.ReadAllBytes(host.Receipt)), "Download dry-run mutated state or wrote a new baseline");
        });
        test("agent updates never silently downgrade from an older catalog", () =>
        {
            using var host = new Host();
            var older = Next with
            {
                Version = "0.0.1",
                BuildId = "agent-old"
            };
            host.Source.Releases = [older];
            host.Check();
            Require(host.Send(Request("update-download", older.BuildId)).Outcome == "Conflict" && host.Source.Stages == 0, "Automatic latest could downgrade the installed version");
            Require(host.Send(Request("update-download", older.BuildId) with
            {
                AllowDowngrade = true
            }).Outcome == "Requested", "Explicit downgrade was not allowed to prepare");
            host.Finish();
            Require(host.Status() is { Phase: "Ready", TargetBuildId: "agent-old" }, "Explicit downgrade did not prepare the exact build");
        });
        test("agent updates verified download remains ready until an independent apply and keeps settings draft", () =>
        {
            using var host = new Host();
            var w = host.Window;
            w.Controller.Change(w.Controller.Settings with
            {
                UiScalePercent = 130
            });
            w.RefreshRows();
            var selection = w.SelectedAction;
            var focus = Keyboard.FocusedElement;
            host.Check();
            var reply = host.Send(Request("update-download", Next.BuildId));
            host.Finish();
            var state = host.Status();
            Require(reply.Outcome == "Requested"
                && state is { Phase: "Ready", Progress: 100, Terminal: true, SettingsDirty: true }
                && state.TargetBuildId == Next.BuildId
                && state.TargetVersion == Next.Version, "Download did not preserve its exact ready state and dirty draft");
            Require(host.Source.Stages == 1 && w.Controller.Settings.UiScalePercent == 130 && w.Controller.SettingsDirty, "Download applied or discarded unsaved settings");
            var receipt = File.ReadAllBytes(host.Receipt);
            var apply = host.Send(Request("update-apply", Next.BuildId, state.JobId));
            Require(apply.Outcome == "Conflict" && apply.Reason.Contains("Salve ou descarte") && host.Status().Phase == "Ready" && receipt.SequenceEqual(File.ReadAllBytes(host.Receipt)), "Dirty apply crossed the settings guard or changed its prepared receipt");
            Require(!w.TransitionPending && !w.IsVisible && selection == w.SelectedAction && ReferenceEquals(focus, Keyboard.FocusedElement), "Download or refused apply changed focus, visibility or process lifecycle");
        });
        test("agent updates cancellation requires exact job and ignores late check success", () =>
        {
            using var host = new Host();
            host.Source.CheckCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = Request("update-check");
            host.Send(request);
            WaitFor(() => host.Source.Checks == 1);
            Require(host.Send(Request("update-cancel", jobId: "foreign")).Outcome == "Conflict" && !host.Source.CheckToken.IsCancellationRequested, "Foreign job cancelled another operation");
            var before = File.ReadAllBytes(host.Receipt);
            Require(host.Send(Request("update-cancel", jobId: request.RequestId, dryRun: true)).Outcome == "DryRun"
                && !host.Source.CheckToken.IsCancellationRequested
                && before.SequenceEqual(File.ReadAllBytes(host.Receipt)), "Dry-run cancelled the actual job");
            Require(host.Send(Request("update-cancel", jobId: request.RequestId)).Outcome == "Cancelled" && host.Source.CheckToken.IsCancellationRequested, "Exact job was not cancelled");
            Require(host.Send(Request("update-check")).Outcome == "Busy", "New job raced the still-draining source");
            host.Source.CheckCompletion.SetResult([Next]);
            host.Finish();
            Require(host.Status() is { Phase: "Cancelled", Terminal: true, Available.Length: 0 } && host.Source.Stages == 0, "Late success replaced terminal cancellation or began download");
        });
        test("agent updates cancellation discards late verified payload and allows recovery", () =>
        {
            using var host = new Host();
            host.Check();
            host.Source.StageCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = Request("update-download", Next.BuildId);
            host.Send(request);
            WaitFor(() => host.Source.Stages == 1);
            host.Send(Request("update-cancel", jobId: request.RequestId));
            host.Source.StageCompletion.SetResult(Prepared());
            host.Finish();
            Require(host.Status().Phase == "Cancelled" && host.Source.Discards == 1 && !host.Window.TransitionPending, "Late verified payload remained ready after cancel");
            host.Source.StageCompletion = null;
            Require(host.Send(Request("update-download", Next.BuildId)).Outcome == "Requested", "Cancelled job left the shared update lock busy");
            host.Finish();
            Require(host.Status().Phase == "Ready", "Retry could not recover after cancellation");
        });
        test("agent updates failed source produces honest terminal failure and no payload", () =>
        {
            using var host = new Host();
            host.Source.CheckFailure = new IOException("fixture offline");
            host.Send(Request("update-check"));
            host.Finish();
            Require(host.Status() is { Phase: "Failed", Terminal: true, Available.Length: 0 } && File.Exists(host.Receipt), "Source error falsely completed or escaped without receipt");
            host.Source.CheckFailure = null;
            host.Check();
            host.Source.StageFailure = new InvalidDataException("fixture invalid package");
            host.Send(Request("update-download", Next.BuildId));
            host.Finish();
            Require(host.Status() is { Phase: "Failed", Terminal: true } && !host.Window.TransitionPending, "Failed download exposed install or left an in-progress state");
        });
        test("agent updates bounded progress cannot overwrite terminal ready state", () =>
        {
            using var host = new Host();
            host.Check();
            host.Source.StageCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Send(Request("update-download", Next.BuildId));
            WaitFor(() => host.Source.Stages == 1);
            host.Source.Progress!.Report(new("Downloading", long.MaxValue, 1));
            Flush();
            Require(host.Status().Progress == 100, "Progress overflowed its upper bound");
            host.Source.Progress.Report(new("Downloading", -100, 1));
            Flush();
            Require(host.Status().Progress == 0, "Negative progress escaped its bound");
            host.Source.StageCompletion.SetResult(Prepared());
            host.Finish();
            host.Source.Progress.Report(new("Late progress", 1, 100));
            Flush();
            Require(host.Status() is { Phase: "Ready", Progress: 100 }, "Late callback regressed the terminal verified result");
        });
        test("agent updates reject a prepared package with mismatched immutable identity", () =>
        {
            using var host = new Host();
            host.Check();
            host.Source.StageResult = Prepared() with
            {
                Build = Prepared().Build with
                {
                    Commit = new string('c', 40)
                }
            };
            host.Send(Request("update-download", Next.BuildId));
            host.Finish();
            Require(host.Status().Phase == "Failed" && host.Source.Discards == 1 && !host.Window.TransitionPending, "Mismatched package became ready");
        });
        test("agent updates default preview and rollback cannot perform native update work", () =>
        {
            using var host = new Host(defaultSource: true);
            Require(host.Send(Request("update-check")).Outcome == "Unavailable" && !File.Exists(host.Receipt), "Default preview queried a source");
            using var custom = new Host();
            Require(custom.Send(Request("update-rollback", Next.BuildId)).Outcome == "Unavailable" && custom.Source.Checks == 0 && custom.Source.Stages == 0, "Diagnostic rollback bypassed real installed history");
        });
        test("agent updates ready identity requires both exact build and nonempty exact job", () =>
        {
            var state = new AutomationUpdateState
            {
                Phase = "Ready",
                JobId = "job-a",
                TargetBuildId = Next.BuildId
            };
            Require(MainWindow.AgentReadyIdentityMatches(state, Prepared(), Next.BuildId, "job-a"), "Exact ready identity rejected");
            foreach (var job in new[]
            {
                "",
                "job-b"
            }

            )
                Require(!MainWindow.AgentReadyIdentityMatches(state, Prepared(), Next.BuildId, job), "Missing or foreign job accepted");
            Require(!MainWindow.AgentReadyIdentityMatches(state, Prepared(), "another-build", "job-a"), "Foreign build accepted");
            Require(!MainWindow.AgentReadyIdentityMatches(state, Prepared() with
            {
                Build = Prepared().Build with
                {
                    BuildId = "replaced"
                }
            }, Next.BuildId, "job-a"), "Replaced prepared identity accepted");
        });
        test("agent updates commit pin rejects same-build replacement, generation, job, expiry and package changes", () =>
        {
            var prepared = Prepared();
            var expires = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
            var signature = string.Join("|", prepared.Build.BuildId, prepared.Build.Version, prepared.Build.Commit, prepared.Build.Channel, prepared.PackagePath, null, null, null);
            Require(MainWindow.AgentApplyPinMatches(expires, "job", "job", 4, 4, prepared, prepared, signature), "Exact live pin rejected");
            Require(!MainWindow.AgentApplyPinMatches(expires, "job", "job", 4, 4, prepared, prepared with
            {
            }, signature), "Equivalent replaced object reused an acknowledged pin");
            Require(!MainWindow.AgentApplyPinMatches(expires, "job", "job", 4, 5, prepared, prepared, signature), "New generation reused old pin");
            Require(!MainWindow.AgentApplyPinMatches(expires, "job", "changed", 4, 4, prepared, prepared, signature), "New ready job reused old pin");
            Require(!MainWindow.AgentApplyPinMatches(0, "job", "job", 4, 4, prepared, prepared, signature), "Expired pin accepted");
            Require(!MainWindow.AgentApplyPinMatches(expires, "job", "job", 4, 4, prepared, prepared, signature + "changed"), "Package signature mismatch accepted");
        });
        test("agent updates disconnect before commit preserves ready receipt without native side effects", () =>
        {
            using var host = new Host();
            host.Check();
            host.Send(Request("update-download", Next.BuildId));
            host.Finish();
            var prepared = (PreparedUpdate)Field("preparedUpdate").GetValue(host.Window)!;
            var request = Request("update-apply", Next.BuildId, host.Status().JobId);
            var pinType = typeof(MainWindow).GetNestedType("AgentUpdateApplyPin", BindingFlags.NonPublic)!;
            var pin = Activator.CreateInstance(pinType, request.RequestId, request.JobId, 0L, prepared, "not-used-before-client-check", DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());
            Field("agentUpdateApplyPin").SetValue(host.Window, pin);
            var receipt = File.ReadAllBytes(host.Receipt);
            ReviewFixTests.Complete(host.Window.CommitAgentUpdate(request, CancellationToken.None, () => false));
            Require(host.Status().Phase == "Ready" && receipt.SequenceEqual(File.ReadAllBytes(host.Receipt)) && !host.Window.TransitionPending, "Disconnected apply crossed its acknowledgement boundary or changed Ready");
        });
        test("agent updates completion receipt requires actual installed process and all four target identity fields", () =>
        {
            var target = Prepared().Build;
            var state = new AutomationUpdateState
            {
                Phase = "Restarting",
                JobId = "job",
                TargetBuildId = target.BuildId,
                TargetVersion = target.Version,
                Terminal = false
            };
            Require(MainWindow.ResolveAgentUpdateReceipt(state, target, target, true, true) is { Phase: "Completed", Progress: 100, Terminal: true }, "Exact installed new process did not confirm target");
            foreach (var mismatch in new[]
            {
                target with
                {
                    Version = "1.0.0"
                },
                target with
                {
                    BuildId = "other"
                },
                target with
                {
                    Commit = "other"
                },
                target with
                {
                    Channel = target.Channel == "stable" ? "testing" : "stable"
                }
            }

            )
                Require(MainWindow.ResolveAgentUpdateReceipt(state, target, mismatch, true, true) is { Phase: "Interrupted", Terminal: true }, "Mismatched identity falsely confirmed completion");
            Require(MainWindow.ResolveAgentUpdateReceipt(state, target, target, false, true).Phase == "Interrupted", "Portable process confirmed installation");
            Require(MainWindow.ResolveAgentUpdateReceipt(state, target, target, true, false).Phase == "Restarting", "Old process claimed its own replacement completed");
            Require(MainWindow.ResolveAgentUpdateReceipt(state with
            {
                Phase = "Downloading"
            }, target, target, true, true).Phase == "Interrupted", "Unfinished download falsely completed on next launch");
        });
        foreach (var key in new[]
        {
            "state.targetBuildId",
            "state.targetVersion",
            "state.phase",
            "targetCommit",
            "targetChannel",
            "state.jobId",
            "state.message"
        }

        )
        {
            var invalidKey = key;
            test("agent updates reject corrupt receipt null field " + invalidKey, () =>
            {
                using var host = new Host();
                var document = Receipt();
                var parts = invalidKey.Split('.');
                var owner = parts.Length == 2 ? document[parts[0]]!.AsObject() : document;
                owner[parts[^1]] = null;
                File.WriteAllText(host.Receipt, document.ToJsonString());
                Require(host.Status() is { Phase: "Interrupted", Terminal: true } && host.Source.Checks == 0, "Null receipt field escaped validation or initiated network");
            });
        }

        test("agent updates reject truncated, oversized and absurd receipt identities", () =>
        {
            foreach (var contents in new[]
            {
                "{",
                new string ('x', 49153),
                ReceiptWithLongIdentity()
            }

            )
            {
                using var host = new Host();
                File.WriteAllText(host.Receipt, contents);
                Require(host.Status() is { Phase: "Interrupted", Terminal: true }, "Corrupt receipt was accepted");
            }
        });
        test("agent updates dispose before dispatch prevents even a first source request", () =>
        {
            using var host = new Host();
            host.Send(Request("update-check"));
            host.Window.Close();
            host.Finish();
            Require(host.Source.Checks == 0 && host.Source.Disposed, "Queued job ran after window cleanup");
            var receipt = JsonNode.Parse(File.ReadAllText(host.Receipt))!;
            Require(receipt["state"]!["phase"]!.GetValue<string>() == "Interrupted", "Cleanup did not preserve interrupted job receipt");
        });
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("Missing field: " + name);
    private static JsonObject Receipt() => new()
    {
        ["schemaVersion"] = 1,
        ["originPid"] = -1,
        ["originStarted"] = 1,
        ["targetCommit"] = Next.Commit,
        ["targetChannel"] = Next.Channel,
        ["state"] = new JsonObject
        {
            ["phase"] = "Restarting",
            ["jobId"] = "previous-job",
            ["terminal"] = false,
            ["targetBuildId"] = Next.BuildId,
            ["targetVersion"] = Next.Version,
            ["message"] = "Previous update",
            ["progress"] = 0
        }
    };
    private static string ReceiptWithLongIdentity()
    {
        var value = Receipt();
        value["state"]!["targetBuildId"] = new string('a', 161);
        return value.ToJsonString();
    }

    private sealed class Host : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        internal readonly SourceFake Source = new();
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "AgentUpdateTests", Guid.NewGuid().ToString("N"));
        internal string Receipt => Path.Combine(Root, "agent-update-receipt.json");
        internal MainWindow Window
        {
            get;
        }

        internal Host(bool defaultSource = false)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = Application.Current ?? new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            if (app.TryFindResource(ThemeSupport.Card) is null)
                ThemeSupport.ForceForValidation(app, false);
            Directory.CreateDirectory(Root);
            Window = new MainWindow(renderOnly: true, platform: new PreviewPlatform(new(101, 42, 100, "Fixture game", "fixture")), diagnostics: new(Root), updates: defaultSource ? null : Source);
            Window.PhysicalKeyReader = _ => 0;
            Window.Controller.CaptureReturn();
            ReviewFixTests.Complete(Window.Controller.Refresh());
            Window.RefreshRows();
        }

        internal AutomationReply Send(AutomationRequest request, CancellationToken token = default, Func<bool>? continuing = null) => Complete(Window.AgentUpdate(request, token, continuing ?? (() => true)));
        internal AutomationUpdateState Status() => Send(Request("update-status")).Update ?? throw new Exception("Status missing update snapshot");
        internal void Check()
        {
            Require(Send(Request("update-check")).Outcome == "Requested", "Fixture query refused");
            Finish();
        }

        internal void Finish()
        {
            ReviewFixTests.Complete(Window.UpdateTask);
            Flush();
        }

        public void Dispose()
        {
            try
            {
                Window.Close();
                Source.CheckCompletion?.TrySetCanceled();
                Source.StageCompletion?.TrySetCanceled();
                Finish();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                Directory.Delete(Root, true);
            }
        }
    }

    private sealed class SourceFake : IUpdateSource
    {
        internal int Checks, Stages, Discards;
        internal bool Disposed;
        internal string Channel = "";
        internal CancellationToken CheckToken;
        internal IProgress<UpdateProgress>? Progress;
        internal IReadOnlyList<UpdateCandidate> Releases = [Next];
        internal TaskCompletionSource<IReadOnlyList<UpdateCandidate>>? CheckCompletion;
        internal TaskCompletionSource<PreparedUpdate>? StageCompletion;
        internal PreparedUpdate? StageResult;
        internal Exception? CheckFailure, StageFailure;
        public Task<IReadOnlyList<UpdateCandidate>> CheckAsync(string channel, CancellationToken token)
        {
            Checks++;
            Channel = channel;
            CheckToken = token;
            if (CheckFailure is { } failure)
                return Task.FromException<IReadOnlyList<UpdateCandidate>>(failure);
            return CheckCompletion?.Task ?? Task.FromResult(Releases);
        }

        public Task<PreparedUpdate> StageAsync(UpdateCandidate candidate, IProgress<UpdateProgress>? progress, CancellationToken token)
        {
            Stages++;
            Progress = progress;
            if (StageFailure is { } failure)
                return Task.FromException<PreparedUpdate>(failure);
            return StageCompletion?.Task ?? Task.FromResult(StageResult ?? Prepared(candidate));
        }

        public void Discard(PreparedUpdate prepared) => Discards++;
        public void Dispose() => Disposed = true;
    }
}

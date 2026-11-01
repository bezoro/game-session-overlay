using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ApolloOverlay;
using ApolloOverlay.Automation;

internal static class IsolatedActivationTests
{
    internal const string HangArgument = "--activation-test-hang";
    internal const string ParentExitArgument = "--activation-test-parent-exit";
    internal const string PassiveArgument = "--activation-test-passive";
    internal const string BrokerArgument = "--activation-test-broker";
    internal const string ForegroundArgument = "--activation-test-foreground";

    internal static int RunBrokerHost(string encoded, string receiptPath)
    {
        using var current = Process.GetCurrentProcess();
        var stages = new List<NativeStage>();
        var code = 1;
        try
        {
            code = IsolatedWindowActivation.RunHost(encoded, (window, foreground, owner) =>
            {
                var native = new BrokerRequiredNative(window, stages);
                return new WindowActivation(native).Execute(window, foreground, owner, CancellationToken.None)
                    ? WindowActivation.BrokerConfirmed : native.FailureCode;
            });
            return code;
        }
        finally
        {
            File.WriteAllText(receiptPath, JsonSerializer.Serialize(new BrokerReceipt(current.Id,
                current.StartTime.ToUniversalTime().Ticks, code, stages)));
        }
    }

    internal static int RunForegroundHost(string readyName, string identityPath)
    {
        using var current = Process.GetCurrentProcess();
        using var ready = EventWaitHandle.OpenExisting(readyName);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new Window { Title = "Activation separate-process foreground fixture", Width = 180, Height = 100, Left = -12000, Top = -12000 };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        var deadline = new Stopwatch();
        nint handle = 0;
        window.Loaded += (_, _) =>
        {
            handle = new WindowInteropHelper(window).Handle;
            Native.SetForegroundWindow(handle);
            deadline.Start();
            timer.Start();
        };
        timer.Tick += (_, _) =>
        {
            var confirmed = Native.GetForegroundWindow() == handle && window.IsActive;
            if (!confirmed && deadline.Elapsed < TimeSpan.FromSeconds(3))
                return;
            timer.Stop();
            File.WriteAllText(identityPath, JsonSerializer.Serialize(new ForegroundIdentity(current.Id,
                current.StartTime.ToUniversalTime().Ticks, handle.ToInt64(), confirmed)));
            ready.Set();
            if (!confirmed)
                app.Shutdown(3);
        };
        try
        {
            return app.Run(window);
        }
        finally { timer.Stop(); }
    }

    internal static int Hang(string readyName, string identityPath)
    {
        using var current = Process.GetCurrentProcess();
        File.WriteAllText(identityPath, JsonSerializer.Serialize(new ChildIdentity(current.Id, current.StartTime.ToUniversalTime().Ticks)));
        using var ready = EventWaitHandle.OpenExisting(readyName);
        ready.Set();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    internal static int ExitParent(string readyName, string identityPath)
    {
        using var job = new ActivationProcessJob();
        using var child = Process.Start(ProbeStart(HangArgument, readyName, identityPath))
            ?? throw new InvalidOperationException("Job fixture child was not started.");
        job.Attach(child);
        using var ready = EventWaitHandle.OpenExisting(readyName);
        if (!ready.WaitOne(TimeSpan.FromSeconds(5)))
            return 3;
        // Environment.Exit deliberately bypasses finally/Dispose. The kernel must
        // close the parent-owned job and terminate its already-running child.
        Environment.Exit(0);
        return 0;
    }

    internal static void Run(Action<string, Action> test)
    {
        test("isolated native broker transfers real foreground after direct parent requests are refused", () =>
        {
            using var windows = new Windows(separateProcessForeground: true);
            using var proof = new Probe();
            var brokerCompleted = 0;
            var brokerHelperPid = 0;
            var client = new IsolatedWindowActivation(encoded => ProbeStart(BrokerArgument, encoded, proof.ReceiptPath), (message, outcome) =>
            {
                Trace(message, outcome);
                if (message.Contains("stage=broker-confirmed", StringComparison.Ordinal) && outcome == ResultKind.Completed)
                    Interlocked.Exchange(ref brokerCompleted, 1);
                const string startPrefix = "focus-host/start/pid=";
                if (message.StartsWith(startPrefix, StringComparison.Ordinal) && int.TryParse(message.AsSpan(startPrefix.Length), out var pid))
                    Interlocked.Exchange(ref brokerHelperPid, pid);
            });
            var acquired = Complete(client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, CancellationToken.None, timeoutMs: 3000));
            Require(File.Exists(proof.ReceiptPath) && new FileInfo(proof.ReceiptPath).Length <= 65536, "The bounded broker-native receipt was not produced");
            var receiptText = File.ReadAllText(proof.ReceiptPath);
            Console.WriteLine("ACTIVATION broker-native-receipt " + receiptText);
            var receipt = JsonSerializer.Deserialize<BrokerReceipt>(receiptText) ?? throw new Exception("The broker receipt was empty");
            Require(acquired,
                "The real helper-owned broker did not transfer foreground after direct refusal");
            windows.RequireTargetActivation();
            Require(receipt.Pid != Environment.ProcessId && receipt.Pid == Volatile.Read(ref brokerHelperPid) && receipt.Started > 0,
                "The broker receipt did not identify the exact separate helper");
            var broker = receipt.Stages.SingleOrDefault(stage => stage.Name == "broker-created")?.Window ?? 0;
            Require(receipt.Stages.Any(stage => stage.Name == "direct-target-refused" && stage.Window == windows.TargetHandle && stage.Accepted == false)
                && broker != 0
                && receipt.Stages.Any(stage => stage.Name == "broker-acknowledged" && stage.Window == broker && stage.Foreground == broker && stage.Accepted == true)
                && receipt.Stages.Any(stage => stage.Name == "target-transfer-request" && stage.Window == windows.TargetHandle && stage.PreviousForeground == broker && stage.Accepted == true),
                "The receipt did not prove direct refusal, real broker acquisition, and native target transfer");
            // The helper may finish before its cross-queue acknowledgement. Its
            // native stages and the parent's actual WPF ownership prove the path;
            // an unsuccessful helper result keeps its honest failure diagnostic.
            Require(Volatile.Read(ref brokerCompleted) == (receipt.Code == WindowActivation.BrokerConfirmed ? 1 : 0),
                "The broker's native completion diagnostic disagreed with its exact receipt");
        });
        test("isolated activation transfers real native and WPF focus across GUI threads", () =>
        {
            using var windows = new Windows();
            var client = new IsolatedWindowActivation(trace: Trace);
            Require(Complete(client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, CancellationToken.None, timeoutMs: 3000)), "The real authenticated helper did not confirm activation");
            windows.RequireTargetActivation();
        });
        test("expired activation helper is terminated and the next opening recovers", () =>
        {
            using var windows = new Windows();
            using var probe = new Probe();
            var starts = 0;
            var client = new IsolatedWindowActivation(encoded => ++starts == 1
                ? ProbeStart(HangArgument, probe.ReadyName, probe.IdentityPath)
                : IsolatedWindowActivation.CreateStartInfo(encoded), Trace);
            var elapsed = Stopwatch.StartNew();
            var blocked = client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, CancellationToken.None, timeoutMs: 2000);
            PumpUntil(() => probe.Ready.WaitOne(0), "The helper never entered its blocking fixture");
            var child = probe.Identity();
            Require(!Complete(blocked), "An expired helper claimed successful activation");
            Require(elapsed.Elapsed < TimeSpan.FromSeconds(4), "A non-returning helper blocked the caller beyond its bounded cleanup");
            RequireStopped(child);
            Require(Native.GetForegroundWindow() == windows.ForegroundHandle && !windows.Target.IsActive, "A timed-out helper changed foreground ownership");
            Require(Complete(client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, CancellationToken.None)), "A timed-out child permanently blocked the next opening");
            Require(starts == 2, "Recovery did not start a fresh helper");
            windows.RequireTargetActivation();
        });
        test("activation cancellation kills its helper and preserves a fresh retry", () =>
        {
            using var windows = new Windows();
            using var probe = new Probe();
            using var cancellation = new CancellationTokenSource();
            var starts = 0;
            var client = new IsolatedWindowActivation(encoded => ++starts == 1
                ? ProbeStart(HangArgument, probe.ReadyName, probe.IdentityPath)
                : IsolatedWindowActivation.CreateStartInfo(encoded), Trace);
            var pending = client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, cancellation.Token, timeoutMs: 4000);
            PumpUntil(() => probe.Ready.WaitOne(0), "The cancellable helper never started");
            var child = probe.Identity();
            cancellation.Cancel();
            Require(!Complete(pending), "A cancelled request claimed activation");
            RequireStopped(child);
            Require(Native.GetForegroundWindow() == windows.ForegroundHandle, "A cancelled helper took focus");
            Require(Complete(client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, CancellationToken.None, timeoutMs: 3000)), "Cancellation left the activation client permanently busy");
            windows.RequireTargetActivation();
        });
        foreach (var closeTarget in new[] { false, true })
            test("host success without observed target ownership is rejected target-closed=" + closeTarget, () =>
            {
                using var windows = new Windows();
                var observedSuccessfulExit = false;
                var client = new IsolatedWindowActivation(_ =>
                {
                    if (closeTarget)
                        windows.Target.Close();
                    return PassiveHost();
                }, (message, kind) =>
                {
                    Trace(message, kind);
                    observedSuccessfulExit |= message.StartsWith("focus-host/exit/code=0/", StringComparison.Ordinal);
                });
                Require(!Complete(client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, CancellationToken.None, timeoutMs: 3000)), "An exit code fabricated native ownership of an unchanged or destroyed target");
                Require(observedSuccessfulExit, "PRECONDITION: the passive host did not finish successfully");
                Require(Native.GetForegroundWindow() == windows.ForegroundHandle, "A passive host changed the competing native foreground");
            });
        test("activation deadline terminates its helper while the WPF dispatcher is blocked", () =>
        {
            using var windows = new Windows();
            using var probe = new Probe();
            using var cancellation = new CancellationTokenSource();
            var client = new IsolatedWindowActivation(_ => ProbeStart(HangArgument, probe.ReadyName, probe.IdentityPath), Trace);
            var pending = client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, cancellation.Token, timeoutMs: 2000);
            try
            {
                PumpUntil(() => probe.Ready.WaitOne(0), "The blocking helper never became ready");
                var identity = probe.Identity();
                using var child = Process.GetProcessById(identity.Pid);
                Require(child.StartTime.ToUniversalTime().Ticks == identity.Started, "The helper lifetime changed before observation");
                // Deliberately do not pump WPF while another thread observes the child.
                // A captured dispatcher continuation would leave the job alive here.
                var stopped = Task.Run(() => child.WaitForExit(4000));
                Require(stopped.Wait(4500) && stopped.Result, "Deadline cleanup depended on the blocked WPF dispatcher");
                Require(pending.Wait(1000) && !pending.GetAwaiter().GetResult(), "The deadline did not complete independently of WPF");
                RequireStopped(identity);
            }
            finally
            {
                cancellation.Cancel();
                Complete(pending);
            }
        });
        test("a concurrent activation request cannot spawn a second helper", () =>
        {
            using var windows = new Windows();
            using var probe = new Probe();
            using var cancellation = new CancellationTokenSource();
            var starts = 0;
            var client = new IsolatedWindowActivation(_ =>
            {
                starts++;
                return ProbeStart(HangArgument, probe.ReadyName, probe.IdentityPath);
            }, Trace);
            var first = client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, cancellation.Token, timeoutMs: 4000);
            try
            {
                PumpUntil(() => probe.Ready.WaitOne(0), "The first helper did not start");
                Require(!Complete(client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, CancellationToken.None)), "A concurrent request was admitted");
                Require(starts == 1, "A concurrent request created an unbounded helper");
            }
            finally
            {
                cancellation.Cancel();
                Complete(first);
                if (File.Exists(probe.IdentityPath))
                    RequireStopped(probe.Identity());
            }
        });
        test("activation job kills a running child when its owning process exits", () =>
        {
            using var probe = new Probe();
            using var parent = Process.Start(ProbeStart(ParentExitArgument, probe.ReadyName, probe.IdentityPath))
                ?? throw new Exception("Job-owner fixture was not started");
            try
            {
                PumpUntil(() => probe.Ready.WaitOne(0), "The job-owner child did not reach its blocking fixture");
                var child = probe.Identity();
                Complete(parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)));
                Require(parent.ExitCode == 0, "Job-owner fixture did not exit normally");
                RequireStopped(child);
            }
            finally
            {
                if (!parent.HasExited)
                    parent.Kill(entireProcessTree: true);
            }
        });

        foreach (var invalid in new[] { "parent-pid", "parent-start", "parent-user", "null-parent", "null-job", "target-thread" })
            test("activation host rejects forged request " + invalid, () =>
            {
                using var windows = new Windows();
                var client = new IsolatedWindowActivation(encoded =>
                {
                    var request = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)))!.AsObject();
                    var peer = request["Parent"]!.AsObject();
                    switch (invalid)
                    {
                        case "parent-pid":
                            peer["Pid"] = Environment.ProcessId + 1;
                            break;
                        case "parent-start":
                            peer["Started"] = peer["Started"]!.GetValue<long>() + 1;
                            break;
                        case "parent-user":
                            peer["User"] = "S-1-5-18";
                            break;
                        case "null-parent":
                            request["Parent"] = null;
                            break;
                        case "null-job":
                            request["Job"] = null;
                            break;
                        case "target-thread":
                            request["Window"] = windows.ForegroundHandle.ToInt64();
                            break;
                    }
                    return IsolatedWindowActivation.CreateStartInfo(Convert.ToBase64String(Encoding.UTF8.GetBytes(request.ToJsonString())));
                }, Trace);
                Require(!Complete(client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, CancellationToken.None, timeoutMs: 3000)), "A forged host request was accepted");
                Require(Native.GetForegroundWindow() == windows.ForegroundHandle && !windows.Target.IsActive, "Rejected input still changed focus");
            });

        test("activation host cannot run native calls without membership in its exact owning job", () =>
        {
            using var windows = new Windows();
            var owner = Native.GetWindowThreadProcessId(windows.TargetHandle, out _);
            var other = Native.GetWindowThreadProcessId(windows.ForegroundHandle, out var otherPid);
            var request = new
            {
                Parent = AutomationPipeSecurity.Current(),
                Window = windows.TargetHandle.ToInt64(),
                Foreground = windows.ForegroundHandle.ToInt64(),
                Owner = owner,
                Other = other,
                OtherPid = otherPid,
                Job = @"Local\Bezoro.Overlay.Activation." + Guid.NewGuid().ToString("N")
            };
            var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request));
            using var child = Process.Start(IsolatedWindowActivation.CreateStartInfo(encoded)) ?? throw new Exception("Unassigned helper did not start");
            try
            {
                Complete(child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)));
                Require(child.ExitCode == 6, "An unassigned helper did not reject missing job ownership");
                Require(Native.GetForegroundWindow() == windows.ForegroundHandle, "An unassigned helper changed focus");
            }
            finally
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
            }
        });
        test("activation host rejects malformed input without starting UI", () =>
        {
            foreach (var payload in new[] { "invalid-base64", Convert.ToBase64String("null"u8), Convert.ToBase64String("{}"u8), new string('A', 4097) })
                Require(IsolatedWindowActivation.RunHost(payload) == 4, "Malformed host input did not fail closed");
        });
    }

    private static void Trace(string message, ResultKind kind) => Console.WriteLine("ACTIVATION " + kind + " " + message);

    private static ProcessStartInfo PassiveHost()
    {
        // The same minimal test host avoids measuring an unrelated shell's cold
        // startup. It performs no activation and still must actually exit zero.
        return ProbeStart(PassiveArgument);
    }

    private static ProcessStartInfo ProbeStart(params string[] arguments)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new Exception("Test executable unavailable"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    private static bool Complete(Task<bool> task)
    {
        ReviewFixTests.Complete(task);
        return task.Result;
    }
    private static void Complete(Task task) => ReviewFixTests.Complete(task);

    private static void PumpUntil(Func<bool> condition, string failure, int timeoutMs = 3000)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Require(deadline.ElapsedMilliseconds < timeoutMs, failure);
            Complete(Task.Delay(10));
        }
    }

    private static void RequireStopped(ChildIdentity identity)
    {
        try
        {
            using var child = Process.GetProcessById(identity.Pid);
            if (child.StartTime.ToUniversalTime().Ticks != identity.Started)
                return;
            Require(child.WaitForExit(1000), "The owning job left its blocked helper alive");
        }
        catch (ArgumentException)
        {
            // The exact process has already left the process table.
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed record ChildIdentity(int Pid, long Started);
    private sealed record ForegroundIdentity(int Pid, long Started, long Window, bool Confirmed);
    private sealed record NativeStage(string Name, long Window, long PreviousForeground, long Foreground, bool? Accepted, int FailureCode, uint Thread = 0);
    private sealed record BrokerReceipt(int Pid, long Started, int Code, List<NativeStage> Stages);

    private sealed class BrokerRequiredNative(nint target, List<NativeStage> stages) : IWindowActivationNative
    {
        private readonly WindowActivation.Win32Activation native = new();
        private nint broker;
        internal int FailureCode => native.FailureCode;
        public uint CurrentThread => native.CurrentThread;
        public nint Foreground
        {
            get
            {
                var observed = native.Foreground;
                stages.Add(new("foreground-observed", 0, observed.ToInt64(), observed.ToInt64(), null, native.FailureCode));
                return observed;
            }
        }
        public uint WindowThread(nint window)
        {
            var thread = native.WindowThread(window);
            Record("window-thread", window, native.Foreground, thread: thread);
            return thread;
        }
        public void EnsureQueue()
        {
            native.EnsureQueue();
            Record("queue-created", 0, native.Foreground);
        }
        public bool Attach(uint current, uint foreground, bool attach)
        {
            var previous = native.Foreground;
            var accepted = native.Attach(current, foreground, attach);
            Record(attach ? "queue-attached" : "queue-detached", 0, previous, accepted, current);
            return accepted;
        }
        public bool AwaitActivation(nint window, int timeoutMs)
        {
            var previous = native.Foreground;
            var accepted = native.AwaitActivation(window, timeoutMs);
            Record(window == broker ? "broker-acknowledged" : "target-acknowledged", window, previous, accepted);
            return accepted;
        }
        public bool SetForeground(nint window)
        {
            var previous = native.Foreground;
            // Model the observed refusal while preserving every real native call
            // needed to acquire the broker and complete the parent's WPF activation.
            if (window == target && (broker == 0 || previous != broker))
            {
                Record("direct-target-refused", window, previous, false);
                return false;
            }
            var accepted = native.SetForeground(window);
            Record(window == broker ? "broker-request" : window == target ? "target-transfer-request" : "foreground-restore-request", window, previous, accepted);
            return accepted;
        }
        public nint CreateBrokerWindow()
        {
            var previous = native.Foreground;
            broker = native.CreateBrokerWindow();
            Record("broker-created", broker, previous, broker != 0, native.CurrentThread);
            return broker;
        }
        public void DestroyBrokerWindow(nint window)
        {
            var previous = native.Foreground;
            native.DestroyBrokerWindow(window);
            Record("broker-destroyed", window, previous);
        }
        private void Record(string name, nint window, nint previous, bool? accepted = null, uint thread = 0) =>
            stages.Add(new(name, window.ToInt64(), previous.ToInt64(), native.Foreground.ToInt64(), accepted, native.FailureCode, thread));
    }

    private sealed class Probe : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "overlay-activation-" + Guid.NewGuid().ToString("N"));
        internal string ReadyName { get; } = @"Local\Overlay.Activation.Test." + Guid.NewGuid().ToString("N");
        internal EventWaitHandle Ready
        {
            get;
        }
        internal string IdentityPath => Path.Combine(root, "child.json");
        internal string ReceiptPath => Path.Combine(root, "broker.json");
        internal Probe()
        {
            Directory.CreateDirectory(root);
            Ready = new(false, EventResetMode.ManualReset, ReadyName);
        }
        internal ChildIdentity Identity() => JsonSerializer.Deserialize<ChildIdentity>(File.ReadAllText(IdentityPath)) ?? throw new Exception("Helper identity absent");
        public void Dispose()
        {
            Ready.Dispose();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private sealed class Windows : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        private Thread? foregroundThread;
        private Dispatcher? foregroundDispatcher;
        private Action? closeForeground;
        private ActivationProcessJob? foregroundJob;
        private Process? foregroundProcess;
        private Probe? foregroundProbe;
        private ChildIdentity? foregroundIdentity;
        private bool foregroundAttached;
        internal Window Target
        {
            get;
        }
        internal nint TargetHandle
        {
            get; private set;
        }
        internal nint ForegroundHandle
        {
            get; private set;
        }

        internal Windows(bool separateProcessForeground = false)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Target = new()
            {
                Title = "Activation target fixture",
                Width = 180,
                Height = 100,
                Left = -12000,
                Top = -12000,
                ShowActivated = !separateProcessForeground
            };
            try
            {
                Target.Show();
                TargetHandle = new WindowInteropHelper(Target).Handle;
                Target.Activate();
                Native.SetForegroundWindow(TargetHandle);
                PumpUntil(() => Native.GetForegroundWindow() == TargetHandle && Target.IsActive,
                    "The initial target fixture did not obtain real foreground ownership");
                if (separateProcessForeground)
                {
                    StartForegroundProcess();
                    return;
                }
                var ready = new TaskCompletionSource<(Dispatcher Dispatcher, nint Handle, Action Close)>(TaskCreationOptions.RunContinuationsAsynchronously);
                foregroundThread = new Thread(() =>
                {
                    Window? window = null;
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    try
                    {
                        window = new Window { Title = "Activation foreground fixture", Width = 180, Height = 100, Left = -12000, Top = -12000 };
                        window.Show();
                        var handle = new WindowInteropHelper(window).Handle;
                        ready.SetResult((dispatcher, handle, () => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background)));
                        Dispatcher.Run();
                    }
                    catch (Exception error)
                    {
                        ready.TrySetException(error);
                    }
                    finally
                    {
                        window?.Close();
                    }
                })
                {
                    IsBackground = true,
                    Name = "Activation foreground fixture"
                };
                foregroundThread.SetApartmentState(ApartmentState.STA);
                foregroundThread.Start();
                Complete(ready.Task);
                var other = ready.Task.Result;
                foregroundDispatcher = other.Dispatcher;
                ForegroundHandle = other.Handle;
                closeForeground = other.Close;
                foregroundDispatcher.BeginInvoke(DispatcherPriority.Normal, () => Native.SetForegroundWindow(ForegroundHandle));
                PumpUntil(() => Native.GetForegroundWindow() == ForegroundHandle && !Target.IsActive,
                    "The independent foreground GUI thread was not activated");
                Console.WriteLine($"ACTIVATION fixture/target=0x{TargetHandle:X}/foreground=0x{ForegroundHandle:X}/target-thread={Native.GetWindowThreadProcessId(TargetHandle, out _)}/other-thread={Native.GetWindowThreadProcessId(ForegroundHandle, out _)}");
            }
            catch
            {
                Console.WriteLine($"ACTIVATION fixture/rejected/observed=0x{Native.GetForegroundWindow():X}/target=0x{TargetHandle:X}/other=0x{ForegroundHandle:X}/active={Target.IsActive}");
                Cleanup(requireStopped: false);
                throw;
            }
        }

        public void Dispose() => Cleanup(requireStopped: true);

        internal void RequireTargetActivation() => PumpUntil(
            () => Native.GetForegroundWindow() == TargetHandle && Target.IsActive,
            "Native foreground ownership did not complete its WPF activation");

        private void StartForegroundProcess()
        {
            foregroundProbe = new();
            foregroundJob = new();
            foregroundProcess = Process.Start(ProbeStart(ForegroundArgument, foregroundProbe.ReadyName, foregroundProbe.IdentityPath))
                ?? throw new Exception("The separate foreground process did not start");
            foregroundIdentity = new(foregroundProcess.Id, foregroundProcess.StartTime.ToUniversalTime().Ticks);
            foregroundJob.Attach(foregroundProcess);
            foregroundAttached = true;
            PumpUntil(() => foregroundProbe.Ready.WaitOne(0), "The separate foreground process never published its native identity", timeoutMs: 5000);
            Require(new FileInfo(foregroundProbe.IdentityPath).Length <= 4096, "The separate foreground identity exceeded its fixture bound");
            var identity = JsonSerializer.Deserialize<ForegroundIdentity>(File.ReadAllText(foregroundProbe.IdentityPath))
                ?? throw new Exception("The separate foreground identity was empty");
            Require(identity.Pid == foregroundIdentity.Pid && identity.Started == foregroundIdentity.Started && identity.Confirmed
                && identity.Pid != Environment.ProcessId && identity.Window != 0 && !foregroundProcess.HasExited,
                "The separate foreground fixture did not confirm its exact live process");
            ForegroundHandle = (nint)identity.Window;
            Require(Native.GetWindowThreadProcessId(ForegroundHandle, out var pid) != 0 && pid == identity.Pid,
                "The foreground fixture handle belonged to a different process");
            PumpUntil(() => Native.GetForegroundWindow() == ForegroundHandle && !Target.IsActive,
                "The separate foreground process did not retain real foreground ownership");
            Console.WriteLine($"ACTIVATION separate-process-fixture/target=0x{TargetHandle:X}/foreground=0x{ForegroundHandle:X}/foreground-pid={identity.Pid}/foreground-started={identity.Started}");
        }

        private void Cleanup(bool requireStopped)
        {
            try
            {
                foregroundJob?.Dispose();
                if (foregroundProcess is { } process)
                {
                    // The Process returned by Start owns the exact child handle;
                    // never reacquire an arbitrary PID when terminating a fixture.
                    if (!foregroundAttached && !process.HasExited)
                        process.Kill(entireProcessTree: true);
                    Complete(process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)));
                    if (requireStopped && foregroundIdentity is { } identity)
                        RequireStopped(identity);
                }
                var dispatcher = foregroundDispatcher ?? (foregroundThread is null ? null : Dispatcher.FromThread(foregroundThread));
                if (dispatcher is { HasShutdownStarted: false })
                    dispatcher.BeginInvoke(DispatcherPriority.Normal, closeForeground ?? (() => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background)));
                if (foregroundThread is { IsAlive: true } thread)
                {
                    var joined = Task.Run(() => thread.Join(TimeSpan.FromSeconds(3)));
                    Complete(joined);
                    if (requireStopped)
                        Require(joined.Result, "Foreground fixture GUI thread did not shut down");
                    else if (!joined.Result)
                        Console.WriteLine("ACTIVATION fixture/cleanup-thread-did-not-stop");
                }
            }
            finally
            {
                foregroundProcess?.Dispose();
                foregroundProbe?.Dispose();
                Target.Close();
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
    }
}

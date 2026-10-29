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
        test("isolated activation transfers real native and WPF focus across GUI threads", () =>
        {
            using var windows = new Windows();
            var client = new IsolatedWindowActivation();
            Require(Complete(client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, CancellationToken.None, timeoutMs: 3000)), "The real authenticated helper did not confirm activation");
            Require(Native.GetForegroundWindow() == windows.TargetHandle && windows.Target.IsActive, "Helper success did not correspond to native and WPF activation");
        });
        test("expired activation helper is terminated and the next opening recovers", () =>
        {
            using var windows = new Windows();
            using var probe = new Probe();
            var starts = 0;
            var client = new IsolatedWindowActivation(encoded => ++starts == 1
                ? ProbeStart(HangArgument, probe.ReadyName, probe.IdentityPath)
                : IsolatedWindowActivation.CreateStartInfo(encoded));
            var elapsed = Stopwatch.StartNew();
            var blocked = client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, CancellationToken.None, timeoutMs: 2000);
            PumpUntil(() => probe.Ready.WaitOne(0), "The helper never entered its blocking fixture");
            var child = probe.Identity();
            Require(!Complete(blocked), "An expired helper claimed successful activation");
            Require(elapsed.Elapsed < TimeSpan.FromSeconds(4), "A non-returning helper blocked the caller beyond its bounded cleanup");
            RequireStopped(child);
            Require(Native.GetForegroundWindow() == windows.ForegroundHandle && !windows.Target.IsActive, "A timed-out helper changed foreground ownership");
            Require(Complete(client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, CancellationToken.None)), "A timed-out child permanently blocked the next opening");
            Require(starts == 2 && Native.GetForegroundWindow() == windows.TargetHandle && windows.Target.IsActive, "Recovery did not use a fresh helper and real focus");
        });
        test("activation cancellation kills its helper and preserves a fresh retry", () =>
        {
            using var windows = new Windows();
            using var probe = new Probe();
            using var cancellation = new CancellationTokenSource();
            var starts = 0;
            var client = new IsolatedWindowActivation(encoded => ++starts == 1
                ? ProbeStart(HangArgument, probe.ReadyName, probe.IdentityPath)
                : IsolatedWindowActivation.CreateStartInfo(encoded));
            var pending = client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, cancellation.Token, timeoutMs: 4000);
            PumpUntil(() => probe.Ready.WaitOne(0), "The cancellable helper never started");
            var child = probe.Identity();
            cancellation.Cancel();
            Require(!Complete(pending), "A cancelled request claimed activation");
            RequireStopped(child);
            Require(Native.GetForegroundWindow() == windows.ForegroundHandle, "A cancelled helper took focus");
            Require(Complete(client.RequestAsync(windows.TargetHandle, windows.ForegroundHandle, CancellationToken.None, timeoutMs: 3000)), "Cancellation left the activation client permanently busy");
            Require(windows.Target.IsActive && Native.GetForegroundWindow() == windows.TargetHandle, "Retry after cancellation did not acquire real focus");
        });
        test("activation deadline terminates its helper while the WPF dispatcher is blocked", () =>
        {
            using var windows = new Windows();
            using var probe = new Probe();
            using var cancellation = new CancellationTokenSource();
            var client = new IsolatedWindowActivation(_ => ProbeStart(HangArgument, probe.ReadyName, probe.IdentityPath));
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
            });
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
                });
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

    private static void PumpUntil(Func<bool> condition, string failure)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Require(deadline.Elapsed < TimeSpan.FromSeconds(3), failure);
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

    private sealed class Probe : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "overlay-activation-" + Guid.NewGuid().ToString("N"));
        internal string ReadyName { get; } = @"Local\Overlay.Activation.Test." + Guid.NewGuid().ToString("N");
        internal EventWaitHandle Ready
        {
            get;
        }
        internal string IdentityPath => Path.Combine(root, "child.json");
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
        private readonly Thread foregroundThread;
        private readonly Dispatcher foregroundDispatcher;
        private readonly Action closeForeground;
        internal Window Target
        {
            get;
        }
        internal nint TargetHandle
        {
            get;
        }
        internal nint ForegroundHandle
        {
            get;
        }

        internal Windows()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Target = new()
            {
                Title = "Activation target fixture",
                Width = 180,
                Height = 100,
                Left = -12000,
                Top = -12000
            };
            Target.Show();
            TargetHandle = new WindowInteropHelper(Target).Handle;
            var ready = new TaskCompletionSource<(Dispatcher Dispatcher, nint Handle, Action Close)>(TaskCreationOptions.RunContinuationsAsynchronously);
            foregroundThread = new Thread(() =>
            {
                try
                {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    var window = new Window { Title = "Activation foreground fixture", Width = 180, Height = 100, Left = -12000, Top = -12000 };
                    window.Show();
                    var handle = new WindowInteropHelper(window).Handle;
                    ready.SetResult((dispatcher, handle, () =>
                    {
                        window.Close();
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                    ));
                    Dispatcher.Run();
                }
                catch (Exception error)
                {
                    ready.TrySetException(error);
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
            PumpUntil(() => Native.GetForegroundWindow() == ForegroundHandle && !Target.IsActive, "The independent foreground GUI thread was not activated");
        }

        public void Dispose()
        {
            try
            {
                Target.Close();
                foregroundDispatcher.BeginInvoke(DispatcherPriority.Normal, closeForeground);
                var joined = Task.Run(() => foregroundThread.Join(TimeSpan.FromSeconds(3)));
                Complete(joined);
                Require(joined.Result, "Foreground fixture GUI thread did not shut down");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
    }
}

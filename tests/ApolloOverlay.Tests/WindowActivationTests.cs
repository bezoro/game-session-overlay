using System.Diagnostics;
using ApolloOverlay;

internal static class WindowActivationTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("Guide broker keeps WPF queue separate and detaches after activation", () =>
        {
            var api = new Fake();
            var broker = new WindowActivation(api);
            Require(Wait(broker.RequestAsync(10, 20, CancellationToken.None)) && api.Foreground == 10 && api.Requests == 1);
            Require(api.Attachments.SequenceEqual(new[] { true, false }) && !api.Attached && api.QueueCreated);
        });
        test("Guide broker cannot grant foreground merely by attaching", () =>
        {
            var api = new Fake
            {
                Accepted = false
            };
            Require(!Wait(new WindowActivation(api).RequestAsync(10, 20, CancellationToken.None)) && api.Foreground == 20 && api.Requests == 1);
            Require(api.Attachments.SequenceEqual(new[] { true, false }) && !api.Attached);
        });
        test("Guide broker waits for asynchronous activation before detaching", () =>
        {
            var api = new Fake { DeferredActivation = true };
            Require(Wait(new WindowActivation(api).RequestAsync(10, 20, CancellationToken.None)));
            Require(api.Foreground == 10 && api.ActivationWaits == 1 && !api.Attached);
        });
        test("Guide broker rejects an accepted request without observed activation", () =>
        {
            var api = new Fake { DeferredActivation = true, ActivateOnWait = false };
            Require(!Wait(new WindowActivation(api).RequestAsync(10, 20, CancellationToken.None)));
            Require(api.Foreground == 20 && api.ActivationWaits == 1 && !api.Attached);
        });
        test("Guide broker activation timeout cannot claim foreground ownership", () =>
        {
            var api = new Fake { Acknowledged = false };
            Require(!Wait(new WindowActivation(api).RequestAsync(10, 20, CancellationToken.None)));
            Require(api.ActivationWaits == 1 && !api.Attached);
        });
        test("Guide broker cancellation while acknowledging activation prevents confirmation", () =>
        {
            using var cancellation = new CancellationTokenSource();
            var api = new Fake { OnActivationWait = () => cancellation.Cancel() };
            Require(!Wait(new WindowActivation(api).RequestAsync(10, 20, cancellation.Token)));
            Require(SpinWait.SpinUntil(() => !api.Attached, 2000));
        });
        test("Guide broker attachment refusal does not activate or detach unrelated queues", () =>
        {
            var api = new Fake
            {
                AttachAllowed = false
            };
            Require(!Wait(new WindowActivation(api).RequestAsync(10, 20, CancellationToken.None)) && api.Requests == 0 && api.Attachments.SequenceEqual(new[] { true }));
        });
        test("Guide broker does not follow changed foreground before attachment", () =>
        {
            var api = new Fake
            {
                ForegroundWindow = 30
            };
            Require(!Wait(new WindowActivation(api).RequestAsync(10, 20, CancellationToken.None)) && api.Requests == 0 && api.Attachments.Count == 0);
        });
        test("Guide broker revalidates foreground after attaching and always detaches", () =>
        {
            var api = new Fake
            {
                OnAttach = a => a.ForegroundWindow = 30
            };
            Require(!Wait(new WindowActivation(api).RequestAsync(10, 20, CancellationToken.None)) && api.Requests == 0 && !api.Attached);
            Require(api.Attachments.SequenceEqual(new[] { true, false }));
        });
        test("Guide broker revalidates target after attaching", () =>
        {
            var api = new Fake
            {
                OnAttach = a => a.TargetThread = 99
            };
            Require(!Wait(new WindowActivation(api).RequestAsync(10, 20, CancellationToken.None)) && api.Requests == 0 && !api.Attached);
        });
        test("Guide broker detaches even if native activation fails exceptionally", () =>
        {
            var api = new Fake
            {
                ThrowOnRequest = true
            };
            Require(!Wait(new WindowActivation(api).RequestAsync(10, 20, CancellationToken.None)) && !api.Attached && api.Attachments.SequenceEqual(new[] { true, false }));
        });
        test("Guide broker rejects invalid and foreign target threads", () =>
        {
            foreach (var target in new nint[]
            {
                0,
                20,
                30
            }

            )
            {
                var api = new Fake();
                Require(!Wait(new WindowActivation(api).RequestAsync(target, 20, CancellationToken.None)) && api.Requests == 0);
            }

            var missing = new Fake
            {
                TargetThread = 0
            };
            Require(!Wait(new WindowActivation(missing).RequestAsync(10, 20, CancellationToken.None)) && missing.Requests == 0);
        });
        test("Guide broker never attaches WPF to its own or unknown foreground queue", () =>
        {
            foreach (var thread in new uint[]
            {
                0,
                1,
                2
            }

            )
            {
                var api = new Fake
                {
                    ForegroundThread = thread
                };
                Require(!Wait(new WindowActivation(api).RequestAsync(10, 20, CancellationToken.None)) && api.Requests == 0 && api.Attachments.Count == 0);
            }
        });
        test("Guide broker cancellation before dispatch creates no native request", () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var api = new Fake();
            Require(!Wait(new WindowActivation(api).RequestAsync(10, 20, cancellation.Token)) && api.Requests == 0 && !api.QueueCreated);
        });
        test("Guide broker timeout leaves dispatcher free and prevents worker multiplication and late activation", () =>
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var api = new Fake
            {
                OnAttach = _ =>
                {
                    entered.Set();
                    release.Wait();
                }
            };
            var broker = new WindowActivation(api);
            var watch = Stopwatch.StartNew();
            var first = broker.RequestAsync(10, 20, CancellationToken.None, 80);
            Require(entered.Wait(2000));
            try
            {
                Require(!Wait(first) && watch.ElapsedMilliseconds < 2000 && api.Attached && api.Requests == 0);
                Require(!Wait(broker.RequestAsync(10, 20, CancellationToken.None)) && api.Attachments.Count == 1);
            }
            finally
            {
                release.Set();
            }

            Require(SpinWait.SpinUntil(() => !api.Attached, 2000) && api.Requests == 0);
        });
        test("Guide broker cancellation after attachment prevents stale activation", () =>
        {
            using var cancellation = new CancellationTokenSource();
            var api = new Fake
            {
                OnAttach = _ => cancellation.Cancel()
            };
            Require(!Wait(new WindowActivation(api).RequestAsync(10, 20, cancellation.Token)));
            Require(SpinWait.SpinUntil(() => !api.Attached, 2000) && api.Requests == 0);
        });
    }

    private static bool Wait(Task<bool> task)
    {
        Require(task.Wait(3000));
        return task.GetAwaiter().GetResult();
    }

    private static void Require(bool value)
    {
        if (!value)
            throw new Exception("Explicit activation boundary violated");
    }

    private sealed class Fake : IWindowActivationNative
    {
        private readonly int owner = Environment.CurrentManagedThreadId;
        public uint CurrentThread => Environment.CurrentManagedThreadId == owner ? 1u : 2u;

        internal uint TargetThread = 1, ForegroundThread = 3;
        internal nint ForegroundWindow = 20;
        public nint Foreground => ForegroundWindow;

        internal bool Accepted = true, AttachAllowed = true, ThrowOnRequest, QueueCreated;
        internal bool DeferredActivation, ActivateOnWait = true, Acknowledged = true;
        internal volatile bool Attached;
        internal int Requests;
        internal int ActivationWaits;
        internal Action? OnActivationWait;
        internal Action<Fake>? OnAttach;
        internal List<bool> Attachments = [];
        public uint WindowThread(nint window) => window == 10 ? TargetThread : window == 20 ? ForegroundThread : 4;
        public void EnsureQueue()
        {
            Require(CurrentThread == 2);
            QueueCreated = true;
        }

        public bool SetForeground(nint window)
        {
            Require(CurrentThread == 2 && WindowThread(window) == 1 && Attached && QueueCreated);
            Requests++;
            if (ThrowOnRequest)
                throw new InvalidOperationException("fixture");
            if (Accepted && !DeferredActivation)
                ForegroundWindow = window;
            return Accepted;
        }

        public bool AwaitActivation(nint window, int timeoutMs)
        {
            Require(CurrentThread == 2 && Attached && window == 10 && timeoutMs is > 0 and <= 100);
            ActivationWaits++;
            OnActivationWait?.Invoke();
            if (DeferredActivation && ActivateOnWait && Acknowledged)
                ForegroundWindow = window;
            return Acknowledged;
        }

        public bool Attach(uint current, uint foreground, bool attach)
        {
            Require(current == 2 && foreground == 3 && CurrentThread == 2);
            Attachments.Add(attach);
            if (attach && !AttachAllowed)
                return false;
            Attached = attach;
            if (attach)
                OnAttach?.Invoke(this);
            return true;
        }
    }
}

using ApolloOverlay;

internal static class WindowActivationTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("native activation keeps the target queue separate and detaches after activation", () =>
        {
            var api = new Fake();
            Require(new WindowActivation(api).Execute(10, 20, 1, CancellationToken.None)
                && api.Foreground == 10 && api.Requests == 1);
            Require(api.Attachments.SequenceEqual(new[] { true, false }) && !api.Attached && api.QueueCreated);
        });
        test("native queue attachment alone does not grant foreground", () =>
        {
            var api = new Fake { Accepted = false };
            Require(!new WindowActivation(api).Execute(10, 20, 1, CancellationToken.None)
                && api.Foreground == 20 && api.Requests == 1);
            Require(api.Attachments.SequenceEqual(new[] { true, false }) && !api.Attached);
        });
        test("native activation observes asynchronous delivery before detaching", () =>
        {
            var api = new Fake { DeferredActivation = true };
            Require(new WindowActivation(api).Execute(10, 20, 1, CancellationToken.None));
            Require(api.Foreground == 10 && api.ActivationWaits == 1 && !api.Attached);
        });
        test("native activation rejects an accepted request without observed ownership", () =>
        {
            var api = new Fake { DeferredActivation = true, ActivateOnWait = false };
            Require(!new WindowActivation(api).Execute(10, 20, 1, CancellationToken.None));
            Require(api.Foreground == 20 && api.ActivationWaits == 1 && !api.Attached);
        });
        test("failed native acknowledgement cannot confirm activation", () =>
        {
            var api = new Fake { Acknowledged = false };
            Require(!new WindowActivation(api).Execute(10, 20, 1, CancellationToken.None));
            Require(api.ActivationWaits == 1 && !api.Attached);
        });
        test("cancellation during native acknowledgement prevents confirmation", () =>
        {
            using var cancellation = new CancellationTokenSource();
            var api = new Fake { OnActivationWait = () => cancellation.Cancel() };
            Require(!new WindowActivation(api).Execute(10, 20, 1, cancellation.Token) && !api.Attached);
        });
        test("native activation revalidates target identity after acknowledgement", () =>
        {
            var api = new Fake();
            api.OnActivationWait = () => api.TargetThread = 99;
            Require(!new WindowActivation(api).Execute(10, 20, 1, CancellationToken.None) && !api.Attached);
        });
        test("refused native attachment does not activate or detach an unrelated queue", () =>
        {
            var api = new Fake { AttachAllowed = false };
            Require(!new WindowActivation(api).Execute(10, 20, 1, CancellationToken.None)
                && api.Requests == 0 && api.Attachments.SequenceEqual(new[] { true }));
        });
        test("native activation does not follow a changed foreground before attachment", () =>
        {
            var api = new Fake { ForegroundWindow = 30 };
            Require(!new WindowActivation(api).Execute(10, 20, 1, CancellationToken.None)
                && api.Requests == 0 && api.Attachments.Count == 0);
        });
        test("native activation revalidates foreground after attachment and always detaches", () =>
        {
            var api = new Fake { OnAttach = a => a.ForegroundWindow = 30 };
            Require(!new WindowActivation(api).Execute(10, 20, 1, CancellationToken.None)
                && api.Requests == 0 && !api.Attached);
            Require(api.Attachments.SequenceEqual(new[] { true, false }));
        });
        test("native activation revalidates target after attachment", () =>
        {
            var api = new Fake { OnAttach = a => a.TargetThread = 99 };
            Require(!new WindowActivation(api).Execute(10, 20, 1, CancellationToken.None)
                && api.Requests == 0 && !api.Attached);
        });
        test("native activation detaches when foreground activation throws", () =>
        {
            var api = new Fake { ThrowOnRequest = true };
            var failed = false;
            try
            {
                new WindowActivation(api).Execute(10, 20, 1, CancellationToken.None);
            }
            catch (InvalidOperationException error) when (error.Message == "fixture")
            {
                failed = true;
            }
            Require(failed && !api.Attached && api.Attachments.SequenceEqual(new[] { true, false }));
        });
        test("native activation rejects invalid and foreign target ownership", () =>
        {
            foreach (var target in new nint[] { 0, 20, 30 })
            {
                var api = new Fake();
                Require(!new WindowActivation(api).Execute(target, 20, 1, CancellationToken.None)
                    && api.Requests == 0 && api.Attachments.Count == 0);
            }
            var missing = new Fake { TargetThread = 0 };
            Require(!new WindowActivation(missing).Execute(10, 20, 1, CancellationToken.None)
                && missing.Requests == 0 && missing.Attachments.Count == 0);
            var unknownOwner = new Fake();
            Require(!new WindowActivation(unknownOwner).Execute(10, 20, 0, CancellationToken.None)
                && !unknownOwner.QueueCreated);
        });
        test("native activation never attaches the target queue or an unknown foreground queue", () =>
        {
            foreach (var thread in new uint[] { 0, 1, 2 })
            {
                var api = new Fake { ForegroundThread = thread };
                Require(!new WindowActivation(api).Execute(10, 20, 1, CancellationToken.None)
                    && api.Requests == 0 && api.Attachments.Count == 0);
            }
            var targetCaller = new Fake { CurrentThread = 1 };
            Require(!new WindowActivation(targetCaller).Execute(10, 20, 1, CancellationToken.None)
                && targetCaller.Requests == 0 && targetCaller.Attachments.Count == 0);
        });
        test("pre-cancelled native activation performs no attachment or foreground request", () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var api = new Fake();
            Require(!new WindowActivation(api).Execute(10, 20, 1, cancellation.Token)
                && api.Requests == 0 && api.Attachments.Count == 0);
        });
        test("cancellation after native attachment prevents stale activation", () =>
        {
            using var cancellation = new CancellationTokenSource();
            var api = new Fake { OnAttach = _ => cancellation.Cancel() };
            Require(!new WindowActivation(api).Execute(10, 20, 1, cancellation.Token));
            Require(!api.Attached && api.Requests == 0 && api.Attachments.SequenceEqual(new[] { true, false }));
        });
    }

    private static void Require(bool value)
    {
        if (!value)
            throw new Exception("Explicit activation boundary violated");
    }

    private sealed class Fake : IWindowActivationNative
    {
        public uint CurrentThread { get; init; } = 2;
        internal uint TargetThread = 1, ForegroundThread = 3;
        internal nint ForegroundWindow = 20;
        public nint Foreground => ForegroundWindow;

        internal bool Accepted = true, AttachAllowed = true, ThrowOnRequest, QueueCreated;
        internal bool DeferredActivation, ActivateOnWait = true, Acknowledged = true, Attached;
        internal int Requests, ActivationWaits;
        internal Action? OnActivationWait;
        internal Action<Fake>? OnAttach;
        internal List<bool> Attachments = [];
        public uint WindowThread(nint window) => window == 10 ? TargetThread : window == 20 ? ForegroundThread : 4;
        public void EnsureQueue() => QueueCreated = true;

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

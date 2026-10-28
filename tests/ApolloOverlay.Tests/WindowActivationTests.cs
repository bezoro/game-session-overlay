using ApolloOverlay;

internal static class WindowActivationTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("Guide activation normal acceptance never attaches queues", () =>
        {
            var api = new Fake { FirstAccepted = true };
            Require(WindowActivation.Request(10, api) && api.Requests == 1 && api.Attachments.Count == 0);
        });
        test("Guide activation observes foreground despite false native result", () =>
        {
            var api = new Fake { OnFirst = a => a.ForegroundWindow = 10 };
            Require(WindowActivation.Request(10, api) && api.Requests == 1 && api.Attachments.Count == 0);
        });
        test("Guide activation retries refused foreground with temporary attachment", () =>
        {
            var api = new Fake();
            Require(WindowActivation.Request(10, api) && api.Requests == 2 && api.Foreground == 10);
            Require(api.Attachments.SequenceEqual(new[] { true, false }) && !api.Attached);
        });
        test("Guide activation cannot grant foreground merely by attaching", () =>
        {
            var api = new Fake { SecondAccepted = false };
            Require(!WindowActivation.Request(10, api) && api.Foreground == 20 && api.Requests == 2);
            Require(api.Attachments.SequenceEqual(new[] { true, false }) && !api.Attached);
        });
        test("Guide activation attachment refusal does not retry or detach unrelated queues", () =>
        {
            var api = new Fake { AttachAllowed = false };
            Require(!WindowActivation.Request(10, api) && api.Requests == 1 && api.Attachments.SequenceEqual(new[] { true }));
        });
        test("Guide activation does not follow a changed foreground", () =>
        {
            var api = new Fake { OnFirst = a => a.ForegroundWindow = 30 };
            Require(!WindowActivation.Request(10, api) && api.Requests == 1 && api.Attachments.Count == 0);
        });
        test("Guide activation revalidates foreground after attaching and always detaches", () =>
        {
            var api = new Fake { OnAttach = a => a.ForegroundWindow = 30 };
            Require(!WindowActivation.Request(10, api) && api.Requests == 1 && !api.Attached);
            Require(api.Attachments.SequenceEqual(new[] { true, false }));
        });
        test("Guide activation revalidates target after attaching", () =>
        {
            var api = new Fake { OnAttach = a => a.TargetThread = 99 };
            Require(!WindowActivation.Request(10, api) && api.Requests == 1 && !api.Attached);
        });
        test("Guide activation detaches even if activation throws", () =>
        {
            var api = new Fake { ThrowOnRetry = true }; var threw = false;
            try { WindowActivation.Request(10, api); } catch (InvalidOperationException) { threw = true; }
            Require(threw && !api.Attached && api.Attachments.SequenceEqual(new[] { true, false }));
        });
        test("Guide activation rejects invalid foreign and zero-thread targets", () =>
        {
            foreach (var target in new nint[] { 0, 20, 30 })
            {
                var api = new Fake(); Require(!WindowActivation.Request(target, api) && api.Requests == 0);
            }
            var missing = new Fake { TargetThread = 0 };
            Require(!WindowActivation.Request(10, missing) && missing.Requests == 0);
        });
        test("Guide activation does not attach to unknown or same-thread foreground", () =>
        {
            foreach (var thread in new uint[] { 0, 1 })
            {
                var api = new Fake { ForegroundThread = thread };
                Require(!WindowActivation.Request(10, api) && api.Requests == 1 && api.Attachments.Count == 0);
            }
        });
    }

    private static void Require(bool value) { if (!value) throw new Exception("Explicit activation boundary violated"); }
    private sealed class Fake : IWindowActivationNative
    {
        public uint CurrentThread => 1;
        internal uint TargetThread = 1, ForegroundThread = 2;
        internal nint ForegroundWindow = 20;
        public nint Foreground => ForegroundWindow;
        internal bool FirstAccepted, SecondAccepted = true, AttachAllowed = true, Attached, ThrowOnRetry;
        internal int Requests;
        internal Action<Fake>? OnFirst, OnAttach;
        internal List<bool> Attachments = [];
        public uint WindowThread(nint window) => window == 10 ? TargetThread : window == 20 ? ForegroundThread : 3;
        public bool SetForeground(nint window)
        {
            Requests++;
            if (Requests == 1) { OnFirst?.Invoke(this); return FirstAccepted; }
            Require(Attached);
            if (ThrowOnRetry) throw new InvalidOperationException("fixture");
            if (SecondAccepted) ForegroundWindow = window;
            return SecondAccepted;
        }
        public bool Attach(uint current, uint foreground, bool attach)
        {
            Require(current == 1 && foreground == 2);
            Attachments.Add(attach);
            if (attach && !AttachAllowed) return false;
            Attached = attach; if (attach) OnAttach?.Invoke(this); return true;
        }
    }
}

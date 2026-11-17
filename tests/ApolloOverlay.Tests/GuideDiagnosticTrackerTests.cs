using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ApolloOverlay;

internal static unsafe class GuideDiagnosticTrackerTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("Guide diagnostics share the backend gesture decision and omit idle polls", () =>
        {
            using var host = new Host();
            host.Status[0] = 0;
            host.Poll(0);
            host.Poll(20);
            Require(host.Backend.Diagnostics.Count == 0 && host.Toggles == 0, "Idle polling emitted diagnostics or a toggle");
            host.Down[0] = true;
            host.Poll(40);
            Require(host.Toggles == 0 && host.Has("guide-button", "down/slot-1"), "Guide DOWN was not attributed or opened before release");
            host.Poll(60);
            Require(host.Backend.Diagnostics.Count == 0, "A held Guide generated repeated diagnostics");
            host.Down[0] = false;
            host.Poll(80);
            Require(host.Toggles == 1 && host.Has("guide-button", "up/slot-1")
                && host.Has("guide-opening", "release-qualified/slot-1"), "The actual opening has no matching qualified release");
        });
        test("Guide hold-limit cancellation is issued once by the actual gesture state", () =>
        {
            using var host = new Host();
            host.Status[0] = 0;
            host.Poll(0, limit: 500);
            host.Down[0] = true;
            host.Poll(10, limit: 500);
            host.Poll(511, limit: 2000);
            Require(host.Has("guide-opening", "hold-limit-exceeded/slot-1"), "The configured DOWN limit was lost");
            host.Poll(700);
            Require(host.Backend.Diagnostics.Count == 0, "Rejected hold repeated its cancellation");
            host.Down[0] = false;
            host.Poll(800);
            Require(host.Toggles == 0 && !host.Has("guide-opening", "release-qualified/slot-1"), "A rejected hold later qualified");
        });
        test("another controller connecting cannot falsely cancel Guide diagnostics", () =>
        {
            using var host = new Host();
            host.Status[0] = 0;
            host.Poll(0);
            host.Down[0] = true;
            host.Poll(10);
            host.Status[1] = 0;
            host.Poll(20);
            Require(host.Has("guide-connections", "slot-mask-3")
                && !host.Backend.Diagnostics.Any(entry => entry.Outcome == ResultKind.Cancelled), "An unrelated connection cancelled an active slot gesture");
            host.Down[0] = false;
            host.Poll(30);
            Require(host.Toggles == 1 && host.Has("guide-opening", "release-qualified/slot-1"), "Topology change separated the journal from the actual opening");
        });
        test("overlapping Guide presses retain independent slot diagnostics", () =>
        {
            using var host = new Host();
            host.Status[0] = host.Status[1] = 0;
            host.Poll(0);
            host.Down[0] = true;
            host.Poll(10);
            host.Down[1] = true;
            host.Poll(20);
            Require(host.Has("guide-button", "down/slot-2"), "Aggregate held state hid the second controller press");
            host.Down[0] = false;
            host.Poll(30);
            Require(host.Toggles == 1 && host.Backend.Held && host.Has("guide-opening", "release-qualified/slot-1"), "First release was hidden by another held slot");
            host.Down[1] = false;
            host.Poll(40);
            Require(host.Toggles == 2 && !host.Backend.Held && host.Has("guide-opening", "release-qualified/slot-2"), "Second release inherited the wrong slot or gesture");
        });
        test("invalid Guide snapshots cancel every pending opening without a later false release", () =>
        {
            using var host = new Host();
            host.Status[0] = 0;
            host.Poll(0);
            host.Down[0] = true;
            host.Poll(10);
            host.Status[1] = 5;
            host.Poll(20);
            Require(!host.Backend.SnapshotValid && host.Has("guide-opening", "reading-invalid/slot-1"), "An uncertain snapshot did not cancel the actual opening");
            host.Status[1] = 1167;
            host.Down[0] = false;
            host.Poll(30);
            Require(host.Toggles == 0 && !host.Has("guide-opening", "release-qualified/slot-1"), "An uncertain press later opened on release");
            host.Down[0] = true;
            host.Poll(40);
            host.Down[0] = false;
            host.Poll(50);
            Require(host.Toggles == 1, "A fresh valid press did not recover");
        });
        test("Guide guard polls deliver diagnostics before another poll replaces the snapshot", () =>
        {
            using var host = new Host();
            var observed = new List<GuideDiagnostic>();
            host.Backend.DiagnosticObserved = observed.Add;
            host.Status[0] = 0;
            host.Poll(0);
            host.Down[0] = true;
            host.Poll(10);
            host.Down[0] = false;
            host.Poll(20);
            host.Poll(30);
            Require(host.Backend.Diagnostics.Count == 0 && host.Toggles == 1
                && observed.Count(entry => entry.Reason == "release-qualified/slot-1") == 1, "A guard poll's semantic decision was lost before logging");
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static readonly uint[] status = new uint[4];
    private static readonly bool[] down = new bool[4];

    private sealed class Host : IDisposable
    {
        internal uint[] Status => status;
        internal bool[] Down => down;
        internal int Toggles;
        internal XInputGuideBackend Backend
        {
            get;
        }
        private readonly GuideConnection connection;

        internal Host()
        {
            Array.Fill(status, 1167u);
            Array.Clear(down);
            Backend = new(() => ((nint)1, (nint)(delegate* unmanaged[Stdcall]<uint, XInputGuideBackend.State*, uint>)&Read), _ => { });
            connection = new(Backend);
            Require(connection.Start(() => Toggles++), "PRECONDITION: owned Guide fixture could not start");
        }

        internal void Poll(long now, int limit = 2000) => Backend.PollForOverlay(false, 1, now, limit);
        internal bool Has(string command, string reason) => Backend.Diagnostics.Any(entry => entry.Command == command && entry.Reason == reason);
        public void Dispose() => connection.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Read(uint slot, XInputGuideBackend.State* state)
    {
        state->Buttons = down[slot] ? (ushort)0x400 : (ushort)0;
        return status[slot];
    }
}

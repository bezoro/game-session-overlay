using System.Collections.Concurrent;
using ApolloOverlay;

internal static class ControllerBatteryNotificationTests
{
    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static ControllerDeviceSnapshot Battery(uint slot = 0, ControllerBatteryLevel? level = ControllerBatteryLevel.Low,
        ControllerBatteryType type = ControllerBatteryType.Alkaline, bool connected = true) => new(slot, 1, connected, type, level, ControllerVibrationCapability.Unknown, 0, "fixture");
    private static ControllerIdentity Identity(int? percent, bool charging = false, bool wireless = true, string id = "controller") => new(id, "Xbox One", ControllerFamily.Xbox, wireless, percent, charging);
    private static void Complete(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

    internal static void Run(Action<string, Action> test)
    {
        test("battery alert thresholds use actual percentages and allow one critical escalation", () =>
        {
            var policy = new ControllerBatteryAlerts();
            Require(policy.Observe([Battery()], [Identity(21)], 0).Count == 0, "Alert above the low threshold");
            var low = policy.Observe([Battery()], [Identity(20)], 1).Single();
            Require(low.Level == ControllerBatteryAlertLevel.Low && low.Message.Contains("20%") && low.ControllerName == "Xbox One", "Measured charge was lost");
            Require(policy.Observe([Battery()], [Identity(6)], 2).Count == 0, "Low alert repeated");
            var critical = policy.Observe([Battery()], [Identity(5)], 3).Single();
            Require(critical.Level == ControllerBatteryAlertLevel.Critical && critical.Message.Contains("5%"), "Critical charge was not reported");
            Require(policy.Observe([Battery()], [Identity(0)], 4).Count == 0, "Critical alert repeated");
        });
        test("categorical battery alerts never fabricate percentages or physical controller models", () =>
        {
            var policy = new ControllerBatteryAlerts();
            var low = policy.Observe([Battery()], [], 0).Single();
            Require(low.BatteryPercent is null && low.ControllerName == "Controller 1" && !low.Message.Contains('%'), "Categorical charge was guessed");
            Require(policy.Observe([Battery(level: ControllerBatteryLevel.Empty)], [], 1).Single().Level == ControllerBatteryAlertLevel.Critical, "Empty battery did not escalate");
        });
        test("unknown invalid wired and charging batteries do not raise low-charge notifications", () =>
        {
            Require(new ControllerBatteryAlerts().Observe([Battery(level: null, type: ControllerBatteryType.Unknown)], [], 0).Count == 0, "Unknown charge became zero");
            Require(new ControllerBatteryAlerts().Observe([Battery(level: null)], [Identity(-1)], 0).Count == 0, "Invalid negative percentage accepted");
            Require(new ControllerBatteryAlerts().Observe([Battery(level: null)], [Identity(101)], 0).Count == 0, "Invalid excessive percentage accepted");
            Require(new ControllerBatteryAlerts().Observe([Battery(type: ControllerBatteryType.Wired)], [Identity(1)], 0).Count == 0, "Wired device raised an alert");
            Require(new ControllerBatteryAlerts().Observe([Battery()], [Identity(1, charging: true)], 0).Count == 0, "Charging device raised an alert");
            Require(new ControllerBatteryAlerts().Observe([Battery()], [Identity(1, wireless: false)], 0).Count == 0, "USB-powered metadata raised an alert");
            Require(new ControllerBatteryAlerts().Observe([Battery(connected: false)], [Identity(1)], 0).Count == 0, "Disconnected slot raised an alert");
            Require(new ControllerBatteryAlerts().Observe([Battery(type: ControllerBatteryType.Unknown) with { NativeResult = 5 }], [Identity(1)], 0).Count == 0, "Failed connection was associated with metadata");
        });
        test("confirmed percentage takes precedence over a low categorical reading", () =>
        {
            Require(new ControllerBatteryAlerts().Observe([Battery()], [Identity(76)], 0).Count == 0, "Categorical reading overrode actual charge");
            var alert = new ControllerBatteryAlerts().Observe([Battery(level: ControllerBatteryLevel.Full)], [Identity(18)], 0).Single();
            Require(alert.BatteryPercent == 18, "Low actual charge was ignored");
        });
        test("ambiguous controller identities remain unassociated with XInput slots", () =>
        {
            var alerts = new ControllerBatteryAlerts().Observe([Battery(), Battery(1)], [Identity(2)], 0);
            Require(alerts.Count == 2 && alerts.All(alert => alert.BatteryPercent is null)
                && alerts.Select(alert => alert.ControllerName).SequenceEqual(["Controller 1", "Controller 2"]), "A measured percentage was assigned among several slots");
            Require(new ControllerBatteryAlerts().Observe([Battery(level: ControllerBatteryLevel.Full)], [Identity(1), Identity(2, id: "other")], 0).Count == 0, "Ambiguous metadata raised a false alarm");
        });
        test("unknown samples and connection epochs cannot reset a discharge alert", () =>
        {
            var policy = new ControllerBatteryAlerts();
            Require(policy.Observe([Battery()], [], 0).Count == 1, "Initial low charge missing");
            policy.Observe([Battery(connected: false)], [], 1);
            policy.Observe([Battery(level: null, type: ControllerBatteryType.Unknown)], [], 2);
            Require(policy.Observe([Battery() with { ConnectionEpoch = 3 }], [], 3).Count == 0, "Reconnect repeated the same warning");
            Require(policy.Observe([Battery()], [Identity(19)], 4).Count == 0, "Newly available identity repeated the same warning");
        });
        test("battery recovery uses hysteresis and a cooldown before another low-charge alert", () =>
        {
            var policy = new ControllerBatteryAlerts();
            policy.Observe([Battery()], [Identity(20)], 0);
            policy.Observe([Battery()], [Identity(25)], 1);
            Require(policy.Observe([Battery()], [Identity(19)], 700_000).Count == 0, "Hysteresis did not keep the old discharge latched");
            policy.Observe([Battery()], [Identity(26)], 700_001);
            Require(policy.Observe([Battery()], [Identity(19)], 700_002).Count == 1, "Confirmed recovery did not rearm the warning");
            policy.Observe([Battery()], [Identity(30)], 700_003);
            Require(policy.Observe([Battery()], [Identity(18)], 700_004).Count == 0, "Rapid recovery bounce repeated a warning");
            Require(policy.Observe([Battery()], [Identity(5)], 700_005).Single().Level == ControllerBatteryAlertLevel.Critical, "Cooldown blocked a new critical escalation");
        });
        test("a confirmed replacement controller has an independent discharge state", () =>
        {
            var policy = new ControllerBatteryAlerts();
            policy.Observe([Battery()], [Identity(19)], 0);
            Require(policy.Observe([Battery()], [Identity(18, id: "replacement")], 1).Count == 1, "Confirmed replacement inherited another controller's warning");
        });
        test("background battery monitor samples without foreground ownership and coalesces persistent low charge", () =>
        {
            using var observed = new ManualResetEventSlim();
            var devices = new Devices();
            var alerts = new ConcurrentQueue<ControllerBatteryAlert>();
            var events = new ConcurrentQueue<string>();
            using var monitor = new ControllerBatteryMonitor(devices, new Identities(), alert => { alerts.Enqueue(alert); observed.Set(); }, (reason, _) => events.Enqueue(reason), TimeSpan.FromMilliseconds(10));
            Require(observed.Wait(5000), "Background monitor did not request a notification");
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref devices.Reads) >= 12, 2000), "Monitor stopped sampling");
            monitor.Dispose();
            Complete(monitor.Completion);
            Require(alerts.Count == 1 && devices.Disposals == 1, "Persistent low charge spammed notifications or leaked the owned source");
            Require(events.Count == 4 && events.Any(reason => reason.Contains("/level=Low/"))
                && events.All(reason => reason.Length <= 256 && reason.All(character => char.IsAsciiLetterOrDigit(character) || ".-_:/=+".Contains(character))),
                "Stable observations spammed or became redacted/unreadable in the command journal");
        });
        test("battery monitor shutdown never waits on a native read or notifies after it completes", () =>
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var devices = new Devices
            {
                Sample = slot =>
                {
                    if (slot == 0)
                    {
                        entered.Set();
                        Require(release.Wait(5000), "Fixture native read was not released");
                    }
                    return Battery(slot, connected: slot == 0);
                }
            };
            var alerts = new ConcurrentQueue<ControllerBatteryAlert>();
            var events = new ConcurrentQueue<string>();
            using var monitor = new ControllerBatteryMonitor(devices, new Identities(), alerts.Enqueue, (reason, _) => events.Enqueue(reason));
            try
            {
                Require(entered.Wait(5000), "Native fixture did not enter");
                monitor.Dispose();
                Require(!monitor.Completion.IsCompleted && devices.Disposals == 0, "Shutdown disposed a source still being read");
            }
            finally { release.Set(); }
            Complete(monitor.Completion);
            Require(alerts.IsEmpty && events.IsEmpty && devices.Disposals == 1, "Late native completion escaped shutdown fencing");
        });
        test("battery monitor cancellation after the final slot skips Windows metadata enumeration", () =>
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var devices = new Devices
            {
                Sample = slot =>
                {
                    if (slot == 3)
                    {
                        entered.Set();
                        Require(release.Wait(5000), "Fixture final read was not released");
                    }
                    return Battery(slot, connected: slot == 0);
                }
            };
            var identities = new Identities();
            using var monitor = new ControllerBatteryMonitor(devices, identities, _ => throw new InvalidOperationException("Late notification"), (_, _) => { });
            try
            {
                Require(entered.Wait(5000), "Final slot did not enter");
                monitor.Dispose();
            }
            finally { release.Set(); }
            Complete(monitor.Completion);
            Require(identities.Reads == 0 && devices.Disposals == 1, "Cancellation started another native stage");
        });
        test("battery monitor logs a transient failure once and recovers without overlapping reads", () =>
        {
            var attempts = 0;
            var devices = new Devices
            {
                Sample = slot => slot == 0 && Interlocked.Increment(ref attempts) <= 2
                    ? throw new System.IO.IOException("fixture transient failure") : Battery(slot, connected: slot == 0)
            };
            using var observed = new ManualResetEventSlim();
            var events = new ConcurrentQueue<string>();
            using var monitor = new ControllerBatteryMonitor(devices, new Identities(), _ => observed.Set(), (reason, _) => events.Enqueue(reason), TimeSpan.FromMilliseconds(10));
            Require(observed.Wait(5000), "Monitor did not recover");
            monitor.Dispose();
            Complete(monitor.Completion);
            Require(events.Count(value => value == "read-failed") == 1 && events.Count(value => value == "read-recovered") == 1, "Failure spammed or recovery was not recorded");
        });
        test("Windows metadata failure still permits a truthful XInput battery warning", () =>
        {
            var devices = new Devices();
            using var observed = new ManualResetEventSlim();
            var alerts = new ConcurrentQueue<ControllerBatteryAlert>();
            using var monitor = new ControllerBatteryMonitor(devices, new Identities { Fail = true }, alert => { alerts.Enqueue(alert); observed.Set(); }, (_, _) => { });
            Require(observed.Wait(5000), "Metadata failure blocked categorical charge");
            monitor.Dispose();
            Complete(monitor.Completion);
            Require(alerts.Single().BatteryPercent is null, "Failed metadata fabricated a percentage");
        });
    }

    private sealed class Identities : IControllerIdentitySource
    {
        internal bool Fail;
        internal int Reads;
        public IReadOnlyList<ControllerIdentity> Read()
        {
            Interlocked.Increment(ref Reads);
            return Fail ? throw new NotSupportedException("fixture metadata failure") : [];
        }
    }

    private sealed class Devices : IControllerDevices
    {
        internal int Reads, Disposals;
        internal Func<uint, ControllerDeviceSnapshot> Sample = slot => Battery(slot, connected: slot == 0);
        public void UpdateConnections(IReadOnlyList<bool> connected) => throw new InvalidOperationException("Monitor must not mutate UI connection tracking");
        public ControllerDeviceSnapshot Read(uint slot)
        {
            Interlocked.Increment(ref Reads);
            return Sample(slot);
        }
        public ControllerVibrationResult SetVibration(uint slot, long connectionEpoch, ushort leftMotor, ushort rightMotor) => throw new InvalidOperationException("Battery monitor must not vibrate");
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}

using ApolloOverlay;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;

internal static class SteamOverlayDetectionTests
{
    private static readonly WindowRef Game = new(101, 42, 100, "Fixture game", "game");
    private static readonly SteamProcessEntry[] Chain = [new(42, 30, "game.exe"), new(30, 20, "HYP.exe"), new(20, 0, "steam.exe")];
    private static Dictionary<uint, SteamProcessIdentity> Identities() => new()
    {
        [42] = new(42, "game.exe", 100, 1),
        [30] = new(30, "HYP.exe", 90, 1),
        [20] = new(20, "steam.exe", 80, 1)
    };
    private static void Require(bool value, string reason)
    {
        if (!value)
            throw new Exception(reason);
    }

    private static void Pump(Task task)
    {
        var deadline = Stopwatch.StartNew();
        while (!task.IsCompleted && deadline.ElapsedMilliseconds < 5000)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }

        Require(task.IsCompleted, "Fixture task deadline exceeded");
        task.GetAwaiter().GetResult();
    }

    private static bool Observe(SteamProcessEntry[]? entries = null, Dictionary<uint, SteamProcessIdentity>? identities = null, bool complete = true, WindowRef? target = null)
    {
        var live = identities ?? Identities();
        return SteamLaunchContext.Observe(target ?? Game, _ => true, () => new(entries ?? Chain, complete), pid => live.GetValueOrDefault(pid));
    }

    internal static void Run(Action<string, Action> test)
    {
        test("Steam launch process ABI is Unicode x64 568 bytes", () => Require(Marshal.SizeOf<SteamLaunchContext.ProcessEntry>() == 568, "PROCESSENTRY32W layout differs"));
        test("Steam launch ancestry accepts ordered HYP chain case insensitively", () =>
        {
            Require(Observe(), "Valid chain refused");
            var entries = Chain.ToArray();
            entries[2] = entries[2] with
            {
                Name = "STEAM.EXE"
            };
            Require(Observe(entries), "Case insensitive executable comparison refused");
        });
        foreach (var failure in new[]
        {
            "unrelated-steam",
            "newer-parent",
            "cross-session",
            "missing-parent",
            "unreadable-parent",
            "renamed-parent",
            "reused-target",
            "wrong-target-name",
            "wrong-live-pid",
            "cycle",
            "zero-link",
            "partial",
            "duplicate-pid"
        }

        )
        {
            var scenario = failure;
            test("Steam launch ancestry rejects " + scenario, () =>
            {
                var entries = Chain.ToArray();
                var identities = Identities();
                var target = Game;
                var complete = true;
                switch (scenario)
                {
                    case "unrelated-steam":
                        entries[1] = entries[1] with
                        {
                            ParentPid = 0
                        };
                        break;
                    case "newer-parent":
                        identities[30] = identities[30] with
                        {
                            Started = 101
                        };
                        break;
                    case "cross-session":
                        identities[30] = identities[30] with
                        {
                            SessionId = 2
                        };
                        break;
                    case "missing-parent":
                        entries = entries.Where(e => e.Pid != 30).ToArray();
                        break;
                    case "unreadable-parent":
                        identities.Remove(30);
                        break;
                    case "renamed-parent":
                        entries[1] = entries[1] with
                        {
                            Name = "other.exe"
                        };
                        break;
                    case "reused-target":
                        target = target with
                        {
                            Started = 101
                        };
                        break;
                    case "wrong-target-name":
                        target = target with
                        {
                            ProcessName = "other"
                        };
                        break;
                    case "wrong-live-pid":
                        identities[30] = identities[30] with
                        {
                            Pid = 31
                        };
                        break;
                    case "cycle":
                        entries[1] = entries[1] with
                        {
                            ParentPid = 42
                        };
                        break;
                    case "zero-link":
                        entries[0] = entries[0] with
                        {
                            ParentPid = 0
                        };
                        break;
                    case "partial":
                        complete = false;
                        break;
                    case "duplicate-pid":
                        entries = [.. entries, entries[0]];
                        break;
                }

                Require(!Observe(entries, identities, complete, target), "Inconclusive ancestry accepted: " + scenario);
            });
        }

        test("Steam launch depth eight accepted and nine refused", () =>
        {
            foreach (var depth in new[]
            {
                8,
                9
            }

            )
            {
                var entries = new List<SteamProcessEntry>();
                var identities = new Dictionary<uint, SteamProcessIdentity>();
                for (uint i = 0; i <= depth; i++)
                {
                    var pid = 42 + i;
                    var name = i == 0 ? "game.exe" : i == depth ? "steam.exe" : "launcher.exe";
                    entries.Add(new(pid, i == depth ? 0 : pid + 1, name));
                    identities[pid] = new(pid, name, 100 - i, 1);
                }

                Require(Observe(entries.ToArray(), identities) == (depth == 8), "Ancestry depth boundary differs");
            }
        });
        test("Steam launch process cap requires complete bounded enumeration", () =>
        {
            var entries = Chain.Concat(Enumerable.Range(1000, SteamLaunchContext.ProcessLimit - 3).Select(i => new SteamProcessEntry((uint)i, 0, "unrelated.exe"))).ToArray();
            Require(Observe(entries), "Complete exact-cap snapshot refused");
            Require(!Observe([.. entries, new(99999, 0, "extra.exe")]), "Over-cap snapshot accepted");
            Require(!Observe(entries, complete: false), "Capped/incomplete snapshot accepted");
        });
        test("Steam launch changed window identity and read errors never grant permission", () =>
        {
            var checks = 0;
            Require(!SteamLaunchContext.Observe(Game, _ => ++checks == 1, () => new(Chain, true), pid => Identities().GetValueOrDefault(pid)), "Changed target accepted");
            Require(!SteamLaunchContext.Observe(Game, _ => true, () => throw new Win32Exception(5)), "Access failure escaped or granted permission");
            foreach (var target in new[]
            {
                Game with
                {
                    Protected = true
                },
                Game with
                {
                    ProcessName = "steam"
                },
                Game with
                {
                    Pid = 0
                }
            }

            )
                Require(!Observe(target: target), "Protected/launcher/invalid target accepted");
        });
        ControllerChecks(test);
    }

    private static void ControllerChecks(Action<string, Action> test)
    {
        test("Steam refresh collects equal target once and never during eligibility samples", () =>
        {
            using var p = new Platform();
            using var c = new OverlayController(p);
            c.CaptureReturn();
            Require(!c.SteamOverlayEligible(p.A), "Pending discovery granted launch context");
            Pump(c.Refresh());
            Require(p.Queries.SequenceEqual(new[] { p.A }) && c.SteamOverlayEligible(p.A), "Equal captured targets not observed once");
            var scans = p.Queries.Count;
            for (var i = 0; i < 30; i++)
                Require(c.SteamOverlayEligible(p.A), "Historical launch permission lost");
            Require(p.Queries.Count == scans, "Eligibility sampled process ancestry");
            p.Launched = _ => false;
            Pump(c.Refresh());
            Require(c.SteamOverlayEligible(p.A), "Steam exit erased same-target historical launch fact");
        });
        test("Steam captured ReturnTarget and selected Anchor have separate launch observations", () =>
        {
            using var p = new Platform();
            using var c = new OverlayController(p);
            p.Current = p.B;
            c.CaptureReturn();
            c.Select(p.A);
            p.Launched = t => t.Same(p.A);
            Pump(c.Refresh());
            Require(p.Queries.SequenceEqual(new[] { p.A, p.B }) && c.SteamOverlayEligible(p.A) && !c.SteamOverlayEligible(p.B), "Launch permission crossed captured targets");
            c.Select(p.B);
            c.CaptureReturn();
            Require(!c.SteamOverlayEligible(p.A) && !c.SteamOverlayEligible(p.B), "Context change retained unrelated permission");
        });
        test("Steam module positive remains independent of inconclusive launch and launcher/protected refused", () =>
        {
            using var p = new Platform
            {
                Module = ModuleEvidence.Loaded,
                Launched = _ => false
            };
            using var c = new OverlayController(p);
            c.CaptureReturn();
            Pump(c.Refresh());
            Require(c.SteamOverlayEligible(p.A), "Existing module-positive route lost");
            Require(!c.SteamOverlayEligible(p.A with
            {
                Protected = true
            }) && !c.SteamOverlayEligible(p.A with
            {
                ProcessName = "steam"
            }), "Protected/launcher module route accepted");
            p.ValidIdentity = false;
            Require(!c.SteamOverlayEligible(p.A), "Reused identity accepted");
        });
        foreach (var end in new[]
        {
            "new-refresh",
            "context-change",
            "dispose"
        }

        )
        {
            var scenario = end;
            test("Steam pending ancestry cannot publish after " + scenario, () =>
            {
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var p = new Platform();
                using var c = new OverlayController(p);
                c.CaptureReturn();
                p.Launched = t =>
                {
                    if (t.Same(p.A))
                    {
                        entered.Set();
                        Require(release.Wait(4000), "Fixture worker not released");
                        return true;
                    }

                    return false;
                };
                var old = c.Refresh();
                try
                {
                    Require(entered.Wait(1000), "Fixture collector not entered");
                    if (scenario == "dispose")
                        c.Dispose();
                    else
                    {
                        p.Current = p.B;
                        c.CaptureReturn();
                        if (scenario == "new-refresh")
                            Pump(c.Refresh());
                    }
                }
                finally
                {
                    release.Set();
                }

                try
                {
                    Pump(old);
                }
                catch (OperationCanceledException)
                {
                }

                Require(!c.SteamOverlayEligible(p.A) && !c.SteamOverlayEligible(p.B), "Stale/cancelled launch observation published");
            });
        }

        test("Steam new window generation cannot inherit observed launch", () =>
        {
            using var p = new Platform();
            using var c = new OverlayController(p);
            c.CaptureReturn();
            Pump(c.Refresh());
            var replacement = p.A with
            {
                Generation = 1
            };
            p.A = replacement;
            p.Current = replacement;
            c.CaptureReturn();
            Require(!c.SteamOverlayEligible(replacement), "Replacement generation inherited launch fact");
        });
    }

    internal static void NativeSmoke(Action<string, Action> test)
    {
        test("Steam production process-only collector confirms exact current ZZZ launch context", () =>
        {
            const int pid = 57656;
            var expected = DateTime.Parse("2026-10-26T17:36:39.7888151Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).Ticks;
            using var process = Process.GetProcessById(pid);
            Require(process.StartTime.ToUniversalTime().Ticks == expected && process.ProcessName == "ZenlessZoneZero", "Expected game process identity changed; no fallback probe allowed");
            var target = new WindowRef(process.MainWindowHandle, pid, expected, "Scoped read-only ZZZ smoke", "ZenlessZoneZero");
            Require(target.Handle != 0, "Game window unavailable");
            bool Valid(WindowRef window)
            {
                Native.GetWindowThreadProcessId(window.Handle, out var actual);
                return Native.IsWindow(window.Handle) && actual == pid && SteamLaunchContext.ReadIdentity(actual) is { } identity && identity.Started == window.Started;
            }

            var elapsed = Stopwatch.StartNew();
            SteamProcessSnapshot? observed = null;
            var confirmed = SteamLaunchContext.Observe(target, Valid, () => observed = SteamLaunchContext.Capture());
            Console.WriteLine($"STEAM-READONLY pid={pid}; started={expected}; structBytes={Marshal.SizeOf<SteamLaunchContext.ProcessEntry>()}; entries={observed?.Entries.Count}; complete={observed?.Complete}; confirmed={confirmed}; elapsedMs={elapsed.Elapsed.TotalMilliseconds:F2}; inputCalls=0");
            Require(confirmed, "Production collector did not confirm supplied launch chain");
        });
    }

    private sealed class Platform : IPlatform
    {
        internal WindowRef A = Game;
        internal readonly WindowRef B = new(102, 52, 200, "Other app", "other");
        internal WindowRef Current = Game;
        internal bool ValidIdentity = true;
        internal ModuleEvidence Module = ModuleEvidence.Denied;
        internal readonly List<WindowRef> Queries = [];
        internal Func<WindowRef, bool> Launched = _ => true;
        public bool IsPreview => true;

        public WindowRef? Foreground() => Current;
        public IReadOnlyList<WindowRef> Windows() => [A, B];
        public bool Valid(WindowRef window) => ValidIdentity && (window.Same(A) || window.Same(B));
        public IReadOnlyList<DisplayRef> Displays() => [new("fixture", 0, 0, 1280, 720, true)];
        public ModuleEvidence SteamEvidence(WindowRef? window) => Module;
        public bool HasSteamLaunchContext(WindowRef? target)
        {
            if (target is null)
                return false;
            lock (Queries)
                Queries.Add(target);
            return Launched(target);
        }

        public Task<ActionResult> Switch(WindowRef window) => throw new Exception("No real/fake input expected");
        public Task<ActionResult> Shortcut(WindowRef window, string keys) => throw new Exception("No input expected");
        public Task<ActionResult> Close(WindowRef window, CancellationToken cancellation) => throw new Exception("No close expected");
        public ActionResult Launch(bool playnite, string? path) => throw new Exception("No launch expected");
        public void Dispose()
        {
        }
    }
}

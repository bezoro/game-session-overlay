using ApolloOverlay.Testing;
using System.IO;
using System.Text.Json;
using ApolloOverlay;

static class Program
{
    private static readonly WindowRef Game = new(101, 42, 100, "Game", "game");
    private static readonly List<TestCaseResult> results = [];
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}, got {actual}");
    }

    private static void True(bool actual)
    {
        if (!actual)
            throw new Exception("Expected true");
    }

    private static void Case(string name, Action test)
    {
        try
        {
            test();
            results.Add(new(name, true, null));
            Console.WriteLine("PASS " + name);
        }
        catch (Exception e)
        {
            var failure = TestFailure.Capture(e);
            results.Add(new(name, false, failure.Details, failure));
            Console.Error.WriteLine("FAIL " + name + Environment.NewLine + failure.Details);
            failed++;
        }
    }

    private static int failed;
    private static ActionResult Run(Task<ActionResult> task) => task.GetAwaiter().GetResult();
    private static void TargetChangedDuringFocus()
    {
        var f = new FakeEffects
        {
            InvalidateOnFocus = true
        };
        Equal(ResultKind.Failed, Run(new GuardedActions(f).Shortcut(Game, "Shift+Tab")).Kind);
        Equal(0, f.Sends);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is [IsolatedActivationTests.PassiveArgument])
        {
            Thread.Sleep(150);
            return 0;
        }
        if (args is [IsolatedWindowActivation.HostArgument, var activation])
            return IsolatedWindowActivation.RunHost(activation);
        if (args is [IsolatedActivationTests.BrokerArgument, var brokerActivation, var brokerReceipt])
            return IsolatedActivationTests.RunBrokerHost(brokerActivation, brokerReceipt);
        if (args is [IsolatedActivationTests.ForegroundArgument, var foregroundReady, var foregroundIdentity])
            return IsolatedActivationTests.RunForegroundHost(foregroundReady, foregroundIdentity);
        if (args is [IsolatedActivationTests.HangArgument, var ready, var identity])
            return IsolatedActivationTests.Hang(ready, identity);
        if (args is [InstalledIntegrationTests.StartupLeaseOwnerArgument, var leaseReady, var leaseRelease, var leaseState])
            return InstalledIntegrationTests.RunStartupLeaseOwner(leaseReady, leaseRelease, leaseState);
        if (args is [ForeignWindowActivationTests.HungWindowArgument, var hungWindowReady, var hungWindowIdentity])
            return ForeignWindowActivationTests.RunHungWindowHost(hungWindowReady, hungWindowIdentity);
        if (args is [IsolatedActivationTests.ParentExitArgument, var parentReady, var parentIdentity])
            return IsolatedActivationTests.ExitParent(parentReady, parentIdentity);
        var request = TestRunnerArguments.Parse(args, Commands.ContainsKey);
        if (request.Mode is TestRunnerMode.Help or TestRunnerMode.List)
        {
            if (request.Mode == TestRunnerMode.Help)
            {
                Console.WriteLine("Usage: ApolloOverlay.Tests <report.json> [suite] | --list | --help");
                Console.WriteLine("Without a suite, all behavior tests run. Capture commands use an output directory instead of a report file.");
            }
            foreach (var command in Commands.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                Console.WriteLine($"{command.Key}  {command.Value.Description}");
            return 0;
        }
        if (request.Mode == TestRunnerMode.Invalid)
        {
            Console.Error.WriteLine(request.Error);
            Console.Error.WriteLine("Use --list to discover suites or --help for usage.");
            return 2;
        }
        return request.Command is { } selected
            ? Commands[selected].Execute(request.Output!)
            : RunAll(request.Output!);
    }

    private sealed record TestCommand(string Description, Func<string, int> Execute);

    private static TestCommand Suite(string description, Action<string> run) => new(description, output =>
    {
        run(output);
        return Finish(output);
    });

    private static readonly Dictionary<string, TestCommand> Commands = new(StringComparer.Ordinal)
    {
        ["--test-infrastructure"] = Suite("Runner evidence and WPF fixture ownership", _ => TestInfrastructureTests.Run(Case)),
        ["--master-audio"] = Suite("Serialized master audio ownership and verification", _ => MasterAudioTests.Run(Case)),
        ["--startup-status"] = Suite("Observed startup registration outcomes", _ => StartupStatusTests.Run(Case)),
        ["--installed-integration"] = Suite("Installed startup and cross-process mutation ownership", _ => InstalledIntegrationTests.Run(Case)),
        ["--input-safety"] = Suite("Controller slot handoff and input safety", _ => InputSafetyTests.Run(Case)),
        ["--active-display"] = Suite("Active display", output =>
        {
            ActiveDisplayTests.Run(Case);
        }),
        ["--active-display-related"] = Suite("Active display related", output =>
        {
            AdaptiveInterfaceTests.Run(Case);
            SessionUtilityTests.Run(Case);
        }),
        ["--battery-notifications"] = Suite("Battery notifications", output =>
        {
            ControllerBatteryNotificationTests.Run(Case);
        }),
        ["--updates"] = Suite("Updates", output =>
        {
            UpdateSourceTests.Run(Case);
            UpdateDiscoveryTests.Run(Case);
            LocalUpdateHistoryTests.Run(Case);
        }),
        ["--automatic-updates"] = Suite("Automatic updates", output =>
        {
            UpdateDiscoveryTests.Run(Case);
            AutomaticUpdateTests.Run(Case);
        }),
        ["--command-log"] = Suite("Command log", output =>
        {
            CommandLogTests.Run(Case);
            DiagnosticReportTests.Run(Case);
            GuideDiagnosticTrackerTests.Run(Case);
        }),
        ["--settings-focus"] = Suite("Settings focus", output =>
        {
            SettingsFocusTests.Run(Case);
        }),
        ["--window-activation"] = Suite("Window activation", output =>
        {
            WindowActivationTests.Run(Case);
            ForeignWindowActivationTests.Run(Case);
        }),
        ["--isolated-activation"] = Suite("Isolated activation", output =>
        {
            IsolatedActivationTests.Run(Case);
        }),
        ["--administrator-compatibility"] = Suite("Administrator compatibility", output =>
        {
            AdministratorCompatibilityTests.Run(Case);
        }),
        ["--elevated-close"] = Suite("Elevated close", output =>
        {
            Case("confirmed access-denied close offers one elevated request for the verified target", ConfirmedAccessDeniedClose);
            ElevatedWindowCloseTests.Run(Case);
        }),
        ["--interface-feedback"] = Suite("Interface feedback", output =>
        {
            InterfaceFeedbackTests.Run(Case);
        }),
        ["--mouse-keyboard"] = Suite("Mouse keyboard", output =>
        {
            MouseKeyboardTests.Run(Case);
        }),
        ["--agent-updates"] = Suite("Agent updates", output =>
        {
            AgentUpdateTests.Run(Case);
        }),
        ["--session-interface"] = Suite("Session interface", output =>
        {
            SessionInterfaceTests.Run(Case);
        }),
        ["--appearance"] = Suite("Appearance", output =>
        {
            AppearanceTests.Run(Case);
        }),
        ["--capture-presentation"] = Suite("Capture presentation", output =>
        {
            CapturePresentationTests.Run(Case);
        }),
        ["--session-interface-captures"] = new("Session interface captures", output =>
        {
            SessionInterfaceTests.Capture(output);
            return 0;
        }),
        ["--session-utilities"] = Suite("Session utilities", output =>
        {
            SessionUtilityTests.Run(Case);
        }),
        ["--update-captures"] = new("Update captures", output =>
        {
            UpdateInterfaceTests.Capture(output);
            return 0;
        }),
        ["--review-captures"] = new("Review captures", output =>
        {
            ReviewFixTests.Capture(output);
            return 0;
        }),
        ["--busy-pad"] = Suite("Busy pad", output =>
        {
            SessionHubTests.BusyControllerChecks(Case);
        }),
        ["--guide-settings"] = Suite("Guide settings", output =>
        {
            GuideAbiTests.GuideSettingsChecks(Case, Path.GetDirectoryName(Path.GetFullPath(output))!);
        }),
        ["--session-hub-services"] = Suite("Session hub services", output =>
        {
            SessionHubServiceTests.Run(Case);
        }),
        ["--session-hub-captures"] = new("Session hub captures", output =>
        {
            SessionHubTests.Capture(output);
            return 0;
        }),
        ["--session-hub-focus-captures"] = new("Session hub focus captures", output =>
        {
            SessionHubTests.Capture(output, true);
            return 0;
        }),
        ["--session-target"] = Suite("Session target", output =>
        {
            SessionHubTests.SessionTargetRegression(Case);
        }),
        ["--session-hub"] = Suite("Session hub", output =>
        {
            SessionHubTests.Run(Case);
        }),
        ["--session-hub-telemetry"] = Suite("Session hub telemetry", output =>
        {
            SessionHubTests.TelemetryFailures(Case);
        }),
        ["--automation-host"] = new("Automation host", output =>
        {
            return AutomationTests.Host(output);
        }),
        ["--automation-smoke"] = Suite("Automation smoke", output =>
        {
            AutomationTests.Prerequisites(Case);
        }),
        ["--automation"] = Suite("Automation", output =>
        {
            AutomationTests.Run(Case);
        }),
        ["--steam-sequence"] = Suite("Steam sequence", output =>
        {
            SteamShortcutFocusTests.SequenceChecks(Case);
        }),
        ["--steam-effects-handshake"] = Suite("Steam effects handshake", output =>
        {
            SteamShortcutFocusTests.EffectsHandshakeChecks(Case);
        }),
        ["--steam-closing"] = Suite("Steam closing", output =>
        {
            SteamShortcutFocusTests.ClosingChecks(Case);
        }),
        ["--steam-owner"] = Suite("Steam owner", output =>
        {
            SteamShortcutFocusTests.SequenceOwnerChecks(Case);
        }),
        ["--steam-focus"] = Suite("Steam focus", output =>
        {
            Case("target change during focus prevents dispatch", TargetChangedDuringFocus);
            SteamShortcutFocusTests.Run(Case);
        }),
        ["--steam-detection"] = Suite("Steam detection", output =>
        {
            SteamOverlayDetectionTests.Run(Case);
            GuideAbiTests.SteamLaunchRegression(Case);
        }),
        ["--steam-launch-smoke"] = Suite("Steam launch smoke", output =>
        {
            SteamOverlayDetectionTests.NativeSmoke(Case);
        }),
        ["--premium"] = Suite("Premium", output =>
        {
            PremiumInterfaceTests.Run(Case);
        }),
        ["--close-confirmation"] = Suite("Close confirmation", output =>
        {
            PremiumInterfaceTests.RunCloseConfirmation(Case);
        }),
        ["--close-confirmation-captures"] = new("Close confirmation captures", output =>
        {
            ReviewFixTests.Capture(output, closeConfirmation: true);
            return 0;
        }),
        ["--premium-safety"] = Suite("Premium safety", output =>
        {
            PremiumInterfaceTests.RunSafety(Case);
        }),
        ["--premium-local-dismiss"] = Suite("Premium local dismiss", output =>
        {
            PremiumInterfaceTests.LocalDismissalRegression(Case);
        }),
        ["--premium-navigation"] = Suite("Premium navigation", output =>
        {
            PremiumInterfaceTests.RunNavigation(Case);
        }),
        ["--reader-evidence"] = Suite("Reader evidence", output =>
        {
            PremiumInterfaceTests.ReaderEvidence(Case, Path.GetDirectoryName(Path.GetFullPath(output))!);
        }),
        ["--premium-safety-captures"] = new("Premium safety captures", output =>
        {
            ReviewFixTests.Capture(output, true);
            return 0;
        }),
        ["--review-fixes"] = Suite("Review fixes", output =>
        {
            ReviewFixTests.Run(Case);
        }),
        ["--guide-release-red"] = Suite("Guide release red", output =>
        {
            GuideAbiTests.GuideReleaseRed(Case);
        }),
        ["--focus-open-red"] = Suite("Focus open red", output =>
        {
            GuideAbiTests.FocusOpenRed(Case);
        }),
        ["--focus-suite"] = Suite("Focus suite", output =>
        {
            GuideAbiTests.HandoffTests(Case);
            GuideAbiTests.FocusDismiss(Case);
            GuideAbiTests.FocusChecks(Case);
            GuideAbiTests.GuideOpeningChecks(Case, Path.GetDirectoryName(Path.GetFullPath(output))!);
            Case("Guide status actual row", GuideAbiTests.LegacyStatusView);
            Case("legacy Guide ABI lifetime", GuideAbiTests.LegacyAbi);
            Case("legacy Guide failures", GuideAbiTests.LegacyFailures);
        }),
        ["--dismiss-known-held"] = Suite("Dismiss known held", output =>
        {
            GuideAbiTests.KnownHeldDismiss(Case);
        }),
        ["--dismiss-cancel"] = Suite("Dismiss cancel", output =>
        {
            GuideAbiTests.DismissCancellation(Case);
        }),
        ["--dismiss-cancel-suite"] = Suite("Dismiss cancel suite", output =>
        {
            GuideAbiTests.HandoffTests(Case);
            GuideAbiTests.FocusDismiss(Case);
        }),
        ["--system-key-baseline"] = Suite("System key baseline", output =>
        {
            Case("System AltY holds handoff after AltUp until YUp plus100ms", GuideAbiTests.SystemKeyBaseline);
        }),
        ["--disposed-platform-baseline"] = Suite("Disposed platform baseline", output =>
        {
            Case("Disposed platform refuses owned target validation and activation", GuideAbiTests.DisposedPlatformBaseline);
        }),
        ["--late-launch-baseline"] = Suite("Late launch baseline", output =>
        {
            Case("Cleanup prevents late Steam launch after pending focus completes", GuideAbiTests.LateLaunchBaseline);
        }),
        ["--release-baseline"] = Suite("Release baseline", output =>
        {
            GuideAbiTests.ReleaseBaseline(Case);
        }),
        ["--focus-dismiss"] = Suite("Focus dismiss", output =>
        {
            GuideAbiTests.FocusDismiss(Case);
        }),
        ["--guide-status"] = Suite("Guide status", output =>
        {
            Case("G3-01 Guide subtitle follows actual poll without rebuilding or hidden UI work", GuideAbiTests.LegacyStatusView);
        }),
        ["--corrections"] = Suite("Corrections", output =>
        {
            CorrectionTests.Run(Case, Path.GetDirectoryName(Path.GetFullPath(output))!);
        }),
    };

    private static int RunAll(string output)
    {
        Case("session survives our focus and launcher chain", () =>
        {
            var s = new SessionTracker();
            s.Observe(Game);
            s.Observe(Game with
            {
                Handle = 2,
                ProcessName = "Playnite.FullscreenApp"
            });
            s.Observe(Game with
            {
                Handle = 3,
                ProcessName = "steam"
            });
            s.Observe(Game with
            {
                Handle = 4,
                Protected = true
            });
            True(Game.Same(s.Anchor));
            s.Observe(Game with
            {
                Handle = 5,
                Pid = 50
            });
            Equal((nint)5, s.Anchor!.Handle);
        });
        Case("launcher alone does not become game", () =>
        {
            var s = new SessionTracker();
            s.Observe(Game with
            {
                ProcessName = "steam"
            });
            Equal<WindowRef?>(null, s.Anchor);
        });
        Case("session anchor immutable across panel observation", () =>
        {
            var s = new SessionTracker();
            s.Observe(Game);
            var held = s.Anchor;
            s.Observe(Game with
            {
                Handle = 8,
                Protected = true
            });
            True(Game.Same(held));
            Equal(2L, s.Sequence);
        });
        Case("process exit clears anchor", () =>
        {
            var s = new SessionTracker();
            s.Observe(Game);
            s.ClearIfStale(_ => false);
            Equal<WindowRef?>(null, s.Anchor);
        });
        Case("reused PID or HWND start identity rejected", () =>
        {
            True(!Game.Same(Game with
            {
                Started = 200
            }));
            True(!Game.Same(Game with
            {
                Pid = 43
            }));
            True(!Game.Same(Game with
            {
                Handle = 102
            }));
        });
        Case("same-process HWND replacement uses destruction generation", () =>
        {
            True(!Game.Same(Game with
            {
                Generation = 1
            }));
            var f = new FakeEffects
            {
                IsValid = false
            };
            Equal(ResultKind.Failed, Run(new GuardedActions(f).Close(Game, CancellationToken.None)).Kind);
            Equal(0, f.Closes);
        });
        Case("system and unknown identities cannot close", () =>
        {
            True(!(Game with
            {
                Protected = true
            }).CanClose);
            True(!(Game with
            {
                Started = 0
            }).CanClose);
        });
        Case("launchers cannot close even with valid native identity", () =>
        {
            foreach (var name in new[]
            {
                "steam",
                "Playnite.FullscreenApp",
                "Playnite.DesktopApp"
            }

            )
            {
                var f = new FakeEffects();
                Equal(ResultKind.Failed, Run(new GuardedActions(f).Close(Game with
                {
                    ProcessName = name
                }, CancellationToken.None)).Kind);
                Equal(0, f.Closes);
            }
        });
        Case("steam running alone stays unknown", () =>
        {
            var s = new SteamPermission();
            True(!s.ToggleAllowed(Game, ModuleEvidence.Unknown, "Shift+Tab"));
        });
        Case("steam missing and access denied stay disabled", () =>
        {
            var s = new SteamPermission();
            s.Verify(Game, "Shift+Tab");
            True(!s.ToggleAllowed(Game, ModuleEvidence.Absent, "Shift+Tab"));
            True(!s.ToggleAllowed(Game, ModuleEvidence.Denied, "Shift+Tab"));
        });
        Case("loaded non-steam shortcut requires manual verification", () =>
        {
            var s = new SteamPermission();
            True(!s.ToggleAllowed(Game, ModuleEvidence.Loaded, "Shift+Tab"));
            s.Verify(Game, "Shift+Tab");
            True(s.ToggleAllowed(Game, ModuleEvidence.Loaded, "Shift+Tab"));
        });
        Case("steam verification invalidated by session or shortcut", () =>
        {
            var s = new SteamPermission();
            s.Verify(Game, "Shift+Tab");
            True(!s.ToggleAllowed(Game with
            {
                Started = 200
            }, ModuleEvidence.Loaded, "Shift+Tab"));
            True(!s.ToggleAllowed(Game, ModuleEvidence.Loaded, "Ctrl+Tab"));
            s.Invalidate();
            True(!s.ToggleAllowed(Game, ModuleEvidence.Loaded, "Shift+Tab"));
        });
        Case("A release before rearm and no repeated activation", () =>
        {
            var p = new PadInterpreter();
            Equal(PadButtons.None, p.Update(new(true, PadButtons.A), 0).Pressed);
            p.Update(new(true, 0), 1);
            Equal(PadButtons.A, p.Update(new(true, PadButtons.A), 2).Pressed);
            Equal(PadButtons.None, p.Update(new(true, PadButtons.A), 3).Pressed);
        });
        Case("reconnect requires neutral", () =>
        {
            var p = new PadInterpreter();
            p.Update(new(true, 0), 0);
            p.Update(default, 1);
            Equal(PadButtons.None, p.Update(new(true, PadButtons.A), 2).Pressed);
        });
        Case("direction deadzone and bounded repeat", () =>
        {
            var p = new PadInterpreter();
            p.Update(new(true, 0), 0);
            Equal(0, p.Update(new(true, 0, 0, 9000), 1).Direction);
            Equal(-1, p.Update(new(true, PadButtons.Up), 2).Direction);
            Equal(0, p.Update(new(true, PadButtons.Up), 100).Direction);
            Equal(-1, p.Update(new(true, PadButtons.Up), 422).Direction);
            Equal(0, p.Update(new(true, PadButtons.Up), 430).Direction);
        });
        Case("foreground refusal prevents shortcut dispatch", () =>
        {
            var f = new FakeEffects
            {
                FocusAllowed = false
            };
            Equal(ResultKind.Failed, Run(new GuardedActions(f).Shortcut(Game, "Win+G")).Kind);
            Equal(0, f.Sends);
        });
        Case("foreground timeout is unknown and sends nothing", () =>
        {
            var f = new FakeEffects
            {
                Foreground = false
            };
            Equal(ResultKind.Unknown, Run(new GuardedActions(f).Shortcut(Game, "Win+G")).Kind);
            Equal(0, f.Sends);
            Equal(6, f.Waits);
        });
        Case("target change during focus prevents dispatch", TargetChangedDuringFocus);
        Case("partial SendInput batch does not retry Ctrl Tab shortcut", () =>
        {
            var f = new FakeEffects
            {
                Sent = 2
            };
            Equal(ResultKind.Partial, Run(new GuardedActions(f).Shortcut(Game, "Ctrl+Tab")).Kind);
            Equal(1, f.Sends);
        });
        Case("dispatch is requested not opened", () =>
        {
            var f = new FakeEffects();
            Equal(ResultKind.Requested, Run(new GuardedActions(f).Shortcut(Game, "Win+Tab")).Kind);
        });
        Case("close target does not follow current foreground", () =>
        {
            var f = new FakeEffects
            {
                StillExists = false
            };
            Equal(ResultKind.Completed, Run(new GuardedActions(f).Close(Game, CancellationToken.None)).Kind);
            True(Game.Same(f.Closed));
        });
        Case("stale target blocks close", () =>
        {
            var f = new FakeEffects
            {
                IsValid = false
            };
            Equal(ResultKind.Failed, Run(new GuardedActions(f).Close(Game, CancellationToken.None)).Kind);
            Equal(0, f.Closes);
        });
        Case("close timeout never escalates", () =>
        {
            var f = new FakeEffects();
            Equal(ResultKind.Unknown, Run(new GuardedActions(f).Close(Game, CancellationToken.None)).Kind);
            Equal(1, f.Closes);
            Equal(20, f.Waits);
        });
        Case("close cancellation before dispatch prevents effects", () =>
        {
            var f = new FakeEffects();
            using var c = new CancellationTokenSource();
            c.Cancel();
            Equal(ResultKind.Cancelled, Run(new GuardedActions(f).Close(Game, c.Token)).Kind);
            Equal(0, f.Closes);
        });
        Case("close cancellation after dispatch is unknown", () =>
        {
            using var c = new CancellationTokenSource();
            var f = new FakeEffects
            {
                OnWait = () => c.Cancel()
            };
            Equal(ResultKind.Unknown, Run(new GuardedActions(f).Close(Game, c.Token)).Kind);
            Equal(1, f.Closes);
        });
        Case("close API failure has no observation or retry", () =>
        {
            var f = new FakeEffects
            {
                CloseAllowed = false
            };
            Equal(ResultKind.Failed, Run(new GuardedActions(f).Close(Game, CancellationToken.None)).Kind);
            Equal(1, f.Closes);
            Equal(0, f.Waits);
        });
        Case("blank caption with live identity never confirms close", () =>
        {
            var f = new FakeEffects
            {
                Probe = new(true, Game.Pid, Game.Started, Game.Generation, "")
            };
            Equal(ResultKind.Unknown, Run(new GuardedActions(f).Close(Game, CancellationToken.None)).Kind);
            Equal(1, f.Closes);
            Equal(20, f.Waits);
        });
        Case("denied identity inspection never confirms close", () =>
        {
            var f = new FakeEffects
            {
                Probe = new(true, Game.Pid, null, Game.Generation, "Still open")
            };
            Equal(ResultKind.Unknown, Run(new GuardedActions(f).Close(Game, CancellationToken.None)).Kind);
            Equal(20, f.Waits);
        });
        Case("failed PID inspection never confirms close", () =>
        {
            var f = new FakeEffects
            {
                Probe = new(true, null, null, Game.Generation, "Still open")
            };
            Equal(ResultKind.Unknown, Run(new GuardedActions(f).Close(Game, CancellationToken.None)).Kind);
        });
        Case("reliable destruction confirms close", () =>
        {
            var f = new FakeEffects
            {
                Probe = new(false, null, null, Game.Generation, "")
            };
            Equal(ResultKind.Completed, Run(new GuardedActions(f).Close(Game, CancellationToken.None)).Kind);
        });
        Case("reliable identity replacement confirms old window gone", () =>
        {
            foreach (var probe in new[]
            {
                new WindowProbe(true, Game.Pid + 1, null, Game.Generation, ""),
                new(true, Game.Pid, Game.Started + 1, Game.Generation, ""),
                new(true, Game.Pid, null, Game.Generation + 1, "")
            }

            )
            {
                var f = new FakeEffects
                {
                    Probe = probe
                };
                Equal(ResultKind.Completed, Run(new GuardedActions(f).Close(Game, CancellationToken.None)).Kind);
            }
        });
        Case("display removal falls back to current primary", () =>
        {
            DisplayRef[] d = [new("left", -1920, 0, 1920, 1080, false), new("primary", 0, 0, 3840, 2160, true)];
            Equal("primary", DisplayRef.Choose(d, "removed").Id);
            Equal("left", DisplayRef.Choose(d, "left").Id);
        });
        Case("negative origin and DPI convert once", () =>
        {
            var d = new DisplayRef("left", -1920, -300, 1920, 1080, false).Dips(144);
            Equal(-1280d, d.X);
            Equal(-200d, d.Y);
            Equal(1280d, d.Width);
        });
        Case("invalid settings rejected", () =>
        {
            foreach (var text in new[]
            {
                "{",
                "null",
                "{\"SteamShortcut\":\"Alt+F4\"}",
                "{\"PlaynitePath\":\"relative.exe\"}"
            }

            )
            {
                var rejected = false;
                try
                {
                    UserSettings.Parse(text);
                }
                catch (Exception e) when (e is JsonException or InvalidDataException)
                {
                    rejected = true;
                }

                True(rejected);
            }
        });
        Case("valid settings source-generated roundtrip", () =>
        {
            var s = UserSettings.Parse("{\"SteamShortcut\":\"Ctrl+Tab\",\"DisplayId\":\"left\"}");
            Equal("Ctrl+Tab", s.SteamShortcut);
            Equal("left", s.DisplayId);
        });
        Case("saving recovered settings preserves previous-good backup", () =>
        {
            var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "settings-fixtures");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "recovery.json");
            UserSettings.Save(path, new(DisplayId: "first"));
            UserSettings.Save(path, new(DisplayId: "second"));
            File.WriteAllText(path, "{");
            var recovered = UserSettings.Load(path, out var message);
            Equal("first", recovered.DisplayId);
            True(message.Contains("recovered"));
            UserSettings.Save(path, recovered);
            Equal("first", UserSettings.Parse(File.ReadAllText(path + ".previous")).DisplayId);
        });
        Case("preview actions remain simulated", () =>
        {
            using var p = new PreviewPlatform(Game);
            var target = p.Foreground()!;
            Equal(ResultKind.Simulated, p.Launch(true, "missing.exe").Kind);
            Equal(ResultKind.Simulated, Run(p.Shortcut(target, "Win+G")).Kind);
            Equal(ResultKind.Simulated, Run(p.Switch(target)).Kind);
            Equal(ResultKind.Simulated, Run(p.Close(target, CancellationToken.None)).Kind);
            True(!p.Valid(target));
        });
        Case("guide initialization failure releases without registration", () =>
        {
            var f = new FakeGuide
            {
                Init = false
            };
            using (var c = new GuideConnection(f))
            {
                True(!c.Start(() =>
                {
                }));
            }

            Equal("initialize,release", string.Join(',', f.Calls));
        });
        Case("guide registration failure releases initialized instance", () =>
        {
            var f = new FakeGuide
            {
                Registration = false
            };
            using (var c = new GuideConnection(f))
            {
                True(!c.Start(() =>
                {
                }));
            }

            Equal("initialize,register,release", string.Join(',', f.Calls));
        });
        Case("guide stops and unregisters before release", () =>
        {
            var f = new FakeGuide();
            using (var c = new GuideConnection(f))
            {
                True(c.Start(() =>
                {
                }));
                True(!c.Observed);
                f.Press!();
                True(c.Observed);
            }

            Equal("initialize,register,stop,unregister,release", string.Join(',', f.Calls));
        });
        Case("guide failed unregister never unloads live callback", () =>
        {
            var f = new FakeGuide
            {
                Removal = false
            };
            var c = new GuideConnection(f);
            c.Start(() =>
            {
            });
            c.Dispose();
            c.Dispose();
            Equal("initialize,register,stop,unregister", string.Join(',', f.Calls));
        });
        Case("guide native vtable callback ABI and successful lifetime", GuideAbiTests.Success);
        Case("guide native registration failure releases safely", GuideAbiTests.RegistrationFailure);
        Case("guide native failed unregister retains callback context", GuideAbiTests.UnregisterFailure);
        Case("legacy Guide ABI16 offset4 and held startup never toggles", GuideAbiTests.LegacyAbi);
        Case("legacy Guide repress error disconnect reconnect latch", GuideAbiTests.LegacyCycles);
        Case("legacy Guide four independent slots coalesce one toggle", GuideAbiTests.LegacySlots);
        Case("legacy Guide DLL export failure and disposed late poll unload once", GuideAbiTests.LegacyFailures);
        Case("legacy Guide polling interval and single-consumption latch", GuideAbiTests.LegacyTick);
        Case("G3-01 Guide subtitle follows actual poll without rebuilding or hidden UI work", GuideAbiTests.LegacyStatusView);
        GuideAbiTests.FocusDismiss(Case);
        GuideAbiTests.ReleaseBaseline(Case);
        Case("Cleanup prevents late Steam launch after pending focus completes", GuideAbiTests.LateLaunchBaseline);
        Case("Disposed platform refuses owned target validation and activation", GuideAbiTests.DisposedPlatformBaseline);
        GuideAbiTests.HandoffTests(Case);
        GuideAbiTests.FadeHandoffChecks(Case);
        SteamShortcutFocusTests.Run(Case);
        AutomationTests.Run(Case);
        SteamOverlayDetectionTests.Run(Case);
        GuideAbiTests.SteamLaunchRegression(Case);
        Case("Guide defaults on and preserves explicit disabled backup", () =>
        {
            True(UserSettings.Parse("{}").GuideXInputEnabled);
            True(!UserSettings.Parse("{\"GuideXInputEnabled\":false}").GuideXInputEnabled);
            True(UserSettings.Parse("{\"GuideXInputEnabled\":true}").GuideXInputEnabled);
            var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "guide-settings.json");
            UserSettings.Save(path, new(GuideXInputEnabled: false));
            UserSettings.Save(path, new(GuideXInputEnabled: true));
            True(UserSettings.Load(path, out _).GuideXInputEnabled);
            True(!UserSettings.Parse(File.ReadAllText(path + ".previous")).GuideXInputEnabled);
        });
        GuideAbiTests.GuideSettingsChecks(Case, Path.GetDirectoryName(Path.GetFullPath(output))!);
        UpdateInterfaceTests.Run(Case);
        UpdateSourceTests.Run(Case);
        UpdateDiscoveryTests.Run(Case);
        AutomaticUpdateTests.Run(Case);
        LocalUpdateHistoryTests.Run(Case);
        CommandLogTests.Run(Case);
        CommandDiagnosticsTests.Run(Case);
        InterfaceFeedbackTests.Run(Case);
        AdaptiveInterfaceTests.Run(Case);
        ActiveDisplayTests.Run(Case);
        SettingsFocusTests.Run(Case);
        WindowActivationTests.Run(Case);
        ForeignWindowActivationTests.Run(Case);
        IsolatedActivationTests.Run(Case);
        DiagnosticReportTests.Run(Case);
        GuideDiagnosticTrackerTests.Run(Case);
        AdministratorCompatibilityTests.Run(Case);
        Case("confirmed access-denied close offers one elevated request for the verified target", ConfirmedAccessDeniedClose);
        ElevatedWindowCloseTests.Run(Case);
        MouseKeyboardTests.Run(Case);
        AgentUpdateTests.Run(Case);
        MotionAndControllerTests.Run(Case);
        ControllerBatteryNotificationTests.Run(Case);
        SessionMediaLifecycleTests.Run(Case);
        SessionUtilityTests.Run(Case);
        SessionInterfaceTests.Run(Case);
        AppearanceTests.Run(Case);
        CapturePresentationTests.Run(Case);
        PremiumInterfaceTests.Run(Case);
        SessionHubTests.Run(Case);
        SessionHubServiceTests.Run(Case);
        ReviewFixTests.Run(Case);
        NavigationTests.Run(Case);
        GuardedActionCancellationTests.Run(Case);
        TvUsabilityTests.Run(Case);
        WindowIconCacheTests.Run(Case);
        PlayniteBrowserTests.Run(Case);
        StartupRegistrationTests.Run(Case);
        InstalledIntegrationTests.Run(Case);
        CorrectionTests.Run(Case, Path.GetDirectoryName(Path.GetFullPath(output))!);
        StartupStatusTests.Run(Case);
        MasterAudioTests.Run(Case);
        InputSafetyTests.Run(Case);
        TestInfrastructureTests.Run(Case);
        return Finish(output);
    }

    private static int Finish(string path)
    {
        var report = new TestReport(results.Count, results.Count - failed, failed, results.ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, TestJson.Default.TestReport));
        Console.WriteLine($"RESULT {results.Count - failed}/{results.Count} passed; failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static void ConfirmedAccessDeniedClose()
    {
        var fixture = new FakeEffects
        {
            CloseAllowed = false,
            StillExists = false
        };
        var result = Run(new GuardedActions(fixture).Close(Game, CancellationToken.None,
            (target, _) => fixture.PostClose(target) ? ClosePost.Accepted : ClosePost.AccessDenied, fixture.OfferElevation));
        Equal(1, fixture.Closes);
        True(Game.Same(fixture.Closed));
        Equal(1, fixture.ElevationOffers);
        Equal(ResultKind.Completed, result.Kind);
    }

    private sealed class FakeEffects : IWindowEffects
    {
        public bool IsValid = true, Foreground = true, FocusAllowed = true, InvalidateOnFocus, StillExists = true, CloseAllowed = true;
        public uint Sent = 4;
        public int Sends, Closes, Waits;
        public int ElevationOffers
        {
            get; private set;
        }
        public Task<ActionResult> OfferElevation(WindowRef target, CancellationToken cancellation)
        {
            True(Game.Same(target));
            True(!cancellation.IsCancellationRequested);
            ElevationOffers++;
            return Task.FromResult(new ActionResult(ResultKind.Requested, "Fixture accepted WM_CLOSE."));
        }
        public WindowRef? Closed;
        public Action? OnWait;
        public WindowProbe? Probe;
        public bool Valid(WindowRef window) => IsValid;
        public WindowObservation ObserveWindow(WindowRef window) => Probe is { } probe ? ClosureObservation.Classify(window, probe) : StillExists ? WindowObservation.SameIdentity : WindowObservation.GoneOrReplaced;
        public bool IsForeground(WindowRef window) => Foreground;
        public bool RequestFocus(WindowRef window)
        {
            if (InvalidateOnFocus)
                IsValid = false;
            return FocusAllowed;
        }

        public uint SendKeys(string keys)
        {
            Sends++;
            return Sent;
        }

        public bool PostClose(WindowRef window)
        {
            Closes++;
            Closed = window;
            return CloseAllowed;
        }

        public Task Wait(int milliseconds)
        {
            Waits++;
            OnWait?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGuide : IGuideBackend
    {
        public bool Init = true, Registration = true, Removal = true;
        public readonly List<string> Calls = [];
        public Action? Press;
        public bool Initialize(out string reason)
        {
            Calls.Add("initialize");
            reason = "test";
            return Init;
        }

        public bool Register(Action pressed, out string reason)
        {
            Calls.Add("register");
            Press = pressed;
            reason = "test";
            return Registration;
        }

        public void Stop() => Calls.Add("stop");
        public bool Unregister()
        {
            Calls.Add("unregister");
            return Removal;
        }

        public void Release() => Calls.Add("release");
    }
}

internal sealed record TestCaseResult(string Name, bool Passed, string? Error, TestFailure? Failure = null);
internal sealed record TestReport(int Total, int Passed, int Failed, TestCaseResult[] Cases);
[System.Text.Json.Serialization.JsonSerializable(typeof(TestReport))]
internal partial class TestJson : System.Text.Json.Serialization.JsonSerializerContext;

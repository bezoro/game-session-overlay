using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Windows.Interop;
using System.Windows.Threading;
using ApolloOverlay;
using ApolloOverlay.Automation;

internal static class ElevatedWindowCloseTests
{
    private static readonly AutomationPipeSecurity.Peer Standard = new("user", "logon", 1, 41, 100);
    private static readonly AutomationPipeSecurity.Peer Elevated = Standard with { Pid = 42, Started = 200, Elevated = true };
    private static readonly WindowRef Target = new(100, 42, 200, "Game", "game", Generation: 300);

    internal static void Run(Action<string, Action> test)
    {
        test("ordinary, allowed elevated, protected, launcher, and other native close results use no consent", StandardFirst);
        test("a close receipt or missing receipt requires reliable observation of the original window", Observation);
        test("expired or stale authorization cannot start writing a grant", BeforeGrant);
        test("write failure and cancellation after the grant begins remain unconfirmed", UncertainExchange);
        test("one grant distinguishes helper rejection, refused post, and queued post", Receipts);
        test("one-shot host rejects malformed arguments and versioned grants", Protocol);
        test("one-shot peer authorization rejects wrong process, lifetime, elevation, and user/logon/session", Peers);
        test("child checks parent, target, lifetime, cancellation and channel before the single post", ChildDispatch);
        test("denied consent leaves no child and releases the prompt gate", DeniedConsent);
        test("cancelled or expired consent retains prompt exclusion until the shell returns", LateConsent);
        test("restricted one-shot pipe verifies the connected kernel process on each end", RealPipe);
        test("ordinary Windows close posts only to its verified disposable window", OrdinaryWindowsClose);
    }

    private static void StandardFirst()
    {
        foreach (var peer in new[] { Elevated, Elevated with { Elevated = false } })
        {
            var fixture = new ObservationEffects();
            var offers = 0;
            var posts = 0;
            var result = new GuardedActions(fixture).Close(Target, CancellationToken.None,
                (_, _) => { posts++; return ClosePost.Accepted; },
                (_, _) =>
                {
                    if (ElevatedWindowClose.ElevatedTarget(peer, Standard, Target.Pid, Target.Started))
                        offers++;
                    return Task.FromResult(new ActionResult(ResultKind.Unavailable, "Unexpected fallback"));
                }).GetAwaiter().GetResult();
            Check(result.Kind == ResultKind.Unknown && posts == 1 && offers == 0, "An accepted standard request did not remain observation-only");
        }

        foreach (var target in new[] { Target, Target with { Protected = true }, Target with { ProcessName = "steam" } })
        {
            var posts = 0;
            var result = new GuardedActions(new ObservationEffects()).Close(target, CancellationToken.None,
                (_, _) => { posts++; return ClosePost.Rejected; },
                (_, _) => throw new Exception("A protected target or other error offered consent")).GetAwaiter().GetResult();
            Check(result.Kind == ResultKind.Failed && posts == (target.CanClose ? 1 : 0), "A blocked target was posted or retried");
        }

        using IPlatform preview = new PreviewPlatform(Target);
        var simulated = preview.CloseConfirmed(Target, CancellationToken.None,
            () => throw new Exception("Preview offered consent")).GetAwaiter().GetResult();
        Check(simulated.Kind == ResultKind.Simulated, "Preview stopped simulating close");
    }

    private static void Observation()
    {
        foreach (var receipt in new[] { ResultKind.Requested, ResultKind.Unknown })
        {
            foreach (var observation in new[] { WindowObservation.SameIdentity, WindowObservation.Unknown, WindowObservation.GoneOrReplaced })
            {
                var fixture = new ObservationEffects
                {
                    Observation = observation
                };
                var result = new GuardedActions(fixture).Close(Target, CancellationToken.None, (_, _) => ClosePost.AccessDenied,
                    (_, _) => Task.FromResult(new ActionResult(receipt, "Fixture receipt"))).GetAwaiter().GetResult();
                var expected = observation == WindowObservation.GoneOrReplaced ? ResultKind.Completed : ResultKind.Unknown;
                Check(result.Kind == expected, $"Receipt {receipt} and observation {observation} yielded {result.Kind}");
            }
        }

        using var cancellation = new CancellationTokenSource();
        var afterGrant = new GuardedActions(new ObservationEffects()).Close(Target, cancellation.Token, (_, _) => ClosePost.AccessDenied,
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(new ActionResult(ResultKind.Unknown, "Fixture lost its receipt"));
            }).GetAwaiter().GetResult();
        Check(afterGrant.Kind == ResultKind.Unknown, "Cancellation after a grant was treated as definite failure");
    }

    private static void BeforeGrant()
    {
        using var pipe = new ExchangeStream();
        var expired = NewGrant() with
        {
            Deadline = Environment.TickCount64
        };
        Check(Exchange(pipe, expired).Kind == ResultKind.Unavailable && pipe.Writes == 0, "An expired grant was written");
        Check(Exchange(pipe, NewGrant(), () => false).Kind == ResultKind.Unavailable && pipe.Writes == 0,
            "A changed window generation allowed a grant");
        using var cancellation = new CancellationTokenSource();
        var result = Exchange(pipe, NewGrant(), () =>
        {
            cancellation.Cancel();
            return true;
        }, cancellation.Token);
        Check(result.Kind == ResultKind.Cancelled && pipe.Writes == 0, "Cancellation during revalidation allowed a grant");
    }

    private static void UncertainExchange()
    {
        using var cancellation = new CancellationTokenSource();
        using var lost = new ExchangeStream();
        Check(Exchange(lost, NewGrant()).Kind == ResultKind.Unknown && lost.Writes == 1, "A lost receipt proved an effect or prompted a retry");
        using var partial = new ExchangeStream
        {
            OnWrite = () => throw new IOException("Grant may have crossed the pipe")
        };
        Check(Exchange(partial, NewGrant()).Kind == ResultKind.Unknown && partial.Writes == 1, "A write failure proved no effect");
        using var interrupted = new ExchangeStream
        {
            OnWrite = cancellation.Cancel
        };
        Check(Exchange(interrupted, NewGrant(), cancellation: cancellation.Token).Kind == ResultKind.Unknown && interrupted.Writes == 1,
            "Cancellation after writing began proved no effect");
    }

    private static void Receipts()
    {
        foreach (var (receipt, expected) in new[]
        {
            (ElevatedWindowClose.Receipt.Rejected, ResultKind.Unavailable),
            (ElevatedWindowClose.Receipt.Refused, ResultKind.Failed),
            (ElevatedWindowClose.Receipt.Accepted, ResultKind.Requested)
        })
        {
            using var pipe = new ExchangeStream
            {
                Reply = (byte)receipt
            };
            var grant = NewGrant();
            var result = Exchange(pipe, grant);
            Check(result.Kind == expected && pipe.Writes == 1 && pipe.Grant == grant, $"Receipt {receipt} changed or retried the grant");
        }
    }

    private static void Protocol()
    {
        var request = new ElevatedWindowClose.HostRequest(Guid.NewGuid(), Standard.Pid, Standard.Started);
        var argument = ElevatedWindowClose.HostArgument;
        var encoded = request.Encode();
        Check(ElevatedWindowClose.HostRequest.TryDecode(encoded, out var actual) && actual == request, "The channel envelope changed identity");
        Check(ElevatedWindowClose.Bootstrap(["--startup"]) is null, "Ordinary startup invoked the helper");
        foreach (var args in new[]
        {
            new[] { argument }, [argument, ""], [argument, encoded + ":100"], ["--startup", argument, encoded],
            [argument, encoded, "--show"], [argument.ToUpperInvariant(), encoded], [argument + "=" + encoded]
        })
            Check(ElevatedWindowClose.Bootstrap(args) == 2, "A malformed internal invocation fell through startup");
        var grant = NewGrant();
        var bytes = grant.Encode();
        Check(ElevatedWindowClose.Grant.TryDecode(bytes, out var decoded) && decoded == grant, "The encoded grant changed identity");
        Check(!ElevatedWindowClose.Grant.TryDecode(bytes.AsSpan(1), out _), "A truncated grant was accepted");
        bytes[0]++;
        Check(!ElevatedWindowClose.Grant.TryDecode(bytes, out _), "An unknown protocol version was accepted");
        Check(!ElevatedWindowClose.Grant.TryDecode((grant with
        {
            Thread = 0
        }).Encode(), out _), "A grant without an owner thread was accepted");
    }

    private static void Peers()
    {
        Check(ElevatedWindowClose.MatchesProcess(Elevated, Standard, Elevated.Pid, Elevated.Started, requireElevated: true)
            && ElevatedWindowClose.MatchesProcess(Standard, Elevated, Standard.Pid, Standard.Started, requireElevated: false),
            "Expected parent/child peer identities were refused");
        foreach (var changed in new[]
        {
            Elevated with { Pid = 60 }, Elevated with { Started = 400 }, Elevated with { Elevated = false },
            Elevated with { User = "other" }, Elevated with { Logon = "other" }, Elevated with { Session = 2 }
        })
        {
            Check(!ElevatedWindowClose.MatchesProcess(changed, Standard, Elevated.Pid, Elevated.Started, requireElevated: true),
                "A different child process was trusted");
            Check(!ElevatedWindowClose.ElevatedTarget(changed, Standard, Target.Pid, Target.Started), "A different target token was trusted");
        }

        Check(!ElevatedWindowClose.MatchesProcess(Standard with
        {
            Elevated = true
        }, Elevated, Standard.Pid, Standard.Started, requireElevated: false),
            "An elevated parent was trusted");
        Check(!ElevatedWindowClose.MatchesProcess(Elevated, Standard with
        {
            Elevated = true
        }, Elevated.Pid, Elevated.Started, requireElevated: true),
            "An already-elevated caller was accepted");
    }

    private static void ChildDispatch()
    {
        using var cancellation = new CancellationTokenSource();
        var grant = NewGrant();
        var posted = 0;
        ClosePost Send(nint handle)
        {
            Check(handle == grant.Handle, "The post was redirected to another window");
            posted++;
            return ClosePost.Accepted;
        }

        (ElevatedWindowClose.Grant, Func<bool>, Func<bool>, Func<bool>)[] rejected =
        [
            (grant, () => false, () => true, () => true),
            (grant, () => true, () => false, () => true),
            (grant, () => true, () => true, () => false),
            (grant with { Deadline = Environment.TickCount64 }, () => true, () => true, () => true),
            (grant, () => true, () => true, () => { cancellation.Cancel(); return true; })
        ];
        foreach (var (request, parent, target, channel) in rejected)
        {
            Check(ElevatedWindowClose.Dispatch(request, cancellation.Token, parent, target, channel, Send)
                == ElevatedWindowClose.Receipt.Rejected && posted == 0, "A stale, expired or cancelled child request posted");
        }

        var receipt = ElevatedWindowClose.Dispatch(grant, CancellationToken.None, () => true, () => true, () => true, Send);
        Check(receipt == ElevatedWindowClose.Receipt.Accepted && posted == 1, "The authorized helper did not post exactly once");
        receipt = ElevatedWindowClose.Dispatch(grant, CancellationToken.None, () => true, () => true, () => true, _ => ClosePost.AccessDenied);
        Check(receipt == ElevatedWindowClose.Receipt.Refused, "An OS refusal was reported as accepted");
    }

    private static void DeniedConsent()
    {
        Check(AdministratorRestart.TryReservePrompt(), "The shared prompt gate was unexpectedly busy");
        var denied = ElevatedWindowClose.AwaitConsentAsync(() => throw new Win32Exception(1223), CancellationToken.None, CancellationToken.None)
            .GetAwaiter().GetResult();
        Check(denied.Child is null && denied.Failure?.Kind == ResultKind.Cancelled, "UAC denial permitted a child");
        Check(AdministratorRestart.TryReservePrompt(), "The denied shell call held the gate after returning");
        AdministratorRestart.ReleasePrompt();
    }

    private static void LateConsent()
    {
        foreach (var explicitCancellation in new[] { true, false })
        {
            using var consent = new CancellationTokenSource();
            using var cancellation = new CancellationTokenSource();
            using var shellEntered = new ManualResetEventSlim();
            using var releaseShell = new ManualResetEventSlim();
            Check(AdministratorRestart.TryReservePrompt(), "The shared prompt gate was unexpectedly busy");
            var pending = ElevatedWindowClose.AwaitConsentAsync(() =>
            {
                shellEntered.Set();
                releaseShell.Wait(TimeSpan.FromSeconds(8));
                return null;
            }, consent.Token, cancellation.Token);
            try
            {
                Check(shellEntered.Wait(TimeSpan.FromSeconds(4)), "The synthetic shell was never called");
                if (explicitCancellation)
                    cancellation.Cancel();
                consent.Cancel();
                var aborted = pending.GetAwaiter().GetResult();
                Check(aborted.Child is null && aborted.Failure?.Kind == (explicitCancellation ? ResultKind.Cancelled : ResultKind.Unavailable),
                    "A cancelled or expired prompt returned an authorized child");
                Check(!AdministratorRestart.TryReservePrompt(), "Cancellation or expiry allowed an overlapping prompt");
            }
            finally
            {
                releaseShell.Set();
            }

            Check(SpinWait.SpinUntil(AdministratorRestart.TryReservePrompt, TimeSpan.FromSeconds(4)), "The returned shell call did not release the gate");
            AdministratorRestart.ReleasePrompt();
        }
    }

    private static void RealPipe()
    {
        var name = new ElevatedWindowClose.HostRequest(Guid.NewGuid(), Environment.ProcessId, 1).PipeName;
        using var server = AutomationPipeSecurity.CreateOneShot(name);
        try
        {
            using var second = AutomationPipeSecurity.CreateOneShot(name);
            throw new Exception("A second server instance claimed the channel");
        }
        catch (UnauthorizedAccessException)
        {
        }

        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        Task.WhenAll(server.WaitForConnectionAsync(deadline.Token), client.ConnectAsync(deadline.Token)).GetAwaiter().GetResult();
        Check(AutomationPipeSecurity.IsClientProcess(server, Environment.ProcessId) && AutomationPipeSecurity.IsServerProcess(client, Environment.ProcessId),
            "Kernel process identities did not match the connected fixture");
        Check(!AutomationPipeSecurity.IsClientProcess(server, int.MaxValue) && !AutomationPipeSecurity.IsServerProcess(client, int.MaxValue),
            "The pipe trusted a process that was not connected");
        client.Dispose();
        Check(!AutomationPipeSecurity.Alive(server), "The pipe reported a disposed client as live");
    }

    private static void OrdinaryWindowsClose()
    {
        var parameters = new HwndSourceParameters("Disposable close test")
        {
            WindowStyle = 0x10CF0000, // WS_VISIBLE | WS_OVERLAPPEDWINDOW
            ExtendedWindowStyle = 0x08000000, // WS_EX_NOACTIVATE
            PositionX = -32000,
            PositionY = -32000,
            Width = 120,
            Height = 80
        };
        using var fixture = new HwndSource(parameters);
        using var platform = new WindowsPlatform(allowOwnedFixture: true);
        var target = platform.Windows().Single(window => window.Handle == fixture.Handle);
        var thread = Native.GetWindowThreadProcessId(fixture.Handle, out _);
        Check(WindowsPlatform.HasOwner(target.Handle, target.Pid, thread)
            && !WindowsPlatform.HasOwner(target.Handle, target.Pid, thread + 1)
            && !WindowsPlatform.HasOwner(target.Handle, target.Pid + 1, thread)
            && !WindowsPlatform.CanPostElevatedClose(target.Handle, target.Pid, thread, target.Started),
            "Native identity did not bind the owner thread and process or protect the helper's own window");
        var posts = 0;
        var refuse = true;
        fixture.AddHook((nint _, int message, nint _, nint _, ref bool handled) =>
        {
            if (message == 0x10)
            {
                posts++;
                handled = refuse;
            }

            return 0;
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        ActionResult Close(WindowRef window)
        {
            var task = platform.CloseConfirmed(window, cancellation.Token, () => throw new Exception("An ordinary native post offered elevation"));
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            return task.GetAwaiter().GetResult();
        }

        Check(Close(target with
        {
            Generation = target.Generation + 1
        }).Kind == ResultKind.Failed && posts == 0,
            "A replaced window generation received WM_CLOSE");
        Check(Close(target).Kind == ResultKind.Unknown && posts == 1, "A live window refusal was reported as closure or retried");
        refuse = false;
        Check(Close(target).Kind == ResultKind.Completed && posts == 2, "Destruction of the verified disposable window was not observed");
    }

    private static ElevatedWindowClose.Grant NewGrant() => new(Target.Handle, Target.Pid, 9, Target.Started,
        Environment.TickCount64 + ElevatedWindowClose.ExchangeMilliseconds);

    private static ActionResult Exchange(Stream pipe, ElevatedWindowClose.Grant grant, Func<bool>? canGrant = null,
        CancellationToken cancellation = default) => ElevatedWindowClose.ExchangeAsync(pipe, grant, canGrant ?? (() => true), cancellation, cancellation)
        .GetAwaiter().GetResult();

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ExchangeStream : MemoryStream
    {
        internal byte? Reply;
        internal Action? OnWrite;
        internal int Writes;
        internal ElevatedWindowClose.Grant Grant;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            Check(ElevatedWindowClose.Grant.TryDecode(buffer.Span, out Grant), "The exchange sent an invalid grant");
            OnWrite?.Invoke();
            return ValueTask.CompletedTask;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Reply is not { } reply)
                return ValueTask.FromResult(0);
            buffer.Span[0] = reply;
            return ValueTask.FromResult(1);
        }
    }

    private sealed class ObservationEffects : IWindowEffects
    {
        internal WindowObservation Observation = WindowObservation.SameIdentity;
        public bool Valid(WindowRef window) => Target.Same(window);
        public WindowObservation ObserveWindow(WindowRef window) => Observation;
        public Task Wait(int milliseconds) => Task.CompletedTask;
        public bool IsForeground(WindowRef window) => throw new NotSupportedException();
        public bool RequestFocus(WindowRef window) => throw new NotSupportedException();
        public uint SendKeys(string keys) => throw new NotSupportedException();
        public bool PostClose(WindowRef window) => throw new NotSupportedException();
    }
}

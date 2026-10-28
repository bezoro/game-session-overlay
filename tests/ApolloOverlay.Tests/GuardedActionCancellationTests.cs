using System.Windows;
using System.Windows.Threading;
using ApolloOverlay;

internal static class GuardedActionCancellationTests
{
    private static readonly WindowRef Target = new(405, 55, 101, "Target", "fixture");
    private static void Require(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    private static void Complete(Task task)
    {
        var until = Environment.TickCount64 + 5000;
        while (!task.IsCompleted)
        {
            if (Environment.TickCount64 >= until)
                throw new TimeoutException("Effect did not settle within five seconds.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
    }

    private static void WaitFor(Func<bool> condition)
    {
        var until = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 >= until)
                throw new TimeoutException("Expected effect phase did not arrive.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    private static void WithDispatcher(Action action)
    {
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class PendingEffects : IWindowEffects
    {
        internal readonly TaskCompletionSource Waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Foreground, ValidTarget = true;
        internal int FocusRequests, Sends;
        internal string? Keys;
        public bool Valid(WindowRef window) => ValidTarget;
        public WindowObservation ObserveWindow(WindowRef window) => WindowObservation.SameIdentity;
        public bool IsForeground(WindowRef window) => Foreground;
        public bool RequestFocus(WindowRef window)
        {
            FocusRequests++;
            return true;
        }

        public uint SendKeys(string keys)
        {
            Sends++;
            Keys = keys;
            return keys == "F1" ? 2u : 4u;
        }

        public bool PostClose(WindowRef window) => throw new Exception("Unexpected close");
        public Task Wait(int milliseconds) => Waiter.Task;
    }

    private sealed class PendingLauncher : IPlatform
    {
        internal readonly WindowRef Steam = new(701, 72, 102, "Steam", "steam");
        internal readonly TaskCompletionSource<ActionResult> Focus = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Switches, Launches;
        internal bool Disposed;
        public bool IsPreview => true;

        public WindowRef? Foreground() => Steam;
        public IReadOnlyList<WindowRef> Windows() => [Steam];
        public bool Valid(WindowRef window) => !Disposed && Steam.Same(window);
        public IReadOnlyList<DisplayRef> Displays() => [new("fixture", 0, 0, 1280, 720, true)];
        public ModuleEvidence SteamEvidence(WindowRef? window) => ModuleEvidence.Unknown;
        public Task<ActionResult> Switch(WindowRef window)
        {
            Switches++;
            return Focus.Task;
        }

        public ActionResult Launch(bool playnite, string? path)
        {
            Launches++;
            return new(ResultKind.Requested, "fixture launch");
        }

        public Task<ActionResult> Close(WindowRef window, CancellationToken cancellation) => throw new Exception("Unexpected close");
        public Task<ActionResult> Shortcut(WindowRef window, string keys) => throw new Exception("Unexpected shortcut");
        public void Dispose() => Disposed = true;
    }

    private sealed class TwoPhaseShortcut : IPlatform
    {
        internal readonly WindowRef Game = new(702, 73, 103, "Game", "game");
        internal readonly TaskCompletionSource First = new(TaskCreationOptions.RunContinuationsAsynchronously), Second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ReachedSecond;
        internal int Sends;
        public bool IsPreview => true;

        public WindowRef? Foreground() => Game;
        public IReadOnlyList<WindowRef> Windows() => [Game];
        public bool Valid(WindowRef window) => Game.Same(window);
        public IReadOnlyList<DisplayRef> Displays() => [new("fixture", 0, 0, 1280, 720, true)];
        public ModuleEvidence SteamEvidence(WindowRef? window) => ModuleEvidence.Unknown;
        public Task<ActionResult> Switch(WindowRef window) => Task.FromResult(new ActionResult(ResultKind.Completed, "fixture"));
        public Task<ActionResult> Close(WindowRef window, CancellationToken token) => throw new Exception("Unexpected close");
        public Task<ActionResult> Shortcut(WindowRef window, string keys) => throw new Exception("Cancelable overload required");
        public async Task<ActionResult> Shortcut(WindowRef window, string keys, CancellationToken token, Func<bool> continuing)
        {
            await First.Task;
            if (token.IsCancellationRequested || !continuing())
                return new(ResultKind.Cancelled, "Cancelled before send");
            ReachedSecond = true;
            await Second.Task;
            if (token.IsCancellationRequested || !continuing())
                return new(ResultKind.Cancelled, "Cancelled before send");
            Sends++;
            return new(ResultKind.Requested, "fixture sent");
        }

        public ActionResult Launch(bool playnite, string? path) => throw new Exception("Unexpected launch");
        public void Dispose()
        {
        }
    }

    internal static void Run(Action<string, Action> test)
    {
        void Case(string name, Action action) => test(name, () => WithDispatcher(action));
        Case("effect test fixture restores previous synchronization context on failure", () =>
        {
            var previous = SynchronizationContext.Current;
            var marker = new SynchronizationContext();
            try
            {
                SynchronizationContext.SetSynchronizationContext(marker);
                var threw = false;
                try
                {
                    WithDispatcher(() => throw new InvalidOperationException("fixture stop"));
                }
                catch (InvalidOperationException error) when (error.Message == "fixture stop")
                {
                    threw = true;
                }

                Require(threw && ReferenceEquals(SynchronizationContext.Current, marker), "Fixture did not restore the previous context");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
        foreach (var menu in new[]
        {
            false,
            true
        }

        )
            foreach (var cancel in new[]
            {
                false,
                true
            }

            )
            {
                var isMenu = menu;
                var cancelRequest = cancel;
                Case((isMenu ? "F1" : "Win+G") + (cancelRequest ? " cancellation" : " continuing-context loss") + " after focus sends no keys", () =>
                {
                    var effects = new PendingEffects();
                    using var cts = new CancellationTokenSource();
                    var allowed = true;
                    var actions = new GuardedActions(effects);
                    var request = isMenu ? actions.SteamMenu(Target, cts.Token, () => allowed, () => effects.ValidTarget) : actions.Shortcut(Target, "Win+G", cts.Token, () => allowed);
                    Require(!request.IsCompleted && effects.FocusRequests == 1, "Focus fixture did not suspend");
                    if (cancelRequest)
                        cts.Cancel();
                    else
                        allowed = false;
                    effects.Foreground = true;
                    effects.Waiter.SetResult();
                    Complete(request);
                    Require(effects.Sends == 0 && request.Result.Kind == ResultKind.Cancelled, "Cancelled focus emitted keys or reported a successful send");
                });
            }

        foreach (var menu in new[]
        {
            false,
            true
        }

        )
            Case((menu ? "F1" : "Win+G") + " sends once when focus and request remain valid", () =>
            {
                var effects = new PendingEffects
                {
                    Foreground = true
                };
                var actions = new GuardedActions(effects);
                var request = menu ? actions.SteamMenu(Target, CancellationToken.None, () => true, () => true) : actions.Shortcut(Target, "Win+G", CancellationToken.None, () => true);
                Complete(request);
                Require(effects.FocusRequests == 1 && effects.Sends == 1 && effects.Keys == (menu ? "F1" : "Win+G") && request.Result.Kind == ResultKind.Requested, "Valid shortcut did not send exactly once");
            });
        foreach (var cancel in new[]
        {
            false,
            true
        }

        )
            Case("Steam launch aborts after focus when " + (cancel ? "cancelled" : "context lost"), () =>
            {
                using var platform = new PendingLauncher();
                using var controller = new OverlayController(platform);
                using var cts = new CancellationTokenSource();
                var allowed = true;
                Complete(controller.Refresh());
                var request = controller.Launch(false, cts.Token, () => allowed);
                Require(platform.Switches == 1 && !request.IsCompleted, "Focus fixture did not suspend");
                if (cancel)
                    cts.Cancel();
                else
                    allowed = false;
                platform.Focus.SetResult(new(ResultKind.Completed, "fixture focus"));
                Complete(request);
                Require(platform.Launches == 0 && request.Result.Kind == ResultKind.Cancelled, "Steam URI was dispatched after cancellation or context loss");
            });
        foreach (var scenario in new[]
        {
            "valid target",
            "focus returns to overlay",
            "new controller input"
        }

        )
            Case("UI shortcut guard after target focus: " + scenario, () =>
            {
                using var platform = new TwoPhaseShortcut();
                var window = new MainWindow(renderOnly: true, platform: platform);
                var own = true;
                var frames = new PadFrame[4];
                window.InputContextReader = () => new(true, true, own, own);
                window.PhysicalKeyReader = _ => 0;
                window.FocusForeground = () => own ? 0 : platform.Game.Handle;
                window.EffectFramesFixture = () => frames;
                try
                {
                    window.Controller.CaptureReturn();
                    Complete(window.Controller.Refresh());
                    window.RefreshRows();
                    ReviewFixTests.Button(window, "gamebar").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    window.AdvanceHandoff(new(new PadFrame[4], 0), 0);
                    window.AdvanceHandoff(new(new PadFrame[4], 100), 100);
                    Require(!window.OperationTask.IsCompleted, "Fixture did not wait before the shortcut");
                    own = false;
                    platform.First.SetResult();
                    WaitFor(() => platform.ReachedSecond || window.OperationTask.IsCompleted);
                    Require(platform.ReachedSecond, "Valid foreground was rejected before the WPF Deactivated event");
                    if (scenario == "focus returns to overlay")
                        own = true;
                    if (scenario == "new controller input")
                        frames[0] = new(true, PadButtons.B);
                    platform.Second.SetResult();
                    Complete(window.OperationTask);
                    Require(platform.Sends == (scenario == "valid target" ? 1 : 0), "Shortcut was sent after loss of target focus or new input");
                }
                finally
                {
                    platform.First.TrySetResult();
                    platform.Second.TrySetResult();
                    window.Close();
                }
            });
        Case("Back cancels a pending operation without dismissing the visible panel after a late result", () =>
        {
            var completion = new TaskCompletionSource<ActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var platform = new PremiumInterfaceTests.FixturePlatform
            {
                Next = completion.Task
            };
            var window = PremiumInterfaceTests.FixtureWindow(platform);
            try
            {
                window.Left = -12000;
                window.Top = -12000;
                window.ShowDiagnostic();
                ReviewFixTests.Button(window, "taskview").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.AdvanceHandoff(new(new PadFrame[4], 0), 0);
                window.AdvanceHandoff(new(new PadFrame[4], 100), 100);
                Require(!window.OperationTask.IsCompleted && platform.Calls == 1, "Fixture did not dispatch a pending request");
                window.Back();
                Require(window.Controller.Operation?.State == "Não confirmado", "Back failed to mark the pending request uncertain");
                completion.SetResult(new(ResultKind.Requested, "Late external request already sent"));
                Complete(window.OperationTask);
                Require(window.IsVisible && platform.Calls == 1, "A late result dismissed the panel or retried the effect");
            }
            finally
            {
                window.Close();
            }
        });
    }
}

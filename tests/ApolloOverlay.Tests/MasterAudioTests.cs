using ApolloOverlay;

internal static class MasterAudioTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("master audio keeps a timed-out native call on its sole owner", () =>
        {
            var backend = new Backend { BlockReads = true };
            using var audio = new WindowsAudio(_ => backend, TimeSpan.FromMilliseconds(200));
            try
            {
                audio.SetVisible(true);
                Require(backend.Entered.Wait(1000), "The master-audio owner did not start");
                var observation = audio.RefreshAsync();
                Require(observation.Wait(1500) && !observation.Result.Available, "A stalled read did not return a bounded unavailable snapshot");
                var change = audio.SetAsync(.3f, expectedEndpoint: "fixture");
                Require(change.Wait(1500) && change.Result.Kind == ResultKind.Cancelled, "A queued change did not expire before dispatch");
                Require(backend.Calls == 1 && backend.Writes == 0, "Timeout released ownership or dispatched a queued write");
                audio.SetVisible(false);
                Require(!audio.Current.Available, "Hiding retained an available master snapshot");
            }
            finally
            {
                backend.Release.Set();
                audio.Dispose();
                Require(backend.Disposed.Wait(1500), "The native owner did not release after its call drained");
            }
            Require(backend.Threads.Distinct().Count() == 1 && backend.Threads.All(id => id != Environment.CurrentManagedThreadId), "Native work or release escaped its one background owner");
        });
        test("master audio cancellation after dispatch stays unknown and cannot overlap", () =>
        {
            var backend = new Backend();
            using var audio = new WindowsAudio(_ => backend, TimeSpan.FromSeconds(2));
            try
            {
                audio.SetVisible(true);
                Require(audio.RefreshAsync().GetAwaiter().GetResult().Available, "Fixture endpoint did not bind");
                backend.BlockWrites = true;
                using var cancellation = new CancellationTokenSource();
                var change = audio.SetAsync(.5f, expectedEndpoint: "fixture", cancellation: cancellation.Token);
                Require(backend.Entered.Wait(1000), "The write did not enter its native owner");
                cancellation.Cancel();
                Require(change.Wait(1000) && change.Result.Kind == ResultKind.Unknown, "Cancellation claimed an already dispatched write was undone");
                audio.SetVisible(false);
                Require(!backend.Disposed.IsSet && backend.Writes == 1, "A still-running native write was disposed concurrently");
                backend.Release.Set();
                Require(backend.Disposed.Wait(1000), "The cancelled write did not drain and release");
                Require(!audio.Current.Available, "A late write republished the hidden endpoint");
            }
            finally { backend.Release.Set(); }
        });
        test("master audio bounds pending requests while its owner is blocked", () =>
        {
            var backend = new Backend { BlockReads = true };
            using var audio = new WindowsAudio(_ => backend, TimeSpan.FromSeconds(2));
            try
            {
                audio.SetVisible(true);
                Require(backend.Entered.Wait(1000), "Fixture did not block");
                for (var index = 0; index < 16; index++)
                    _ = audio.RefreshAsync();
                var refused = audio.SetAsync(.2f, expectedEndpoint: "fixture");
                Require(refused.IsCompletedSuccessfully && refused.Result.Kind == ResultKind.Unavailable && backend.Writes == 0, "The master-audio pending queue exceeded its limit");
            }
            finally
            {
                audio.Dispose();
                backend.Release.Set();
                Require(backend.Disposed.Wait(1000), "Bounded queue did not drain during disposal");
            }
        });
        test("master audio confirms requested fields rather than successful HRESULT alone", () =>
        {
            var actual = new AudioSnapshot("fixture", .4f, false, "fixture");
            Require(CoreMasterAudioBackend.Verify(.4f, false, "fixture", actual, true).Kind == ResultKind.Completed, "Matching readback was not confirmed");
            foreach (var (volume, muted, endpoint, snapshot, continuing) in new[]
            {
                ((float?).6f, (bool?)false, "fixture", actual, true),
                ((float?).4f, (bool?)true, "fixture", actual, true),
                ((float?).4f, (bool?)false, "different", actual, true),
                ((float?).4f, (bool?)false, "fixture", actual with { EndpointId = null }, true),
                ((float?).4f, (bool?)false, "fixture", actual, false),
                ((float?).4f, (bool?)false, "fixture", actual with { Volume = float.NaN }, true)
            })
                Require(CoreMasterAudioBackend.Verify(volume, muted, endpoint, snapshot, continuing).Kind == ResultKind.Unknown, "Unconfirmed state was reported as completed");
        });
        test("master audio rejects nonfinite input without calling its backend", () =>
        {
            var backend = new Backend();
            using var audio = new WindowsAudio(_ => backend, TimeSpan.FromSeconds(2));
            foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                Require(audio.SetAsync(value).GetAwaiter().GetResult().Kind == ResultKind.Unavailable, "Nonfinite master volume was accepted");
            Require(backend.Calls == 0 && backend.Writes == 0, "Invalid input touched native audio");
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class Backend : IMasterAudioBackend
    {
        internal readonly ManualResetEventSlim Entered = new(false), Release = new(false), Disposed = new(false);
        internal readonly System.Collections.Concurrent.ConcurrentBag<int> Threads = [];
        internal bool BlockReads, BlockWrites;
        internal int Calls, Writes;
        private AudioSnapshot state = new("fixture", .7f, false, "fixture");
        private void Record() => Threads.Add(Environment.CurrentManagedThreadId);
        public AudioSnapshot Bind() => Read();
        public AudioSnapshot Read()
        {
            Record();
            Interlocked.Increment(ref Calls);
            if (BlockReads)
            {
                Entered.Set();
                if (!Release.Wait(5000))
                    throw new InvalidOperationException("Fixture release timed out");
            }
            return state;
        }
        public MasterAudioChange Set(float? volume, bool? muted, string? expectedEndpoint, Func<bool> dispatch, Func<bool> continuing)
        {
            Record();
            if (!dispatch())
                return new(new(ResultKind.Cancelled, "fixture cancelled"), state);
            Interlocked.Increment(ref Writes);
            if (BlockWrites)
            {
                Entered.Set();
                if (!Release.Wait(5000))
                    throw new InvalidOperationException("Fixture write release timed out");
            }
            state = state with
            {
                Volume = volume ?? state.Volume,
                Muted = muted ?? state.Muted
            };
            return new(CoreMasterAudioBackend.Verify(volume, muted, expectedEndpoint!, state, continuing()), state);
        }
        public void Dispose()
        {
            Record();
            Disposed.Set();
        }
    }
}

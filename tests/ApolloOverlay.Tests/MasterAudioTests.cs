using ApolloOverlay;

internal static class MasterAudioTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("master audio retains its real device observer across a missing output and recovers while visible", () =>
        {
            var native = new NativeFixture { OutputAvailable = false };
            using var audio = NativeOwner(native);
            audio.SetVisible(true);
            Require(!audio.RefreshAsync().GetAwaiter().GetResult().Available && native.ObserverRegistered,
                "Missing output dropped the device observer");
            Require(native.EnumeratorCreations == 1 && native.ObserverRegistrations == 1 && native.EnumeratorReleases == 0,
                "Endpoint failure replaced or disposed its observer");
            native.OutputAvailable = true;
            native.NotifyDefaultChanged();
            Require(SpinWait.SpinUntil(() => audio.Current.Available, 1500), "Device notification did not recover master volume without reopening");
            Require(native.EnumeratorCreations == 1 && native.ObserverRegistrations == 1,
                "Recovery abandoned the original observer or created another owner");
            Require(audio.SetAsync(.35f, expectedEndpoint: "fixture").GetAwaiter().GetResult().Kind == ResultKind.Completed
                && Math.Abs(audio.Current.Volume - .35f) < .005f,
                "The recovered real endpoint did not admit and verify a volume change");
            native.OutputAvailable = false;
            native.NotifyDefaultChanged();
            Require(SpinWait.SpinUntil(() => !audio.Current.Available, 1500) && native.ObserverRegistered,
                "Unplugging the active output lost device observation");
            native.OutputAvailable = true;
            native.NotifyDefaultChanged();
            Require(SpinWait.SpinUntil(() => audio.Current.Available, 1500), "Replugging did not recover the retained owner");
            audio.SetVisible(false);
            Require(native.ObserverReleased.Wait(1500) && !native.ObserverRegistered, "Hiding retained its device registration");
            audio.Dispose();
            Require(native.OwnerThreads.Distinct().Count() == 1 && native.OwnerThreads.All(id => id != Environment.CurrentManagedThreadId),
                "Recovery or release escaped the native owner");
        });
        test("master audio retries failed observer registration through explicit refresh", () =>
        {
            var native = new NativeFixture { FailObserverRegistration = true };
            using var audio = NativeOwner(native);
            audio.SetVisible(true);
            Require(!audio.RefreshAsync().GetAwaiter().GetResult().Available && !native.ObserverRegistered,
                "Rejected observer registration produced available audio");
            native.FailObserverRegistration = false;
            Require(audio.RefreshAsync(rebind: true).GetAwaiter().GetResult().Available && native.ObserverRegistered,
                "Explicit refresh did not retry observer registration");
            Require(native.EnumeratorCreations == 1 && native.ObserverRegistrations >= 2, "Registration recovery leaked or replaced its enumerator");
            audio.Dispose();
            Require(native.ObserverReleased.Wait(1500) && !native.ObserverRegistered, "Disposal failed to unregister a recovered observer");
        });
        test("master audio retains device observation when volume callback registration fails", () =>
        {
            var native = new NativeFixture { FailVolumeRegistration = true };
            using var audio = NativeOwner(native);
            audio.SetVisible(true);
            Require(!audio.RefreshAsync().GetAwaiter().GetResult().Available && native.ObserverRegistered
                && native.VolumeReleases > 0 && native.DeviceReleases > 0,
                "Failed endpoint callback retained partial endpoint resources or dropped its observer");
            native.FailVolumeRegistration = false;
            native.NotifyDefaultChanged();
            Require(SpinWait.SpinUntil(() => audio.Current.Available, 1500) && native.EnumeratorCreations == 1,
                "Endpoint callback failure could not recover through the retained device notification");
            audio.Dispose();
            Require(native.ObserverReleased.Wait(1500), "Recovered endpoint did not release its observer");
        });
        test("master audio reconnects a disconnected enumerator on explicit refresh", () =>
        {
            var native = new NativeFixture { EnumeratorDisconnected = true };
            using var audio = NativeOwner(native);
            audio.SetVisible(true);
            Require(!audio.RefreshAsync().GetAwaiter().GetResult().Available && !native.ObserverRegistered,
                "A disconnected enumerator remained registered");
            native.EnumeratorDisconnected = false;
            Require(audio.RefreshAsync(rebind: true).GetAwaiter().GetResult().Available && native.EnumeratorCreations >= 2,
                "Explicit refresh reused the disconnected enumerator");
            audio.Dispose();
            Require(SpinWait.SpinUntil(() => !native.ObserverRegistered, 1500), "Reconnected observer survived disposal");
        });
        test("master audio discards a late real-source bind after hiding and ignores late notifications", () =>
        {
            var native = new NativeFixture { BlockActivation = true };
            using var audio = NativeOwner(native);
            try
            {
                audio.SetVisible(true);
                Require(native.ActivationEntered.Wait(1000), "The real backend did not begin endpoint activation");
                var callback = native.Observer ?? throw new Exception("Binding did not register device observation first");
                audio.SetVisible(false);
                native.ActivationRelease.Set();
                Require(native.ObserverReleased.Wait(1500) && !audio.Current.Available, "A hidden generation published a late endpoint");
                audio.Dispose();
                callback.OnDefaultDeviceChanged(0, 0, "fixture");
                Require(!audio.RefreshAsync().GetAwaiter().GetResult().Available && native.EnumeratorCreations == 1,
                    "A late callback restarted hidden or disposed native observation");
            }
            finally { native.ActivationRelease.Set(); }
        });
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

    private static WindowsAudio NativeOwner(NativeFixture native) => new(
        changed => new CoreMasterAudioBackend(changed, native.CreateEnumerator, native.Release), TimeSpan.FromSeconds(2));

    // Implement the native ABI boundary so tests exercise CoreMasterAudioBackend's
    // registration, endpoint cleanup and recovery decisions, rather than replacing it.
    private sealed class NativeFixture
    {
        internal volatile bool OutputAvailable = true, FailObserverRegistration, FailVolumeRegistration, EnumeratorDisconnected, BlockActivation;
        internal int EnumeratorCreations, ObserverRegistrations, EnumeratorReleases, VolumeReleases, DeviceReleases;
        internal readonly System.Collections.Concurrent.ConcurrentBag<int> OwnerThreads = [];
        internal readonly ManualResetEventSlim ObserverReleased = new(false), ActivationEntered = new(false), ActivationRelease = new(false);
        private IAudioDeviceCallback? observer;
        private readonly Device device;
        private readonly Volume volume;
        internal IAudioDeviceCallback? Observer => Volatile.Read(ref observer);
        internal bool ObserverRegistered => Observer is not null;

        internal NativeFixture()
        {
            device = new(this);
            volume = new(this);
        }

        private void Record() => OwnerThreads.Add(Environment.CurrentManagedThreadId);
        internal IMMDeviceEnumerator CreateEnumerator()
        {
            Record();
            Interlocked.Increment(ref EnumeratorCreations);
            return new Enumerator(this);
        }
        internal void Release(object value)
        {
            Record();
            if (value is Enumerator)
            {
                Interlocked.Increment(ref EnumeratorReleases);
                ObserverReleased.Set();
            }
            else if (value is Device)
                Interlocked.Increment(ref DeviceReleases);
            else if (value is Volume)
                Interlocked.Increment(ref VolumeReleases);
        }
        internal void NotifyDefaultChanged() => Observer?.OnDefaultDeviceChanged(0, 0, "fixture");

        private sealed class Enumerator(NativeFixture owner) : IMMDeviceEnumerator
        {
            public int RegisterEndpointNotificationCallback(IAudioDeviceCallback callback)
            {
                owner.Record();
                Interlocked.Increment(ref owner.ObserverRegistrations);
                if (owner.FailObserverRegistration)
                    return unchecked((int)0x80070005);
                Volatile.Write(ref owner.observer, callback);
                return 0;
            }
            public int UnregisterEndpointNotificationCallback(IAudioDeviceCallback callback)
            {
                owner.Record();
                Interlocked.CompareExchange(ref owner.observer, null, callback);
                return 0;
            }
            public int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice found)
            {
                owner.Record();
                found = null!;
                if (owner.EnumeratorDisconnected)
                    return unchecked((int)0x80010108);
                if (!owner.OutputAvailable)
                    return unchecked((int)0x80070490);
                found = owner.device;
                return 0;
            }
            public int GetDevice(string id, out IMMDevice found) => GetDefaultAudioEndpoint(0, 0, out found);
            public int EnumAudioEndpoints(int flow, uint mask, out nint collection)
            {
                collection = 0;
                return unchecked((int)0x80004001);
            }
        }

        private sealed class Device(NativeFixture owner) : IMMDevice
        {
            public int Activate(ref Guid iid, uint context, nint parameters, out object instance)
            {
                owner.Record();
                if (owner.BlockActivation)
                {
                    owner.ActivationEntered.Set();
                    if (!owner.ActivationRelease.Wait(5000))
                        throw new InvalidOperationException("Fixture activation release timed out");
                }
                instance = owner.volume;
                return 0;
            }
            public int GetId(out string id)
            {
                owner.Record();
                id = "fixture";
                return 0;
            }
            public int GetState(out uint state)
            {
                state = 1;
                return 0;
            }
            public int OpenPropertyStore(uint access, out nint store)
            {
                store = 0;
                return unchecked((int)0x80004001);
            }
        }

        private sealed class Volume(NativeFixture owner) : IAudioEndpointVolume
        {
            private float level = .6f;
            private bool muted;
            public int RegisterControlChangeNotify(IAudioVolumeCallback callback)
            {
                owner.Record();
                return owner.FailVolumeRegistration ? unchecked((int)0x80004005) : 0;
            }
            public int UnregisterControlChangeNotify(IAudioVolumeCallback callback)
            {
                owner.Record();
                return 0;
            }
            public int GetMasterVolumeLevelScalar(out float value)
            {
                owner.Record();
                value = level;
                return 0;
            }
            public int GetMute(out bool value)
            {
                owner.Record();
                value = muted;
                return 0;
            }
            public int SetMasterVolumeLevelScalar(float value, ref Guid context)
            {
                owner.Record();
                level = value;
                return 0;
            }
            public int SetMute(bool value, ref Guid context)
            {
                owner.Record();
                muted = value;
                return 0;
            }
            public int GetChannelCount(out uint count)
            {
                count = 1;
                return 0;
            }
            public int SetMasterVolumeLevel(float db, ref Guid context) => unchecked((int)0x80004001);
            public int GetMasterVolumeLevel(out float db)
            {
                db = 0;
                return unchecked((int)0x80004001);
            }
            public int SetChannelVolumeLevel(uint channel, float db, ref Guid context) => unchecked((int)0x80004001);
            public int SetChannelVolumeLevelScalar(uint channel, float value, ref Guid context) => unchecked((int)0x80004001);
            public int GetChannelVolumeLevel(uint channel, out float db)
            {
                db = 0;
                return unchecked((int)0x80004001);
            }
            public int GetChannelVolumeLevelScalar(uint channel, out float value)
            {
                value = 0;
                return unchecked((int)0x80004001);
            }
        }
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

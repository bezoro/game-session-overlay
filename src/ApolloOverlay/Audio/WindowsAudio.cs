using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ApolloOverlay;

/// <summary>One MTA owns all master-endpoint work. A timeout revokes permission, never native ownership.</summary>
public sealed class WindowsAudio : IDisposable
{
    private sealed class Work
    {
        internal required long Epoch;
        internal required bool Mutation, Rebind;
        internal float? Volume;
        internal bool? Muted;
        internal string? Endpoint;
        internal readonly object Gate = new();
        internal readonly TaskCompletionSource<MasterAudioChange> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Invalid, Dispatched;
        internal Timer? Timer;
        internal CancellationTokenRegistration Cancellation;
    }

    private readonly object gate = new();
    private readonly ConcurrentQueue<Work> queue = new();
    private readonly HashSet<Work> pending = [];
    private readonly AutoResetEvent wake = new(false);
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Func<Action<bool>, IMasterAudioBackend> factory;
    private readonly TimeSpan timeout;
    private IMasterAudioBackend? backend;
    private AudioSnapshot current = Empty("Audio has not been queried.");
    private bool visible, disposed;
    private long epoch;
    private int queued, releaseRequested, refreshRequested, rebindRequested, notificationQueued;
    public AudioSnapshot Current
    {
        get => Volatile.Read(ref current);
        private set => Volatile.Write(ref current, value);
    }

    public event Action? Changed;
    public WindowsAudio() : this(changed => new CoreMasterAudioBackend(changed), TimeSpan.FromSeconds(3)) { }
    internal WindowsAudio(Func<Action<bool>, IMasterAudioBackend> factory, TimeSpan timeout)
    {
        this.factory = factory;
        this.timeout = timeout;
        var worker = new Thread(Worker) { IsBackground = true, Name = "Game Session master audio" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    private static AudioSnapshot Empty(string reason) => new(null, 0, false, reason);
    private void Signal()
    {
        try
        {
            wake.Set();
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Returns immediately; binding and release belong to the native owner.</summary>
    public void SetVisible(bool value)
    {
        Work[] cancelled;
        lock (gate)
        {
            if (disposed || visible == value)
                return;
            visible = value;
            epoch++;
            cancelled = pending.ToArray();
            Current = Empty(value ? "Refreshing Windows audio…" : "Audio observation suspended.");
        }
        foreach (var work in cancelled)
            Invalidate(work, "The audio context changed.");
        Interlocked.Exchange(ref releaseRequested, 1);
        Signal();
        if (value)
            _ = RefreshAsync();
    }

    public async Task<AudioSnapshot> RefreshAsync(bool rebind = false, CancellationToken cancellation = default) =>
        (await Submit(false, rebind, null, null, null, cancellation).ConfigureAwait(false)).Snapshot;

    public async Task<ActionResult> SetAsync(float? volume = null, bool? muted = null, string? expectedEndpoint = null, CancellationToken cancellation = default)
    {
        if (volume is { } scalar && !float.IsFinite(scalar) || volume is null && muted is null)
            return new(ResultKind.Unavailable, "Choose a valid volume or mute state.");
        return (await Submit(true, false, volume is { } level ? Math.Clamp(level, 0, 1) : null, muted,
            expectedEndpoint ?? Current.EndpointId, cancellation).ConfigureAwait(false)).Result;
    }

    private Task<MasterAudioChange> Submit(bool mutation, bool rebind, float? volume, bool? muted, string? endpoint, CancellationToken cancellation)
    {
        Work work;
        lock (gate)
        {
            if (disposed || !visible || queued >= 16)
            {
                var reason = disposed || !visible ? "Audio observation is stopped." : "The audio owner is busy. Try again manually.";
                return Task.FromResult(new MasterAudioChange(new(ResultKind.Unavailable, reason), Current));
            }
            work = new()
            {
                Epoch = epoch,
                Mutation = mutation,
                Rebind = rebind,
                Volume = volume,
                Muted = muted,
                Endpoint = endpoint
            };
            pending.Add(work);
            queued++;
        }
        work.Timer = new Timer(_ => Invalidate(work, "Windows audio did not respond in time."), null, timeout, Timeout.InfiniteTimeSpan);
        work.Cancellation = cancellation.Register(() => Invalidate(work, "Windows audio was cancelled."));
        if (work.Completion.Task.IsCompleted)
        {
            work.Timer.Dispose();
            work.Cancellation.Unregister();
        }
        queue.Enqueue(work);
        Signal();
        return work.Completion.Task;
    }

    private bool Continuing(Work work)
    {
        lock (work.Gate)
            lock (gate)
                return !work.Invalid && !disposed && visible && work.Epoch == epoch;
    }

    private bool Dispatch(Work work)
    {
        lock (work.Gate)
        {
            if (!Continuing(work))
                return false;
            work.Dispatched = true;
            return true;
        }
    }

    private void Invalidate(Work work, string reason)
    {
        MasterAudioChange change;
        lock (work.Gate)
        {
            work.Invalid = true;
            change = new(new(work.Dispatched ? ResultKind.Unknown : ResultKind.Cancelled,
                reason + (work.Dispatched ? " A change may have been sent; check the output before trying again." : " No change was sent.")), Empty(reason));
        }
        Finish(work, change, false);
    }

    private void Finish(Work work, MasterAudioChange change, bool publish)
    {
        lock (work.Gate)
        {
            if (work.Completion.Task.IsCompleted)
                return;
            if (publish && !Continuing(work))
            {
                Invalidate(work, "The audio context changed before confirmation.");
                return;
            }
            lock (gate)
            {
                pending.Remove(work);
                if (visible && !disposed && work.Epoch == epoch)
                    Current = change.Snapshot;
            }
            work.Completion.TrySetResult(change);
        }
        work.Timer?.Dispose();
        work.Cancellation.Unregister();
        QueueNotification();
    }

    private void QueueNotification()
    {
        if (dispatcher.HasShutdownStarted || Interlocked.Exchange(ref notificationQueued, 1) != 0)
            return;
        try
        {
            dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref notificationQueued, 0);
                lock (gate)
                    if (disposed || !visible)
                        return;
                Changed?.Invoke();
            });
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref notificationQueued, 0); }
    }

    private void NativeChanged(bool rebind)
    {
        if (rebind)
            Interlocked.Exchange(ref rebindRequested, 1);
        Interlocked.Exchange(ref refreshRequested, 1);
        Signal();
    }

    private void ReleaseBackend()
    {
        var previous = backend;
        backend = null;
        previous?.Dispose();
    }

    private void Worker()
    {
        try
        {
            while (true)
            {
                if (Interlocked.Exchange(ref releaseRequested, 0) != 0)
                    ReleaseBackend();
                if (queue.TryDequeue(out var work))
                {
                    lock (gate)
                        queued--;
                    if (!Continuing(work))
                    {
                        Invalidate(work, "Windows audio was superseded or cancelled.");
                        continue;
                    }
                    try
                    {
                        backend ??= factory(NativeChanged);
                        MasterAudioChange result;
                        if (work.Mutation)
                            result = backend.Set(work.Volume, work.Muted, work.Endpoint, () => Dispatch(work), () => Continuing(work));
                        else
                        {
                            var snapshot = work.Rebind ? backend.Bind() : backend.Read();
                            result = new(new(snapshot.Available ? ResultKind.Completed : ResultKind.Unavailable, snapshot.Status), snapshot);
                        }
                        Finish(work, result, true);
                    }
                    catch (Exception error) when (error is COMException or InvalidCastException or InvalidOperationException or UnauthorizedAccessException)
                    {
                        var snapshot = Empty("Windows audio is unavailable. Refresh before trying again.");
                        Finish(work, new(new(work.Dispatched ? ResultKind.Unknown : ResultKind.Unavailable, snapshot.Status), snapshot), true);
                        // Endpoint failures must not drop device-change observation:
                        // the retained owner can recover on notification or explicit refresh.
                    }
                    continue;
                }
                if (Interlocked.Exchange(ref refreshRequested, 0) != 0)
                {
                    _ = RefreshAsync(Interlocked.Exchange(ref rebindRequested, 0) != 0);
                    continue;
                }
                lock (gate)
                    if (disposed)
                        break;
                wake.WaitOne();
            }
        }
        finally
        {
            ReleaseBackend();
            wake.Dispose();
        }
    }

    public void Dispose()
    {
        Work[] cancelled;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            visible = false;
            epoch++;
            cancelled = pending.ToArray();
            Current = Empty("Audio observation stopped.");
            Changed = null;
        }
        foreach (var work in cancelled)
            Invalidate(work, "The audio service has stopped.");
        Interlocked.Exchange(ref releaseRequested, 1);
        Signal();
    }
}

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ApolloOverlay;

internal interface IApplicationAudioSession
{
    string Key
    {
        get;
    }

    int ProcessId(out uint pid);
    int State(out int state);
    int ReadMute(out bool muted);
    int WriteMute(bool muted);
    int ReadVolume(out float volume)
    {
        volume = 0;
        return unchecked((int)0x80004001);
    }

    int WriteVolume(float volume) => unchecked((int)0x80004001);
}

internal sealed record ApplicationAudioCatalogSnapshot(IReadOnlyList<IApplicationAudioSession> Sessions, int Gaps);
internal interface IApplicationAudioCatalog : IDisposable
{
    ApplicationAudioCatalogSnapshot Discover(Func<bool> continuing);
}

/// <summary>Only the MTA worker touches the catalog or its sessions. Cancellation invalidates permission for subsequent writes.</summary>
public sealed class ApplicationAudio : IApplicationAudio
{
    private sealed class Work
    {
        public required WindowRef Target;
        public required long Generation, Epoch;
        public required bool Mutation, DesiredMute;
        public float? DesiredVolume;
        public required long Deadline;
        public readonly object Gate = new();
        public readonly TaskCompletionSource<ApplicationAudioChange> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public required ApplicationAudioSnapshot Snapshot;
        public int Invalid, Attempted, Succeeded, Verified;
        public bool Dispatched;
        public Timer? Timer;
        public CancellationTokenRegistration Cancellation;
    }

    private readonly object gate = new();
    private readonly ConcurrentQueue<Work> queue = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly HashSet<Work> pending = [];
    private readonly Func<WindowRef, bool> valid;
    private readonly Func<Action, Func<bool>, IApplicationAudioCatalog> factory;
    private readonly Func<long> clock;
    private readonly Thread worker;
    private IApplicationAudioCatalog? catalog;
    private WindowRef? target;
    private long generation, epoch, revision;
    private bool visible, disposed;
    private int queued, releaseRequested, observationQueued;
    private long observationRevision, observationOwner;
    public event Action<ApplicationAudioSnapshot>? Changed;
    public ApplicationAudio(Func<WindowRef, bool> valid) : this(valid, (changed, continuing) => new CoreAudioSessionCatalog(changed, continuing), () => Environment.TickCount64)
    {
    }

    internal ApplicationAudio(Func<WindowRef, bool> valid, Func<Action, Func<bool>, IApplicationAudioCatalog> factory, Func<long> clock)
    {
        this.valid = valid;
        this.factory = factory;
        this.clock = clock;
        worker = new Thread(Worker)
        {
            IsBackground = true,
            Name = "Apollo application audio"
        };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    private ApplicationAudioSnapshot Empty(WindowRef? window, long context, string reason, ApplicationAudioAvailability availability = ApplicationAudioAvailability.Unknown) => new(window, context, Interlocked.Increment(ref revision), availability, ApplicationMuteState.Unknown, 0, 0, reason);
    private void Signal()
    {
        try
        {
            wake.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Track(WindowRef? window, bool isVisible, long context)
    {
        Work[] cancel;
        lock (gate)
        {
            if (disposed)
                return;
            if (visible == isVisible && generation == context && (target is null ? window is null : target.Same(window)))
                return;
            target = window;
            visible = isVisible;
            generation = context;
            epoch++;
            cancel = pending.ToArray();
        }

        foreach (var work in cancel)
            Invalidate(work, "The audio context changed; refresh it again.");
        Interlocked.Exchange(ref releaseRequested, 1);
        Signal();
        if (isVisible && window is not null)
            _ = RefreshAsync(window, context);
    }

    private bool Current(Work work)
    {
        lock (gate)
            return !disposed && work.Epoch == epoch && (target is null || target.Same(work.Target) && generation == work.Generation);
    }

    private bool Continuing(Work work) => Volatile.Read(ref work.Invalid) == 0 && clock() < work.Deadline && Current(work);
    private bool Finish(Work work, ApplicationAudioChange change)
    {
        if (!work.Completion.TrySetResult(change))
            return false;
        work.Timer?.Dispose();
        work.Cancellation.Unregister();
        lock (gate)
            pending.Remove(work);
        return true;
    }

    private void Invalidate(Work work, string reason)
    {
        ApplicationAudioChange change;
        lock (work.Gate)
        {
            Volatile.Write(ref work.Invalid, 1);
            var snapshot = work.Snapshot with
            {
                Revision = Interlocked.Increment(ref revision),
                Availability = ApplicationAudioAvailability.Unknown,
                MuteState = ApplicationMuteState.Unknown,
                Reason = reason
            };
            change = new(new(work.Dispatched ? ResultKind.Unknown : ResultKind.Cancelled, work.Dispatched ? reason + " A command was sent; it cannot be undone or its effect confirmed." : reason + " No change was sent."), snapshot, work.Attempted, work.Succeeded, work.Verified, work.Dispatched);
        }

        Finish(work, change);
    }

    private Task<ApplicationAudioChange> Submit(WindowRef window, long context, bool mutation, bool desired, CancellationToken cancellation, float? volume = null)
    {
        var work = new Work
        {
            Target = window,
            Generation = context,
            Epoch = 0,
            Mutation = mutation,
            DesiredMute = desired,
            DesiredVolume = volume,
            Deadline = clock() + (mutation ? 2000 : 1500),
            Snapshot = Empty(window, context, "Query not yet confirmed.")
        };
        lock (gate)
        {
            work.Epoch = epoch;
            if (disposed || queued >= 32)
            {
                var reason = disposed ? "The audio service has stopped." : "The audio queue is full; try again manually.";
                work.Completion.SetResult(new(new(ResultKind.Unavailable, reason), work.Snapshot with
                {
                    Reason = reason
                }, 0, 0, 0, false));
                return work.Completion.Task;
            }

            pending.Add(work);
            queued++;
        }

        work.Timer = new Timer(_ => Invalidate(work, "The audio query timed out; a native call may still be running in its owner."), null, mutation ? 2000 : 1500, Timeout.Infinite);
        work.Cancellation = cancellation.Register(() => Invalidate(work, "Audio observation cancelled."));
        if (cancellation.IsCancellationRequested)
            Invalidate(work, "Audio observation cancelled.");
        if (work.Completion.Task.IsCompleted)
        {
            work.Timer?.Dispose();
            work.Cancellation.Unregister();
        }

        queue.Enqueue(work);
        Signal();
        return work.Completion.Task;
    }

    public async Task<ApplicationAudioSnapshot> RefreshAsync(WindowRef window, long context, CancellationToken cancellation = default) => (await Submit(window, context, false, false, cancellation).ConfigureAwait(false)).Snapshot;
    public Task<ApplicationAudioChange> SetMutedAsync(WindowRef window, bool desiredMute, long context, CancellationToken cancellation = default) => Submit(window, context, true, desiredMute, cancellation);
    public Task<ApplicationAudioChange> SetVolumeAsync(WindowRef window, float volume, long context, CancellationToken cancellation = default)
    {
        if (!float.IsFinite(volume) || volume is < 0 or > 1)
            return Task.FromResult(new ApplicationAudioChange(new(ResultKind.Unavailable, "Choose a volume between 0 and 100%."), Empty(window, context, "Invalid volume."), 0, 0, 0, false));
        return Submit(window, context, true, false, cancellation, volume);
    }

    private void QueueObservation()
    {
        long owner;
        lock (gate)
        {
            if (disposed || !visible || target is null)
                return;
            observationRevision++;
            if (observationQueued != 0 || queued >= 32)
                return;
            observationQueued = 1;
            owner = ++observationOwner;
        }

        _ = ObserveChanges(owner);
    }

    private async Task ObserveChanges(long owner)
    {
        try
        {
            while (true)
            {
                WindowRef window;
                long context, observedEpoch, observedRevision;
                lock (gate)
                {
                    if (disposed || !visible || target is null)
                    {
                        observationQueued = 0;
                        return;
                    }

                    window = target;
                    context = generation;
                    observedEpoch = epoch;
                    observedRevision = observationRevision;
                }

                await RefreshAsync(window, context).ConfigureAwait(false);
                lock (gate)
                {
                    if (disposed || !visible || target is null)
                    {
                        observationQueued = 0;
                        return;
                    }

                    if (epoch == observedEpoch && observationRevision == observedRevision)
                    {
                        observationQueued = 0;
                        return;
                    }
                }
                // Coalesce notifications/context changes into a fresh read only; never repeat a mutation.
            }
        }
        finally
        {
            lock (gate)
            {
                if (observationOwner == owner)
                    observationQueued = 0;
            }
        }
    }

    private void ReleaseCatalog()
    {
        var old = catalog;
        catalog = null;
        old?.Dispose();
    }

    private void Worker()
    {
        try
        {
            while (true)
            {
                if (Interlocked.Exchange(ref releaseRequested, 0) != 0)
                    ReleaseCatalog();
                if (queue.TryDequeue(out var work))
                {
                    lock (gate)
                        queued--;
                    if (!Continuing(work))
                    {
                        Invalidate(work, "Query superseded or cancelled.");
                        continue;
                    }

                    try
                    {
                        Execute(work);
                    }
                    catch (Exception e) when (e is COMException or InvalidCastException or InvalidOperationException or ArgumentException)
                    {
                        var snapshot = work.Snapshot with
                        {
                            Revision = Interlocked.Increment(ref revision),
                            Availability = ApplicationAudioAvailability.Unknown,
                            MuteState = ApplicationMuteState.Unknown,
                            Reason = "The audio service or device is unavailable; the result is unconfirmed."
                        };
                        Complete(work, new(new(work.Dispatched ? ResultKind.Unknown : ResultKind.Unavailable, snapshot.Reason), snapshot, work.Attempted, work.Succeeded, work.Verified, work.Dispatched));
                        ReleaseCatalog();
                    }

                    bool retain;
                    lock (gate)
                        retain = visible && !disposed;
                    if (!retain)
                        ReleaseCatalog();
                    continue;
                }

                lock (gate)
                {
                    if (disposed)
                        break;
                }

                wake.WaitOne();
            }
        }
        finally
        {
            ReleaseCatalog();
            wake.Dispose();
        }
    }

    private void Complete(Work work, ApplicationAudioChange change)
    {
        if (!Continuing(work))
        {
            Invalidate(work, "The context or deadline changed during the query.");
            return;
        }

        if (!Finish(work, change))
            return;
        lock (gate)
        {
            if (!disposed && Volatile.Read(ref work.Invalid) == 0 && visible && target is not null && target.Same(work.Target) && generation == work.Generation && work.Epoch == epoch)
                Changed?.Invoke(change.Snapshot);
        }
    }

    private (ApplicationAudioSnapshot Snapshot, List<IApplicationAudioSession> Sessions) Observe(Work work, ApplicationAudioCatalogSnapshot discovery)
    {
        var sessions = new List<IApplicationAudioSession>();
        var muted = 0;
        var audible = 0;
        var unread = 0;
        var gaps = discovery.Gaps;
        var volumes = new List<float>();
        foreach (var session in discovery.Sessions)
        {
            if (!Continuing(work))
            {
                gaps++;
                break;
            }

            var hr = session.ProcessId(out var pid);
            if (hr != 0)
            {
                gaps++;
                continue;
            } // AUDCLNT_S_NO_SINGLE_PROCESS is positive and is deliberately rejected.

            if (pid == 0 || pid != work.Target.Pid)
                continue;
            if (!Continuing(work))
            {
                gaps++;
                break;
            }

            if (session.State(out var state) != 0)
            {
                gaps++;
                continue;
            }

            if (state == 2)
                continue;
            if (state is not (0 or 1))
            {
                gaps++;
                continue;
            }

            sessions.Add(session);
            if (!Continuing(work) || session.ReadMute(out var value) != 0)
            {
                unread++;
                gaps++;
            }
            else if (value)
                muted++;
            else
                audible++;
            if (Continuing(work) && session.ReadVolume(out var volume) == 0 && float.IsFinite(volume) && volume is >= 0 and <= 1)
                volumes.Add(volume);
        }

        var availability = sessions.Count == 0 ? (gaps > 0 ? ApplicationAudioAvailability.Unknown : ApplicationAudioAvailability.Unavailable) : gaps > 0 ? ApplicationAudioAvailability.Partial : ApplicationAudioAvailability.Available;
        var mute = sessions.Count == 0 || unread > 0 ? ApplicationMuteState.Unknown : muted > 0 && audible > 0 ? ApplicationMuteState.Mixed : muted > 0 ? ApplicationMuteState.Muted : ApplicationMuteState.Audible;
        var reason = sessions.Count == 0 ? (gaps > 0 ? "Discovery was incomplete; no session could be safely identified for this process." : "No audio session was identified for this process.") : gaps > 0 ? $"{sessions.Count} identified session(s); discovery is partial. Future sessions are not affected." : $"{sessions.Count} identified session(s) for this process. Future sessions are not affected.";
        return (new ApplicationAudioSnapshot(work.Target, work.Generation, Interlocked.Increment(ref revision), availability, mute, sessions.Count, gaps, reason) { VolumeSessions = volumes.Count, VolumeMinimum = volumes.Count > 0 ? volumes.Min() : null, VolumeMaximum = volumes.Count > 0 ? volumes.Max() : null }, sessions);
    }

    private void Execute(Work work)
    {
        if (work.Target.Pid <= 0 || work.Target.Started <= 0 || work.Target.Protected || !valid(work.Target))
        {
            var snapshot = Empty(work.Target, work.Generation, "Window identity unavailable; no audio was changed.", ApplicationAudioAvailability.Unavailable);
            Complete(work, new(new(ResultKind.Unavailable, snapshot.Reason), snapshot, 0, 0, 0, false));
            return;
        }

        catalog ??= factory(QueueObservation, () => Continuing(work));
        var observed = Observe(work, catalog.Discover(() => Continuing(work)));
        work.Snapshot = observed.Snapshot;
        if (!work.Mutation || observed.Sessions.Count == 0)
        {
            Complete(work, new(new(observed.Snapshot.Availability switch
            {
                ApplicationAudioAvailability.Available => ResultKind.Completed,
                ApplicationAudioAvailability.Partial => ResultKind.Partial,
                ApplicationAudioAvailability.Unavailable => ResultKind.Unavailable,
                _ => ResultKind.Unknown
            }, observed.Snapshot.Reason), observed.Snapshot, 0, 0, 0, false));
            return;
        }

        if (work.DesiredVolume is not null && !observed.Snapshot.CanSetVolume)
        {
            Complete(work, new(new(ResultKind.Unavailable, "Volume was not confirmed in every identified session. No change was sent."), observed.Snapshot, 0, 0, 0, false));
            return;
        }

        var keys = observed.Sessions.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var session in observed.Sessions)
        {
            if (!Continuing(work) || !valid(work.Target))
                break;
            if (session.ProcessId(out var pid) != 0 || pid != work.Target.Pid || !Continuing(work) || session.State(out var state) != 0 || state is not (0 or 1))
                break;
            lock (work.Gate)
            {
                if (!Continuing(work) || !valid(work.Target) || !Continuing(work))
                    break;
                work.Attempted++;
                work.Dispatched = true;
            }

            var hr = work.DesiredVolume is { } desiredVolume ? session.WriteVolume(desiredVolume) : session.WriteMute(work.DesiredMute);
            if (hr == 0)
                Interlocked.Increment(ref work.Succeeded);
            if (!Continuing(work))
                break;
            if (hr == 0
                && (work.DesiredVolume is { } desired ? session.ReadVolume(out var actual) == 0
                && float.IsFinite(actual)
                && Math.Abs(actual - desired) < .005f : session.ReadMute(out var muted) == 0
                && muted == work.DesiredMute))
                Interlocked.Increment(ref work.Verified);
        }

        if (!Continuing(work))
        {
            Invalidate(work, "Observation was cancelled or timed out after discovery.");
            return;
        }

        if (!valid(work.Target))
        {
            Complete(work, new(new(work.Dispatched ? ResultKind.Unknown : ResultKind.Unavailable, "The process identity changed; no further commands will be sent."), work.Snapshot with
            {
                Availability = ApplicationAudioAvailability.Unknown,
                MuteState = ApplicationMuteState.Unknown,
                Reason = "The process identity changed."
            }, work.Attempted, work.Succeeded, work.Verified, work.Dispatched));
            return;
        }

        var after = Observe(work, catalog.Discover(() => Continuing(work)));
        var same = keys.SetEquals(after.Sessions.Select(s => s.Key));
        var finalMatches = work.DesiredVolume is { } finalVolume ? after.Snapshot.CanSetVolume
            && after.Snapshot.VolumeMinimum is { } minimum
            && after.Snapshot.VolumeMaximum is { } maximum
            && Math.Abs(minimum - finalVolume) < .005f
            && Math.Abs(maximum - finalVolume) < .005f : after.Snapshot.MuteState == (work.DesiredMute ? ApplicationMuteState.Muted : ApplicationMuteState.Audible);
        var complete = same
            && finalMatches
            && observed.Snapshot.Availability == ApplicationAudioAvailability.Available
            && after.Snapshot.Availability == ApplicationAudioAvailability.Available
            && work.Verified == keys.Count
            && work.Succeeded == keys.Count;
        var reason = complete ? (work.DesiredVolume is { } level ? $"Volume of {level:P0} confirmed in {work.Verified} identified session(s) for this process. Future sessions are not affected." : $"State {(work.DesiredMute ? "muted" : "unmuted")} confirmed in {work.Verified} identified session(s) for this process. Future sessions are not affected.") : $"Partial or unconfirmed state: {work.Verified}/{keys.Count} session(s) verified; {work.Attempted} dispatch(es). There will be no automatic retry.";
        if (!complete && !finalMatches)
            reason = "The final observation does not match the requested state. " + reason;
        var final = after.Snapshot with
        {
            Availability = complete ? ApplicationAudioAvailability.Available : after.Sessions.Count > 0 ? ApplicationAudioAvailability.Partial : ApplicationAudioAvailability.Unknown,
            Reason = reason
        };
        work.Snapshot = final;
        Complete(work, new(new(complete ? ResultKind.Completed : work.Verified > 0 ? ResultKind.Partial : ResultKind.Unknown, reason), final, work.Attempted, work.Succeeded, work.Verified, work.Dispatched));
    }

    public void Suspend()
    {
        Work[] cancel;
        lock (gate)
        {
            if (disposed)
                return;
            visible = false;
            target = null;
            epoch++;
            cancel = pending.ToArray();
        }

        foreach (var work in cancel)
            Invalidate(work, "Observation suspended.");
        Interlocked.Exchange(ref releaseRequested, 1);
        Signal();
    }

    public void Dispose()
    {
        Work[] cancel;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            visible = false;
            target = null;
            epoch++;
            cancel = pending.ToArray();
            Changed = null;
        }

        foreach (var work in cancel)
            Invalidate(work, "The audio service has stopped.");
        Interlocked.Exchange(ref releaseRequested, 1);
        Signal();
    }
}

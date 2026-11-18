using System.Runtime.InteropServices;
using System.Windows.Threading;
using Windows.Media.Control;

namespace ApolloOverlay;
/// <summary>SMTC sessions retain their native identities until removed or the panel is hidden.</summary>
public sealed class SessionMedia : ISessionMedia
{
    private sealed record Owner(string Id, GlobalSystemMediaTransportControlsSession Session);
    private readonly SemaphoreSlim owner = new(1, 1);
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<nint, Owner> sessions = [];
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private bool visible, disposed;
    private bool refreshPending;
    private int refreshQueued;
    private readonly Func<CancellationToken, Task<SessionMediaSnapshot>>? read;
    private long generation, revision;
    public SessionMedia()
    {
    }

    internal SessionMedia(Func<CancellationToken, Task<SessionMediaSnapshot>> read)
    {
        this.read = read;
    }

    public SessionMediaSnapshot Current
    {
        get;
        private set;
    } = SessionMediaSnapshot.Empty;

    public event Action<SessionMediaSnapshot>? Changed;
    private static nint Identity(GlobalSystemMediaTransportControlsSession session) => session is WinRT.IWinRTObject native ? native.NativeObject.ThisPtr : throw new InvalidOperationException("The session has no native identity.");
    private static string Text(string? value, int maximum = 180) => new((value ?? "").Where(c => !char.IsControl(c)).Take(maximum).ToArray());
    private static bool Failure(Exception error) => error is COMException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
    public void SetVisible(bool value)
    {
        if (!dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(() => SetVisible(value));
            return;
        }

        if (disposed || visible == value)
            return;
        visible = value;
        generation++;
        refreshPending = false;
        if (value)
            _ = RefreshAsync();
        else
        {
            foreach (var item in sessions.Values)
                Unsubscribe(item.Session);
            sessions.Clear();
            try
            {
                if (manager is not null)
                    manager.SessionsChanged -= SessionsChanged;
            }
            catch (Exception error) when (Failure(error))
            {
            }

            manager = null;
            Current = SessionMediaSnapshot.Empty with
            {
                Revision = ++revision
            };
        }
    }

    private void SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => QueueRefresh();
    private void PlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => QueueRefresh();
    private void PropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => QueueRefresh();
    private void QueueRefresh()
    {
        if (dispatcher.HasShutdownStarted || Interlocked.Exchange(ref refreshQueued, 1) != 0)
            return;
        try
        {
            dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref refreshQueued, 0);
                if (visible && !disposed)
                    _ = RefreshAsync();
            });
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref refreshQueued, 0);
        }
    }

    private void Subscribe(GlobalSystemMediaTransportControlsSession session)
    {
        session.PlaybackInfoChanged += PlaybackChanged;
        try
        {
            session.MediaPropertiesChanged += PropertiesChanged;
        }
        catch
        {
            Unsubscribe(session);
            throw;
        }
    }

    private void Unsubscribe(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            session.PlaybackInfoChanged -= PlaybackChanged;
        }
        catch (Exception error) when (Failure(error))
        {
        }
        try
        {
            session.MediaPropertiesChanged -= PropertiesChanged;
        }
        catch (Exception error) when (Failure(error))
        {
        }
    }

    private static bool CanToggle(GlobalSystemMediaTransportControlsSessionPlaybackInfo info) => info.Controls.IsPlayPauseToggleEnabled
        || (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? info.Controls.IsPauseEnabled : info.Controls.IsPlayEnabled);
    public async Task RefreshAsync(CancellationToken cancellation = default)
    {
        if (!dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(() => RefreshAsync(cancellation)).Task.Unwrap();
            return;
        }

        if (!visible || disposed)
            return;
        if (!await owner.WaitAsync(0, cancellation))
        {
            refreshPending = true;
            return;
        }

        refreshPending = false;
        var epoch = generation;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (read is not null)
            {
                var observed = await read(deadline.Token);
                if (visible && !disposed && epoch == generation)
                {
                    Current = observed with
                    {
                        Revision = ++revision
                    };
                    Changed?.Invoke(Current);
                }

                return;
            }

            if (manager is null)
            {
                var requested = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(deadline.Token);
                if (!visible || disposed || epoch != generation)
                    return;
                requested.SessionsChanged += SessionsChanged;
                manager = requested;
            }

            var live = manager.GetSessions().Take(12).ToArray();
            var present = live.Select(Identity).ToHashSet();
            foreach (var stale in sessions.Keys.Where(key => !present.Contains(key)).ToArray())
            {
                Unsubscribe(sessions[stale].Session);
                sessions.Remove(stale);
            }

            var current = manager.GetCurrentSession();
            var currentPointer = current is null ? 0 : Identity(current);
            var items = new List<SessionMediaItem>();
            var selected = "";
            foreach (var session in live)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var pointer = Identity(session);
                if (!sessions.TryGetValue(pointer, out var entry))
                {
                    entry = new(Guid.NewGuid().ToString("N"), session);
                    Subscribe(session);
                    sessions.Add(pointer, entry);
                }

                var properties = await session.TryGetMediaPropertiesAsync().AsTask(deadline.Token);
                if (!visible || disposed || epoch != generation)
                    return;
                var playback = session.GetPlaybackInfo();
                var controls = playback.Controls;
                items.Add(new(entry.Id, Text(properties.Title), Text(properties.Artist), Text(session.SourceAppUserModelId, 100), playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, CanToggle(playback), controls.IsPreviousEnabled, controls.IsNextEnabled));
                if (pointer == currentPointer)
                    selected = entry.Id;
            }

            if (!visible || disposed || epoch != generation)
                return;
            Current = new(++revision, items.ToArray(), selected.Length > 0 ? selected : items.FirstOrDefault()?.Id ?? "", items.Count == 0 ? "No active media." : "Windows media controls.");
            Changed?.Invoke(Current);
        }
        catch (OperationCanceledException)
        {
            if (visible && !disposed && epoch == generation && !cancellation.IsCancellationRequested)
            {
                Current = new(++revision, [], "", "Media refresh interrupted. Refresh to check playback.", ObservationFailed: true);
                Changed?.Invoke(Current);
            }
        }
        catch (Exception error) when (Failure(error))
        {
            if (visible && !disposed && epoch == generation)
            {
                Current = new(++revision, [], "", "Could not read playback. Refresh to try again.", ObservationFailed: true);
                Changed?.Invoke(Current);
            }
        }
        finally
        {
            owner.Release();
            if (refreshPending && visible && !disposed)
                QueueRefresh();
        }
    }

    public async Task<ActionResult> CommandAsync(string id, SessionMediaCommand command, CancellationToken cancellation = default)
    {
        if (!dispatcher.CheckAccess())
            return await dispatcher.InvokeAsync(() => CommandAsync(id, command, cancellation)).Task.Unwrap();
        if (!visible || disposed || !await owner.WaitAsync(0, cancellation))
            return new(ResultKind.Unavailable, "Media is being refreshed. Try again.");
        var dispatched = false;
        var epoch = generation;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(3000);
        try
        {
            var entry = sessions.Values.SingleOrDefault(value => value.Id == id);
            if (entry is null || manager is null || !manager.GetSessions().Any(value => Identity(value) == Identity(entry.Session)))
                return new(ResultKind.Unavailable, "The media session changed. Select it again.");
            return await DispatchAsync(entry.Session, command, false,
                () => visible && !disposed && generation == epoch, deadline.Token, () => dispatched = true);
        }
        catch (OperationCanceledException)
        {
            return new(dispatched ? ResultKind.Unknown : ResultKind.Cancelled, dispatched ? "The command may have been sent. Check the media before trying again." : "Command cancelled before dispatch.");
        }
        catch (Exception error) when (Failure(error))
        {
            return new(dispatched ? ResultKind.Unknown : ResultKind.Unavailable, "The media session did not respond. Check the app before trying again.");
        }
        finally
        {
            owner.Release();
            if (visible && !disposed)
                _ = RefreshAsync();
        }
    }

    public async Task<SessionMediaOperation> OperateCurrentAsync(SessionMediaCommand? command, bool dryRun, CancellationToken cancellation = default)
    {
        if (!dispatcher.CheckAccess())
            return await dispatcher.InvokeAsync(() => OperateCurrentAsync(command, dryRun, cancellation)).Task.Unwrap();

        var snapshot = SessionMediaSnapshot.Empty;
        if (disposed || !await owner.WaitAsync(0, cancellation))
            return new(snapshot, new(ResultKind.Unavailable, "Media is unavailable or busy."));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var dispatched = false;
        try
        {
            var source = manager ?? await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(deadline.Token);
            var session = source.GetCurrentSession();
            if (session is null)
                return new(snapshot, new(command is null ? ResultKind.Completed : ResultKind.Unavailable, snapshot.Status));
            var properties = await session.TryGetMediaPropertiesAsync().AsTask(deadline.Token);
            var info = session.GetPlaybackInfo();
            var identity = Identity(session);
            bool StillCurrent() => !disposed && source.GetCurrentSession() is { } current && Identity(current) == identity;
            deadline.Token.ThrowIfCancellationRequested();
            if (!StillCurrent())
                return new(snapshot, new(ResultKind.Unavailable, "The current media session changed."));
            var item = new SessionMediaItem("current", Text(properties.Title), Text(properties.Artist), Text(session.SourceAppUserModelId, 100),
                info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, CanToggle(info),
                info.Controls.IsPreviousEnabled, info.Controls.IsNextEnabled);
            snapshot = new(revision, [item], item.Id, "Windows media controls.");
            var result = command is null ? new ActionResult(ResultKind.Completed, "Current playback observed.")
                : await DispatchAsync(session, command.Value, dryRun, StillCurrent, deadline.Token, () => dispatched = true);
            return new(snapshot, result, dispatched);
        }
        catch (OperationCanceledException)
        {
            return new(snapshot, new(dispatched ? ResultKind.Unknown : cancellation.IsCancellationRequested ? ResultKind.Cancelled : ResultKind.Unavailable,
                dispatched ? "Command outcome unconfirmed." : "Media observation interrupted."), dispatched);
        }
        catch (Exception error) when (Failure(error))
        {
            return new(snapshot, new(dispatched ? ResultKind.Unknown : ResultKind.Unavailable, "Windows could not confirm the media operation."), dispatched);
        }
        finally
        {
            owner.Release();
            if (visible && !disposed && (dispatched || refreshPending))
                QueueRefresh();
        }
    }

    private static async Task<ActionResult> DispatchAsync(GlobalSystemMediaTransportControlsSession session, SessionMediaCommand command,
        bool dryRun, Func<bool> valid, CancellationToken token, Action started)
    {
        var info = session.GetPlaybackInfo();
        bool AtTarget(GlobalSystemMediaTransportControlsSessionPlaybackStatus state) => command switch
        {
            SessionMediaCommand.Play => state == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            SessionMediaCommand.Pause => state == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
            _ => false
        };
        token.ThrowIfCancellationRequested();
        if (!valid())
            return new(ResultKind.Unavailable, "The selected media session changed.");
        if (AtTarget(info.PlaybackStatus))
            return new(ResultKind.Completed, "Playback already has the requested state.");
        var controls = info.Controls;
        var allowed = command switch
        {
            SessionMediaCommand.Play => controls.IsPlayEnabled,
            SessionMediaCommand.Pause => controls.IsPauseEnabled,
            SessionMediaCommand.Previous => controls.IsPreviousEnabled,
            SessionMediaCommand.Next => controls.IsNextEnabled,
            SessionMediaCommand.Toggle => CanToggle(info),
            _ => false
        };
        if (!allowed)
            return new(ResultKind.Unavailable, "This app does not support that command.");
        token.ThrowIfCancellationRequested();
        if (!valid())
            return new(ResultKind.Unavailable, "The selected media session changed.");
        if (dryRun)
            return new(ResultKind.Completed, "Media command available.");
        started();
        var accepted = await (command switch
        {
            SessionMediaCommand.Play => session.TryPlayAsync(),
            SessionMediaCommand.Pause => session.TryPauseAsync(),
            SessionMediaCommand.Previous => session.TrySkipPreviousAsync(),
            SessionMediaCommand.Next => session.TrySkipNextAsync(),
            _ when info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && controls.IsPauseEnabled => session.TryPauseAsync(),
            _ when info.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && controls.IsPlayEnabled => session.TryPlayAsync(),
            _ => session.TryTogglePlayPauseAsync()
        }).AsTask(token);
        if (!accepted)
            return new(ResultKind.Failed, "The app rejected the media command.");
        if (command is SessionMediaCommand.Play or SessionMediaCommand.Pause or SessionMediaCommand.Toggle && valid())
        {
            var after = session.GetPlaybackInfo().PlaybackStatus;
            if (AtTarget(after) || command == SessionMediaCommand.Toggle && after != info.PlaybackStatus
                && after is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                return new(ResultKind.Completed, after == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "Playback resumed." : "Playback paused.");
        }
        return new(ResultKind.Requested, "The app accepted the command. Check playback.");
    }

    public void Dispose()
    {
        // Complete cleanup on the same owner as SetVisible and native subscriptions.
        // Setting disposed before this dispatch would make SetVisible skip teardown.
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Dispose);
            return;
        }
        if (disposed)
            return;
        SetVisible(false);
        disposed = true;
        Changed = null;
    }
}

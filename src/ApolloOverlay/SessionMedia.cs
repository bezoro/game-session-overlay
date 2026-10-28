using System.Runtime.InteropServices;
using System.Windows.Threading;
using Windows.Media.Control;

namespace ApolloOverlay;

public enum SessionMediaCommand { Toggle, Previous, Next }
public sealed record SessionMediaItem(string Id, string Title, string Artist, string Application, bool Playing,
    bool CanToggle, bool CanPrevious, bool CanNext);
public sealed record SessionMediaSnapshot(long Revision, IReadOnlyList<SessionMediaItem> Items, string CurrentId, string Status)
{
    public static SessionMediaSnapshot Empty { get; } = new(0, [], "", "Nenhuma mídia ativa.");
}
public interface ISessionMedia : IDisposable
{
    SessionMediaSnapshot Current { get; }
    event Action<SessionMediaSnapshot>? Changed;
    void SetVisible(bool visible);
    Task RefreshAsync(CancellationToken cancellation = default);
    Task<ActionResult> CommandAsync(string id, SessionMediaCommand command, CancellationToken cancellation = default);
}

/// <summary>SMTC sessions retain their native identities until removed or the panel is hidden.</summary>
public sealed class SessionMedia : ISessionMedia
{
    private sealed record Owner(string Id, GlobalSystemMediaTransportControlsSession Session);
    private readonly SemaphoreSlim owner = new(1, 1);
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<nint, Owner> sessions = [];
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private bool visible, disposed;
    private long generation, revision;
    public SessionMediaSnapshot Current { get; private set; } = SessionMediaSnapshot.Empty;
    public event Action<SessionMediaSnapshot>? Changed;

    private static nint Identity(GlobalSystemMediaTransportControlsSession session) =>
        session is WinRT.IWinRTObject native ? native.NativeObject.ThisPtr : throw new InvalidOperationException("Sessão sem identidade nativa.");
    private static string Text(string? value, int maximum = 180) => new((value ?? "").Where(c => !char.IsControl(c)).Take(maximum).ToArray());
    private static bool Failure(Exception error) => error is COMException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
    public void SetVisible(bool value)
    {
        if (!dispatcher.CheckAccess()) { if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(() => SetVisible(value)); return; }
        if (disposed || visible == value) return;
        visible = value; generation++;
        if (value) _ = RefreshAsync();
        else
        {
            foreach (var item in sessions.Values) Unsubscribe(item.Session);
            sessions.Clear();
            if (manager is not null) manager.SessionsChanged -= SessionsChanged;
            manager = null;
            Current = SessionMediaSnapshot.Empty with { Revision = ++revision };
        }
    }
    private void SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => QueueRefresh();
    private void PlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => QueueRefresh();
    private void PropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => QueueRefresh();
    private void QueueRefresh()
    {
        if (dispatcher.HasShutdownStarted) return;
        try { dispatcher.BeginInvoke(() => { if (visible && !disposed) _ = RefreshAsync(); }); }
        catch (InvalidOperationException) { }
    }
    private void Subscribe(GlobalSystemMediaTransportControlsSession session)
    {
        session.PlaybackInfoChanged += PlaybackChanged;
        session.MediaPropertiesChanged += PropertiesChanged;
    }
    private void Unsubscribe(GlobalSystemMediaTransportControlsSession session)
    {
        try { session.PlaybackInfoChanged -= PlaybackChanged; session.MediaPropertiesChanged -= PropertiesChanged; }
        catch (Exception error) when (Failure(error)) { }
    }
    public async Task RefreshAsync(CancellationToken cancellation = default)
    {
        if (!dispatcher.CheckAccess()) { await dispatcher.InvokeAsync(() => RefreshAsync(cancellation)).Task.Unwrap(); return; }
        if (!visible || disposed || !await owner.WaitAsync(0, cancellation)) return;
        var epoch = generation;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (manager is null)
            {
                var requested = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(deadline.Token);
                if (!visible || disposed || epoch != generation) return;
                manager = requested; manager.SessionsChanged += SessionsChanged;
            }
            var live = manager.GetSessions().Take(12).ToArray();
            var present = live.Select(Identity).ToHashSet();
            foreach (var stale in sessions.Keys.Where(key => !present.Contains(key)).ToArray()) { Unsubscribe(sessions[stale].Session); sessions.Remove(stale); }
            var current = manager.GetCurrentSession();
            var currentPointer = current is null ? 0 : Identity(current);
            var items = new List<SessionMediaItem>(); var selected = "";
            foreach (var session in live)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var pointer = Identity(session);
                if (!sessions.TryGetValue(pointer, out var entry)) { entry = new(Guid.NewGuid().ToString("N"), session); sessions.Add(pointer, entry); Subscribe(session); }
                var properties = await session.TryGetMediaPropertiesAsync().AsTask(deadline.Token);
                if (!visible || disposed || epoch != generation) return;
                var playback = session.GetPlaybackInfo(); var controls = playback.Controls;
                items.Add(new(entry.Id, Text(properties.Title), Text(properties.Artist), Text(session.SourceAppUserModelId, 100),
                    playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    controls.IsPlayPauseToggleEnabled, controls.IsPreviousEnabled, controls.IsNextEnabled));
                if (pointer == currentPointer) selected = entry.Id;
            }
            if (!visible || disposed || epoch != generation) return;
            Current = new(++revision, items.ToArray(), selected.Length > 0 ? selected : items.FirstOrDefault()?.Id ?? "", items.Count == 0 ? "Nenhuma mídia ativa." : "Controles de mídia do Windows.");
            Changed?.Invoke(Current);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (Failure(error))
        {
            if (visible && !disposed && epoch == generation) { Current = new(++revision, [], "", "Mídia indisponível. Abra um aplicativo compatível e tente novamente."); Changed?.Invoke(Current); }
        }
        finally { owner.Release(); }
    }
    public async Task<ActionResult> CommandAsync(string id, SessionMediaCommand command, CancellationToken cancellation = default)
    {
        if (!dispatcher.CheckAccess()) return await dispatcher.InvokeAsync(() => CommandAsync(id, command, cancellation)).Task.Unwrap();
        if (!visible || disposed || !await owner.WaitAsync(0, cancellation)) return new(ResultKind.Unavailable, "A mídia está sendo atualizada. Tente novamente.");
        var dispatched = false; var epoch = generation;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(3000);
        try
        {
            var entry = sessions.Values.SingleOrDefault(value => value.Id == id);
            if (entry is null || manager is null || !manager.GetSessions().Any(value => Identity(value) == Identity(entry.Session)))
                return new(ResultKind.Unavailable, "A sessão de mídia mudou. Selecione a mídia novamente.");
            var info = entry.Session.GetPlaybackInfo(); var controls = info.Controls;
            var allowed = command switch { SessionMediaCommand.Toggle => controls.IsPlayPauseToggleEnabled, SessionMediaCommand.Previous => controls.IsPreviousEnabled, SessionMediaCommand.Next => controls.IsNextEnabled, _ => false };
            if (!allowed) return new(ResultKind.Unavailable, "Este aplicativo não oferece esse comando.");
            deadline.Token.ThrowIfCancellationRequested();
            if (!visible || disposed || generation != epoch) return new(ResultKind.Cancelled, "Painel ocultado antes do envio.");
            dispatched = true;
            var accepted = await (command switch
            {
                SessionMediaCommand.Toggle => entry.Session.TryTogglePlayPauseAsync(),
                SessionMediaCommand.Previous => entry.Session.TrySkipPreviousAsync(),
                _ => entry.Session.TrySkipNextAsync()
            }).AsTask(deadline.Token);
            if (!accepted) return new(ResultKind.Failed, "O aplicativo recusou o comando de mídia.");
            if (command == SessionMediaCommand.Toggle && visible && !disposed && generation == epoch)
            {
                var after = entry.Session.GetPlaybackInfo().PlaybackStatus;
                if (after != info.PlaybackStatus && after is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                    return new(ResultKind.Completed, after == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "Reprodução retomada." : "Reprodução pausada.");
            }
            return new(ResultKind.Requested, "Comando aceito pelo aplicativo. Confira a reprodução.");
        }
        catch (OperationCanceledException) { return new(dispatched ? ResultKind.Unknown : ResultKind.Cancelled, dispatched ? "O comando pode ter sido enviado. Confira a mídia antes de repetir." : "Comando cancelado antes do envio."); }
        catch (Exception error) when (Failure(error)) { return new(dispatched ? ResultKind.Unknown : ResultKind.Unavailable, "A sessão de mídia não respondeu. Confira o aplicativo antes de repetir."); }
        finally { owner.Release(); if (visible && !disposed) _ = RefreshAsync(); }
    }
    public void Dispose() { if (disposed) return; SetVisible(false); disposed = true; Changed = null; }
}

public sealed class PreviewSessionMedia : ISessionMedia
{
    public SessionMediaSnapshot Current { get; private set; } = SessionMediaSnapshot.Empty;
    public event Action<SessionMediaSnapshot>? Changed;
    public void SetVisible(bool value) { }
    public Task RefreshAsync(CancellationToken cancellation = default) => Task.CompletedTask;
    public Task<ActionResult> CommandAsync(string id, SessionMediaCommand command, CancellationToken cancellation = default) => Task.FromResult(new ActionResult(ResultKind.Simulated, "Prévia de mídia; nenhum comando enviado."));
    internal void SetFixture(SessionMediaSnapshot snapshot) { Current = snapshot; Changed?.Invoke(snapshot); }
    public void Dispose() { Changed = null; }
}

namespace ApolloOverlay;

public enum SessionMediaCommand
{
    Toggle,
    Previous,
    Next,
    Play,
    Pause
}

public sealed record SessionMediaItem(string Id, string Title, string Artist, string Application, bool Playing, bool CanToggle, bool CanPrevious, bool CanNext);
public sealed record SessionMediaSnapshot(long Revision, IReadOnlyList<SessionMediaItem> Items, string CurrentId, string Status,
    bool ObservationFailed = false)
{
    public static SessionMediaSnapshot Empty { get; } = new(0, [], "", "No active media.");
}

/// <summary>The snapshot describes the observation made before any command dispatch.</summary>
public sealed record SessionMediaOperation(SessionMediaSnapshot Snapshot, ActionResult Result, bool Dispatched = false);

public interface ISessionMedia : IDisposable
{
    SessionMediaSnapshot Current
    {
        get;
    }

    event Action<SessionMediaSnapshot>? Changed;
    void SetVisible(bool visible);
    Task RefreshAsync(CancellationToken cancellation = default);
    Task<SessionMediaOperation> OperateCurrentAsync(SessionMediaCommand? command, bool dryRun, CancellationToken cancellation = default);
    Task<ActionResult> CommandAsync(string id, SessionMediaCommand command, CancellationToken cancellation = default);
}

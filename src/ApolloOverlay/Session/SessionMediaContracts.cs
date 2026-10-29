namespace ApolloOverlay;

public enum SessionMediaCommand
{
    Toggle,
    Previous,
    Next
}

public sealed record SessionMediaItem(string Id, string Title, string Artist, string Application, bool Playing, bool CanToggle, bool CanPrevious, bool CanNext);
public sealed record SessionMediaSnapshot(long Revision, IReadOnlyList<SessionMediaItem> Items, string CurrentId, string Status)
{
    public static SessionMediaSnapshot Empty { get; } = new(0, [], "", "No active media.");
}

public interface ISessionMedia : IDisposable
{
    SessionMediaSnapshot Current
    {
        get;
    }

    event Action<SessionMediaSnapshot>? Changed;
    void SetVisible(bool visible);
    Task RefreshAsync(CancellationToken cancellation = default);
    Task<ActionResult> CommandAsync(string id, SessionMediaCommand command, CancellationToken cancellation = default);
}

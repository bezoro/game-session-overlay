namespace ApolloOverlay;

public sealed class PreviewSessionMedia : ISessionMedia
{
    public SessionMediaSnapshot Current
    {
        get;
        private set;
    } = SessionMediaSnapshot.Empty;

    public event Action<SessionMediaSnapshot>? Changed;
    public void SetVisible(bool value)
    {
    }

    public Task RefreshAsync(CancellationToken cancellation = default) => Task.CompletedTask;
    public Task<ActionResult> CommandAsync(string id, SessionMediaCommand command, CancellationToken cancellation = default) => Task.FromResult(new ActionResult(ResultKind.Simulated, "Media preview; no command was sent."));
    public Task<SessionMediaOperation> OperateCurrentAsync(SessionMediaCommand? command, bool dryRun, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var item = Current.Items.FirstOrDefault(item => item.Id == Current.CurrentId);
        var available = command switch
        {
            null => true,
            SessionMediaCommand.Next => item?.CanNext == true,
            SessionMediaCommand.Previous => item?.CanPrevious == true,
            _ => item?.CanToggle == true
        };
        var result = available ? new ActionResult(ResultKind.Simulated, "Preview media observation.") : new(ResultKind.Unavailable, "Preview media command unavailable.");
        return Task.FromResult(new SessionMediaOperation(Current, result));
    }

    internal void SetFixture(SessionMediaSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(snapshot);
    }

    public void Dispose()
    {
        Changed = null;
    }
}

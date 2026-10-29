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
    public Task<ActionResult> CommandAsync(string id, SessionMediaCommand command, CancellationToken cancellation = default) => Task.FromResult(new ActionResult(ResultKind.Simulated, "Prévia de mídia; nenhum comando enviado."));
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

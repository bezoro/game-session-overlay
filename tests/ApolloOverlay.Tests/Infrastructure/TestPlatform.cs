using ApolloOverlay;

internal sealed class TestPlatform(bool preview = true) : IPlatform
{
    public WindowRef Window = new(101, 42, 100, "Production report — an ordinary window with a complete and deliberately long title", "fixture");
    public bool ValidIdentity = true, Empty;
    public int Calls, Closes;
    public Task<ActionResult> Next = Task.FromResult(new ActionResult(ResultKind.Failed, "fixture refusal"));
    public Func<Task<ActionResult>>? OnTaskView;
    public Func<WindowRef, CancellationToken, Func<bool>, Task<ActionResult>>? OnConfirmedClose;
    public bool IsPreview => preview;

    public WindowRef? Foreground() => Empty ? null : Window;
    public IReadOnlyList<WindowRef> Windows() => Empty ? [] : [Window];
    public bool Valid(WindowRef w) => ValidIdentity && Window.Same(w);
    public IReadOnlyList<DisplayRef> Displays() => [new("fixture", 0, 0, 1280, 720, true)];
    public ModuleEvidence SteamEvidence(WindowRef? w) => ModuleEvidence.Unknown;
    public Task<ActionResult> TaskView()
    {
        Calls++;
        return OnTaskView?.Invoke() ?? Next;
    }

    public Task<ActionResult> Switch(WindowRef w)
    {
        Calls++;
        return Next;
    }

    public Task<ActionResult> Close(WindowRef w, CancellationToken token)
    {
        Closes++;
        return Task.FromResult(new ActionResult(ResultKind.Simulated, "fixture close"));
    }

    public Task<ActionResult> CloseConfirmed(WindowRef w, CancellationToken token, Func<bool> prepareForConsent) =>
        OnConfirmedClose?.Invoke(w, token, prepareForConsent) ?? Close(w, token);

    public Task<ActionResult> Shortcut(WindowRef w, string keys) => throw new Exception("No native keys allowed");
    public ActionResult Launch(bool playnite, string? path) => throw new Exception("No native launch allowed");
    public void Dispose()
    {
    }
}

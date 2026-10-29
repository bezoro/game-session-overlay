namespace ApolloOverlay;

public sealed class PreviewPlatform : IPlatform
{
    public bool IsPreview => true;

    private readonly List<WindowRef> windows = [];
    public PreviewPlatform(WindowRef? ownedFixture = null)
    {
        if (ownedFixture is not null)
            windows.Add(ownedFixture);
    }

    public WindowRef? Foreground() => windows.FirstOrDefault();
    public IReadOnlyList<WindowRef> Windows() => windows.ToArray();
    public bool Valid(WindowRef window) => windows.Any(window.Same);
    public IReadOnlyList<DisplayRef> Displays() => [new("EXAMPLE DISPLAY", 0, 0, 1920, 1080, true), new("SECOND EXAMPLE DISPLAY", -1280, 0, 1280, 720, false)];
    public ModuleEvidence SteamEvidence(WindowRef? window) => ModuleEvidence.Unknown;
    public Task<ActionResult> Switch(WindowRef window) => Task.FromResult(new ActionResult(ResultKind.Simulated, $"Would return to {window.Title}."));
    public Task<ActionResult> Close(WindowRef window, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return Task.FromResult(new ActionResult(ResultKind.Cancelled, "Nothing was sent."));
        windows.RemoveAll(window.Same);
        return Task.FromResult(new ActionResult(ResultKind.Simulated, $"Would close only {window.Title}."));
    }

    public Task<ActionResult> Shortcut(WindowRef window, string keys) => Task.FromResult(new ActionResult(ResultKind.Simulated, $"Would request {keys}; no external command was sent."));
    public Task<ActionResult> Shortcut(WindowRef window, string keys, CancellationToken cancellation, Func<bool> continuing) => Task.FromResult(cancellation.IsCancellationRequested || !continuing() ? new ActionResult(ResultKind.Cancelled, "Cancelled without dispatch.") : new ActionResult(ResultKind.Simulated, "Preview: no external shortcut was sent."));
    public Task<ActionResult> Minimize(WindowRef window, CancellationToken cancellation) => Task.FromResult(cancellation.IsCancellationRequested ? new ActionResult(ResultKind.Cancelled, "Nothing was sent.") : !Valid(window) || window.Protected ? new ActionResult(ResultKind.Unavailable, "The window is unavailable for minimizing.") : new ActionResult(ResultKind.Simulated, $"Would minimize {window.Title}; no external command was sent."));
    public ActionResult Launch(bool playnite, string? path) => new(ResultKind.Simulated, playnite ? "Would request Playnite in fullscreen mode." : "Would request Steam Big Picture.");
    public void Dispose()
    {
    }
}

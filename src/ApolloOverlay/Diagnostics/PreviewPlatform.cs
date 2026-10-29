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
    public IReadOnlyList<DisplayRef> Displays() => [new("TELA DE EXEMPLO", 0, 0, 1920, 1080, true), new("SEGUNDA TELA DE EXEMPLO", -1280, 0, 1280, 720, false)];
    public ModuleEvidence SteamEvidence(WindowRef? window) => ModuleEvidence.Unknown;
    public Task<ActionResult> Switch(WindowRef window) => Task.FromResult(new ActionResult(ResultKind.Simulated, $"Voltaria para {window.Title}."));
    public Task<ActionResult> Close(WindowRef window, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return Task.FromResult(new ActionResult(ResultKind.Cancelled, "Nada foi enviado."));
        windows.RemoveAll(window.Same);
        return Task.FromResult(new ActionResult(ResultKind.Simulated, $"Fecharia somente {window.Title}."));
    }

    public Task<ActionResult> Shortcut(WindowRef window, string keys) => Task.FromResult(new ActionResult(ResultKind.Simulated, $"Solicitaria {keys}; nenhum comando externo foi enviado."));
    public Task<ActionResult> Shortcut(WindowRef window, string keys, CancellationToken cancellation, Func<bool> continuing) => Task.FromResult(cancellation.IsCancellationRequested || !continuing() ? new ActionResult(ResultKind.Cancelled, "Cancelado sem envio.") : new ActionResult(ResultKind.Simulated, "Prévia: nenhum atalho externo enviado."));
    public Task<ActionResult> Minimize(WindowRef window, CancellationToken cancellation) => Task.FromResult(cancellation.IsCancellationRequested ? new ActionResult(ResultKind.Cancelled, "Nada foi enviado.") : !Valid(window) || window.Protected ? new ActionResult(ResultKind.Unavailable, "Janela indisponível para minimizar.") : new ActionResult(ResultKind.Simulated, $"Minimizaria {window.Title}; nenhum comando externo foi enviado."));
    public ActionResult Launch(bool playnite, string? path) => new(ResultKind.Simulated, playnite ? "Solicitaria o Playnite em tela cheia." : "Solicitaria Steam Big Picture.");
    public void Dispose()
    {
    }
}

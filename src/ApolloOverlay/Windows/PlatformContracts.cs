using System.Windows.Media;

namespace ApolloOverlay;

public readonly record struct ShortcutDispatch(long Revision, int Expected, uint? Accepted, int CleanupExpected, uint? CleanupAccepted, Automation.SteamSequenceEvidence? Sequence = null);
public interface IPlatform : IDisposable
{
    bool IsPreview
    {
        get;
    }

    WindowRef? Foreground();
    IReadOnlyList<WindowRef> Windows();
    bool Valid(WindowRef window);
    ImageSource? Icon(WindowRef window) => null;
    IReadOnlyList<DisplayRef> Displays();
    ModuleEvidence SteamEvidence(WindowRef? window);
    bool HasSteamLaunchContext(WindowRef? window) => false;
    Task<ActionResult> Switch(WindowRef window);
    Task<ActionResult> Close(WindowRef window, CancellationToken cancellation);
    Task<ActionResult> Minimize(WindowRef window, CancellationToken cancellation) => Task.FromResult(new ActionResult(ResultKind.Unavailable, "Minimização indisponível nesta plataforma."));
    Task<ActionResult> Shortcut(WindowRef window, string keys);
    Task<ActionResult> Shortcut(WindowRef window, string keys, CancellationToken cancellation, Func<bool> continuing) => cancellation.IsCancellationRequested || !continuing() ? Task.FromResult(new ActionResult(ResultKind.Cancelled, "Cancelado antes do envio.")) : IsPreview ? Shortcut(window, keys) : Task.FromResult(new ActionResult(ResultKind.Unavailable, "Esta plataforma não oferece atalho cancelável."));
    async Task<ActionResult> Focus(WindowRef window, CancellationToken cancellation, Func<bool> continuing)
    {
        if (cancellation.IsCancellationRequested || !continuing())
            return new(ResultKind.Cancelled, "Cancelado antes do foco.");
        var result = await Switch(window);
        return cancellation.IsCancellationRequested || !continuing() ? new(ResultKind.Unknown, "Foco solicitado; observação cancelada.") : result;
    }

    ShortcutDispatch? LastShortcutDispatch => null;

    int SteamOwnedKeys => 0;

    Task<ActionResult> Shortcut(WindowRef window, string keys, CancellationToken cancellation, Func<bool> continuing, Action<ShortcutDispatch> progress) => Shortcut(window, keys, cancellation, continuing);
    Task<ActionResult> TaskView() => Task.FromResult(new ActionResult(ResultKind.Unavailable, "Visão de tarefas indisponível nesta plataforma."));
    bool CanSteamMenu(WindowRef? window) => false;
    Task<ActionResult> SteamMenu(WindowRef window) => Task.FromResult(new ActionResult(ResultKind.Unavailable, "Menu Big Picture não confirmado."));
    Task<ActionResult> SteamMenu(WindowRef window, CancellationToken cancellation, Func<bool> continuing) => cancellation.IsCancellationRequested || !continuing() ? Task.FromResult(new ActionResult(ResultKind.Cancelled, "Cancelado antes do envio.")) : IsPreview ? SteamMenu(window) : Task.FromResult(new ActionResult(ResultKind.Unavailable, "Esta plataforma não oferece menu cancelável."));
    ActionResult Launch(bool playnite, string? path);
}

public interface IWindowIconNotifications
{
    // Raised off the UI thread only after the window identity was revalidated.
    event Action<WindowRef>? IconAvailable;
}

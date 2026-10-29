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
    Task<ActionResult> Minimize(WindowRef window, CancellationToken cancellation) => Task.FromResult(new ActionResult(ResultKind.Unavailable, "Minimizing is unavailable on this platform."));
    Task<ActionResult> Shortcut(WindowRef window, string keys);
    Task<ActionResult> Shortcut(WindowRef window, string keys, CancellationToken cancellation, Func<bool> continuing) => cancellation.IsCancellationRequested || !continuing() ? Task.FromResult(new ActionResult(ResultKind.Cancelled, "Cancelled before dispatch.")) : IsPreview ? Shortcut(window, keys) : Task.FromResult(new ActionResult(ResultKind.Unavailable, "This platform does not support cancellable shortcuts."));
    async Task<ActionResult> Focus(WindowRef window, CancellationToken cancellation, Func<bool> continuing)
    {
        if (cancellation.IsCancellationRequested || !continuing())
            return new(ResultKind.Cancelled, "Cancelled before focus acquisition.");
        var result = await Switch(window);
        return cancellation.IsCancellationRequested || !continuing() ? new(ResultKind.Unknown, "Focus requested; observation cancelled.") : result;
    }

    ShortcutDispatch? LastShortcutDispatch => null;

    int SteamOwnedKeys => 0;

    Task<ActionResult> Shortcut(WindowRef window, string keys, CancellationToken cancellation, Func<bool> continuing, Action<ShortcutDispatch> progress) => Shortcut(window, keys, cancellation, continuing);
    Task<ActionResult> TaskView() => Task.FromResult(new ActionResult(ResultKind.Unavailable, "Task View is unavailable on this platform."));
    bool CanSteamMenu(WindowRef? window) => false;
    Task<ActionResult> SteamMenu(WindowRef window) => Task.FromResult(new ActionResult(ResultKind.Unavailable, "Big Picture menu unconfirmed."));
    Task<ActionResult> SteamMenu(WindowRef window, CancellationToken cancellation, Func<bool> continuing) => cancellation.IsCancellationRequested || !continuing() ? Task.FromResult(new ActionResult(ResultKind.Cancelled, "Cancelled before dispatch.")) : IsPreview ? SteamMenu(window) : Task.FromResult(new ActionResult(ResultKind.Unavailable, "This platform does not support a cancellable menu request."));
    ActionResult Launch(bool playnite, string? path);
}

public interface IWindowIconNotifications
{
    // Raised off the UI thread only after the window identity was revalidated.
    event Action<WindowRef>? IconAvailable;
}

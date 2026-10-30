namespace ApolloOverlay;

public sealed class RecentWindows
{
    private readonly List<WindowRef> windows = [];
    public WindowRef? OpeningForeground
    {
        get;
        private set;
    }
    public IReadOnlyList<WindowRef> Items => windows.ToArray();

    public void Open(WindowRef? foreground, Func<WindowRef, bool> valid)
    {
        OpeningForeground = foreground;
        Observe(foreground, valid);
        Prune(valid);
    }

    public void Observe(WindowRef? target, Func<WindowRef, bool> valid)
    {
        if (target is null || target.Protected || target.Launcher || !valid(target))
            return;
        windows.RemoveAll(w => w.Same(target));
        windows.Insert(0, target);
        if (windows.Count > 5)
            windows.RemoveRange(5, windows.Count - 5);
    }

    public void ConfirmSwitch(WindowRef target, ActionResult result, WindowRef? foreground, Func<WindowRef, bool> valid)
    {
        if (result.Kind == ResultKind.Completed && target.Same(foreground))
        {
            Observe(foreground, valid);
            OpeningForeground = foreground;
        }
    }

    public WindowRef? Previous(Func<WindowRef, bool> valid)
    {
        Prune(valid);
        return windows.FirstOrDefault(w => !w.Same(OpeningForeground));
    }

    public void Prune(Func<WindowRef, bool> valid) => windows.RemoveAll(w => !valid(w));
    public void RefreshTitles(IReadOnlyList<WindowRef> live, Func<WindowRef, bool> valid)
    {
        Prune(valid);
        for (var i = 0; i < windows.Count; i++)
            windows[i] = live.FirstOrDefault(w => w.Same(windows[i])) ?? windows[i];
    }
}

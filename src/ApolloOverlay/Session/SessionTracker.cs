namespace ApolloOverlay;

public sealed class SessionTracker
{
    public WindowRef? Anchor
    {
        get;
        private set;
    }
    public long Sequence
    {
        get;
        private set;
    }

    public void Observe(WindowRef? window)
    {
        Sequence++;
        if (window is null || window.Protected || window.Launcher)
            return;
        Anchor = window;
    }

    public void Select(WindowRef window)
    {
        if (!window.Protected && !window.Launcher)
            Anchor = window;
    }

    public void Clear() => Anchor = null;
    public void ClearIfStale(Func<WindowRef, bool> valid)
    {
        if (Anchor is not null && !valid(Anchor))
            Anchor = null;
    }
}

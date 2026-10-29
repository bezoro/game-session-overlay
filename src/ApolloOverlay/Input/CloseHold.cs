namespace ApolloOverlay;

public sealed class CloseHold
{
    public WindowRef? Target
    {
        get;
        private set;
    }

    private bool released;
    private long? since;
    public double Progress
    {
        get;
        private set;
    }

    public void Open(WindowRef target)
    {
        Target = target;
        released = false;
        since = null;
        Progress = 0;
    }

    public void Cancel()
    {
        Target = null;
        since = null;
        released = false;
        Progress = 0;
    }

    public bool Tick(bool a, bool cancel, bool focused, bool connected, WindowRef? current, long now)
    {
        if (Target is null)
            return false;
        if (cancel || !focused || !connected || !Target.Same(current))
        {
            Cancel();
            return false;
        }

        if (!a)
        {
            released = true;
            since = null;
            Progress = 0;
            return false;
        }

        if (!released)
            return false;
        since ??= now;
        Progress = Math.Clamp((now - since.Value) / 1500d, 0, 1);
        if (Progress < 1)
            return false;
        Cancel();
        return true;
    }
}

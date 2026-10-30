using ApolloOverlay.Automation;

namespace ApolloOverlay;

public readonly record struct SteamKeyReceipt(bool Started, uint? Accepted);
public interface IWindowEffects
{
    bool Valid(WindowRef window);
    WindowObservation ObserveWindow(WindowRef window);
    bool IsForeground(WindowRef window);
    bool RequestFocus(WindowRef window);
    uint SendKeys(string keys);
    bool PostClose(WindowRef window);
    Task Wait(int milliseconds);
    long ShortcutTime => Environment.TickCount64;

    SteamKeyReceipt SendSteamEvent(ushort key, bool up) => new(false, 0);
    bool SteamKeysReleased(int mask) => false;
    void SteamOwnership(int mask)
    {
    }

    void RecordSteamSequence(SteamSequenceEvidence evidence)
    {
    }

    bool SupportsMinimize => false;

    bool IsMinimized(WindowRef window) => false;
    bool RequestMinimize(WindowRef window) => false;
}

public sealed class GuardedActions(IWindowEffects effects, Action<SteamSequenceEvidence>? progress = null)
{
    public async Task<ActionResult> Minimize(WindowRef window, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return new(ResultKind.Cancelled, "Cancelled before dispatch; nothing was minimized.");
        if (!effects.SupportsMinimize)
            return new(ResultKind.Unavailable, "Minimizing is unavailable on this platform.");
        if (window.Protected || window.Handle == 0 || window.Pid <= 0 || window.Started <= 0 || !effects.Valid(window))
            return new(ResultKind.Unavailable, "Invalid identity or protected window; minimizing was blocked.");
        if (effects.IsMinimized(window))
            return new(ResultKind.Completed, "The verified window is already minimized.");
        if (cancellation.IsCancellationRequested || !effects.Valid(window))
            return new(ResultKind.Cancelled, "The target changed before dispatch; nothing was minimized.");
        if (!effects.RequestMinimize(window))
            return new(ResultKind.Failed, "Windows rejected the minimize request.");
        for (var i = 0; i <= 20; i++)
        {
            if (cancellation.IsCancellationRequested)
                return new(ResultKind.Unknown, "Observation cancelled after dispatch; the request cannot be undone.");
            if (!effects.Valid(window))
                return new(ResultKind.Unknown, "The window identity changed after the request; minimizing is unconfirmed.");
            if (effects.IsMinimized(window))
                return new(ResultKind.Completed, "The same window was confirmed as minimized.");
            if (i < 20)
                await effects.Wait(50);
        }

        return new(ResultKind.Unknown, "Minimizing requested; the state was not confirmed within one second. The panel remains available.");
    }

    public async Task<ActionResult> Switch(WindowRef window)
    {
        if (!effects.Valid(window))
            return new(ResultKind.Failed, "The window identity changed or could not be verified. Select it again.")
            {
                Summary = "Select window again."
            };
        if (!effects.RequestFocus(window))
            return new(ResultKind.Failed, "Windows rejected window activation.");
        for (var i = 0; i < 6; i++)
        {
            if (!effects.Valid(window))
                return new(ResultKind.Failed, "The window was replaced during activation.");
            if (effects.IsForeground(window))
                return new(ResultKind.Completed, "The selected window is in the foreground.");
            await effects.Wait(50);
        }

        return new(ResultKind.Unknown, "Activation unconfirmed; no shortcut was sent.");
    }

    public Task<ActionResult> Shortcut(WindowRef window, string keys) => Shortcut(window, keys, CancellationToken.None, () => true);
    public async Task<ActionResult> SteamMenu(WindowRef window, CancellationToken cancellation, Func<bool> continuing, Func<bool> canUse)
    {
        bool Allowed() => !cancellation.IsCancellationRequested && continuing() && canUse();
        if (!Allowed())
            return new(ResultKind.Cancelled, "Context or request unavailable; no F1 was sent.");
        if (!effects.RequestFocus(window))
            return new(ResultKind.Failed, "Windows rejected Big Picture focus.");
        for (var i = 0; i < 6; i++)
        {
            if (!Allowed())
                return new(ResultKind.Cancelled, "Focus was already requested; F1 was cancelled before dispatch.");
            if (effects.IsForeground(window))
            {
                if (!Allowed() || !effects.IsForeground(window))
                    return new(ResultKind.Cancelled, "The target changed before F1.");
                return Outcome.Keys(effects.SendKeys("F1"), 2);
            }

            await effects.Wait(50);
        }

        return new(ResultKind.Unknown, "Big Picture focus unconfirmed; no F1 was sent.");
    }

    private async Task<ActionResult> SteamSequence(WindowRef window, CancellationToken cancellation, Func<bool> continuing)
    {
        var events = new List<SteamKeyEvent>(6);
        var start = effects.ShortcutTime;
        var held = 0;
        var interrupted = false;
        var uncertain = false;
        var logFailed = false;
        var refused = false;
        bool Allowed() => !cancellation.IsCancellationRequested && !logFailed && effects.ShortcutTime - start < 10000 && continuing() && effects.Valid(window) && effects.IsForeground(window);
        bool Released(int mask)
        {
            try
            {
                return effects.SteamKeysReleased(mask);
            }
            catch (Exception)
            {
                uncertain = true;
                return false;
            }
        }

        void Publish(bool? released = null)
        {
            var evidence = new SteamSequenceEvidence("Shift+Tab", [20, 60, 20], 500, events.ToArray(), held, released);
            try
            {
                effects.RecordSteamSequence(evidence);
                if (!logFailed)
                    progress?.Invoke(evidence);
            }
            catch (Exception)
            {
                logFailed = true;
                uncertain = true;
            }
        }

        bool Send(ushort key, bool up, bool cleanup = false)
        {
            if (!up && !Allowed())
            {
                interrupted = true;
                return false;
            }

            SteamKeyReceipt receipt;
            try
            {
                receipt = effects.SendSteamEvent(key, up);
            }
            catch (Exception)
            {
                receipt = new(true, null);
            }

            var bit = key == 16 ? 1 : 2;
            // Started is reported by the adapter only at the actual native-call boundary.
            if (!up && receipt.Started && receipt.Accepted != 0)
                held |= bit;
            if (receipt.Accepted is null || receipt.Accepted > 1)
                uncertain = true;
            if (cleanup && (!receipt.Started || receipt.Accepted != 1))
                uncertain = true;
            if (up && receipt.Accepted == 1 && Released(bit))
                held &= ~bit;
            effects.SteamOwnership(held);
            events.Add(new(key, up, cleanup, effects.ShortcutTime - start, receipt.Started, receipt.Accepted));
            Publish();
            if (!receipt.Started || receipt.Accepted != 1)
            {
                refused = true;
                return false;
            }

            return !logFailed;
        }

        async Task<bool> Pause(int milliseconds)
        {
            var until = effects.ShortcutTime + milliseconds;
            while (effects.ShortcutTime < until)
            {
                if (!Allowed())
                {
                    interrupted = true;
                    return false;
                }

                await effects.Wait((int)Math.Min(10, until - effects.ShortcutTime));
                if (!Allowed())
                {
                    interrupted = true;
                    return false;
                }
            }

            return true;
        }

        Publish();
        try
        {
            if (Send(16, false) && await Pause(20) && Send(9, false) && await Pause(60) && Send(9, true) && await Pause(20))
                Send(16, true);
        }
        catch (Exception)
        {
            uncertain = true;
        }
        finally
        {
            foreach (var key in new ushort[]
            {
                9,
                16
            }

            )
            {
                var bit = key == 16 ? 1 : 2;
                if ((held & bit) != 0)
                    try
                    {
                        Send(key, true, true);
                    }
                    catch (Exception)
                    {
                        uncertain = true;
                    }
            }

            var until = effects.ShortcutTime + 100;
            while (held != 0 && effects.ShortcutTime < until)
            {
                if (Released(held))
                {
                    held = 0;
                    break;
                }

                try
                {
                    await effects.Wait(10);
                }
                catch (Exception)
                {
                    uncertain = true;
                    break;
                }
            }

            effects.SteamOwnership(held);
            Publish(held == 0);
        }

        var normal = events.Where(e => !e.Cleanup).ToArray();
        var accepted = normal.Where(e => e.Accepted.HasValue).Sum(e => (long)e.Accepted!.Value);
        if (uncertain || held != 0)
            return new(ResultKind.Unknown, "Sequence or key release unconfirmed; check the state before toggling again.");
        if (accepted == 4 && !refused && !interrupted)
            return new(ResultKind.Requested, "Shift+Tab requested; keys released. Steam visibility is unconfirmed.");
        if (accepted > 0)
            return new(ResultKind.Partial, "Sequence interrupted after partial dispatch; keys released. Check the state before trying again.");
        return new(interrupted ? ResultKind.Cancelled : ResultKind.Failed, "Sequence interrupted with no accepted events.");
    }

    public async Task<ActionResult> FocusStable(WindowRef window, CancellationToken cancellation, Func<bool> continuing)
    {
        bool Allowed() => !cancellation.IsCancellationRequested && continuing() && effects.Valid(window);
        if (!Allowed())
            return new(ResultKind.Cancelled, "Cancelled before requesting focus.");
        if (!effects.RequestFocus(window))
            return new(ResultKind.Failed, "Windows rejected focus; no shortcut was sent.");
        if (!effects.Valid(window))
            return new(ResultKind.Failed, "The window was replaced during activation; no shortcut was sent.");
        var confirmed = false;
        for (var i = 0; i <= 6; i++)
        {
            if (!Allowed())
                return new(ResultKind.Cancelled, "The target or request changed during focus acquisition; no shortcut was sent.");
            if (effects.IsForeground(window))
            {
                confirmed = true;
                break;
            }

            if (i < 6)
            {
                await effects.Wait(50);
                if (!Allowed())
                    return new(ResultKind.Cancelled, "Focus cancelled; no shortcut was sent.");
            }
        }

        if (!confirmed)
            return new(ResultKind.Unknown, "Focus was not confirmed within 300 ms; no shortcut was sent.");
        for (var i = 0; i < 10; i++)
        {
            if (!Allowed() || !effects.IsForeground(window))
                return new(ResultKind.Cancelled, "Focus or input changed; no shortcut was sent.");
            await effects.Wait(50);
            if (!Allowed() || !effects.IsForeground(window))
                return new(ResultKind.Cancelled, "Focus or input changed; no shortcut was sent.");
        }

        return new(ResultKind.Completed, "The same window remained in the foreground for 500 ms.");
    }

    public async Task<ActionResult> Shortcut(WindowRef window, string keys, CancellationToken cancellation, Func<bool> continuing)
    {
        if (keys is not ("Shift+Tab" or "Ctrl+Tab"))
        {
            if (cancellation.IsCancellationRequested || !continuing())
                return new(ResultKind.Cancelled, "Cancelled before focus acquisition; no shortcut was sent.");
            var focus = await Switch(window);
            if (cancellation.IsCancellationRequested || !continuing())
                return new(ResultKind.Cancelled, "Focus was already requested; shortcut cancelled before dispatch.");
            if (focus.Kind != ResultKind.Completed)
                return focus;
            if (!effects.Valid(window) || !effects.IsForeground(window))
                return new(ResultKind.Failed, "The target changed before dispatch; shortcut blocked.");
            return Outcome.Keys(effects.SendKeys(keys), 4);
        }

        var focused = await FocusStable(window, cancellation, continuing);
        if (focused.Kind != ResultKind.Completed)
            return focused;
        if (cancellation.IsCancellationRequested || !continuing() || !effects.Valid(window) || !effects.IsForeground(window))
            return new(ResultKind.Cancelled, "Cancelled before dispatch; no shortcut was sent.");
        if (keys == "Shift+Tab")
            return await SteamSequence(window, cancellation, continuing);
        return Outcome.Keys(effects.SendKeys(keys), 4);
    }

    public async Task<ActionResult> Close(WindowRef window, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return new(ResultKind.Cancelled, "Cancelled before dispatch.");
        if (!window.CanClose || !effects.Valid(window))
            return new(ResultKind.Failed, "Invalid identity or protected window; closing was blocked.");
        if (!effects.PostClose(window))
            return new(ResultKind.Failed, "Windows rejected WM_CLOSE; no escalation was attempted.");
        for (var i = 0; i < 20; i++)
        {
            if (effects.ObserveWindow(window) == WindowObservation.GoneOrReplaced)
                return Outcome.Closure(true, false);
            if (cancellation.IsCancellationRequested)
                return new(ResultKind.Unknown, "Observation cancelled after dispatch; the request cannot be undone.");
            await effects.Wait(100);
        }

        return Outcome.Closure(false, true);
    }
}

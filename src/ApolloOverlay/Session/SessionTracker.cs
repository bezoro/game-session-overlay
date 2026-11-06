namespace ApolloOverlay;

public readonly record struct SessionTargetCandidate(WindowRef Window, bool LauncherDescendant = false, bool ResumeOnly = false);

public sealed class SessionTracker
{
    private readonly List<WindowRef> foregroundHistory = [];
    private bool explicitCardChoice;
    private long cardRevision;
    public WindowRef? CardTarget
    {
        get; private set;
    }
    internal bool CardPending
    {
        get; private set;
    }
    internal bool CardResumeOnly
    {
        get; private set;
    }
    internal string? CardDisplayId
    {
        get; private set;
    }
    internal WindowRef[] ForegroundHistory => foregroundHistory.ToArray();

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

    public void Observe(WindowRef? window, Func<WindowRef, bool>? valid = null)
    {
        Sequence++;
        explicitCardChoice = false;
        CancelCardResolution();
        if (window is { Protected: false } && valid?.Invoke(window) != false)
        {
            foregroundHistory.RemoveAll(window.Same);
            foregroundHistory.Insert(0, window);
            if (foregroundHistory.Count > 5)
                foregroundHistory.RemoveAt(5);
            if (!window.Launcher && !window.SessionLauncher)
                Anchor = window;
        }

        SetCard(foregroundHistory.FirstOrDefault());
    }

    public void Select(WindowRef window)
    {
        if (window.Protected || window.Launcher && !window.SessionLauncher)
            return;
        explicitCardChoice = true;
        CancelCardResolution();
        SetCard(window);
        if (!window.SessionLauncher)
            Anchor = window;
    }

    public void Clear()
    {
        explicitCardChoice = true;
        CancelCardResolution();
        SetCard(null);
        Anchor = null;
    }

    public void ClearIfStale(Func<WindowRef, bool> valid, Func<WindowRef, bool>? cardValid = null)
    {
        if (Anchor is not null && !valid(Anchor))
            Anchor = null;
        foregroundHistory.RemoveAll(window => !valid(window));
        if (CardTarget is { } card && !(cardValid?.Invoke(card) ?? valid(card)))
            SetCard(explicitCardChoice ? null : foregroundHistory.FirstOrDefault());
    }

    internal long? BeginCardResolution()
    {
        if (explicitCardChoice)
            return null;
        CardPending = true;
        return ++cardRevision;
    }

    internal void CancelCardResolution()
    {
        cardRevision++;
        CardPending = false;
    }

    internal bool ResolvingCard(long revision) => CardPending && revision == cardRevision;

    internal void SettleCard(long revision, SessionTargetCandidate? candidate, string? displayId)
    {
        if (!ResolvingCard(revision))
            return;
        CardPending = false;
        if (candidate is { } chosen)
            SetCard(chosen.Window, chosen.ResumeOnly, displayId);
    }

    private void SetCard(WindowRef? window, bool resumeOnly = false, string? displayId = null)
    {
        CardTarget = window;
        CardResumeOnly = resumeOnly;
        CardDisplayId = displayId;
    }

    private static readonly HashSet<string> NonGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "msedgewebview2", "firefox", "brave", "opera", "vivaldi", "chromium", "ChatGPT",
        "steamwebhelper", "steamservice", "EpicWebHelper", "GalaxyClient Helper", "GalaxyCommunication", "GalaxyOverlay",
        "CrashReportClient", "UnityCrashHandler32", "UnityCrashHandler64", "crashpad_handler", "WerFault",
        "UnrealCEFSubProcess", "Updater", "Update", "Squirrel"
    };

    internal static bool PossibleGame(WindowRef window) => !window.Protected && !window.Launcher && !window.SessionLauncher && !NonGames.Contains(window.ProcessName);

    internal static SessionTargetCandidate? ChooseCard(IReadOnlyList<SessionTargetCandidate> candidates, WindowRef? foreground,
        IReadOnlyList<WindowRef> history, IReadOnlySet<string> profileKeys)
    {
        var uniqueProfiles = candidates.Where(c => PossibleGame(c.Window) && profileKeys.Contains(c.Window.ProcessName))
            .GroupBy(c => c.Window.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var games = candidates.Where(c => PossibleGame(c.Window) && (c.LauncherDescendant || uniqueProfiles.Contains(c.Window.ProcessName)));
        return Prefer(games, foreground, history) ?? ChooseLauncher(candidates, foreground, history);
    }

    internal static SessionTargetCandidate? ChooseLauncher(IReadOnlyList<SessionTargetCandidate> candidates, WindowRef? foreground, IReadOnlyList<WindowRef> history)
    {
        var launchers = candidates.Where(c => c.ResumeOnly ? c.Window.Same(foreground) : !c.Window.Protected && c.Window.SessionLauncher);
        return Prefer(launchers, foreground, history);
    }

    private static SessionTargetCandidate? Prefer(IEnumerable<SessionTargetCandidate> candidatesInTier, WindowRef? foreground, IReadOnlyList<WindowRef> history)
    {
        var ordered = candidatesInTier.ToArray();
        foreach (var candidate in ordered)
            if (candidate.Window.Same(foreground))
                return candidate;
        foreach (var previous in history)
            foreach (var candidate in ordered)
                if (candidate.Window.Same(previous))
                    return candidate;
        return ordered.Length > 0 ? ordered[0] : null;
    }
}

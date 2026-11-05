using ApolloOverlay;

internal static class SessionTargetTests
{
    private static readonly WindowRef Game = new(201, 21, 200, "Game", "game");
    private static readonly WindowRef Launcher = new(202, 22, 100, "Launcher", "Playnite.FullscreenApp");
    private static readonly WindowRef Ordinary = new(203, 23, 300, "ChatGPT", "ChatGPT");
    private static readonly IReadOnlySet<string> NoProfiles = new HashSet<string>();

    internal static void Run(Action<string, Action> test)
    {
        foreach (var launcher in new[] { "Playnite.FullscreenApp", "Playnite.DesktopApp", "steam", "EpicGamesLauncher", "GalaxyClient" })
            test("session card promotes coherent unprofiled game ancestry from " + launcher, () =>
            {
                var parent = Launcher with { ProcessName = launcher };
                var games = Descendants([Game], parent);
                Require(games.Contains(Game), "Launcher ancestry was not recognized");
                var target = SessionTracker.ChooseCard([new(Ordinary), new(parent), new(Game, games.Contains(Game))], parent, [Ordinary], NoProfiles);
                Require(target?.Window.Same(Game) == true, "Running game did not take precedence over the foreground launcher");
            });

        test("session card prefers verified launcher and leaves unknown game to foreground fallback", () =>
        {
            Require(SessionTracker.ChooseCard([new(Ordinary), new(Game), new(Launcher)], Ordinary, [], NoProfiles)?.Window.Same(Launcher) == true,
                "An ordinary foreground or unclassified game hid the launcher");
            Require(SessionTracker.ChooseCard([new(Game), new(Ordinary)], Ordinary, [], NoProfiles) is null,
                "An unknown game was promoted without positive evidence");
        });

        test("session card requires a unique exact profile game match and ranks tier ties", () =>
        {
            var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GAME" };
            var second = Game with { Handle = 204, Pid = 24 };
            Require(SessionTracker.ChooseCard([new(Launcher), new(Game)], Launcher, [], profiles)?.Window.Same(Game) == true,
                "Exact case-insensitive profile match was ignored");
            Require(SessionTracker.ChooseCard([new(Launcher), new(Game), new(second)], Launcher, [], profiles)?.Window.Same(Launcher) == true,
                "Ambiguous profile promoted a game");
            SessionTargetCandidate[] games = [new(Game, true), new(second, true)];
            Require(SessionTracker.ChooseCard(games, Game, [second], NoProfiles)?.Window.Same(Game) == true, "Foreground did not break the tier tie");
            Require(SessionTracker.ChooseCard(games, Launcher, [second], NoProfiles)?.Window.Same(second) == true, "History did not break the tier tie");
            Require(SessionTracker.ChooseCard(games, Launcher, [], NoProfiles)?.Window.Same(Game) == true, "Native order did not break the tier tie");
        });

        test("session card rejects browser and helper ancestry, protected and reused identities", () =>
        {
            foreach (var name in new[] { "chrome", "EpicWebHelper", "GalaxyClient Helper", "UnityCrashHandler64", "Updater", "ChatGPT" })
                Require(Descendants([Game with { ProcessName = name }], Launcher).Count == 0, "False game promotion for " + name);
            Require(Descendants([Game with { Protected = true }], Launcher).Count == 0, "Protected window promoted");
            Require(Descendants([Game], Launcher with { Started = Game.Started + 1 }).Count == 0, "Reused parent PID promoted a game");
            Require(Descendants([Game], Launcher, parentSession: 3).Count == 0, "Different-session ancestry promoted a game");
            Require(Descendants([Game], Launcher, childStart: Game.Started + 1).Count == 0, "Changed game identity promoted a game");
        });

        test("only exact captured verified Big Picture can enter the protected Resume tier", () =>
        {
            var bigPicture = Launcher with { Protected = true, ProcessName = "steamwebhelper", Title = "Steam Big Picture Mode" };
            Require(SessionTracker.ChooseCard([new(bigPicture)], bigPicture, [], NoProfiles) is null, "Unverified protected window promoted");
            Require(SessionTracker.ChooseCard([new(bigPicture, ResumeOnly: true)], Ordinary, [], NoProfiles) is null, "Non-foreground Big Picture promoted");
            Require(SessionTracker.ChooseCard([new(bigPicture, ResumeOnly: true)], bigPicture, [], NoProfiles)?.ResumeOnly == true,
                "Exact Big Picture lost the Resume-only capability");
        });
    }

    private static HashSet<WindowRef> Descendants(WindowRef[] windows, WindowRef parent, int parentSession = 2, long? childStart = null)
    {
        var child = windows[0];
        var snapshot = new SteamProcessSnapshot([new((uint)child.Pid, (uint)parent.Pid, child.ProcessName + ".exe"), new((uint)parent.Pid, 0, parent.ProcessName + ".exe")], true);
        return SteamLaunchContext.LauncherDescendants(windows, snapshot, pid => pid == child.Pid
            ? new(pid, child.ProcessName + ".exe", childStart ?? child.Started, 2)
            : new(pid, parent.ProcessName + ".exe", parent.Started, parentSession), CancellationToken.None);
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }
}

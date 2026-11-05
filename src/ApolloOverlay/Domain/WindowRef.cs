namespace ApolloOverlay;

public sealed record WindowRef(nint Handle, int Pid, long Started, string Title, string ProcessName, bool Protected = false, long Generation = 0)
{
    public bool Same(WindowRef? other) => other is not null && Handle == other.Handle && Pid == other.Pid && Started == other.Started && Generation == other.Generation;
    public bool CanClose => Handle != 0 && Pid > 0 && Started > 0 && !Protected && !Launcher;
    public bool Launcher => ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase) || ProcessName.StartsWith("Playnite", StringComparison.OrdinalIgnoreCase);
    internal bool SessionLauncher => IsSessionLauncher(ProcessName);
    internal static bool IsSessionLauncher(string name) => name.Equals("steam", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Playnite.FullscreenApp", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Playnite.DesktopApp", StringComparison.OrdinalIgnoreCase)
        || name.Equals("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase)
        || name.Equals("GalaxyClient", StringComparison.OrdinalIgnoreCase);
}

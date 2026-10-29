namespace ApolloOverlay;

public enum NavDirection
{
    None = 0,
    Up = -1,
    Down = 1,
    Left = -2,
    Right = 2
}

public readonly record struct NavState(string Id, int GridColumn = 0);
public static class Navigation
{
    public static readonly string[] HomeIds = ["settings", "return", "playnite", "steam", "apps", "gamebar", "steam-overlay", "taskview", "audio", "close"];
    public static NavState Home(NavState state, NavDirection direction)
    {
        var id = state.Id;
        var column = state.GridColumn;
        var grid = id is "gamebar" or "steam-overlay" or "taskview" or "audio";
        if (grid)
            column = id is "steam-overlay" or "audio" ? 1 : 0;
        if (direction is NavDirection.Left or NavDirection.Right)
        {
            if (!grid)
                return state;
            column = direction == NavDirection.Left ? 0 : 1;
            return new(id is "gamebar" or "steam-overlay" ? column == 0 ? "gamebar" : "steam-overlay" : column == 0 ? "taskview" : "audio", column);
        }

        if (direction == NavDirection.Up)
            id = id switch
            {
                "return" => "settings",
                "playnite" => "return",
                "steam" => "playnite",
                "apps" => "steam",
                "gamebar" or "steam-overlay" => "apps",
                "taskview" => "gamebar",
                "audio" => "steam-overlay",
                "close" => column == 0 ? "taskview" : "audio",
                _ => id
            };
        if (direction == NavDirection.Down)
            id = id switch
            {
                "settings" => "return",
                "return" => "playnite",
                "playnite" => "steam",
                "steam" => "apps",
                "apps" => column == 0 ? "gamebar" : "steam-overlay",
                "gamebar" => "taskview",
                "steam-overlay" => "audio",
                "taskview" or "audio" => "close",
                _ => id
            };
        return new(id, column);
    }

    public static NavState Linear(NavState state, IReadOnlyList<string> ids, NavDirection direction)
    {
        var index = Index(ids, state.Id);
        if (direction == NavDirection.Up)
            index = Math.Max(0, index - 1);
        if (direction == NavDirection.Down)
            index = Math.Min(ids.Count - 1, index + 1);
        return ids.Count == 0 ? state : state with
        {
            Id = ids[index]
        };
    }

    public static NavState Sequential(NavState state, IReadOnlyList<string> ids, int step) => ids.Count == 0 ? state : state with
    {
        Id = ids[(Index(ids, state.Id) + step + ids.Count) % ids.Count]
    };
    public static string Spatial(string selected, IReadOnlyList<(string Id, double X, double Y)> visible, NavDirection direction)
    {
        if (visible.Count == 0)
            return selected;
        var origin = visible.FirstOrDefault(p => p.Id == selected);
        if (origin.Id is null)
            return visible[0].Id;
        if (direction == NavDirection.None)
            return selected;
        var vertical = direction is NavDirection.Up or NavDirection.Down;
        var sign = direction is NavDirection.Up or NavDirection.Left ? -1 : 1;
        if (vertical)
        {
            // Select the nearest visual row first. A large horizontal offset must not
            // make the controller skip a full-width row for a later aligned tile.
            // Small center differences belong to the current row, not the next one.
            var candidates = visible.Where(p => p.Id != selected && (p.Y - origin.Y) * sign > 20).ToArray();
            if (candidates.Length == 0)
                return selected;
            var nearest = candidates.Min(p => Math.Abs(p.Y - origin.Y));
            return candidates.Where(p => Math.Abs(p.Y - origin.Y) <= nearest + 20).OrderBy(p => Math.Abs(p.X - origin.X)).ThenBy(p => Math.Abs(p.Y - origin.Y)).Select(p => p.Id).First();
        }

        return visible.Where(p => p.Id != selected && (p.X - origin.X) * sign > 1 && Math.Abs(p.Y - origin.Y) <= 20).OrderBy(p => Math.Abs(p.X - origin.X) + Math.Abs(p.Y - origin.Y) * 2).Select(p => p.Id).FirstOrDefault() ?? selected;
    }

    public static string Preserve(string selected, IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        if (current.Count == 0)
            return "";
        if (current.Contains(selected))
            return selected;
        return current[Math.Min(Index(previous, selected), current.Count - 1)];
    }

    public static int NextAppPage(int offset, int count) => offset + 4 >= count ? 0 : offset + 4;
    public static bool CanActivate(bool busy, bool confirming, bool available) => !busy && !confirming && available;
    private static int Index(IReadOnlyList<string> ids, string id)
    {
        for (var i = 0; i < ids.Count; i++)
            if (ids[i] == id)
                return i;
        return 0;
    }
}

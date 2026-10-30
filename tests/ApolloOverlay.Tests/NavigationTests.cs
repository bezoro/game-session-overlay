using ApolloOverlay;

internal static class NavigationTests
{
    private static void Require(bool condition)
    {
        if (!condition)
            throw new Exception("Fluent navigation assertion failed.");
    }

    private static void Is(string expected, NavState state) => Require(state.Id == expected);
    internal static void Run(Action<string, Action> test)
    {
        test("horizontal dpad and stick produce spatial moves", () =>
        {
            var p = new PadInterpreter();
            p.Update(new(true, 0), 0);
            Require(p.Update(new(true, PadButtons.Right), 1).Direction == 2);
            p.Update(new(true, 0), 2);
            Require(p.Update(new(true, 0, -24000, 0), 3).Direction == -2);
        });
        test("stick diagonal uses dominant axis and vertical tie", () =>
        {
            var p = new PadInterpreter();
            p.Update(new(true, 0), 0);
            Require(p.Update(new(true, 0, 25000, 21000), 1).Direction == 2);
            p.Update(new(true, 0), 2);
            Require(p.Update(new(true, 0, 24000, 24000), 3).Direction == -1);
        });
        test("horizontal repeat stays bounded and neutral resets it", () =>
        {
            var p = new PadInterpreter();
            p.Update(new(true, 0), 0);
            Require(p.Update(new(true, PadButtons.Left), 1).Direction == -2);
            Require(p.Update(new(true, PadButtons.Left), 200).Direction == 0);
            Require(p.Update(new(true, PadButtons.Left), 421).Direction == -2);
            p.Update(new(true, 0), 422);
            Require(p.Update(new(true, PadButtons.Left), 423).Direction == -2);
        });
        test("held horizontal stick cannot arm after reconnect", () =>
        {
            var p = new PadInterpreter();
            p.Update(default, 0);
            Require(p.Update(new(true, 0, 26000, 0), 1).Direction == 0);
            p.Update(new(true, 0), 2);
            Require(p.Update(new(true, 0, 26000, 0), 3).Direction == 2);
        });
        test("home grid edges never wrap rows", () =>
        {
            foreach (var row in new[]
            {
                ("gamebar", "steam-overlay"),
                ("taskview", "audio")
            }

            )
            {
                Is(row.Item1, Navigation.Home(new(row.Item1), NavDirection.Left));
                Is(row.Item2, Navigation.Home(new(row.Item1), NavDirection.Right));
                Is(row.Item2, Navigation.Home(new(row.Item2), NavDirection.Right));
                Is(row.Item1, Navigation.Home(new(row.Item2), NavDirection.Left));
            }
        });
        test("grid up and down preserve column", () =>
        {
            Is("taskview", Navigation.Home(new("gamebar"), NavDirection.Down));
            Is("audio", Navigation.Home(new("steam-overlay", 1), NavDirection.Down));
            Is("gamebar", Navigation.Home(new("taskview"), NavDirection.Up));
            Is("steam-overlay", Navigation.Home(new("audio", 1), NavDirection.Up));
        });
        test("list enters remembered grid column", () =>
        {
            var state = Navigation.Home(new("steam-overlay", 1), NavDirection.Up);
            Is("apps", state);
            Is("steam-overlay", Navigation.Home(state, NavDirection.Down));
        });
        test("close returns to remembered grid column", () =>
        {
            foreach (var pair in new[]
            {
                ("taskview", 0),
                ("audio", 1)
            }

            )
            {
                var state = Navigation.Home(new(pair.Item1, pair.Item2), NavDirection.Down);
                Is("close", state);
                Is(pair.Item1, Navigation.Home(state, NavDirection.Up));
                Is("close", Navigation.Home(state, NavDirection.Left));
            }
        });
        test("home graph reaches every action including settings", () =>
        {
            var seen = new HashSet<string>();
            var queue = new Queue<NavState>();
            queue.Enqueue(new("return"));
            while (queue.Count > 0)
            {
                var state = queue.Dequeue();
                if (!seen.Add(state.Id))
                    continue;
                foreach (var direction in new[]
                {
                    NavDirection.Up,
                    NavDirection.Down,
                    NavDirection.Left,
                    NavDirection.Right
                }

                )
                    queue.Enqueue(Navigation.Home(state, direction));
            }

            Require(Navigation.HomeIds.All(seen.Contains));
        });
        test("sequential fallback reaches unavailable tiles", () =>
        {
            var state = new NavState("return");
            var seen = new HashSet<string>();
            for (var i = 0; i < Navigation.HomeIds.Length; i++)
            {
                seen.Add(state.Id);
                state = Navigation.Sequential(state, Navigation.HomeIds, 1);
            }

            Require(seen.Contains("audio") && seen.Contains("steam-overlay") && seen.Contains("settings"));
        });
        test("spatial vertical navigation does not skip an intervening full-width row", () =>
        {
            (string Id, double X, double Y)[] items = [("left", 100, 100), ("full", 300, 160), ("later", 100, 240)];
            Require(Navigation.Spatial("left", items, NavDirection.Down) == "full");
            Require(Navigation.Spatial("later", items, NavDirection.Up) == "full");
        });
        test("spatial vertical navigation preserves the column within the nearest row", () =>
        {
            (string Id, double X, double Y)[] items = [("right", 300, 100), ("left-next", 100, 180), ("right-next", 300, 185), ("right-later", 300, 260)];
            Require(Navigation.Spatial("right", items, NavDirection.Down) == "right-next");
        });
        test("spatial vertical navigation ignores minor center offsets on the current row", () =>
        {
            (string Id, double X, double Y)[] items = [("card", 100, 100), ("options", 300, 108), ("next", 200, 170)];
            Require(Navigation.Spatial("card", items, NavDirection.Down) == "next");
            Require(Navigation.Spatial("card", items, NavDirection.None) == "card");
        });
        test("stable refresh preserves ID and nearby fallback", () =>
        {
            string[] before = ["settings", "a", "b", "c"];
            Require(Navigation.Preserve("b", before, ["settings", "b", "a"]) == "b");
            Require(Navigation.Preserve("b", before, ["settings", "a", "c"]) == "c");
        });
        test("apps pagination returns to first page without overlap", () =>
        {
            Require(Navigation.NextAppPage(0, 7) == 4);
            Require(Navigation.NextAppPage(4, 7) == 0);
            Require(Navigation.NextAppPage(0, 0) == 0);
        });
        test("unavailable busy and confirming actions cannot dispatch", () =>
        {
            Require(!Navigation.CanActivate(false, false, false));
            Require(!Navigation.CanActivate(true, false, true));
            Require(!Navigation.CanActivate(false, true, true));
            Require(Navigation.CanActivate(false, false, true));
        });
        test("sidebar physical placement leaves game visible at three scales", () =>
        {
            foreach (var d in new[]
            {
                new DisplayRef("720", 0, 0, 1280, 720, true),
                new("1080", -1920, 0, 1920, 1080, false),
                new("4k", 0, -2160, 3840, 2160, false)
            }

            )
            {
                var b = SidebarLayout.Place(d);
                Require(b.X >= d.X && b.Y >= d.Y && b.X + b.Width <= d.X + d.Width && b.Y + b.Height <= d.Y + d.Height && b.Width < d.Width * .4);
            }
        });
        ProductTests.Run(test);
    }
}

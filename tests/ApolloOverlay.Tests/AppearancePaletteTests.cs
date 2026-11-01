using System.Text.Json;
using ApolloOverlay;

internal static class AppearancePaletteTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("every appearance preset resolves a complete readable dark and light palette", () =>
        {
            foreach (var preset in AppearanceCatalog.Presets)
                foreach (var light in new[] { false, true })
                {
                    var palette = AppearanceCatalog.Resolve(new(Preset: preset.Id), light);
                    Require(AppearanceCatalog.TryValidate(palette, out var reason), preset.Id + " has unreadable contrast: " + reason);
                    foreach (var role in AppearanceCatalog.Roles)
                        Require(AppearanceCatalog.TryColor(palette.Get(role.Role), out _), "A preset has no actual color for " + role.Name);
                }
        });
        test("built-in focus remains distinct from routine controls and input feedback", () =>
        {
            var ambientRoles = new[] { PaletteRole.Accent, PaletteRole.SliderActive, PaletteRole.ToggleOn, PaletteRole.Caret,
                PaletteRole.TextSelection, PaletteRole.PromptGuide, PaletteRole.Hover, PaletteRole.Press };
            foreach (var preset in AppearanceCatalog.Presets)
                foreach (var light in new[] { false, true })
                {
                    var palette = AppearanceCatalog.Resolve(new(Preset: preset.Id), light);
                    foreach (var role in ambientRoles)
                        Require(palette.Get(role) != palette.Focus, preset.Id + " reuses its focus color for " + role);
                    Require(palette.IconNavigation != palette.IconTools && palette.IconAudio != palette.IconTools
                        && palette.IconGame != palette.IconAudio, "Semantic action families lost their distinct colors");
                }
        });
        test("custom warm control colors survive default palette refinement and JSON normalization", () =>
        {
            var custom = AppearanceCatalog.Resolve(new(), false) with
            {
                FocusBackground = "#3A2E24",
                Accent = "#E8AE74",
                SliderActive = "#E8AE74",
                IconAudio = "#DABD8D",
                PromptGuide = "#E8AE74",
                Hover = "#E8AE74",
                Press = "#E8AE74"
            };
            Require(AppearanceCatalog.TryValidate(custom, out var reason), "The custom fixture must remain readable: " + reason);
            var source = AppearanceCatalog.Normalize(new(Dark: custom, Light: new ColorPalette { Shadow = "#141516" }));
            var decoded = AppearanceCatalog.ReadJson(JsonSerializer.SerializeToElement(source), out var recovered);
            Require(!recovered && AppearanceCatalog.Resolve(decoded, false) == custom,
                "Changing built-in defaults replaced explicit saved control colors");
            Require(AppearanceCatalog.Resolve(decoded, true).Shadow == "#141516" && source.Dark == custom,
                "Preserving one custom palette changed another variant or mutated its source");
        });
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }
}

using System.IO;
using ApolloOverlay;

internal static class InputSettingsMigrationTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("retired opening settings preserve valid rollback bindings in every supported schema", () =>
        {
            foreach (var version in new[] { 1, 2, 3 })
            {
                var settings = UserSettings.Parse("{\"Version\":" + version + ",\"Hotkey\":\"Alt+Shift+F\",\"Chord\":\"A+RB\",\"ChordHoldMs\":1100,\"Deadzone\":23000,\"GuideXInputEnabled\":false}");
                Require(settings.Hotkey == "Alt+Shift+F" && settings.Chord == "A+RB" && settings.ChordHoldMs == 1100
                    && settings.Deadzone == 23000 && !settings.GuideXInputEnabled, "Valid retired fields or active preferences were lost during migration");
            }
        });
        test("malformed retired opening fields recover individually without discarding active settings", () =>
        {
            foreach (var legacy in new[]
            {
                "\"Hotkey\":null", "\"Hotkey\":[]", "\"Hotkey\":\"Space\"", "\"Hotkey\":\"Ctrl+Ctrl+A\"",
                "\"Chord\":false", "\"Chord\":{}", "\"Chord\":\"Guide\"", "\"Chord\":\"A+A\"",
                "\"ChordHoldMs\":\"650\"", "\"ChordHoldMs\":0", "\"ChordHoldMs\":999999999999"
            })
            {
                var settings = UserSettings.Parse("{" + legacy + ",\"Deadzone\":23000,\"GuideXInputEnabled\":false,\"SoundFeedbackEnabled\":true,\"UiScalePercent\":115}");
                Require(settings.Deadzone == 23000 && !settings.GuideXInputEnabled && settings.SoundFeedbackEnabled && settings.UiScalePercent == 115,
                    "An obsolete binding discarded active settings: " + legacy);
                Require(settings.Hotkey == "Ctrl+Alt+Space" && settings.Chord == "LB+RB+Start" && settings.ChordHoldMs == 650,
                    "Malformed retired fields did not recover their safe rollback defaults: " + legacy);
            }
        });
        test("legacy recovery preserves unrelated valid rollback fields and saves the normalized configuration", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "OverlayLegacyInput", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "settings.json");
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(path, "{\"Hotkey\":null,\"Chord\":\"A+RB\",\"ChordHoldMs\":1100,\"Deadzone\":23000,\"GuideXInputEnabled\":false}");
                File.WriteAllText(path + ".previous", "{\"Deadzone\":8000}");
                var settings = UserSettings.Load(path, out var message);
                Require(message.Contains("recovered") && settings.Deadzone == 23000 && !settings.GuideXInputEnabled
                    && settings.Hotkey == "Ctrl+Alt+Space" && settings.Chord == "A+RB" && settings.ChordHoldMs == 1100,
                    "Local legacy recovery unnecessarily replaced active or valid rollback fields");
                UserSettings.Save(path, settings);
                var saved = UserSettings.Load(path, out message);
                Require(SessionHubSettings.Equivalent(saved, settings) && !message.Contains("recovered"), "Normalized settings did not survive save/reload");
            }
            finally { Directory.Delete(root, true); }
        });
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
}

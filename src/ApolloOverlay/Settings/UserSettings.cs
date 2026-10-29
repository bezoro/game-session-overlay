using System.IO;
using System.Text.Json;

namespace ApolloOverlay;

public sealed record UserSettings(
    string? PlaynitePath = null,
    string? DisplayId = null,
    string SteamShortcut = "Shift+Tab",
    int Version = 3,
    string Hotkey = "Ctrl+Alt+Space",
    string Chord = "LB+RB+Start",
    int ChordHoldMs = 650,
    int Deadzone = 18000,
    bool ReducedMotion = false,
    bool GuideXInputEnabled = true,
    int GuideOpenMaxHoldMs = 2000,
    string[]? FavoriteQuickActions = null,
    int UiScalePercent = 100,
    string PanelSide = "Left",
    bool SoundFeedbackEnabled = false,
    bool HapticFeedbackEnabled = false,
    bool SetupSeen = false
)
{
    public static UserSettings Parse(string text) => Parse(text, out _);
    private static UserSettings Parse(string text, out bool recovered)
    {
        using var original = JsonDocument.Parse(text);
        if (original.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Configuração vazia.");
        using var document = JsonDocument.Parse(SessionHubSettings.NormalizeJson(original.RootElement, out recovered));
        // Normalize only this optional field before typed deserialization: a wrong JSON type
        // must not discard unrelated valid settings or the explicit Guide opt-in.
        using var normalized = new MemoryStream();
        using (var writer = new Utf8JsonWriter(normalized))
        {
            writer.WriteStartObject();
            var limit = 2000;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals(nameof(GuideOpenMaxHoldMs)))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value) && ValidGuideLimit(value))
                        limit = value;
                    else
                        limit = 2000;
                }
                else
                    property.WriteTo(writer);
            }

            writer.WriteNumber(nameof(GuideOpenMaxHoldMs), limit);
            writer.WriteEndObject();
        }

        var parsed = JsonSerializer.Deserialize(normalized.ToArray(), SettingsJson.Default.UserSettings) ?? throw new InvalidDataException("Configuração vazia.");
        if (parsed.SteamShortcut is not ("Shift+Tab" or "Ctrl+Tab")
            || (parsed.PlaynitePath is not null
            && (!Path.IsPathFullyQualified(parsed.PlaynitePath)
            || !parsed.PlaynitePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Configuração inválida.");
        if (parsed.Version is not (1 or 2 or 3) || parsed.Deadzone < 8000 || parsed.Deadzone > 28000)
            throw new InvalidDataException("Versão ou deadzone inválida.");
        try
        {
            _ = OpeningConfig.Parse(parsed.Hotkey, parsed.Chord, parsed.ChordHoldMs);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException("Atalho ou combinação inválida.", e);
        }

        return SessionHubSettings.Copy(parsed);
    }

    public static UserSettings Load(string path, out string message)
    {
        message = "Configuração padrão; nada será salvo automaticamente.";
        if (!File.Exists(path))
            return new();
        try
        {
            var value = Parse(File.ReadAllText(path), out var recovered);
            if (recovered)
                message = "Alguns ajustes opcionais foram recuperados; outros valores foram preservados.";
            return value;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try
            {
                var restored = Parse(File.ReadAllText(path + ".previous"));
                message = "Configuração anterior recuperada em memória; salve para restaurar.";
                return restored;
            }
            catch (Exception back) when (back is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                message = "Configuração inválida; usando padrão seguro.";
                return new();
            }
        }
    }

    public static void Save(string path, UserSettings settings)
    {
        settings = SessionHubSettings.Copy(settings);
        _ = Parse(JsonSerializer.Serialize(settings, SettingsJson.Default.UserSettings));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = path + ".pending";
        File.WriteAllText(pending, JsonSerializer.Serialize(settings, SettingsJson.Default.UserSettings));
        if (File.Exists(path))
        {
            var currentIsGood = true;
            try
            {
                _ = Parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is JsonException or InvalidDataException)
            {
                currentIsGood = false;
            }

            if (!currentIsGood)
                File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), false);
            File.Replace(pending, path, currentIsGood ? path + ".previous" : null);
        }
        else
            File.Move(pending, path);
    }

    internal static bool ValidGuideLimit(int value) => value is >= 500 and <= 3000 && value % 100 == 0;
}

using System.IO;
using System.Text.Json;

namespace ApolloOverlay;

public static class SessionHubSettings
{
    public static readonly IReadOnlyList<string> Catalog = Array.AsReadOnly(new[] { "playnite", "steam", "gamebar", "steam-overlay", "taskview", "audio" });
    public static string[] Favorites(UserSettings value) => (value.FavoriteQuickActions ?? Catalog.ToArray()).Where(Catalog.Contains).Distinct(StringComparer.Ordinal).ToArray();
    public static UserSettings Copy(UserSettings value) => value with
    {
        Version = 3,
        FavoriteQuickActions = Favorites(value),
        UiScalePercent = value.UiScalePercent is 100 or 115 or 130 or 150 ? value.UiScalePercent : 100,
        PanelSide = value.PanelSide is "Left" or "Right" ? value.PanelSide : "Left",
        Appearance = NormalizeAppearance(value.Appearance)
    };

    private static AppearanceSettings? NormalizeAppearance(AppearanceSettings? appearance)
    {
        var normalized = AppearanceCatalog.Normalize(appearance);
        return normalized == new AppearanceSettings() ? null : normalized;
    }
    public static bool Equivalent(UserSettings a, UserSettings b)
    {
        var left = Copy(a);
        var right = Copy(b);
        return left.FavoriteQuickActions!.SequenceEqual(right.FavoriteQuickActions!)
            && (left with
            {
                FavoriteQuickActions = null
            }

        ) == (right with
        {
            FavoriteQuickActions = null
        }

        );
    }

    public static UserSettings Toggle(UserSettings settings, string id)
    {
        if (!Catalog.Contains(id))
            return Copy(settings);
        var items = Favorites(settings).ToList();
        if (!items.Remove(id))
            items.Add(id);
        return Copy(settings with
        {
            FavoriteQuickActions = items.ToArray()
        });
    }

    public static UserSettings Move(UserSettings settings, string id, int delta)
    {
        var items = Favorites(settings).ToList();
        var index = items.IndexOf(id);
        if (index < 0)
            return Copy(settings);
        var next = Math.Clamp(index + delta, 0, items.Count - 1);
        (items[index], items[next]) = (items[next], items[index]);
        return Copy(settings with
        {
            FavoriteQuickActions = items.ToArray()
        });
    }

    internal static byte[] NormalizeJson(JsonElement root, out bool recovered)
    {
        recovered = false;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                var value = property.Value;
                if (property.NameEquals(nameof(UserSettings.Appearance)))
                {
                    var appearance = AppearanceCatalog.ReadJson(value, out var appearanceRecovered);
                    recovered |= appearanceRecovered;
                    writer.WritePropertyName(property.Name);
                    JsonSerializer.Serialize(writer, appearance, SettingsJson.Default.AppearanceSettings);
                    continue;
                }
                var valid = property.Name switch
                {
                    nameof(UserSettings.FavoriteQuickActions) => value.ValueKind is JsonValueKind.Null || value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String),
                    nameof(UserSettings.UiScalePercent) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n is 100 or 115 or 130 or 150,
                    nameof(UserSettings.PanelSide) => value.ValueKind == JsonValueKind.String && value.GetString() is "Left" or "Right",
                    nameof(UserSettings.SoundFeedbackEnabled) or nameof(UserSettings.HapticFeedbackEnabled) or nameof(UserSettings.SetupSeen) => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    _ => true
                };
                if (valid)
                    property.WriteTo(writer);
                else
                    recovered = true;
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}

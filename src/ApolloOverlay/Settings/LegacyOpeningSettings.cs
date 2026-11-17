using System.Text.Json;

namespace ApolloOverlay;

/// <summary>Retains valid retired bindings for rollback; malformed legacy fields cannot discard active preferences.</summary>
internal static class LegacyOpeningSettings
{
    internal static bool TryWrite(JsonProperty property, Utf8JsonWriter writer, out bool recovered)
    {
        recovered = false;
        var value = property.Value;
        switch (property.Name)
        {
            case nameof(UserSettings.Hotkey):
                recovered = value.ValueKind != JsonValueKind.String || !ValidHotkey(value.GetString()!);
                if (recovered)
                    writer.WriteString(property.Name, "Ctrl+Alt+Space");
                break;
            case nameof(UserSettings.Chord):
                recovered = value.ValueKind != JsonValueKind.String || !ValidChord(value.GetString()!);
                if (recovered)
                    writer.WriteString(property.Name, "LB+RB+Start");
                break;
            case nameof(UserSettings.ChordHoldMs):
                recovered = value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var hold) || hold is < 300 or > 2000;
                if (recovered)
                    writer.WriteNumber(property.Name, 650);
                break;
            default:
                return false;
        }

        if (!recovered)
            property.WriteTo(writer);
        return true;
    }

    private static bool ValidHotkey(string value)
    {
        var tokens = value.Split('+');
        if (tokens.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tokens.Length)
            return false;
        var modifiers = 0;
        var keys = 0;
        foreach (var token in tokens)
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Alt", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                modifiers++;
            else if (token.Equals("Space", StringComparison.OrdinalIgnoreCase) || token.Length == 1 && char.IsAsciiLetter(token[0]))
                keys++;
            else
                return false;
        return modifiers > 0 && keys == 1;
    }

    private static bool ValidChord(string value)
    {
        var tokens = value.ToUpperInvariant().Split('+');
        return tokens.Length >= 2
            && tokens.Distinct(StringComparer.Ordinal).Count() == tokens.Length
            && tokens.All(token => token is "LB" or "RB" or "START" or "BACK" or "A" or "B" or "X");
    }
}

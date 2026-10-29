using System.Text.Json.Serialization;

namespace ApolloOverlay;

[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(AppearanceSettings))]
[JsonSerializable(typeof(ColorPalette))]
internal partial class SettingsJson : JsonSerializerContext;

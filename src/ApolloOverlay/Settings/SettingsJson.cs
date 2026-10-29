using System.Text.Json.Serialization;

namespace ApolloOverlay;

[JsonSerializable(typeof(UserSettings))]
internal partial class SettingsJson : JsonSerializerContext;

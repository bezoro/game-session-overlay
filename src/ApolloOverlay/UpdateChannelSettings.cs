using System.IO;
using System.Text.Json;

namespace ApolloOverlay;

internal static class UpdateChannelSettings
{
    internal static string Read(string directory)
    {
        var fallback = AppBuild.Current.Channel == "stable" ? "stable" : "testing";
        try
        {
            var path = Path.Combine(directory, "update-channel.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024)
                return fallback;
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            return json.RootElement.GetProperty("channel").GetString() is "stable" ? "stable" : "testing";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return fallback;
        }
    }

    internal static void Write(string directory, string channel)
    {
        if (channel is not ("stable" or "testing"))
            throw new ArgumentException("Canal inválido.", nameof(channel));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "update-channel.json");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                channel
            }));
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}

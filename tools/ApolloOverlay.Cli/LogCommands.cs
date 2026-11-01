using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApolloOverlay;

internal static class LogCommands
{
    internal static int Run(string[] args)
    {
        var reply = Read(args);
        Console.WriteLine(JsonSerializer.Serialize(reply, CommandLogJson.Default.CommandLogSnapshot));
        return reply.Outcome is "Completed" or "Empty" ? 0 : 1;
    }

    internal static CommandLogSnapshot Read(string[] args, string? root = null)
    {
        var tail = 50;
        if (args is not ["logs"] && (args is not ["logs", "--tail", var tailText]
            || !int.TryParse(tailText, NumberStyles.None, CultureInfo.InvariantCulture, out tail)
            || tail is < 1 or > 200))
            return new(1, "Rejected", "Usage: gso logs [--tail 1..200].", "", [], 0, [], 0, 0, false);
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "GameSessionOverlay");
        return CommandLogReader.Read(root, tail);
    }
}

[JsonSerializable(typeof(CommandLogSnapshot))]
internal partial class CommandLogJson : JsonSerializerContext;

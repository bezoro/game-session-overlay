using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApolloOverlay;

internal sealed record DiagnosticsExportReply(string Outcome, string Reason, string Path = "", int Lines = 0);

internal static class LogCommands
{
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "GameSessionOverlay");

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
        return ReadTail(tail, root);
    }

    internal static CommandLogSnapshot ReadTail(int tail, string? root = null) => CommandLogReader.Read(root ?? Root, tail);

    internal static async Task<DiagnosticsExportReply> Export(CancellationToken token, string? root = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "GameSessionOverlay-diagnostics-" + Guid.NewGuid().ToString("N") + ".md");
        var created = false;
        try
        {
            var report = await Task.Run(() => DiagnosticReport.FromDisk(AppBuild.Current, CommandLogReader.ReadAll(root ?? Root, token)), token);
            token.ThrowIfCancellationRequested();
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            {
                created = true;
                await using var writer = new StreamWriter(file);
                await writer.WriteAsync(report.Text.AsMemory(), token);
            }
            return new("Completed", "Exported retained log files. Live capture state and logger health are unknown.", path, report.Lines);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or System.Security.SecurityException)
        {
            if (created)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
            return error is OperationCanceledException
                ? new("Cancelled", "Diagnostic export cancelled.")
                : new("Unavailable", "Could not read retained logs or write the diagnostic report.");
        }
    }
}

[JsonSerializable(typeof(CommandLogSnapshot))]
[JsonSerializable(typeof(DiagnosticsExportReply))]
internal partial class CommandLogJson : JsonSerializerContext;

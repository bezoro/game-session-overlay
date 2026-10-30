using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

internal sealed record CommandLogFile(string Name, long Bytes);
internal sealed record CommandLogReply(
    int SchemaVersion,
    string Outcome,
    string Reason,
    string Directory,
    CommandLogFile[] Files,
    int Count,
    JsonElement[] Entries,
    int SkippedRecords,
    int SkippedFiles,
    bool LimitReached
);

/// <summary>Reads a bounded recent view of local diagnostics without contacting or starting the overlay.</summary>
internal static class LogCommands
{
    private const int DefaultTail = 50, MaximumTail = 200, MaximumFiles = 8, MaximumCandidates = 64;
    private const int ReadWindowBytes = 256 * 1024, MaximumRecordBytes = 16 * 1024, OutputBudgetBytes = 128 * 1024;
    private static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "GameSessionOverlay");

    internal static int Run(string[] args)
    {
        var reply = Read(args);
        Console.WriteLine(JsonSerializer.Serialize(reply, CommandLogJson.Default.CommandLogReply));
        return reply.Outcome is "Completed" or "Empty" ? 0 : 1;
    }

    internal static CommandLogReply Read(string[] args, string? root = null)
    {
        var tail = DefaultTail;
        if (args is not ["logs"] && (args is not ["logs", "--tail", var tailText]
            || !int.TryParse(tailText, NumberStyles.None, CultureInfo.InvariantCulture, out tail)
            || tail is < 1 or > MaximumTail))
            return Reply("Rejected", "Usage: gso logs [--tail 1..200].", "");

        var directory = "";
        try
        {
            directory = Path.GetFullPath(Path.Combine(root ?? DefaultRoot, "logs"));
            RejectLinkedAncestors(directory);
            if (!System.IO.Directory.Exists(directory))
                return Reply("Empty", "No command logs have been recorded yet.", directory);

            // Enumeration, read windows and returned JSON have separate bounds.
            var candidates = System.IO.Directory.EnumerateFiles(directory, "commands-*.jsonl", SearchOption.TopDirectoryOnly)
                .Take(MaximumCandidates + 1).ToArray();
            var files = candidates.Take(MaximumCandidates).Select(DescribeFile).OfType<LogFileCandidate>()
                .OrderByDescending(file => file.Date).ThenByDescending(file => file.Sequence)
                .ThenByDescending(file => file.Path, StringComparer.Ordinal)
                .Take(MaximumFiles + 1).Select(file => file.Path).ToArray();
            var limited = candidates.Length > MaximumCandidates || files.Length > MaximumFiles;
            var entries = new List<JsonElement>(tail);
            var inspected = new List<CommandLogFile>();
            var skippedRecords = 0;
            var skippedFiles = 0;
            var remaining = OutputBudgetBytes;
            foreach (var path in files.Take(MaximumFiles))
            {
                if (entries.Count == tail || remaining == 0)
                    break;
                try
                {
                    RejectLinkedAncestors(directory);
                    using var stream = OpenVerified(path);
                    var length = stream.Length;
                    inspected.Add(new(Path.GetFileName(path), length));
                    var start = Math.Max(0, length - ReadWindowBytes);
                    var buffer = new byte[(int)(length - start)];
                    stream.Position = start;
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var consumed = stream.Read(buffer.AsSpan(read));
                        if (consumed == 0)
                            break;
                        read += consumed;
                    }

                    var first = 0;
                    if (start > 0)
                    {
                        var newline = buffer.AsSpan(0, read).IndexOf((byte)'\n');
                        first = newline < 0 ? read : newline + 1;
                        limited = true;
                    }

                    var end = read;
                    while (end > first && entries.Count < tail)
                    {
                        if (buffer[end - 1] == '\n')
                            end--;
                        var previous = buffer.AsSpan(first, end - first).LastIndexOf((byte)'\n');
                        var lineStart = previous < 0 ? first : first + previous + 1;
                        var count = end - lineStart;
                        if (count > 0 && buffer[end - 1] == '\r')
                            count--;
                        if (count > 0)
                        {
                            if (count > MaximumRecordBytes)
                                skippedRecords++;
                            else
                            {
                                try
                                {
                                    using var json = JsonDocument.Parse(buffer.AsMemory(lineStart, count), new()
                                    {
                                        MaxDepth = 16
                                    });
                                    if (json.RootElement.ValueKind != JsonValueKind.Object)
                                        skippedRecords++;
                                    else if (count > remaining)
                                    {
                                        limited = true;
                                        remaining = 0;
                                        break;
                                    }
                                    else
                                    {
                                        entries.Add(json.RootElement.Clone());
                                        remaining -= count;
                                    }
                                }
                                catch (JsonException)
                                {
                                    // A running writer can leave an incomplete last record during this snapshot.
                                    skippedRecords++;
                                }
                            }
                        }

                        end = previous < 0 ? first : first + previous;
                    }

                    // Do not jump over an unread prefix into older files and call that a complete tail.
                    if (start > 0)
                        break;
                }
                catch (Exception error) when (ReadFailure(error))
                {
                    // Rotation may rename/delete a file after enumeration. Other retained files remain useful.
                    skippedFiles++;
                }
            }

            entries.Reverse();
            var outcome = entries.Count == 0 ? "Empty" : "Completed";
            var reason = entries.Count == 0 ? "No complete command records are available." : "Recent local command records; the overlay was not contacted.";
            return new(1, outcome, reason, directory, inspected.ToArray(), entries.Count, entries.ToArray(), skippedRecords, skippedFiles, limited);
        }
        catch (Exception error) when (ReadFailure(error))
        {
            return Reply("Unavailable", "The local log directory could not be read safely. Check its access permissions and location.", directory);
        }
    }

    private static CommandLogReply Reply(string outcome, string reason, string directory) => new(1, outcome, reason, directory, [], 0, [], 0, 0, false);
    private static bool ReadFailure(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException
        or System.Security.SecurityException or NotSupportedException or System.ComponentModel.Win32Exception;

    private sealed record LogFileCandidate(string Path, DateOnly Date, int Sequence);
    private static LogFileCandidate? DescribeFile(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith("commands-", StringComparison.Ordinal) || !name.EndsWith(".jsonl", StringComparison.Ordinal))
            return null;
        var stem = name[9..^6];
        if (stem.Length is < 8 or > 19 || !DateOnly.TryParseExact(stem.AsSpan(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;
        if (stem.Length == 8)
            return new(path, date, 0);
        if (stem.Length < 12 || stem[8] != '_' || !int.TryParse(stem.AsSpan(9), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
            return null;
        return new(path, date, sequence);
    }

    private static void RejectLinkedAncestors(string path)
    {
        for (var item = new DirectoryInfo(path); item is not null; item = item.Parent)
        {
            if (item.LinkTarget is not null || item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked diagnostics paths are not read.");
        }
    }

    private static FileStream OpenVerified(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked log files are not read.");
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        try
        {
            if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked log files are not read.");
            string resolved;
            if (OperatingSystem.IsWindows())
            {
                var buffer = new StringBuilder(32768);
                var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity)
                    throw new IOException("The opened log identity could not be verified.");
                resolved = buffer.ToString();
                resolved = resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + resolved[8..]
                    : resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[4..] : resolved;
            }
            else if (OperatingSystem.IsLinux())
                resolved = new FileInfo("/proc/self/fd/" + handle.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture)).LinkTarget
                    ?? throw new IOException("The opened log identity could not be verified.");
            else
                throw new NotSupportedException("Log identity verification is unavailable on this platform.");
            if (!string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("The opened log identity changed.");
            return new FileStream(handle, FileAccess.Read, 4096);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}

[JsonSerializable(typeof(CommandLogReply))]
internal partial class CommandLogJson : JsonSerializerContext;

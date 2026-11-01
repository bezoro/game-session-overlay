using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using IO = System.IO;

namespace ApolloOverlay;

internal sealed record CommandLogFile(string Name, long Bytes);
internal sealed record CommandLogDocument(string Name, string Contents, string Format, int Lines);
internal sealed record CommandLogSnapshot(
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

/// <summary>Bounded, shared snapshot reader for local app and CLI diagnostics.</summary>
internal static class CommandLogReader
{
    private const int MaximumFiles = 8, MaximumCandidates = 64;
    private const int ReadWindowBytes = 256 * 1024, MaximumRecordBytes = 16 * 1024, OutputBudgetBytes = 128 * 1024;

    internal static IReadOnlyList<CommandLogDocument> ReadAll(string root, CancellationToken cancellation)
    {
        const long maximumExportBytes = 64L * 1024 * 1024;
        cancellation.ThrowIfCancellationRequested();
        var directory = IO.Path.GetFullPath(IO.Path.Combine(root, "logs"));
        RejectLinkedAncestors(directory);
        if (!IO.Directory.Exists(directory))
            return [];
        var paths = IO.Directory.EnumerateFiles(directory, "commands-*.jsonl", IO.SearchOption.TopDirectoryOnly)
            .Take(MaximumCandidates + 1).ToArray();
        if (paths.Length > MaximumCandidates)
            throw new IO.IOException("The complete diagnostics exceed the clipboard file limit. Attach the original log files instead.");
        var files = paths.Select(DescribeFile).OfType<LogFileCandidate>()
            .OrderBy(file => file.Date).ThenBy(file => file.Sequence).ThenBy(file => file.Path, StringComparer.Ordinal).ToArray();
        var opened = new List<(string Name, IO.FileStream Stream, long Length)>();
        try
        {
            long bytes = 0;
            // Pin all retained identities before reading, so size rotation can
            // rename/delete files without silently changing the exported snapshot.
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                RejectLinkedAncestors(directory);
                var stream = OpenVerified(file.Path);
                try
                {
                    var length = stream.Length;
                    if (length > maximumExportBytes - bytes)
                        throw new IO.IOException("The complete diagnostics exceed the clipboard size limit. Attach the original log files instead.");
                    bytes += length;
                    opened.Add((IO.Path.GetFileName(file.Path), stream, length));
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            var documents = new List<CommandLogDocument>(opened.Count);
            foreach (var file in opened)
            {
                cancellation.ThrowIfCancellationRequested();
                var buffer = new byte[checked((int)file.Length)];
                file.Stream.ReadExactly(buffer);
                // Preserve torn and malformed lines as evidence rather than
                // filtering them through the recent-record JSON parser.
                string contents;
                var format = "jsonl";
                try
                {
                    contents = new UTF8Encoding(false, true).GetString(buffer);
                    if (contents.Contains('\0'))
                    {
                        contents = Convert.ToBase64String(buffer);
                        format = "base64";
                    }
                }
                catch (DecoderFallbackException)
                {
                    contents = Convert.ToBase64String(buffer);
                    format = "base64";
                }
                var lines = buffer.Count(value => value == (byte)'\n');
                if (buffer.Length > 0 && buffer[^1] != '\n')
                    lines++;
                documents.Add(new(file.Name, contents, format, lines));
            }
            cancellation.ThrowIfCancellationRequested();
            return documents;
        }
        finally
        {
            foreach (var file in opened)
                file.Stream.Dispose();
        }
    }

    internal static CommandLogSnapshot Read(string root, int tail = 50)
    {
        if (tail is < 1 or > 200)
            return Reply("Rejected", "The tail must contain 1 through 200 records.", "");
        var directory = "";
        try
        {
            directory = IO.Path.GetFullPath(IO.Path.Combine(root, "logs"));
            RejectLinkedAncestors(directory);
            if (!IO.Directory.Exists(directory))
                return Reply("Empty", "No command logs have been recorded yet.", directory);

            // Enumeration, read windows and returned JSON have separate bounds.
            var candidates = IO.Directory.EnumerateFiles(directory, "commands-*.jsonl", IO.SearchOption.TopDirectoryOnly)
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
                    inspected.Add(new(IO.Path.GetFileName(path), length));
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

    private static CommandLogSnapshot Reply(string outcome, string reason, string directory) => new(1, outcome, reason, directory, [], 0, [], 0, 0, false);
    private static bool ReadFailure(Exception error) => error is IO.IOException or UnauthorizedAccessException or ArgumentException
        or System.Security.SecurityException or NotSupportedException or System.ComponentModel.Win32Exception;

    private sealed record LogFileCandidate(string Path, DateOnly Date, int Sequence);
    private static LogFileCandidate? DescribeFile(string path)
    {
        var name = IO.Path.GetFileName(path);
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
        for (var item = new IO.DirectoryInfo(path); item is not null; item = item.Parent)
        {
            if (item.LinkTarget is not null || item.Exists && (item.Attributes & IO.FileAttributes.ReparsePoint) != 0)
                throw new IO.IOException("Linked diagnostics paths are not read.");
        }
    }

    private static IO.FileStream OpenVerified(string path)
    {
        if ((IO.File.GetAttributes(path) & IO.FileAttributes.ReparsePoint) != 0)
            throw new IO.IOException("Linked log files are not read.");
        var handle = IO.File.OpenHandle(path, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.ReadWrite | IO.FileShare.Delete, IO.FileOptions.SequentialScan);
        try
        {
            if ((IO.File.GetAttributes(handle) & IO.FileAttributes.ReparsePoint) != 0)
                throw new IO.IOException("Linked log files are not read.");
            string resolved;
            if (OperatingSystem.IsWindows())
            {
                var buffer = new StringBuilder(32768);
                var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity)
                    throw new IO.IOException("The opened log identity could not be verified.");
                resolved = buffer.ToString();
                resolved = resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + resolved[8..]
                    : resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[4..] : resolved;
            }
            else if (OperatingSystem.IsLinux())
                resolved = new IO.FileInfo("/proc/self/fd/" + handle.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture)).LinkTarget
                    ?? throw new IO.IOException("The opened log identity could not be verified.");
            else
                throw new NotSupportedException("Log identity verification is unavailable on this platform.");
            if (!string.Equals(IO.Path.GetFullPath(resolved), IO.Path.GetFullPath(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IO.IOException("The opened log identity changed.");
            return new IO.FileStream(handle, IO.FileAccess.Read, 4096);
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

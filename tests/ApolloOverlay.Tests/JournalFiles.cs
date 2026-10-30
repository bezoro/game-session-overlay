using System.IO;

internal static class JournalFiles
{
    internal static IEnumerable<string> ReadLines(string path)
    {
        // Windows readers must share the write handle retained by the active file sink.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            yield return line;
    }
}

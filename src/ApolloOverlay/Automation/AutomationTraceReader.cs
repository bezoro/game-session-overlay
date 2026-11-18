using System.IO;

namespace ApolloOverlay.Automation;

internal static class AutomationTraceReader
{
    internal static string[] ReadTail(string root)
    {
        var path = Path.Combine(root, "automation-trace.jsonl");
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            var lines = new Queue<string>(24);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == 24)
                    lines.Dequeue();
                lines.Enqueue(line);
            }
            return lines.ToArray();
        }
        catch (FileNotFoundException)
        {
            return [];
        }
    }
}

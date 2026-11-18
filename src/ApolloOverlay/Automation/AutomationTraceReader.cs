using IO = System.IO;

namespace ApolloOverlay.Automation;

internal static class AutomationTraceReader
{
    internal static string[] ReadTail(string root)
    {
        var path = IO.Path.Combine(root, "automation-trace.jsonl");
        try
        {
            using var file = new IO.FileStream(path, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.ReadWrite | IO.FileShare.Delete);
            using var reader = new IO.StreamReader(file);
            var lines = new Queue<string>(24);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == 24)
                    lines.Dequeue();
                lines.Enqueue(line);
            }
            return lines.ToArray();
        }
        catch (IO.FileNotFoundException)
        {
            return [];
        }
    }
}

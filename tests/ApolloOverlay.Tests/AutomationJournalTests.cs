using System.IO;
using ApolloOverlay.Automation;

internal static class AutomationJournalTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("Automation trace reads a bounded tail while an append handle remains open", () =>
        {
            var root = Directory.CreateTempSubdirectory();
            try
            {
                var path = Path.Combine(root.FullName, "automation-trace.jsonl");
                File.WriteAllLines(path, Enumerable.Range(0, 30).Select(index => index.ToString()));
                using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                var lines = AutomationTraceReader.ReadTail(root.FullName);
                if (lines.Length != 24 || lines[0] != "6" || lines[^1] != "29")
                    throw new InvalidDataException("Concurrent trace reader did not retain the latest 24 records.");
            }
            finally
            {
                root.Delete(true);
            }
        });

        test("Automation phases retain order without blocking native callbacks on storage", () =>
        {
            using var allowWrite = new ManualResetEventSlim();
            List<string> phases = [];
            var caller = Environment.CurrentManagedThreadId;
            var writer = new AutomationPhaseJournal((reply, _) =>
            {
                if (Environment.CurrentManagedThreadId == caller || !allowWrite.Wait(TimeSpan.FromSeconds(3)))
                    throw new InvalidOperationException("Phase storage blocked the calling thread.");
                phases.Add(reply.Outcome);
            });
            writer.Record(new()
            {
                Outcome = "first"
            });
            writer.Record(new()
            {
                Outcome = "second"
            });
            allowWrite.Set();
            writer.Drain.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            if (!phases.SequenceEqual(["first", "second"]))
                throw new InvalidDataException("Phase records arrived out of order.");
        });
    }
}

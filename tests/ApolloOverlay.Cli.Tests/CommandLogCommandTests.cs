using System.Text;
using System.Text.Json;

internal static class CommandLogCommandTests
{
    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new Exception(reason);
    }

    internal static async Task Run(Func<string, Func<Task>, Task> test)
    {
        Task Check(string name, Action body) => test(name, () =>
        {
            body();
            return Task.CompletedTask;
        });

        await Check("CLI logs absent diagnostics are Empty without creating directories", () =>
        {
            using var fixture = new LogsFixture();
            var reply = LogCommands.Read(["logs"], fixture.Root);
            Require(reply is { Outcome: "Empty", Count: 0 } && !Directory.Exists(fixture.Root), "Offline inspection created data or failed for a new user");
            var json = JsonSerializer.Serialize(reply, CommandLogJson.Default.CommandLogReply);
            using var parsed = JsonDocument.Parse(json);
            Require(parsed.RootElement.GetProperty("Entries").GetArrayLength() == 0, "Reflection-free log response is invalid");
        });
        await Check("CLI logs tails newest rolled files and returns chronological entries", () =>
        {
            using var fixture = new LogsFixture();
            fixture.Write("commands-20261029.jsonl", Entry(1), Entry(2));
            fixture.Write("commands-20261030.jsonl", Entry(3), Entry(4));
            fixture.Write("commands-20261030_001.jsonl", Entry(5), Entry(6));
            var reply = LogCommands.Read(["logs", "--tail", "3"], fixture.Root);
            Require(reply.Outcome == "Completed" && reply.Entries.Select(item => item.GetProperty("Index").GetInt32()).SequenceEqual([4, 5, 6]), "Tail omitted newest records or reversed chronology");
            Require(reply.Files.Select(file => file.Name).SequenceEqual(["commands-20261030_001.jsonl", "commands-20261030.jsonl"]), "Unneeded older files were read");
        });
        await Check("CLI logs sorts rolling suffixes numerically across the three-digit boundary", () =>
        {
            using var fixture = new LogsFixture();
            fixture.Write("commands-20261030_998.jsonl", Entry(998));
            fixture.Write("commands-20261030_999.jsonl", Entry(999));
            fixture.Write("commands-20261030_1000.jsonl", Entry(1000));
            var latest = LogCommands.Read(["logs", "--tail", "1"], fixture.Root);
            Require(latest is { Count: 1 } && latest.Entries[0].GetProperty("Index").GetInt32() == 1000
                && latest.Files[0].Name == "commands-20261030_1000.jsonl", "Lexical sorting selected an older roll as newest");
            var recent = LogCommands.Read(["logs", "--tail", "3"], fixture.Root);
            Require(recent.Entries.Select(item => item.GetProperty("Index").GetInt32()).SequenceEqual([998, 999, 1000]), "Tail chronology changed at the rolling counter boundary");
        });
        await Check("CLI logs tolerates a live writer and an incomplete final record", () =>
        {
            using var fixture = new LogsFixture();
            var path = fixture.Write("commands-20261030.jsonl", Entry(1));
            using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer.Write(Encoding.UTF8.GetBytes("{\"unfinished\":"));
            writer.Flush();
            var reply = LogCommands.Read(["logs"], fixture.Root);
            Require(reply is { Outcome: "Completed", Count: 1, SkippedRecords: 1 } && reply.Entries[0].GetProperty("Index").GetInt32() == 1, "Live partial append hid valid history or escaped as an error");
        });
        await Check("CLI logs skips malformed or non-object records and escapes terminal controls", () =>
        {
            using var fixture = new LogsFixture();
            fixture.Write("commands-20261030.jsonl", "not json", "[]", "{\"Message\":\"hello\\u001b[31m\\nnext\"}", "{broken");
            var reply = LogCommands.Read(["logs"], fixture.Root);
            Require(reply is { Count: 1, SkippedRecords: 3 }, "Malformed data was accepted or complete JSON discarded");
            var encoded = JsonSerializer.Serialize(reply, CommandLogJson.Default.CommandLogReply);
            Require(!encoded.Contains('\u001b') && encoded.Contains("\\u001B", StringComparison.OrdinalIgnoreCase), "Log content could inject a terminal escape");
            using var roundtrip = JsonDocument.Parse(encoded);
            Require(roundtrip.RootElement.GetProperty("Entries")[0].GetProperty("Message").GetString()!.Contains('\n'), "JSON response did not preserve the safely encoded record");
        });
        await Check("CLI logs rejects invalid tail limits and arbitrary path options before reading", () =>
        {
            using var fixture = new LogsFixture();
            foreach (var args in new string[][]
            {
                ["logs", "--tail", "0"], ["logs", "--tail", "201"], ["logs", "--tail", "-1"],
                ["logs", "--tail", "2147483648"], ["logs", "--tail", "NaN"], ["logs", "--tail"],
                ["logs", "--root", fixture.Root], ["logs", "--pipe", "arbitrary"], ["logs", "--tail", "1", "--tail", "2"]
            })
                Require(LogCommands.Read(args, fixture.Root).Outcome == "Rejected", "Unsupported options reached local I/O");
            Require(!Directory.Exists(fixture.Root), "Rejected input created state");
            fixture.Write("commands-20261030.jsonl", Entry(1), Entry(2));
            Require(LogCommands.Read(["logs", "--tail", "1"], fixture.Root).Count == 1
                && LogCommands.Read(["logs", "--tail", "200"], fixture.Root).Count == 2, "Valid boundary was rejected");
        });
        await Check("CLI logs bounds read windows and does not jump an unread prefix into older files", () =>
        {
            using var fixture = new LogsFixture();
            fixture.Write("commands-20261029.jsonl", Entry(0));
            fixture.Write("commands-20261030.jsonl", new string('x', 1024 * 1024), Entry(1), Entry(2));
            var reply = LogCommands.Read(["logs", "--tail", "200"], fixture.Root);
            Require(reply is { LimitReached: true, Count: 2 } && reply.Files.Length == 1
                && reply.Entries[0].GetProperty("Index").GetInt32() == 1, "Bounded snapshot read an unbounded prefix or claimed older files were the full tail");
        });
        await Check("CLI logs bounds returned record bytes independently of requested count", () =>
        {
            using var fixture = new LogsFixture();
            // JsonSerializer reflection is disabled in the production CLI, so fixture text is explicit.
            var record = "{\"Value\":\"" + new string('x', 8000) + "\"}";
            fixture.Write("commands-20261030.jsonl", Enumerable.Repeat(record, 40).ToArray());
            var reply = LogCommands.Read(["logs", "--tail", "200"], fixture.Root);
            Require(reply.LimitReached && reply.Count is > 0 and < 40, "Response byte budget was not enforced");
            var output = JsonSerializer.Serialize(reply, CommandLogJson.Default.CommandLogReply);
            Require(Encoding.UTF8.GetByteCount(output) < 1024 * 1024, "Serialized response exceeded the bounded output allowance");
        });
        await Check("CLI logs limits retained files and ignores unrelated filenames", () =>
        {
            using var fixture = new LogsFixture();
            for (var day = 1; day <= 10; day++)
                fixture.Write($"commands-202610{day:00}.jsonl", Entry(day));
            fixture.Write("commands-notadate.jsonl", Entry(999));
            fixture.Write("automation-trace.jsonl", Entry(999));
            fixture.Write("commands-20261030_foo.jsonl", Entry(999));
            var reply = LogCommands.Read(["logs", "--tail", "200"], fixture.Root);
            Require(reply is { LimitReached: true, Count: 8 } && reply.Files.Length == 8
                && reply.Entries.Select(item => item.GetProperty("Index").GetInt32()).SequenceEqual(Enumerable.Range(3, 8)), "Unbounded or unrelated files were inspected");
        });
        await Check("CLI logs refuses a linked log directory without reading outside the configured root", () =>
        {
            using var fixture = new LogsFixture();
            Directory.CreateDirectory(fixture.Root);
            Directory.CreateDirectory(fixture.Outside);
            File.WriteAllText(Path.Combine(fixture.Outside, "commands-20261030.jsonl"), Entry(999));
            Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "logs"), fixture.Outside);
            var reply = LogCommands.Read(["logs"], fixture.Root);
            Require(reply is { Outcome: "Unavailable", Count: 0 }, "Linked directory exposed data outside diagnostics");
        });
        await Check("CLI logs skips linked files and still reads retained ordinary files", () =>
        {
            using var fixture = new LogsFixture();
            fixture.Write("commands-20261029.jsonl", Entry(1));
            Directory.CreateDirectory(fixture.Outside);
            var outside = Path.Combine(fixture.Outside, "private.jsonl");
            File.WriteAllText(outside, Entry(999));
            File.CreateSymbolicLink(Path.Combine(fixture.Root, "logs", "commands-20261030.jsonl"), outside);
            var reply = LogCommands.Read(["logs"], fixture.Root);
            Require(reply is { Outcome: "Completed", Count: 1, SkippedFiles: 1 }
                && reply.Entries[0].GetProperty("Index").GetInt32() == 1, "Linked file exposed its target or blocked valid local records");
        });
        await Check("CLI logs rejects a linked ancestor of the diagnostics root", () =>
        {
            using var fixture = new LogsFixture();
            Directory.CreateDirectory(fixture.Outside);
            Directory.CreateSymbolicLink(fixture.Root, fixture.Outside);
            Require(LogCommands.Read(["logs"], fixture.Root).Outcome == "Unavailable", "An ancestor link escaped the configured root");
        });
        await test("CLI logs routes locally and emits rejection as JSON without requiring an overlay instance", async () =>
        {
            var previous = Console.Out;
            using var output = new StringWriter();
            try
            {
                Console.SetOut(output);
                Require(await Program.Main(["logs", "--tail", "0"]) == 1, "Invalid command unexpectedly succeeded");
            }
            finally
            {
                Console.SetOut(previous);
            }

            using var json = JsonDocument.Parse(output.ToString());
            Require(json.RootElement.GetProperty("Outcome").GetString() == "Rejected", "Log inspection was sent through the automation protocol");
        });
    }

    private static string Entry(int index) => "{\"@t\":\"2026-10-30T12:00:00.0000000Z\",\"Index\":" + index + "}";

    private sealed class LogsFixture : IDisposable
    {
        private readonly string temporary = Path.Combine(Path.GetTempPath(), "gso-command-log-cli-" + Guid.NewGuid().ToString("N"));
        internal string Root => Path.Combine(temporary, "overlay");
        internal string Outside => Path.Combine(temporary, "outside");
        internal LogsFixture() => Directory.CreateDirectory(temporary);
        internal string Write(string name, params string[] records)
        {
            var directory = Path.Combine(Root, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, string.Join('\n', records) + "\n", new UTF8Encoding(false));
            return path;
        }

        public void Dispose() => Directory.Delete(temporary, true);
    }
}

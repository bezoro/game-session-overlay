using System.IO;
using System.Text;
using ApolloOverlay;

internal static class DiagnosticReportTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("complete diagnostic export preserves more than the CLI tail and clipboard summary limits", () => WithRoot(root =>
        {
            var content = string.Join('\n', Enumerable.Range(0, 650).Select(index => $"{{\"Index\":{index},\"Data\":\"{new string('x', 200)}\"}}")) + "\n";
            Write(root, "commands-20261031.jsonl", content);
            var documents = CommandLogReader.ReadAll(root, CancellationToken.None);
            Require(documents.Count == 1 && documents[0].Contents == content, "Full export truncated or filtered retained data");
            var report = DiagnosticReport.Create(AppBuild.Current, Healthy, documents, true, "guide-active=True, confirmed=False");
            Require(report.Lines == 650 && report.Text.Contains(content, StringComparison.Ordinal)
                && Encoding.UTF8.GetByteCount(report.Text) > 128 * 1024 && !report.Incomplete, "Report silently applied the recent-record budget");
        }));
        test("complete export orders retained files numerically and keeps malformed and torn evidence", () => WithRoot(root =>
        {
            Write(root, "commands-20261030_999.jsonl", "{\"Index\":999}\n");
            Write(root, "commands-20261030_1000.jsonl", "not-json\n{\"unfinished\":");
            Write(root, "commands-20261031.jsonl", "{\"Index\":1001}\n");
            var documents = CommandLogReader.ReadAll(root, CancellationToken.None);
            Require(documents.Select(file => file.Name).SequenceEqual(new[] { "commands-20261030_999.jsonl", "commands-20261030_1000.jsonl", "commands-20261031.jsonl" })
                && documents[1].Contents == "not-json\n{\"unfinished\":", "Complete export discarded forensic evidence or changed chronology");
        }));
        test("complete export reads a live writer without interrupting append", () => WithRoot(root =>
        {
            var path = Write(root, "commands-20261031.jsonl", "{\"Index\":1}\n");
            using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            Require(CommandLogReader.ReadAll(root, CancellationToken.None).Single().Contents.Contains("Index"), "The active journal could not be exported");
            writer.Write(Encoding.UTF8.GetBytes("{\"Index\":2}\n"));
            writer.Flush();
            Require(CommandLogReader.ReadAll(root, CancellationToken.None).Single().Contents.Contains("2}"), "Export blocked subsequent writes");
        }));
        test("complete export preserves invalid UTF-8 and NUL bytes as explicitly encoded evidence", () => WithRoot(root =>
        {
            var path = Write(root, "commands-20261031.jsonl", "");
            byte[] bytes = [123, 34, 0, 255, 10];
            File.WriteAllBytes(path, bytes);
            var document = CommandLogReader.ReadAll(root, CancellationToken.None).Single();
            Require(document.Format == "base64" && Convert.FromBase64String(document.Contents).SequenceEqual(bytes),
                "Damaged text was replaced or would be silently cut at a clipboard NUL");
            var report = DiagnosticReport.Create(AppBuild.Current, Healthy, [document], true, "fixture");
            Require(report.Text.Contains("Encoding: base64") && report.Lines == 1, "Encoded evidence lost its format or original line count");
        }));
        test("complete export refuses oversized retained data instead of returning a partial report", () => WithRoot(root =>
        {
            var path = Write(root, "commands-20261031.jsonl", "");
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
                file.SetLength(64L * 1024 * 1024 + 1);
            Throws<IOException>(() => CommandLogReader.ReadAll(root, CancellationToken.None));
            File.Delete(path);
        }));
        test("complete export refuses excess files rather than omitting earlier evidence", () => WithRoot(root =>
        {
            for (var index = 0; index < 65; index++)
                Write(root, $"commands-20261031_{index:000}.jsonl", "{}\n");
            Throws<IOException>(() => CommandLogReader.ReadAll(root, CancellationToken.None));
        }));
        test("complete export rejects a linked retained file instead of leaking its target", () => WithRoot(root =>
        {
            var target = Path.Combine(root, "private.txt");
            Directory.CreateDirectory(root);
            File.WriteAllText(target, "private-file-content");
            var path = Write(root, "commands-20261031.jsonl", "{}");
            File.Delete(path);
            File.CreateSymbolicLink(path, target);
            Throws<IOException>(() => CommandLogReader.ReadAll(root, CancellationToken.None));
        }));
        test("complete export cancellation releases all file handles", () => WithRoot(root =>
        {
            var path = Write(root, "commands-20261031.jsonl", "{}\n");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => CommandLogReader.ReadAll(root, cancellation.Token));
            File.Delete(path);
        }));
        test("diagnostic report includes build identity and visible logging gaps without a private path", () =>
        {
            var build = new AppBuildInfo("0.49.2", "fixture-build", new string('a', 40), "stable");
            var health = Healthy with
            {
                DroppedEvents = 2,
                WriteFailures = 3,
                DrainTimedOut = true
            };
            var report = DiagnosticReport.Create(build, health, [], false, "guide-valid=False, confirmed=False");
            Require(report.Incomplete && report.Text.Contains("fixture-build") && report.Text.Contains(build.Commit)
                && report.Text.Contains("dropped=2") && report.Text.Contains("write-failures=3")
                && report.Text.Contains("guide-valid=False") && report.Text.Contains("Queue drained: False"), "An incomplete journal looked healthy or lost its identity");
        });
    }

    private static CommandLogStatus Healthy => new(0, 0, 0, 0, 0, false, false, false, null, null);
    private static string Write(string root, string name, string contents)
    {
        var directory = Path.Combine(root, "logs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        return path;
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
    private static void WithRoot(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "overlay-diagnostic-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            body(root);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

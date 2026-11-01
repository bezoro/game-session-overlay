using System.Text;

namespace ApolloOverlay;

internal sealed record DiagnosticReport(string Text, int Lines, bool Incomplete)
{
    internal static DiagnosticReport Create(AppBuildInfo build, CommandLogStatus health, IReadOnlyList<CommandLogDocument> documents, bool drained, string state)
    {
        var header = $"Game Session Overlay diagnostics\nVersion: {build.Version}\nBuild: {build.BuildId}\nCommit: {build.Commit}\nChannel: {build.Channel}\nOS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\nArchitecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\nReport created (UTC): {DateTimeOffset.UtcNow:O}\n"
            + $"Logger: buffered={health.BufferedEvents}, dropped={health.DroppedEvents}, write-failures={health.WriteFailures}, untracked={health.UntrackedOperations}, active={health.ActiveOperations}, closing={health.Closing}, closed={health.Closed}, drain-timeout={health.DrainTimedOut}\n"
            + $"Last logger error: {health.LastErrorType ?? "none"}; HRESULT={health.LastErrorHResult}\n"
            + $"Capture state: {state}\nQueue drained: {drained}\nAll retained command log files: {documents.Count}\nRetention: up to 8 files and 14 days; unavailable older records cannot be recovered.\n";
        var text = new StringBuilder(header);
        var lines = 0;
        foreach (var document in documents)
        {
            text.Append("\nFile: ").Append(document.Name).Append("\nEncoding: ").Append(document.Format)
                .Append("\n```").Append(document.Format).Append('\n')
                .Append(document.Contents).Append("\n```\n");
            lines += document.Lines;
        }
        var incomplete = !drained || health.DrainTimedOut || health.DroppedEvents > 0 || health.WriteFailures > 0 || health.UntrackedOperations > 0;
        text.Append("\nRaw log lines: ").Append(lines).Append("; known logging gaps: ").Append(incomplete).Append('\n');
        return new(text.ToString(), lines, incomplete);
    }
}

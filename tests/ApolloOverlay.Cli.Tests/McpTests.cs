using System.Diagnostics;
using System.Text.Json;
using ApolloOverlay.Automation;

internal static class McpTests
{
    internal static async Task<int> Offline(string cli)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = new Client(cli, "Gso.Mcp.Tests." + Guid.NewGuid().ToString("N"), timeout.Token);
        await client.Initialize();
        var tools = await client.Request("tools/list", new
        {
        });
        var execute = tools.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "execute_action");
        var schema = execute.GetProperty("inputSchema").GetProperty("properties");
        Require(schema.GetProperty("dryRun").GetProperty("default").GetBoolean(), "Dry run default missing");
        Require(schema.GetProperty("revision").GetProperty("type").GetString() == "integer", "Revision schema missing");
        var logs = await client.Call("read_logs", new
        {
            tail = 0
        });
        Require(logs.GetProperty("isError").GetBoolean() && Data(logs).GetProperty("Outcome").GetString() == "Rejected", "Log bounds not enforced");
        var unavailable = Reply(await client.Call("execute_action", new
        {
            actionId = "focus",
            targetId = "absent"
        }));
        Require(unavailable is { Outcome: "Unavailable", Dispatched: false }, "Offline mutation did not fail closed");
        var export = Data(await client.Call("export_diagnostics", new
        {
        }));
        var report = export.GetProperty("Path").GetString()!;
        try
        {
            Require(export.GetProperty("Outcome").GetString() == "Completed"
                && (await File.ReadAllTextAsync(report, timeout.Token)).Contains("Game Session Overlay diagnostics"), "Offline report missing");
        }
        finally
        {
            if (File.Exists(report))
                File.Delete(report);
        }
        await client.CloseInput();
        Console.WriteLine("PASS MCP stdio initialization, discovery, offline errors, and diagnostic export");
        return 0;
    }

    internal static async Task<int> Session(string cli, string pipe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = new Client(cli, pipe, timeout.Token);
        await client.Initialize();
        var targets = Reply(await client.Call("list_targets", new
        {
        })).Snapshot!;
        Require(!targets.Visible && targets.Route == "TargetOptions" && targets.Targets.Length == 1, "Expected a hidden overlay on the options route with one owned target");
        var targetId = targets.Targets.Single().Id;
        var listed = Reply(await client.Call("list_actions", new
        {
        })).Snapshot!;
        Require(listed.Actions.Any(a => a is { Id: "focus", Scope: "session", Available: true })
            && listed.Actions.Any(a => a is { Id: "session-select", Scope: "session", TargetRequired: true }), "Session actions unavailable outside the hub");

        var mcp = Reply(await client.Call("execute_action", new
        {
            actionId = "focus",
            targetId,
            revision = targets.Revision
        }));
        using var terminal = Start(cli, ["execute", "focus", "--target", targetId, "--revision", targets.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), "--dry-run", "--pipe", pipe]);
        var stdout = terminal.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = terminal.StandardError.ReadToEndAsync(timeout.Token);
        await terminal.WaitForExitAsync(timeout.Token);
        var local = (await stdout).Trim();
        Require(terminal.ExitCode == 0 && (await stderr).Length == 0 && mcp is { Outcome: "DryRun", Dispatched: false }
            && JsonSerializer.Serialize(mcp, AutomationJson.Default.AutomationReply) == local,
            "MCP and CLI focus dry-run receipts differ");
        var unchanged = Reply(await client.Call("get_status", new
        {
        })).Snapshot!;
        Require(!unchanged.Visible && unchanged.Route == targets.Route && unchanged.Revision == targets.Revision, "Dry run changed overlay state");

        var clear = Reply(await client.Call("execute_action", new
        {
            actionId = "session-clear",
            dryRun = false
        }));
        var cleared = Reply(await client.Call("get_status", new
        {
        })).Snapshot!;
        Require(clear.Outcome == "Completed" && cleared.SessionTargetId == "" && cleared.Route == targets.Route, "Clear changed the route or retained the session");
        var stale = Reply(await client.Call("execute_action", new
        {
            actionId = "session-select",
            targetId,
            revision = targets.Revision,
            dryRun = false
        }));
        Require(stale.Outcome == "Rejected", "A stale revision was accepted");
        var select = Reply(await client.Call("execute_action", new
        {
            actionId = "session-select",
            targetId,
            dryRun = false
        }));
        var selected = Reply(await client.Call("get_status", new
        {
        })).Snapshot!;
        Require(select.Outcome == "Completed" && selected.SessionTargetId == targetId && selected.Route == targets.Route && !selected.Visible,
            "Selection did not preserve the hidden route");
        await client.CloseInput();
        Console.WriteLine("PASS hidden WPF automation through CLI and MCP");
        return 0;
    }

    internal static async Task<int> Rollback(string cli, string buildId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(95));
        await using var client = new Client(cli, "", timeout.Token);
        await client.Initialize();
        var result = await client.Call("rollback_update", new
        {
            buildId,
            dryRun = false,
            timeoutSeconds = 90
        });
        var receipt = Data(result);
        Require(receipt.GetProperty("Outcome").GetString() == "Requested", receipt.GetRawText());
        Require(result.GetProperty("_meta").GetProperty("game-session-overlay/reconnect").GetBoolean(), "Missing reconnect indication");
        // Keep stdin open to prove the MCP server releases its installed executable after sending the receipt.
        await client.WaitForExit();
        Console.WriteLine(receipt.GetRawText());
        return 0;
    }

    private static JsonElement Data(JsonElement reply) => reply.GetProperty("structuredContent");
    private static AutomationReply Reply(JsonElement reply) => Data(reply).Deserialize(AutomationJson.Default.AutomationReply)!;

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }

    private static Process Start(string cli, string[] args)
    {
        var dll = cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(dll ? Environment.ProcessPath! : cli)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (dll)
            start.ArgumentList.Add(cli);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the CLI");
    }

    private sealed class Client(string cli, string pipe, CancellationToken token) : IAsyncDisposable
    {
        private readonly Process process = Start(cli, pipe.Length > 0 ? ["mcp", "--pipe", pipe] : ["mcp"]);
        private int id;

        internal async Task Initialize()
        {
            var hello = await Request("initialize", new
            {
                protocolVersion = "2025-11-25",
                capabilities = new
                {
                },
                clientInfo = new
                {
                    name = "overlay-tests",
                    version = "1"
                }
            });
            Require(hello.GetProperty("serverInfo").GetProperty("name").GetString() == "game-session-overlay", "Unexpected MCP server identity");
            await Write(new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized"
            });
        }

        internal Task<JsonElement> Call(string name, object arguments) => Request("tools/call", new { name, arguments });

        internal async Task<JsonElement> Request(string method, object parameters)
        {
            var request = ++id;
            await Write(new
            {
                jsonrpc = "2.0",
                id = request,
                method,
                @params = parameters
            });
            using var document = JsonDocument.Parse(await process.StandardOutput.ReadLineAsync(token) ?? throw new IOException("MCP stdout closed before the reply."));
            var response = document.RootElement;
            Require(response.GetProperty("id").GetInt32() == request && response.TryGetProperty("result", out _), response.GetRawText());
            return response.GetProperty("result").Clone();
        }

        private async Task Write(object message)
        {
            // The test client serializes anonymous envelopes for the wire.
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }

        internal async Task CloseInput()
        {
            process.StandardInput.Close();
            await WaitForExit();
        }

        internal async Task WaitForExit()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(deadline.Token);
            var remaining = await process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = await process.StandardError.ReadToEndAsync(deadline.Token);
            Require(process.ExitCode == 0 && remaining.Length == 0 && errors.Length == 0, "MCP child failed: " + errors + remaining);
        }

        public async ValueTask DisposeAsync()
        {
            using (process)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
    }
}

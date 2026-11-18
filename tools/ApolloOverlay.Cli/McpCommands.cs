using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApolloOverlay;
using ApolloOverlay.Automation;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

internal sealed class McpCommands(string pipe)
{
    private const string Reconnect = "game-session-overlay/reconnect";

    internal static async Task<int> Run(string pipe, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var commands = new McpCommands(pipe);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "game-session-overlay", Version = AppBuild.Current.Version },
            ServerInstructions = "Use list_targets and list_actions for discovery. Session actions work while the overlay is hidden. Check the Outcome and Terminal fields before repeating a request.",
            ToolCollection = commands.Tools()
        };
        options.Filters.Message.OutgoingFilters.Add(next => async (context, cancellation) =>
        {
            var reconnect = context.JsonRpcMessage is JsonRpcResponse { Result: JsonObject result }
                && result["_meta"]?[Reconnect]?.GetValue<bool>() == true;
            try
            {
                await next(context, cancellation);
            }
            finally
            {
                // Velopack needs the installed CLI files released after the receipt is sent.
                if (reconnect)
                    await stop.CancelAsync();
            }
        });

        await using var server = McpServer.Create(new StdioServerTransport(options), options);
        try
        {
            await server.RunAsync(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        return 0;
    }

    private McpServerPrimitiveCollection<McpServerTool> Tools()
    {
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        void Add(Delegate handler, string name, string description, bool readOnly = true, bool destructive = false, bool openWorld = false) =>
            tools.Add(McpServerTool.Create(handler, new()
            {
                Name = name,
                Description = description,
                ReadOnly = readOnly,
                Destructive = destructive,
                OpenWorld = openWorld
            }));
        Add(Status, "get_status", "Read overlay status and the current revision.");
        Add(Targets, "list_targets", "Refresh window identities and read the overlay revision. Use opaque target IDs.");
        Add(Actions, "list_actions", "Read actions, availability, and intents. Session-scoped actions are independent of the displayed route.");
        Add(Trace, "get_trace", "Read recent automation trace records.");
        Add(Result, "get_result", "Read the receipt for an automation request ID.");
        Add(Execute, "execute_action", "Execute or simulate an advertised action against the supplied target and revision. Dry runs are the default.", readOnly: false, destructive: true);
        Add(AudioGet, "get_audio", "Observe the current Windows default output volume and mute state.");
        Add(AudioSet, "set_audio", "Set the default output volume, mute state, or both. Dry runs are the default.", readOnly: false);
        Add(SettingsGet, "get_settings", "Read the current feedback, layout, and controller preferences.");
        Add(SettingsSet, "set_settings", "Save explicit overlay preferences and preserve pending manual edits. Dry runs are the default.", readOnly: false);
        Add(MediaGet, "get_media", "Observe playback from the current Windows media session.");
        Add(MediaControl, "control_media", "Play, pause, or skip in the current Windows media session. Dry runs are the default.", readOnly: false, destructive: true);
        Add(Cancel, "cancel_action", "Request cancellation of an active action. Use get_result to observe cleanup.", readOnly: false);
        Add(Shutdown, "shutdown_overlay", "Request normal overlay shutdown and observe the exact process exit.", readOnly: false, destructive: true);
        Add(Logs, "read_logs", "Read a bounded tail of retained command logs from disk.");
        Add(Diagnostics, "export_diagnostics", "Save all retained command log files into a new temporary Markdown report and return its local path.", readOnly: false);
        Add(UpdateStatus, "get_update_status", "Read running build identity, update job, catalog, and rollback history.");
        Add(CheckUpdate, "check_updates", "Query the published releases in a channel.", readOnly: false, openWorld: true);
        Add(InstallUpdate, "install_update", "Prepare and request a version change. Dry runs are the default. The MCP connection closes after an application receipt so the updater can replace the files. Reconnect and check the build identity.", readOnly: false, destructive: true, openWorld: true);
        Add(Rollback, "rollback_update", "Prepare and request restoration of a retained build. Dry runs are the default. Reconnect after the application receipt and check the build identity.", readOnly: false, destructive: true);
        Add(CancelUpdate, "cancel_update", "Request cancellation of the update preparation job.", readOnly: false);
        return tools;
    }

    private async Task<CallToolResult> Session(AutomationRequest request, CancellationToken token)
    {
        var reply = await Program.SessionCommand(pipe, request, token);
        return AutomationResult(reply);
    }

    private Task<CallToolResult> Status(CancellationToken token) => Session(new() { Verb = "status" }, token);
    private Task<CallToolResult> Targets(CancellationToken token) => Session(new() { Verb = "targets" }, token);
    private Task<CallToolResult> Actions(CancellationToken token) => Session(new() { Verb = "actions" }, token);
    private Task<CallToolResult> Trace(CancellationToken token) => Session(new() { Verb = "trace" }, token);
    private Task<CallToolResult> Result(string requestId, CancellationToken token) => Session(new() { Verb = "result", RequestId = requestId }, token);
    private Task<CallToolResult> Cancel(string requestId, CancellationToken token) => Session(new() { Verb = "cancel", RequestId = requestId }, token);
    private Task<CallToolResult> Shutdown(CancellationToken token) => Session(new() { Verb = "shutdown" }, token);

    private Task<CallToolResult> Execute(
        [Description("Action ID from list_actions.")] string actionId,
        CancellationToken token,
        [Description("Opaque window ID from list_targets.")] string targetId = "",
        [Description("Revision from the latest live snapshot. Omit to read a fresh revision.")] long revision = -1,
        [Description("Validate the action without dispatching it. Set false to execute.")] bool dryRun = true,
        [Description("An advertised intent: activate, left, or right.")] string intent = "activate",
        AutomationAudioChange? audioChange = null, AutomationSettingsChange? settingsChange = null, string? mediaCommand = null) =>
        Session(new()
        {
            Verb = "execute",
            ActionId = actionId,
            TargetId = targetId,
            Revision = revision,
            DryRun = dryRun,
            Intent = intent,
            AudioChange = audioChange,
            SettingsChange = settingsChange,
            MediaCommand = mediaCommand
        }, token);

    private Task<CallToolResult> AudioGet(CancellationToken token) => Operation("audio-get", token);

    private Task<CallToolResult> AudioSet(CancellationToken token,
        [Description("Desired volume from 0 to 100. Omit to leave it as is.")] int? volumePercent = null,
        [Description("Desired mute state. Omit to leave it as is.")] bool? muted = null, bool dryRun = true) =>
        Operation("audio-set", token, dryRun, audio: new(volumePercent, muted));

    private Task<CallToolResult> SettingsGet(CancellationToken token) => Operation("settings-get", token);

    private Task<CallToolResult> SettingsSet(CancellationToken token, bool? soundEnabled = null, bool? hapticsEnabled = null,
        [Description("Left or Right.")] string? panelSide = null,
        [Description("100, 115, 130, or 150 percent.")] int? scalePercent = null,
        [Description("Stick deadzone from 8000 to 28000.")] int? deadzone = null,
        [Description("Guide open limit from 500 to 3000 milliseconds in steps of 100.")] int? guideHoldMs = null,
        bool dryRun = true) =>
        Operation("settings-set", token, dryRun, settings: new(soundEnabled, hapticsEnabled, panelSide, scalePercent, deadzone, guideHoldMs));

    private Task<CallToolResult> MediaGet(CancellationToken token) => Operation("media-get", token);

    private Task<CallToolResult> MediaControl([Description("play, pause, next, or previous.")] string command,
        CancellationToken token, bool dryRun = true) => Operation("media-control", token, dryRun, media: command);

    private Task<CallToolResult> Operation(string actionId, CancellationToken token, bool dryRun = false,
        AutomationAudioChange? audio = null, AutomationSettingsChange? settings = null, string? media = null) =>
        Session(new()
        {
            Verb = "execute",
            ActionId = actionId,
            DryRun = dryRun,
            AudioChange = audio,
            SettingsChange = settings,
            MediaCommand = media
        }, token);

    private static CallToolResult Logs([Description("Number of recent records, from 1 to 200.")] int tail = 50)
    {
        var reply = LogCommands.ReadTail(tail);
        return ToolResult(JsonSerializer.SerializeToElement(reply, CommandLogJson.Default.CommandLogSnapshot), reply.Outcome is not ("Completed" or "Empty"));
    }

    private static async Task<CallToolResult> Diagnostics(CancellationToken token)
    {
        var reply = await LogCommands.Export(token);
        return ToolResult(JsonSerializer.SerializeToElement(reply, CommandLogJson.Default.DiagnosticsExportReply), reply.Outcome != "Completed");
    }

    private Task<CallToolResult> UpdateStatus(CancellationToken token) => Update(["update", "status"], token);
    private Task<CallToolResult> CheckUpdate(CancellationToken token, [Description("testing or stable. Omit for the selected channel.")] string channel = "", int timeoutSeconds = 180) =>
        Update(UpdateArguments(["update", "check"], "", channel, false, timeoutSeconds), token);
    private Task<CallToolResult> InstallUpdate(CancellationToken token, string buildId = "",
        [Description("testing or stable. Omit for the selected channel.")] string channel = "", bool dryRun = true, int timeoutSeconds = 180) =>
        Update(UpdateArguments(["update", "install"], buildId, channel, dryRun, timeoutSeconds), token);
    private Task<CallToolResult> Rollback(string buildId, CancellationToken token, bool dryRun = true, int timeoutSeconds = 180) =>
        Update(UpdateArguments(["rollback", buildId], "", "", dryRun, timeoutSeconds), token);
    private Task<CallToolResult> CancelUpdate(string jobId, CancellationToken token) => Update(["update", "cancel", jobId], token);

    private static string[] UpdateArguments(string[] command, string buildId, string channel, bool dryRun, int timeoutSeconds)
    {
        List<string> args = [.. command, "--timeout", timeoutSeconds.ToString(CultureInfo.InvariantCulture)];
        if (buildId.Length > 0)
            args.AddRange(["--build", buildId]);
        if (channel.Length > 0)
            args.AddRange(["--channel", channel]);
        if (dryRun)
            args.Add("--dry-run");
        return args.ToArray();
    }

    private async Task<CallToolResult> Update(string[] args, CancellationToken token)
    {
        var applying = false;
        var reply = await UpdateCommands.Execute(args, (endpoint, request, cancellation) =>
        {
            applying |= request.Verb == "update-apply";
            return Program.Send(endpoint, request, cancellation);
        }, token, pipe);
        var result = AutomationResult(reply);
        if (applying && reply.Outcome is "Requested" or "Unknown")
            result.Meta = new JsonObject { [Reconnect] = true };
        return result;
    }

    private static CallToolResult AutomationResult(AutomationReply reply) =>
        ToolResult(JsonSerializer.SerializeToElement(reply, AutomationJson.Default.AutomationReply), !Program.Succeeded(reply));

    private static CallToolResult ToolResult(JsonElement data, bool error) => new()
    {
        Content = [new TextContentBlock { Text = data.GetRawText() }],
        StructuredContent = data,
        IsError = error
    };
}

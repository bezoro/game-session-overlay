# Local MCP server

Game Session Overlay ships a Model Context Protocol server over standard input and output. Configure a local MCP client to launch the installed `ApolloOverlay.Cli.exe` with the argument `mcp`. Run the client in the same Windows session, logon, and elevation level as the overlay. The overlay can stay hidden. Offline log reads and exports also work when the overlay is closed.

```json
{
  "mcpServers": {
    "game-session-overlay": {
      "command": "C:\\path\\to\\Game Session Overlay\\current\\ApolloOverlay.Cli.exe",
      "args": ["mcp"]
    }
  }
}
```

Use the actual installation directory from the [CLI setup instructions](cli.md#update-from-a-terminal-or-agent). The executable path is sufficient. Use a working directory outside the installation so Velopack can replace the application files.

| MCP tools | CLI equivalents |
| --- | --- |
| `get_status`, `list_targets`, `list_actions` | `gso status`, `gso targets`, `gso actions` |
| `execute_action`, `cancel_action`, `get_result`, `get_trace` | `gso execute`, `gso cancel`, `gso result`, `gso trace` |
| `get_audio`, `set_audio` | `gso execute audio-get`, `gso execute audio-set` |
| `get_settings`, `set_settings` | `gso execute settings-get`, `gso execute settings-set` |
| `get_media`, `control_media` | `gso execute media-get`, `gso execute media-control` |
| `read_logs`, `export_diagnostics` | `gso logs`, `gso diagnostics` |
| `get_update_status`, `check_updates` | `gso version`, `gso history`, `gso update status`, `gso update check` |
| `install_update`, `rollback_update`, `cancel_update` | `gso update`, `gso rollback`, `gso update cancel` |
| `shutdown_overlay` | `gso shutdown` |

## Discover and simulate a session action

1. Call `list_targets` for the latest window identities and snapshot revision.
2. Call `list_actions` for action IDs, supported intents, and whether a target is required. Actions with `Scope` equal to `session` can be used from any overlay route. A dry run checks eligibility for the specific target.
3. Call `execute_action` with `{"actionId":"focus","targetId":"TARGET_ID","revision":123,"dryRun":true}`, using the ID and revision from your snapshot. Expect `Outcome: DryRun` and `Dispatched: false` for an eligible target. For actual execution, set `dryRun` to `false`. Refresh discovery when the context revision or target is rejected.

`execute_action`, `set_audio`, `set_settings`, `control_media`, `install_update`, and `rollback_update` default to dry run. The tool result includes the CLI JSON receipt in `structuredContent`. Inspect `Outcome`, `Terminal`, and any request or job ID to determine what happened. An uncertain result requires observation before trying the action again. Window closing retains the overlay confirmation.

Use the typed audio, settings and media tools without changing the visible route. For example, call `set_audio` with `{"volumePercent":35,"muted":false,"dryRun":false}`. `set_settings` saves only supplied values and protects pending manual edits. `control_media` accepts `play`, `pause`, `next` and `previous`. Read the [operation reference](cli.md#session-actions) for accepted values and receipt semantics.

## Updates and diagnostics

Disconnect other MCP sessions before changing versions, including when using the GUI or CLI updater. An MCP server that requests application of a version sends the receipt with `game-session-overlay/reconnect` in `_meta`, then exits to release the installed binaries. Reconnect after the restart and use `get_update_status` to confirm the expected build and a new process identity. A receipt with `Outcome: Requested` confirms the restart request and observed exit of the previous overlay instance.

`export_diagnostics` returns a local Markdown file path containing all retained log files at the time of the export. Use `read_logs` for a bounded inline tail. An export reports the CLI build identity. Live state, logger health, and pending writes are unknown in a disk export. The report stays in the local temporary directory until removed.

The installation includes the MCP SDK and its dependencies with their [third-party notices](../src/ApolloOverlay/Assets/ThirdParty/Mcp/ATTRIBUTIONS.txt).

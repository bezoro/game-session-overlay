# Overlay CLI

`gso.cmd` and `ApolloOverlay.Cli.exe` ship with the installed app and communicate with the running instance, including while it is hidden. They do not start or elevate the GUI, retry requests, or accept arbitrary keys, executable paths or scripts.

## Update from a terminal or agent

Install the current Setup once to obtain the CLI and update API. An older installation without those components needs this initial step. Subsequent commands use the same Velopack updater as the interface. Public release lookup and download require no GitHub login or token.

In PowerShell, locate the current installation's CLI:

```powershell
$install = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GameSessionOverlay').InstallLocation
$gso = Join-Path $install 'current\gso.cmd'
& $gso version
& $gso update check --channel testing
& $gso update --channel testing
```

Run as the same user, session and elevation level as the overlay. If the overlay is elevated, the terminal or agent must be elevated too. Update commands do not require focus or interface navigation; the window can remain hidden. Setup does not add `gso` to PATH: use the resolved path above or run `gso.cmd` from `current`.

| Command | Result |
| --- | --- |
| `gso version` | Identity of the instance running now. |
| `gso update status` | Current version, installation, operation, progress, catalog and history. |
| `gso update check --channel testing` | Query channel releases without installing; also accepts `stable`. |
| `gso update` | Query the selected channel, download the chosen version and request application with restart. |
| `gso update install --build BUILD_ID --channel testing` | Select an exact catalog release, including an earlier one. Without `--build`, choose the channel's newest release. |
| `gso history` | List locally preserved versions available for restoration. |
| `gso rollback BUILD_ID` | Prepare the specified preserved version and request application with restart. |
| `gso update cancel JOB_ID` | Request cancellation of the matching preparation. Does not undo an already-requested application. |

Each response is a JSON object. `Update.Version`, `BuildId`, `Commit` and `Channel` describe the queried instance's binary; `Pid`, `Started`, `Session` and `Instance` identify that execution. `Update.TargetBuildId` and `TargetVersion` describe the prepared destination. `JobId`, `Phase`, `Progress`, `Terminal`, `Available` and `History` allow progress tracking. Save or discard pending settings before changing versions.

When preparation starts, outer `Terminal: true` terminates only the start request. The job continues with `Update.Terminal: false` and is tracked through `update status`. Its progress and outcome have a separate receipt rather than permanently occupying CLI request history.

An update or rollback response with `Outcome: Requested` confirms the request to Velopack and exit of the old instance. **It does not yet confirm that the new version started.** The CLI process exits to permit binary replacement. Query the installation again with a fresh `gso version`; only a fresh `Completed` response with the expected `Update.BuildId` and a new process identity confirms the running destination. The query may return `Unavailable` during restart. No `--wait` spans executable replacement.

An agent can use this flow without resending the mutation:

```powershell
$before = (& $gso version | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0 -or $before.Outcome -ne 'Completed') { throw 'Current instance unavailable.' }
$requested = (& $gso update --channel testing | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw $requested.Reason }
if ($requested.Outcome -eq 'Requested') {
    $target = $requested.Update.TargetBuildId
    $confirmed = $false
    $limit = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 500
        $after = (& $gso version | ConvertFrom-Json)
        if ($LASTEXITCODE -eq 0 -and $after.Outcome -eq 'Completed' -and
            $after.Update.BuildId -eq $target -and
            ($after.Pid -ne $before.Pid -or $after.Started -ne $before.Started)) {
            $confirmed = $true
            break
        }
    } while ($limit.Elapsed.TotalSeconds -lt 60)
    if (-not $confirmed) { throw 'Destination not yet confirmed. Query version/status; do not automatically repeat installation.' }
    $after
} else {
    $requested
}
```

`--timeout 5..600` limits CLI preparation in seconds; the default is 180. `--dry-run` validates a change request without executing it. Track or cancel an active preparation from another terminal with `update status` or `update cancel JOB_ID`. An `Unknown` result requires observing `version` and `update status` before deciding what to do next. The wrapper preserves the CLI exit code; zero can also accompany `Requested` and does not replace version confirmation.

## Session actions

```powershell
ApolloOverlay.Cli.exe status
ApolloOverlay.Cli.exe targets
ApolloOverlay.Cli.exe actions
ApolloOverlay.Cli.exe execute route:integrations
ApolloOverlay.Cli.exe execute focus --target ID --dry-run
ApolloOverlay.Cli.exe execute steam-overlay --target ID
ApolloOverlay.Cli.exe result REQUEST_ID
ApolloOverlay.Cli.exe cancel REQUEST_ID
ApolloOverlay.Cli.exe trace
ApolloOverlay.Cli.exe shutdown
```

`targets` refreshes observations when no operation is active and returns opaque identities bound to HWND/PID/creation/generation. `TargetsAtUtc` records the last refresh separately from `AtUtc`. `actions` lists actual route controls, availability, reason, capability and intents. `--intent left|right` exists only for advertised adjustments. Finite routes are `route:home`, `apps`, `system`, `audio`, `integrations`, `options`, `details`, `customize`, `setup` and `result`; options/details require `--target ID`. A stale explicit target is rejected, never silently replaced.

| Capability | Behavior |
| --- | --- |
| `Execute` | Safe synchronous handler or cancellable focus/Steam owner. |
| `RequiresGesture` | Hold-to-close requires normal interaction; no bypass. |
| `RequiresInteractive` | File picker, legacy startup/shortcuts, `operation-details` and `dismiss-operation`, and asynchronous operations without a client guard. The CLI does not invoke these handlers. |
| `Unavailable`, `Denied`, `Unsupported` | Nothing dispatched. The response includes the reason. `diagnostic-dwell` is Unsupported at this stage. |

The Steam shortcut requests focus once and verifies identity plus 500 ms of foreground ownership. For `Shift+Tab`, it sends four separate events: Shift-down, at least 20 ms, Tab-down, at least 60 ms, Tab-up, at least 20 ms, then Shift-up. `Ctrl+Tab` retains its previous batch. Waits revalidate input, connection, target, focus, deadline and cancellation in slices up to 10 ms. UI requests retain release handoff; the CLI independently qualifies neutral input.

Cancellation, reopening, context loss or deadline expiry immediately prohibits further down events. The owner retains exclusion until bounded cleanup of possibly sent keys finishes. `cancel` confirms only the interruption request (`Terminal=false`); query `result` until terminal. Normal shutdown awaits this drain without blocking the window. Cleanup after a crash or force termination is not guaranteed.

`Requested` requires four accepted events and observed release; it does not confirm Steam visibility. `Partial` means a known incomplete send with keys released; `Unknown` means uncertain count or release. Optional `Sequence` in result/trace records configuration, minimum intervals, qualified focus, up to four normal events and two cleanup ups, monotonic offsets, started calls, counts and final mask. Cleanup counts are separate. Physical neutral input is required: reads cannot perfectly distinguish physical Shift/Tab from the same injected keys. Do not repeat an uncertain toggle. Inspect `result`, `trace` and visual evidence before re-establishing the initial state.

Every mutation receives a server ticket, valid on that connection for up to ten seconds. Old, expired, disconnected, changed or evicted ledger identities do not execute again. Persisted intents lacking terminal outcomes recover as `Unknown` after restart, without redispatch. `--dry-run` validates without a mutating handler or durable intent. Preview outcomes are `Simulated`.

The normal pipe requires the same user **and elevation**, logon and session. Without access to the High instance, queries may return observation files marked `Snapshot` with `AtUtc`; these can be stale and authorize no mutations.

## Restricted bridge, opt-in per run

The bridge is off by default and during autostart. Only after binary review and explicit authorization may an elevated run receive:

```text
ApolloOverlay.exe --automation-bridge PID:START_TICKS:HWND [--allow-self-shutdown]
```

The target resolves to its current complete identity. The bridge permits only focus, that target's configured Steam shortcut and, if granted, shutdown of its own instance. It denies launch, paths, settings, audio, other targets and presets. The client uses `--bridge`; there is no automatic elevation. A restarted game requires a new grant. Actual Medium→High verification and real game behavior are authorized-delivery gates, separate from Medium fixtures.

`shutdown` responds `ShutdownRequested`, invokes normal closing and returns `Completed` only after observing exit of the exact PID/creation identity. No force-kill is used. Cancellation before commit prevents Close and records terminal `Cancelled`; after an attempted commit, the ledger records `Unknown` and the client continues observing exact exit. Cancellation cannot undo shutdown after commit.

Coverage is not universal: physical controllers, actual audio, dialogs/gestures, cross-integrity IPC and Steam visibility require their own observations. Fixture suites do not claim to have exercised all of them.

# Command diagnostics

The overlay records local structured diagnostics for meaningful commands from the controller, mouse, keyboard and CLI. No cloud upload or telemetry service is involved. The journal uses the pinned [Serilog packages](third-party-logging.md), separately from the automation safety ledger and Velopack's own updater log.

## Find and inspect logs

Files live in `%LOCALAPPDATA%\Bezoro\GameSessionOverlay\logs\commands-YYYYMMDD.jsonl`. Size rotation adds a numeric suffix, such as `_001`. Existing plaintext `overlay.log` files from older versions are preserved but receive no new entries.

The installed CLI can inspect recent records even when the overlay is closed:

```powershell
& $gso logs
& $gso logs --tail 200
```

Resolve `$gso` as described in [CLI setup](cli.md). This command reads local files only. It does not start the overlay, open a connection, elevate, apply an update or write a log about its own inspection. It returns JSON, with `Directory`, inspected `Files`, `Count`, `Entries`, `SkippedRecords`, `SkippedFiles` and `LimitReached`. An absent log directory returns `Outcome: Empty` successfully. `--tail` accepts 1 through 200; the default is 50.

The reader bounds enumeration, files, byte windows, record size and output. It skips incomplete or malformed records, tolerates rotation while reading, rejects linked directories and skips linked files. A bounded tail may omit older records; inspect the returned limit/skipped indicators rather than treating it as an exhaustive export.

While the overlay is running, `gso status` also reports `Snapshot.Diagnostics`: directory, buffered events, dropped events, write failures, active/untracked operations, drain timeout and the last error type/HRESULT. This makes an unwritable log location distinguishable from a quiet application. The normal CLI identity/session/elevation checks still apply to `status`.

## Read an operation

Each JSON line has a schema version, UTC timestamp, application version/build/commit/channel, process ID and a random logging session ID. `Command`, `Source` and `Route` identify the semantic action and origin. Sources distinguish controller, keyboard, mouse, system and CLI/bridge requests.

Operations use `Event: started` and `Event: finished` with the same `OperationId`; terminal records include monotonic elapsed `DurationMs`. Related dispatch, input-release handoff and actual effect records share a hashed `RequestId`. Targets use hashed identities. Single state observations use `Event: event`.

For example, a UI handler returning after queuing an action records `Requested`, not a successful external effect. The later service operation records its actual result. `Completed`, `Requested`, `Simulated`, `Failed`, `Unavailable`, `Partial`, `Unknown` and `Cancelled` retain their distinct meanings. A requested update/restart does not establish that the new version has started. Query `gso version` after restart to establish that fact.

Coverage includes:

- Startup, existing-instance requests, Windows startup/search integration, exit and unhandled exceptions.
- Action activation, semantic Back/options/tab commands, settings draft changes and saving. Sound, vibration and Guide changes record their safe on/off values; scale, deadzone and Guide-limit changes record numeric settings without user paths.
- Overlay opening and actual focus confirmation/refusal, hide interruption, return-focus results, deferred dispatch and cancellation before sending.
- Actual window/system effects, app/system audio changes, session utilities, profile/appearance saves, capture/gallery operations and failures, including results suppressed from an already-closed or superseded interface.
- Update lookup, download, verification, rollback preparation, application request, cancellation and late failures. Progress polling does not emit an event for each percentage.
- CLI requests, validation refusal, dry runs, transport/backend faults and asynchronous update jobs. Dry runs may append diagnostic evidence but do not mutate game/settings/update state or consume an execution-ledger entry.

Selection movement, idle polling, individual character keys, editor buffers, full command-line payloads, window titles, profile names, user file paths and exception messages are not recorded. Technical exceptions retain type, HRESULT, native error code when available and bounded method-only stack information. Authentication/request tokens are hashed before entering this journal. The existing restricted automation ledger/trace has its own retention and authority rules; this diagnostic journal does not replace them.

## Bounds and failure handling

The asynchronous queue holds up to 1,024 events. Files roll daily or around 4 MiB, retaining at most eight files and at most 14 days of current structured logs, nominally about 32 MiB. There is a separate bound of 256 tracked in-flight operations. A slow/full/unwritable sink never authorizes a retry or blocks input waiting for disk writes. Losses are counted and, when a writable sink is available, summarized by a diagnostic health event.

Normal shutdown terminalizes unfinished tracked operations as `Unknown`, then attempts a bounded two-second drain. Fatal exception observation preserves the existing termination policy. `FlushAsync` is a queue-drain barrier, not proof that disk writes succeeded: consult health counters. A disk failure can require restarting the logging session after repairing its location because the official file sink backs off failed opens. Abrupt process termination or power loss can lose queued records. Use this journal for debugging, not as an unconditional durable audit guarantee.

## Report a problem

Keep the version/build, logging session, operation/request identifiers and the records surrounding the failure together. Include `gso status` diagnostics when the process is available. For a focus problem, include the opening result and native focus checkpoints; for updates, include both the preparation/apply records and a fresh version query. Review files before sharing them: metadata can still reveal usage times and application behavior even though typed text and private paths are excluded.

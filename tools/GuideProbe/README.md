# GuideProbe

This .NET 10/Windows x64 diagnostic tool observes GameInput v2 and XInput. It is separate from the overlay: it does not open its interface, send keys, intercept controller input or change product settings. The [main README](../../README.md) describes the product's current activation policy.

The solution builds this tool, and CI runs synthetic tests only:

```powershell
dotnet tools/GuideProbe/bin/Release/net10.0-windows/GuideProbe.dll --self-test
```

## Explicit hardware queries

Run these commands in a Windows session to investigate an actual issue. The output must be a new file. Ctrl+C cancels observation.

```powershell
# Advertised capabilities and buttons; up to 60 seconds.
GuideProbe.exe --device-check --output capabilities.jsonl

# Find a unique public XboxGuide label before observing edges.
GuideProbe.exe --raw-guide-check --output raw-guide.jsonl

# Passive observation lasting 90 to 120 seconds.
GuideProbe.exe --duration-seconds 90 --output guide.jsonl
```

GameInput uses public v2 contracts. Ephemeral identities group events within this run, without recording pointers, serial numbers or window titles, or automatically mapping a device to an XInput slot. Capability options use the local `BackgroundInput + BackgroundGuide` policy. Passive mode uses `BackgroundGuide`. Failure in one source does not prove that Guide is globally absent.

Records distinguish native timestamps in microseconds, monotonic arrival in milliseconds and down/up pair durations. Orphan edges, repetition, a regressing clock, disconnection, overflow or incomplete reads cannot establish a confirmed duration. Limits are a queue of 256, 16 devices, 6,000 ordinary records and copies of up to 128 buttons/labels.

Cleanup stops all tokens before removing them, drains callbacks and only then releases devices, context and DLL. If the runtime does not confirm removal, those references remain held until process exit to prevent use after release. The result stays incomplete.

Isolated observation does not associate a device with the person's physical controller. Guide delivery, advertised support and TV operation are separate conclusions. Reports retain `inconclusive` when that association or the required reads have not been confirmed.

Public callback reference: [GameInputSystemButtonCallback](https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/input/gameinput/functions/gameinputsystembuttoncallback). Synthetic tests cover v2 signatures, output pointers, offsets, reconnection, reference retention and classification. They do not query hardware.

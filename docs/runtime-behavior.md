# Runtime behavior and diagnostics

For contribution, start with [CONTRIBUTING.md](../CONTRIBUTING.md) and the [architecture map](architecture.md). Installation, TV setup and common recovery steps are in the [user guide](<../Guia de uso.md>).

Game Session Overlay is a native WPF/.NET 10 application for Windows 11. Its self-contained build needs neither the SDK nor administrator privileges for ordinary use. Apollo is optional; a controller connected directly to the PC uses the same interface.

## Installation and startup

Use **GameSessionOverlay-testing-Setup.exe** or the stable Setup from [Releases](https://github.com/bezoro/game-session-overlay/releases). Installation and updates use **Velopack 1.2.161**, with shortcuts and a Windows uninstall entry. Startup registration launches the app hidden at sign-in and preserves an existing user opt-out.

Double-click the tray icon or choose **Open overlay**. `ApolloOverlay.exe` starts hidden; `--show` requests a visible opening. Launching the installed shortcut reuses the current instance. **Exit** closes the app normally.

**Restart as administrator**, available in System and the tray, requests Windows consent for the current session. The existing instance exits only after verifying the elevated handoff. This does not itself persist elevated startup. In an elevated instance, **Administrator startup** separately manages the current user's highest-privilege interactive logon task. Startup Off and Windows opt-outs remain respected.

The installed `startup-task.ps1 -Action Install` also supports explicit administrative task setup. Run it in the permanent installation folder from an administrator session of the intended user. The task uses no stored password. `-Action Uninstall` removes it and disables startup. The actual sign-in and scheduled-task behavior still need target-machine observation.

## Input and handoff

- Controller: a configured short Guide press opens on release; Guide also hides with release protection. D-pad/left stick navigates, A selects, B returns, X opens session/window options, and LB/RB switch tabs. Simultaneous direction and A moves selection without activation. Ctrl+Alt+Space and LB+RB+Start are not overlay activation bindings.
- Keyboard: arrows, Tab/Shift+Tab, Enter/Space, Esc, X and PageUp/PageDown. Readers and sliders retain an exit to actions with Tab/Shift+Tab.
- Mouse: actions, tabs, scrolling and controls accept pointer input. Input-source prompts adapt without rebuilding an editor or changing its focus.
- Close window: release, then start a new 1.5-second A/Enter hold. Release, B, disconnection or focus loss cancels. Steam, Playnite, Explorer and technical windows are protected. No force termination is offered.

Before hiding, switching an app, sending a shortcut or exiting through the interface, the overlay requires all observed controls and keys to remain released for 100 continuous milliseconds. New input restarts that wait. B/Esc cancels pending actions; when already dismissing the panel, B/Esc/Guide reaffirms dismissal.

If focus, controller reads or target identity becomes uncertain, restoration and external effects for that request are permanently discarded. Local dismissal still waits for every readable active key, button, stick or trigger and the release interval. If previously active input becomes unreadable, press Escape again and release to authorize local hiding only, while waiting for other readable inputs. Repeating the original press does not authorize this fallback. An unknown read proves neither release nor gesture consumption.

The wait prevents ordinary handoff while the triggering gesture is still pressed. It does not block other applications from reading XInput in the background. The explicit shared-input warning remains visible.

## Guide and foreground activation

**Guide/Xbox polling is experimental and enabled for new settings.** It uses an undocumented legacy XInput API. Existing `GuideXInputEnabled:false` stays disabled until explicitly changed in System.

While hidden, opening requires a valid short press released after a baseline release. The default maximum hold is 2.0 seconds. Holding longer, disconnecting or failing a read does not open the panel. System offers a 0.5–3.0-second slider in 0.1-second steps; the limit is captured at the beginning of each press and persists only after saving.

Every opening first tries ordinary activation. The provisional window stays transparent, and navigation remains disabled, until native foreground ownership and WPF activation are both observed. A guarded disposable child performs the native fallback with a 900 ms request deadline and a kernel kill-on-close job. Cancellation and cleanup are independent of the WPF dispatcher. Child exit is diagnostic evidence; the exact target HWND, owner thread/PID and WPF activation determine admission.

If activation is refused, the opening hides immediately and reports tray recovery instead of leaving an unusable panel over the game. A new Guide/tray opening can retry. Pending attempts accept safe B/Guide/Esc cancellation. There is no continuous foreground-stealing loop. Owned-dialog return starts another confirmed cycle while preserving page, selection and session.

Open/close fades are brief and reversible, with Windows animation and high-contrast restrictions respected. Handoffs requiring immediate focus, such as the Steam shortcut, hide without waiting for a fade.

The overlay does not consume Guide globally. Steam's Guide shortcuts and Game Bar's controller activation may need disabling to avoid competing actions; Win+G and Win+Tab remain available. Confirmed foreground focus does not isolate controller APIs from background readers. See [controller focus and isolation](controller-isolation.md).

## Session actions

The selected game/window identity precedes a separate **Resume** action, Quick access, Open apps, session audio/utilities and Recent. Resume targets the exact preserved window. X or right-click opens options; there is no duplicate ellipsis. Options provide application audio, minimize, another window, details and protected close.

The Details reader exposes full titles and technical identity. A/Enter enters reading, arrows scroll, and left/right returns to actions. In closing confirmation, the right stick scrolls the full title; keyboard users can use PageUp/PageDown. Application icons load in the background.

Minimize and audio adjustments keep the panel open. An accepted request is not completion: partial, refused and unknown outcomes retain readable details. B/Esc before dispatch cancels without effects; after dispatch it stops observation only, because an already-sent change cannot be undone. There is no automatic retry. An in-flight native call may exceed its observation deadline; that result becomes uncertain and does not authorize another write.

Application audio addresses sessions of the exact process, including identified inactive sessions and outputs. It does not include child processes, future sessions or global volume as a fallback. The mixer separates identified applications. Windows master volume and default communications-microphone mute are distinct controls. Output selection targets game/media playback; physical TV volume is not controlled.

Quick access requests Playnite, Steam Big Picture, Game Bar, Steam Overlay/Big Picture menu, Task View and Windows audio according to their real capabilities. Task View requests Win+Tab without requiring a game. A verified Big Picture context uses F1 for its menu; an identified Steam game module uses the configured overlay shortcut. Sending a shortcut does not confirm menu visibility.

Cancellation while awaiting Game Bar/Big Picture/Steam focus prevents unsent stages. A focus request already sent cannot be undone. Before key dispatch, target identity, request lifetime and input release are checked again. A previously observed connection remains relevant even when its sample ages; disconnection does not establish release.

Recent stores at most five verified identities for this run. Previous window returns to the last observed window different from the current opening. Only a confirmed foreground switch updates history. Enumeration, selection, library launch and minimize do not promote a window. Session selection can also be cleared.

## Layout, appearance and metadata

Automatic monitor selection follows the active window and uses the preserved session window when the tray is foreground. Explicit monitor, left/right side and 100/115/130/150% scale remain available. Effective scaling combines automatic density (1.0–1.5) with the chosen scale. Text is not reduced merely to fit. Bounds use the monitor work area, DIP/DPI conversion and scrolling. Compact Save/Discard and other critical actions remain pinned.

The default palette is slightly warm charcoal with restrained amber and warm off-white text. Light mode has its own palette; Windows high contrast uses SystemColors. All semantic colors are editable through presets, RGB and local hexadecimal entry. Unsafe intermediate drafts remain editable while chrome retains its last readable palette. Saving colors preserves unrelated settings drafts.

A clear selection outline appears immediately. Short sheen, press compression and finite route feedback distinguish input acknowledgement from actual completion. Clocks and transient state stop on hiding or context changes. There is no invented artwork, continuous capture, game injection, driver installation or telemetry.

Controller metadata uses the active observed slot and connection epoch. A measured percentage is shown only when valid corresponding evidence exists; XInput categories remain categories, and unknown charge is **Battery N/A**. Virtual streaming devices do not establish the physical TV controller model, connection or battery. Queries occur on showing/changing the controller and at most every five seconds in foreground; there is no hidden battery polling.

Sounds and vibration are independent opt-ins, off by default. **Test feedback** requests a sample. Deliberate navigation, confirmation and unavailable actions have brief cues; hover, data refresh and progress do not produce repeated sounds. Vibration requires the active controller and confirmed focus; hiding, disconnecting, disabling or exiting invalidates pulses. Accepted XInput output proves neither perception nor isolation from another app's later vibration writes. Preview uses simulated services and sends no audio or vibration.

## Utilities and settings

Profiles store TV/Desktop or custom monitor, scale, panel side, optional output/volume and game binding. Saving does not apply a profile. Applying is explicit; undo preserves values changed afterward.

Media uses actual Windows media-session sources and available play/pause/previous/next commands. The virtual keyboard has an explicit destination and text buffer; **Insert text** never presses Enter. Profile-name and hexadecimal editing stay local. Captures hide the panel before saving the monitor image; a local gallery, details and folder are available. Gallery/session images share bounded off-thread decoding with cancellation and stale-result guards. There is no video or continuous recording.

The optional three-step setup can be skipped, closed and reopened. It does not implicitly enable Guide, sound or vibration or save settings. Drafts survive navigation; Save/Discard remain explicit. Additive settings include `FavoriteQuickActions` (absent/null means default order; `[]` hides all), `UiScalePercent`, `PanelSide`, `SoundFeedbackEnabled`, `HapticFeedbackEnabled`, `SetupSeen` and `Appearance`. Lists are copied and compared by content/order. Malformed optional fields recover their own defaults without discarding unrelated valid settings.

Settings are explicitly saved to `%LOCALAPPDATA%\Bezoro\GameSessionOverlay\settings.json`; the previous configuration and corrupt source are preserved. JSON version 3 accepts versions 1/2 and migrates in memory without automatic rewriting. Historical activation fields remain compatible but activate nothing. Missing/invalid `GuideOpenMaxHoldMs` uses 2000. Structured local command logs use an asynchronous bounded queue, daily/size rotation and bounded retention. They record command outcomes and technical errors without typed text or private titles/paths. Focus checkpoints record native outcomes and observed confirmation, not per-frame logs. See [diagnostics](diagnostics.md) for coverage, limits and offline CLI inspection.

The installed version appears in the header and **System → Version and updates**. Stable/Testing updates, verified downloads and preserved local rollback use Velopack. Public lookup needs no login or token. The installed `gso.cmd` supports JSON automation; fresh post-restart queries establish the running version. See [updates](updates.md) and [CLI](cli.md).

## Build and diagnostics

With the .NET 10 SDK:

```powershell
dotnet build ApolloOverlay.sln -c Release
dotnet tests/ApolloOverlay.Tests/bin/Release/net10.0-windows10.0.19041.0/ApolloOverlay.Tests.dll artifacts/behavior-report.json
dotnet publish src/ApolloOverlay/ApolloOverlay.csproj -c Release -r win-x64 --self-contained true -o artifacts/new-publication/publish
```

`./scripts/verify.ps1 -OutputVersion native-vN` automates checks into a new directory. Review the producing code and files before delivery; do not overwrite a reviewed publication.

`--preview` uses simulated window effects and empty context; it does not represent a real game. Normal operation uses real context and effects. `--capture-native DIRECTORY`, `--capture-alpha DIRECTORY` and `--native-check DIRECTORY` operate only on owned validation windows; the last includes a reversible audio test.

[Windows checks](https://github.com/bezoro/game-session-overlay/actions/workflows/windows-checks.yml) builds on Windows, runs CLI/behavior tests, renders WPF fixtures and packages the self-contained app. Reports remain available when a suite fails. A package from a failed run is diagnostic evidence only. Tests needing actual focus may fail without an interactive desktop; such failures are not converted into passes. Inspect the run for the exact commit before using its package.

Exclusive fullscreen, specific DX11/12/Vulkan games, Bluetooth/virtual controllers, HDR, mixed DPI, virtual displays, external overlays and Apollo/Aurora streaming require the [physical checklist](pilot-checklist.md). Missing hardware does not establish compatibility. Fixture timings and screenshots do not certify physical input latency or GPU frame rates.

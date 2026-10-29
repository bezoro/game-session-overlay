# Game Session Overlay

Control a game session with a controller, mouse or keyboard, on your PC or a TV through streaming. This native WPF/.NET 10 application targets Windows 11 x64 and works with or without Apollo.

## Install and open

Download Setup for your preferred channel from [Releases](https://github.com/bezoro/game-session-overlay/releases). The installer includes the runtime, shortcuts and uninstaller. The overlay starts hidden at sign-in; opening and closing the panel use a short transition.

Press the **Guide/Xbox button**, **double-click the tray icon** or choose **Open overlay** in the tray menu. Guide uses a legacy XInput API and remains experimental. The tray provides an alternative that works without a controller.

Normal use does not require administrator privileges. The [user guide](<Guia de uso.md>) explains optional elevated startup, monitor selection and TV setup.

## During a game

- Resume the session window, switch apps and open Playnite or Steam Big Picture.
- Adjust app volume, the mixer, default-microphone mute and Windows audio output.
- Apply TV/Desktop profiles or create profiles for your monitor, scale, panel side and audio.
- Control supported media, type with the virtual keyboard and save local screenshots.
- See controller model and battery information when Windows provides it. Ambiguous metadata and unknown charge remain explicit.
- Use a clear focus outline, short press feedback and a subtle selection sheen. Interface sounds and vibration are separate options.

The default dark theme combines warm charcoal, a restrained amber accent and warm off-white text. Appearance provides presets and editing for all semantic colors, with separate dark/light palettes and readability checks. Windows high contrast keeps its system colors. See the [reference mapping and validation limits](docs/ui-reference-validation.md).

| Input | Navigate | Select | Back | Tabs |
| --- | --- | --- | --- | --- |
| XInput controller | D-pad / stick | A | B | LB / RB |
| Keyboard | Arrows / Tab | Enter / Space | Esc | PageUp / PageDown |
| Mouse | Pointer and scrolling | Click | Back button | Click |

X opens window options through the controller or keyboard. Closing a window requires a fresh 1.5-second hold; the app does not force termination.

**Focus does not block controller input globally.** Games that read XInput in the background may still receive input. A streaming device may be virtual and may not report the TV controller's battery. See [compatibility](docs/controller-isolation.md) and the [real-use test checklist](docs/pilot-checklist.md).

## Updates and automation

The installed version appears in the header. In **System → Version and updates**, choose a channel, read changes, install and restart, or restore a retained version. The installer and updates use Velopack and do not require GitHub sign-in.

The installation includes `gso.cmd`, a JSON CLI for terminals and agents. See [commands, updates and rollback](docs/cli.md), [updater behavior](docs/updates.md) and [runtime behavior](docs/runtime-behavior.md).

## Contribute

Start with [CONTRIBUTING.md](CONTRIBUTING.md) and the [architecture map](docs/architecture.md). [global.json](global.json) pins the SDK.

```powershell
dotnet build ApolloOverlay.sln -c Release
./scripts/format.ps1 -Verify
dotnet run --project src/ApolloOverlay -- --preview
```

The solution includes the app, CLI, tests and diagnostic tools. CI builds with nullable checks and warnings as errors, verifies formatting, runs tests, renders WPF captures and checks Velopack packaging, installation, upgrades and rollback. Fixtures do not replace tests with a physical controller, game and TV.

Sources, licenses and vector provenance are documented in [icons](docs/third-party-icons.md). Update dependencies are documented in [Velopack](docs/third-party-updates.md).

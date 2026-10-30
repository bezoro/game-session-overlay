# Session command center

Game Session Overlay is a native WPF side panel for controller, keyboard and mouse use. It works with local PC play and with the controller Windows receives from a streaming host. Apollo is optional.

## Primary flow

The Session page prioritizes the selected game or window, a separate **Resume** action, ordered Quick access, Open apps, session audio and utilities, then Recent. Resume targets the exact selected window. X or right-click opens window options; there is no duplicate ellipsis. Apps and Audio remain the other primary tabs; the header gear opens System.

The header uses real application/controller metadata and a short installed version. Game imagery comes from the actual window icon or a validated local capture. No game library, artwork download, statistics or stream telemetry is inferred.

## Appearance and interaction

The default dark palette uses subtly warm charcoal surfaces and a restrained amber accent. Light mode has its own contrast-checked colors; Windows high contrast uses SystemColors. Segoe UI Variable Display/Text, shared spacing and radius resources, original vector frames and one clear focus outline keep the panel consistent at different sizes and DPI.

Appearance exposes presets and all semantic color roles, including surfaces, text, focus, controls, status, icons, input prompts and effects. A local draft supports preview, hexadecimal/RGB entry, Save, Discard and restore defaults. Unsafe intermediate edits remain editable while navigation uses the last valid palette. Saving colors requires readable contrast and preserves unrelated settings drafts. High-contrast colors always remain under Windows control.

Focus responds immediately. Short press and route feedback indicate received input; completion is a separate observed result. Motion uses opacity and render transforms, is interruptible, and stops on hiding or context changes. System animation restrictions remove unnecessary transitions. Sound and vibration remain independent opt-in preferences; failure of either cannot block an action.

Input prompts follow the latest deliberate controller, keyboard or mouse interaction without rebuilding editors. Selecting an action does not copy its description into the footer. Routine acknowledgements stay discreet; uncertain or failed results retain details and recovery actions.

## Feature boundaries

- Windows master volume, per-process audio and physical TV volume are distinct. Output selection and the default microphone mute reflect actual backend capabilities.
- Profiles save display, side, scale and optional audio settings. Saving does not apply a profile. Applying requires a deliberate action; undo preserves subsequent external changes.
- Media controls use actual Windows media-session title, artist and source. Unsupported artwork, seek and duration are not invented.
- The virtual keyboard keeps destination, buffer and insertion visible. Inserting text never presses Enter. Profile names and hexadecimal colors are local editing purposes and never use SendInput.
- Captures are deliberate local screenshots. Gallery thumbnails decode off the UI thread with bounded work, cancellation and identity checks. The overlay does not record video or capture continuously.
- Updates use Velopack, explicit Stable/Testing channels, verified packages and retained local rollback history. Installed build/commit and package facts are real values.

## Focus and input safety

A provisional opening remains transparent until both native foreground ownership and WPF activation are confirmed. Refusal hides it and provides tray recovery. Explicit administrator restart uses Windows consent; persistent elevated startup is configured separately. Input release, window/process identity, cancellation and fresh hold-to-close guards remain part of every handoff.

Window focus does not establish universal controller isolation. Background readers can still consume the public device. Physical controller identity, connection and battery are reported only when Windows provides corresponding evidence; virtual streaming devices do not prove the TV controller model or charge. See [controller focus and isolation](controller-isolation.md).

## Validation

Changes require meaningful behavior and lifecycle tests, actual attached-tree WPF captures, and three independent screenshot reviews. The acceptance threshold is at least 9/10 from each reviewer with no outstanding actionable findings. Screenshots, synthetic fixtures and hosted-runner measurements do not certify physical TV/controller behavior, game compatibility, sign-in or GPU frame rates. Approved previews are published through the Testing workflow before merging.

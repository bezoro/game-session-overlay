# Pending physical compatibility checks

Use the installed shortcut or tray. The product runs normally; no pilot flag or input-consent gate is required.

- USB/Bluetooth/virtual controller: confirm experimental Guide is enabled. After a neutral baseline with Guide released, make a short press and release within the configured limit. Navigate all pages, disconnect/reconnect and return with B. Use the tray when Guide is unavailable. Observe whether another app also receives Guide; polling does not block it globally.
- Borderless DX11/DX12/Vulkan: opening, Alt+Tab, return, refused focus and session switching. Exclusive fullscreen is not guaranteed.
- Steam/Playnite present and absent: Big Picture, shortcuts and launch/reuse. Treat visibility as confirmed only when observed. A Guide hold beyond the limit must not open on release. Short presses can also trigger Steam/Game Bar; physical long-hold/power-off behavior needs manual validation.
- Two monitors, mixed DPI, negative origins, Apollo virtual display, removed/changed resolution and 4K at 100% on the TV: confirm readable text and focus.
- Live light/dark changes, Windows high contrast, disabled Windows animations and HDR. Do not change user settings during automatic checks.
- Audio: change/disconnect endpoints with the page open; alter volume/mute from another app and confirm live refresh.
- Close an owned test window before trying a game: new 1.5-second hold, cancellation, unsaved work and refusal. Never force termination.
- Apollo/Sunshine → Moonlight/Aurora/TV: inspect capture visibility, input and Game Bar. A virtual monitor alone does not establish stream compatibility.
- Elevated games: test an ordinary opening, explicit **Restart as administrator**, rejected UAC, successful same-user restart and separately consented elevated startup. Confirm native/WPF focus and actual game input behavior; administrator privileges do not establish universal controller isolation.

Automatic checks use owned windows and their real context. Offscreen captures show layout, not hardware compatibility. Software-command timings do not measure physical-button or network latency.

## Session panel

- Activate Resume with mouse/A/Enter and options with X/right-click. Read a long title fully in Details; confirm actual focus and arrow-key scrolling to the end.
- In owned audio test apps, compare active/muted/mixed states and multiple outputs. The command affects that application, never global audio as a fallback. Check device loss, external changes and partial results; dispatch is not completion.
- Minimize only an owned window, observe the minimized state and keep the panel open. Previous/Recent must promote only confirmed foreground changes; enumeration must not mutate history.
- Reorder, hide and restore every shortcut. Test 100/115/130/150% scaling, both sides, monitor work areas and revealed selection. At 360×480 with pending settings and a retained result, Save/Discard stay fixed and reading/dismissal remain reachable.
- Traverse all optional setup steps, Back/Esc/Skip/Finish. Preserve drafts and save only explicitly. Check v1/v2→v3 recovery and an empty favorites list.
- With explicit hardware authorization, check active slot, reconnection, categorical/wired/unknown battery, no hidden polling and foreground polling at most every five seconds. Sound/vibration start off; verify deliberate cues, opt-in testing and stopping on focus loss/hide/disconnect. An accepted XInput call proves neither perception nor isolation.
- Inspect warm dark, light and custom colors from the normal viewing distance. Change paired text/background colors through a temporary unsafe draft; navigation must retain a readable palette, Save must reject unsafe contrast and Discard must restore the previous appearance. High contrast remains under Windows control.

Service tests use synthetic catalogs, COM, vibration and audio. Focus/reader tests operate only on owned windows, and captures render the app's own visual tree. They do not certify physical DPI, external audio or native positioning across every monitor.

## Audio, environment and session tools

- In Playnite fullscreen, open with Guide, verify actual focus and traverse actions. Return with B/Guide and confirm the launcher resumes. Repeat without Apollo and from the tray. Background XInput readers may still receive input.
- With two audio-playing apps, adjust only one in the mixer. Change volume/mute externally while the overlay is open; the selected slider must retain focus and reflect the new reading.
- Mute/unmute the default communications microphone and verify in Windows. Switch game/media output, remove a device and inspect results without silent substitution.
- Apply TV/Desktop and a custom profile. Change a value afterward, then undo: the later change must remain. Disconnect the profile's display/output and confirm an informative refusal.
- Use a compatible SMTC music/video app; test separate sources and offered commands. Confirm playback in the source app: API acceptance does not establish a track change.
- Insert accented text and emoji into an owned test editor. Check destination, no implicit Enter, focus loss and cancellation. Test mouse/keyboard editing and Tab exit.
- Deliberately capture the monitor, confirm the overlay is absent from the image, open gallery/folder and inspect the PNG. Test HDR/protected content separately; black images or altered colors are not approval.

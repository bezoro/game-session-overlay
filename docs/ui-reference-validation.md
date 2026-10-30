# Interface reference coverage

This follow-up implements the six user-provided concept boards and `IMPLEMENTACAO.txt` against the existing native WPF application. The user's later preferences take precedence over the boards' cold palette: warm charcoal, restrained amber/orange, warm off-white text and full semantic color customization.

## Reference mapping

| Board | Production routes |
| --- | --- |
| Session and windows | Hub, Apps, TargetOptions, Details, Confirmation |
| Audio | WindowsAudio, Mixer, AudioDevices |
| Session utilities | Profiles, ProfileEdit, Media, Keyboard |
| Captures | Captures, CaptureDetail |
| System and setup | System, Integrations, Customize, Setup, PlayniteBrowser |
| Updates and resilient states | Updates, UpdateReview, OperationResult, plus shared result/empty/unavailable states |

Appearance and ColorEdit extend these 22 routes for the requested complete color editor. The primary tabs remain Session, Apps and Audio. System is reached through one header gear.

## Deliberate adaptations

- The native side panel reflows at narrow widths; the boards' inspection layouts do not become a large dashboard.
- Game identity and Resume are separate. Quick access stays immediately below the selected game. Real icons and local captures replace illustrative artwork.
- Only one control has a focus outline. Hover, press and a short sheen use edge highlights that do not wash over text, input glyphs or sliders. Press feedback uses a render transform. No infinite decorative pulse is required to identify selection.
- Metadata and body text use readable off-white in the dark theme. Light mode has independent colors; Windows high contrast always uses system colors.
- All 49 semantic palette roles are editable. RGB and local hexadecimal entry work with the controller. Unsafe intermediate drafts remain editable while navigation retains the last readable palette. Saving colors does not save unrelated settings drafts.
- Output selection and default-microphone mute follow the actual backend. There is no unsupported microphone picker or physical TV-volume control.
- Media shows actual title, artist and source without an invented cover, duration or seek control.
- Captures use real local files, six cards per page and a shared two-decoder budget, also used by session artwork. Obsolete requests are cancelled and cannot publish into another view.
- Update versions, dates, sizes and verification state come from the installed build and update source. Rollback remains explicit. A frontend change does not establish universal controller isolation.

## Verification evidence

The capture harness targets all 24 routes at 572×940 and 360×480 in dark/light, plus resilient states and representative SystemColors mappings. It renders the attached WPF tree with actual native foreground checks; fixtures are not a physical TV test. Generated local PNG fixtures are labeled as test data.

`capture-manifest.json` records each case. `presentation-cost.json` records declared-environment fixture route/layout/bitmap-render timings and CPU/private-memory samples. Those measurements do not establish real GPU frame time, physical input latency, a same-machine before/after comparison or absence of leaks. Sign-in, UAC approval, elevated games and Apollo/TV behavior require target-machine observation.

Before publication, record the exact Windows workflow, behavior/package/integration results, screenshot artifact, three independent rendered-review scores and exact-head Codex review in the PR. No generated concept image is accepted as implementation evidence.

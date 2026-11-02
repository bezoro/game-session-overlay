# Session controls

Open the panel over your game to switch windows, adjust audio or reach your libraries. It works on Windows 11 with an XInput controller connected to the PC or supplied by an Apollo stream. Apollo does not need to be running for local use.

## First run

1. Download **GameSessionOverlay-testing-Setup.exe** from [Releases](https://github.com/bezoro/game-session-overlay/releases). If an older instance is running, exit it from the tray.
2. Run the Game Session Overlay Setup. Velopack installs the app, creates shortcuts and adds the Windows uninstall entry.
3. In **System**, choose the monitor, panel side and interface scale. Select the Playnite folder if you use it. Choose **Save** to keep the changes.

At the next Windows sign-in, the overlay starts hidden. A short press of **Guide**, the illuminated Xbox button in the middle of the controller, opens the panel when released. You can also double-click the tray icon or choose **Open overlay** from its context menu.

## Controller navigation

| Control | Action |
| --- | --- |
| D-pad or left stick | Move selection |
| A | Activate the selected action |
| B | Go back; hide the panel from Session |
| LB / RB | Switch tabs |
| X | Open window options in Session and Apps |
| Guide | Open or hide the panel |
| Left / right on a volume control | Adjust in 5% steps |

Release the buttons and sticks when returning to the game. The panel waits for release before handing focus back. If direction and A arrive together, only the selection changes; press A again to activate it.

**Resume** returns to the selected session window. If that window has closed, choose another one. Window options include application audio, minimize, details and close. To close a window, choose **Close window…**, release A, then make a new 1.5-second hold. Releasing early or pressing B cancels.

Controller input may still reach the game while the panel is open. Foreground focus does not block every other application's controller reads. Pause the game before navigating if an unintended action could disrupt play.

## Set up your TV

In **System**, start with automatic monitor selection. It follows the active window; when opened from the tray, it tries the preserved session window. Choose a specific monitor to keep the panel on Apollo's virtual display or another screen.

Increase the scale until titles are readable from your usual seat. The panel scrolls when space is limited. Selection has one clear outline, a short subtle sheen and press feedback. Windows animation restrictions and high contrast are respected.

In **Customize quick access**, keep the actions you use and put frequent actions first. Sounds and vibration are separate opt-ins, both off by default. Changes persist only after **Save**; **Discard** restores the saved settings.

Enable **Sounds** in System and use **Test feedback** to request a sample. Navigation, confirmation and back have different cues. Failure has a lower cue; completion sounds only follow confirmed results. Check that the sound actually reaches the TV: a playback request alone does not establish audible output.

The selected action keeps its focus outline. Activating it produces a brief highlight and gentle compression. Separate text and symbols distinguish pending, completed and uncertain outcomes.

**Windows audio** controls the PC's default output. Application audio in window options affects the identified audio sessions of that process. Neither guarantees control of the TV's physical volume.

**Appearance** offers warm charcoal, amber and off-white defaults, presets, and separate dark/light color palettes. Edit colors with RGB controls or local hexadecimal entry. **Keep color** adds the edit to the draft; **Save colors** persists it without saving unrelated settings changes. Unsafe intermediate colors remain editable while navigation keeps its last readable palette. Windows high contrast uses system colors.

## Update and restore an earlier version

The version appears in the header and in **System → Version and updates**. Build and commit identifiers distinguish the changes being tested.

1. Choose **Testing** for approved preview builds or **Stable** for releases from the main branch.
2. The repository is public. No GitHub account connection or token is required.
3. Choose **Check for updates**, select a version and read its changes.
4. Choose **Download update**. B / Esc cancels preparation without changing the installed version.
5. After verification, save or discard pending settings and choose **Install and restart**.

Earlier packages are preserved in a history folder separate from Velopack's cache. Restoration is an explicit choice; there is no automatic application-health rollback.

Use the earlier-version list to select a local build and confirm **Restore and restart**. This path works offline. Compatible settings preserved for that version are restored too. Local history lists versions that ran and had their package preserved.

Updates are published after their configured checks, without replacing earlier releases. An installation lacking the updater needs the current Setup once; after that, update navigation works with the controller and without authentication. See [update details](docs/updates.md).

## Troubleshooting

| Situation | What to try |
| --- | --- |
| Guide does not open the panel | Open it from the tray, check Guide in System, and release a short press within the configured limit. Guide polling is experimental. |
| Steam or Game Bar opens too | Disable their Guide-button activation settings. |
| Windows refuses overlay focus | The failed opening hides instead of leaving an unusable panel over the game. Open it again from the tray. For an elevated game, use **Restart as administrator** and approve Windows consent. |
| Panel does not appear over the game | Try borderless windowed mode. Exclusive fullscreen is not guaranteed. |
| Panel appears on the wrong screen | Choose the monitor in System and save. Recheck after adding or removing displays. |
| Playnite is not found | In System, browse drives and folders to its installation, select the offered Playnite folder and save. |
| An action result is uncertain | Use **Read** to inspect the full result and check the window before retrying. |

Mouse and keyboard are supported: click actions and tabs, use the wheel to scroll, arrows to navigate, Tab/Shift+Tab to move between actions, Enter/Space to select and Esc to go back or hide. Detail readers return to actions with Tab/Shift+Tab. On the Guide-limit control, Tab enters the slider and arrows adjust it. Prompts follow the last deliberate input source.

During focus recovery, navigation does not execute the action beneath it. Release and select again after focus is confirmed. To exit, open the tray menu and choose **Exit**.

The installation also includes a [CLI for agents and terminals](docs/cli.md) to query versions, update and restore preserved builds.

## Startup and removal

Normal startup does not require administrator privileges. Toggle **Start with Windows** in System, or disable it under **Windows Settings → Apps → Startup**. If you move the app folder, run it once from the new location to refresh its existing registration.

For an elevated game, **Restart as administrator** requests Windows consent for this session only. After an approved restart, **Administrator startup** separately configures the current user's highest-privilege startup task. Startup Off and Windows opt-outs remain respected. Elevation does not isolate controller input.

The installed `startup-task.ps1` is also available for explicit administrative setup. From PowerShell running as the intended user with administrator privileges, run `./startup-task.ps1 -Action Install` in the permanent installation folder. `./startup-task.ps1 -Action Uninstall` removes that task and disables its startup. Test the real sign-in session after configuring it.

To uninstall, exit from the tray and use **Windows Settings → Apps → Game Session Overlay → Uninstall**. Remove an elevated startup task if one remains. Settings and logs remain in `%LOCALAPPDATA%\Bezoro\GameSessionOverlay` until you delete them.

Known limits are documented in the [physical validation checklist](docs/pilot-checklist.md) and [controller focus and isolation](docs/controller-isolation.md). Build and diagnostic information is in the [README](README.md).

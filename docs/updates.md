# Versions and updates

Installation and executable replacement use [Velopack 1.2.161](https://github.com/velopack/velopack/releases/tag/1.2.161). The WPF app owns navigation, progress and the history presented in the overlay. The .NET `Velopack` package and official `vpk` tool are pinned to the same version.

## Install and update

Download `GameSessionOverlay-testing-Setup.exe` or `GameSessionOverlay-stable-Setup.exe` from [project releases](https://github.com/bezoro/game-session-overlay/releases) and run it. Setup creates the Start-menu entry and Windows uninstall option. The default installation is per user.

Open **System → Version and updates** to inspect the installed build, check for updates or choose an earlier version. The new package downloads before the overlay is asked to close. Official Velopack machinery applies it after the process exits and restarts the app.

| Channel | Source | Publication |
| --- | --- | --- |
| Stable | Push to `main` | Normal release |
| Testing | Authorized Windows checks dispatch on a repository branch with `channel: testing` | Prerelease |

Only builds passing Windows validation are published. Windows CI runs automatically on pushes to `main`, normally after merging. Feature-branch pushes and PR updates do not trigger it. Use local checks during development and dispatch Windows validation deliberately when a candidate needs Windows-only evidence or a Testing preview. Avoid duplicate runs: `testing` already includes the full validation, so a separate `validate` run immediately before it is unnecessary. Documentation-only and metadata-only changes do not justify a manual Windows run. The lightweight PR-title check runs only when a PR opens or its title changes, with read permissions and no publication access. The repository is also identified by ID `1430327913`, which remains valid after renaming.

`validate` runs checks without publication. `testing` publishes the selected branch commit after all checks pass, including before PR merge. PR events do not run the Windows pipeline; fork branches are not published by this path. A Testing dispatch does not publish to Stable.

For users, choose **Testing** in **System → Version and updates**, check for updates and choose **Install and restart** after download verification. No PR installer download is needed. The catalog identifies version and commit; preserved versions remain available for restoration.

A maintainer or agent with write access can publish an authorized preview from GitHub Actions by selecting the branch and `channel: testing`, or through the CLI. The optional `notes` input supplies updater release notes:

```sh
gh workflow run windows-checks.yml --repo bezoro/game-session-overlay --ref feat/session-command-center -f channel=testing
```

Dispatch requires a branch in the repository. Publication verifies the commit, authorized channel, compiled identity and hashes of the validated package. Testing is shared by published previews. A build becomes available in the overlay only after **Publish validated update** succeeds.

## Identify the version

The displayed version comes from compiled executable metadata. GitVersion 6.8.2 calculates `MAJOR.MINOR.PATCH` from commits and tags; CI adds the preview label and commit. Examples:

- `0.48.0-preview.55.1+abc1234`: preview of 0.48.0, run 55, attempt 1.
- `0.48.0+abc1234`: stable release; run number does not change its meaning.
- `0.2.0-dev+local`: local build without publication identity.

`ci-55-a1-abc1234` tracks the run separately from the version. Velopack's NuGet version uses the portion before `+`; the commit remains in the executable and metadata. Historical tags, including `0.47.1`, remain valid and establish migration ordering.

`feat` increments MINOR; `fix`, `perf`, `refactor`, `build` and `revert` increment PATCH. `!` or `BREAKING CHANGE:` increments MAJOR. `docs`, `style`, `test`, `ci` and `chore` alone do not create a stable release. The [contributor convention](../CONTRIBUTING.md#commits-and-versions) explains titles and examples.

A tag reserves the version; CI does not rebuild an existing stable release with different bytes. To retry a failed publication, rerun only **Publish validated update**, reusing the original approved artifact. Previews receive a new NuGet identity on each attempt. A preview requested from an already-reserved stable version uses the next PATCH so the updater still considers it newer.

## Restore an earlier version

History retains verified copies of packages already used, separate from Velopack's temporary cache. A preserved package can be restored without downloading it again. Older GitHub releases can also be selected while online.

Settings live outside executable directories. Restoration uses backups associated with the chosen version and preserves a copy of the latest settings. Velopack still installs and applies the package, including during rollback. A version can restore only settings it understands; additive fields introduced by newer builds may not survive an older build saving its configuration.

Download or integrity failure prevents application of that package. A completed download does not establish that the update ran successfully. There is no automatic rollback promise based on game or controller health; restoration remains an explicit interface action.

## Packaging and publication

`dotnet tool restore` installs the `vpk` version recorded in `.config/dotnet-tools.json`. The workflow publishes `win-x64` with the .NET runtime included and invokes the official packager:

```powershell
dotnet tool run vpk -- pack --packId GameSessionOverlay --packVersion VERSION --packDir PUBLISH --mainExe ApolloOverlay.exe --packTitle "Game Session Overlay" --channel testing --runtime win-x64 --outputDir RELEASES --noPortable --delta None --skip-updates
```

Update files use Velopack's own formats:

- `GameSessionOverlay-<version>-<channel>-full.nupkg`.
- `GameSessionOverlay-<channel>-Setup.exe`.
- `releases.<channel>.json`, the package index and hashes.
- `assets.<channel>.json`, the official CLI publication inventory for that build.

`build-info.json` records compiled identity inside the app. It verifies build provenance and does not replace Velopack's update index. Release notes also include `Version:`, `Build:`, `Commit:` and `Channel:` lines for identity presentation in the overlay.

Publication uses `vpk upload github` with a temporary job credential in `VPK_TOKEN`. The token is not placed in command-line arguments, the app or packages. The CLI creates a draft release, uploads files and then publishes it. Earlier releases are not automatically removed.

The commit status `overlay/update-release` links the run that published the version and reports delivery completion. Stable repository identity is verified before publication. The public repository requires no personal GitHub token for lookup or download in the app.

## Verification

Alongside WPF and CLI suites, the pipeline verifies Velopack outputs: version/application ID, channel, hashes, publication inventory, NuGet specification and compiled metadata in the packaged executable.

An additional Windows-runner proof executes official Setup in an isolated folder and starts installed WPF A. It compiles an actual WPF B with a different identity and binary, applies B through official `Update.exe` while waiting for A's exact PID, and starts B through Velopack's launcher. Restoration uses the installed CLI: `gso rollback BUILD_ID` prepares A from local history and follows the overlay's own apply flow, including `ScheduleApplyAsync` and SDK restart. Fresh CLI queries verify the running WPF identity in A, B and restored A.

The proof also checks installed version, DLL hash, preserved history packages and settings, then runs the official uninstaller and verifies files and user startup. It does not automate the interface button or establish automatic health-failure recovery. B packages remain in temporary folders and are never published. Proof logs are CI artifacts. Physical Guide, controller, TV and Apollo behavior still require testing on the actual equipment.

The updater license is recorded in [update dependencies](third-party-updates.md).

# Development

The application uses WPF/.NET 10 for Windows 11 x64. Install the SDK specified in [global.json](global.json) and PowerShell 7. The published installer includes the runtime; only development requires the SDK.

Start with [architecture](docs/architecture.md), which maps modules, native-resource ownership and the action flow. [README](README.md) describes the product; [docs/cli.md](docs/cli.md) covers automation.

```powershell
dotnet build ApolloOverlay.sln -c Release
./scripts/format.ps1 -Verify
dotnet tests/ApolloOverlay.Tests/bin/Release/net10.0-windows10.0.19041.0/ApolloOverlay.Tests.dll artifacts/local/behavior.json
dotnet tests/ApolloOverlay.Cli.Tests/bin/Release/net10.0-windows/ApolloOverlay.Cli.Tests.dll
```

The solution also builds GuideProbe and NativeProof, diagnostic tools separate from the product. CI runs their synthetic tests. Run hardware queries and window-operation modes explicitly, following their READMEs.

To explore the interface without activating other applications:

```powershell
dotnet run --project src/ApolloOverlay -- --preview
```

Use the installer and [test checklist](docs/pilot-checklist.md) to exercise real actions. Fixtures and WPF captures do not establish compatibility with a physical controller, TV or particular game.

## Changes

- Put domain rules and system access in their respective modules. The interface coordinates services and displays snapshots; services must not search for WPF controls or depend on `MainWindow`.
- Use the existing contracts where tests need substitutes for audio, platforms, controllers, media and updates. Do not add interfaces for local calculations that need no substitution.
- Preserve cancellation, target identity and checks immediately before external effects. Sending a request does not mean the action completed; report what was observed.
- Keep potentially blocking queries and callbacks off the dispatcher. Publish immutable snapshots, reject old generations and document who releases each native resource.
- Test affected behavior, including focus, cancellation and late completion where relevant. Avoid tests that merely repeat implementation steps.
- Use `ActionButtonContent` and `VectorIcon` for actions. Do not access row parts through `Children` indices. Preserve original SVG frames and record the source, license and hash of new vectors.

Run `./scripts/format.ps1` before submitting. CI verifies C# and XML formatting, builds with nullable checks and warnings as errors, tests behavior, renders captures and checks Velopack packaging and installation. Inspect light/dark and compact captures for layout changes.

To conserve GitHub Actions minutes, the Windows pipeline runs automatically on pushes to `main`, normally after merging. Feature-branch pushes and PR updates do not run it. Use local checks during development and request one deliberate `validate` or `testing` dispatch when Windows evidence is needed before merge. A Testing dispatch includes full validation; do not run both modes for the same candidate. Documentation-only and metadata-only changes do not need the Windows pipeline. Stable publication remains conditional on successful validation and an application version change. The lightweight title check runs when a PR opens or its title changes.

Keep CI-skip markers out of the final merge commit message, including markers copied from intermediate commit messages, so the automatic main-branch validation can run.

In the PR, describe the resulting behavior, validation performed and untested limits. Keep extensive formatting changes in a separate commit from behavior changes.

## Commits and versions

Use [Conventional Commits](https://www.conventionalcommits.org/) for new commits and PR titles. CI validates the title, which also becomes the merge commit title:

```text
feat(audio): add output selection
fix(input): preserve navigation after saving
refactor(session): separate resume policy
build(deps): update a runtime dependency
feat(api)!: change the automation contract
```

| Type | Stable version |
| --- | --- |
| `feat` | MINOR |
| `fix`, `perf`, `refactor`, `build`, `revert` | PATCH |
| `!` or a `BREAKING CHANGE:` footer | MAJOR |
| `docs`, `style`, `test`, `ci`, `chore` | No independent increment |

Use `build(deps)` for dependencies that change the installed application. Reserve `chore` for maintenance that does not need an application update. Versions follow [Semantic Versioning](https://semver.org/) and use the GitVersion pinned in the tool manifest. Previews use `-preview.RUN.ATTEMPT`; `+COMMIT` metadata and the build ID identify the source and do not determine ordering. Commits and tags published before this policy remain intact.

To check the policy against temporary repositories using the actual GitVersion tool:

```powershell
dotnet tool restore
./scripts/test-release-version.ps1
```

Validated publication creates release tags. Do not create a new tag to work around interrupted publication. Rerun its publication job with the original artifact. See [updates](docs/updates.md).

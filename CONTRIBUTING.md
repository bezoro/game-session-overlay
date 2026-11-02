# Development

The application uses WPF/.NET 10 for Windows 11 x64. Install the SDK specified in [global.json](global.json) and PowerShell 7. The published installer includes the runtime; only development requires the SDK.

Start with [architecture](docs/architecture.md), which maps modules, native-resource ownership and the action flow. [README](README.md) describes the product; [docs/cli.md](docs/cli.md) covers automation.

Use English for application copy, accessibility text, code, comments, documentation, commit messages, pull requests and reviews. Name new files in English. Preserve installed settings schemas, command IDs and machine-readable protocol values. Unicode test data may include other characters to verify text handling.

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

- Design service and automation APIs around the caller's intent. Use clear names and parameters, and make ownership, lifecycle, cancellation, side effects and failure behavior explicit. Keep justified optimization complexity internal and document its invariants.
- Prefer the simplest cohesive implementation. Each dependency and abstraction must justify its cost across integration and maintenance. A readable API should not add unnecessary allocations, processing or background work.
- Put domain rules and system access in their respective modules. The interface coordinates services and displays snapshots; services must not search for WPF controls or depend on `MainWindow`.
- Use the existing contracts where tests need substitutes for audio, platforms, controllers, media and updates. Do not add interfaces for local calculations that need no substitution.
- Preserve cancellation, target identity and checks immediately before external effects. Sending a request does not mean the action completed; report what was observed.
- Keep potentially blocking queries and callbacks off the dispatcher. Publish immutable snapshots, reject old generations and document who releases each native resource.
- Test affected behavior, including focus, cancellation and late completion where relevant. Avoid tests that merely repeat implementation steps.
- Use `ActionButtonContent` and `VectorIcon` for actions. Do not access row parts through `Children` indices. Preserve original SVG frames and record the source, license and hash of new vectors.

Run `./scripts/format.ps1` before submitting. CI verifies C# and XML formatting, builds with nullable checks and warnings as errors, tests behavior, renders captures and checks Velopack packaging and installation. Inspect light/dark and compact captures for layout changes.

To conserve GitHub Actions minutes, the Windows pipeline runs automatically on pushes to `main`, normally after merging. Feature-branch pushes and PR updates do not run it. Use local checks during development and request one deliberate `validate` or `testing` dispatch when Windows evidence is needed before merge. A Testing dispatch includes full validation; do not run both modes for the same candidate. Documentation-only and metadata-only changes do not need the Windows pipeline. Stable publication remains conditional on successful validation and an application version change. The lightweight title check runs when a PR opens or its title changes.

Keep CI-skip markers out of the final merge commit message, including markers copied from intermediate commit messages, so the automatic main-branch validation can run.

Open a draft PR after the first coherent implementation is pushed so progress is visible. Keep the title, scope, description and verification status current, and mark it ready when implementation and relevant checks are complete. Start the description with a concise summary, followed by `## Changes` listing all changes and `## Verification` listing every completed check and its result. Keep pending or unperformed checks separate and state relevant limits. The [PR template](.github/PULL_REQUEST_TEMPLATE.md) follows this structure. Describe the final implementation rather than the conversation history. Keep extensive formatting changes in a separate commit from behavior changes.

## Commits and versions

Commit cohesive, validated increments. Release builds resolve their version through [scripts/get-build-version.ps1](scripts/get-build-version.ps1), the pinned GitVersion tool and [GitVersion.yml](GitVersion.yml). Pass the stamped identity through [Directory.Build.props](Directory.Build.props) so the GUI, CLI, installer and update feed agree. Build-identity overrides are for explicit local builds or the workflow's stamped values; do not invent a separate release version in a project or package.

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

Breaking changes are acceptable when they materially improve the design. Prefer a clearer, simpler implementation over keeping obsolete compatibility machinery. Document affected contracts and migration or recovery steps, including changes to settings, CLI protocols, installed identity or update/rollback history. Mark intentional breaking changes with `!` or a `BREAKING CHANGE:` footer. Presentation-only changes must preserve those contracts.

Use `build(deps)` for dependencies that change the installed application. Reserve `chore` for maintenance that does not need an application update. Versions follow [Semantic Versioning](https://semver.org/) and use the GitVersion pinned in the tool manifest. Stable versions are plain `MAJOR.MINOR.PATCH`. Previews use `MAJOR.MINOR.PATCH-preview.RUN.ATTEMPT+COMMIT`. Both channels keep the full commit and build ID in separate diagnostic metadata; those fields do not determine version ordering. Commits and tags published before this policy remain intact.

To check the policy against temporary repositories using the actual GitVersion tool:

```powershell
dotnet tool restore
./scripts/test-release-version.ps1
```

Validated publication creates release tags. Do not create a new tag to work around interrupted publication. Rerun its publication job with the original artifact. See [updates](docs/updates.md).

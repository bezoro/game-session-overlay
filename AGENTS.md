# Repository conventions

## Language

Use English for application copy, accessibility text, code, comments, documentation, file names, commit messages, pull requests and reviews. Preserve installed settings schemas, command IDs and machine-readable protocol values when changing presentation text. Unicode fixtures may include other characters to test text handling.

## Readable APIs

- Make service and automation contracts clear to human readers. Names, parameters and call order should express the caller's intent without cryptic abbreviations, unexplained positional values or surprising behavior.
- State ownership, lifecycle, cancellation, side effects and failure behavior in the contract. Keep implementation and storage details internal when callers do not need them.
- Keep justified optimizations behind readable contracts and document their invariants. Readability must not add unnecessary allocations, processing, background work or mandatory abstractions.

## Implementation economy and architecture

Prefer simple, cohesive solutions. Each abstraction, dependency and additional mechanism must justify its integration and maintenance cost. Remove duplication without trading away readability, correctness, performance or explicit ownership. Do not add interfaces for local calculations that need no substitution.

Keep WPF coordination separate from domain rules and native services. Keep blocking work off the dispatcher, reject stale asynchronous results and document who releases each native resource. Preserve cancellation and target identity checks immediately before external effects. A sent request is not evidence of completion. Use the existing vector and action-content components. See [architecture](docs/architecture.md) and [contribution guidance](CONTRIBUTING.md).

## Compatibility and breaking changes

Breaking changes are acceptable when they produce a clearer, simpler, more robust or more efficient design. Do not retain obsolete compatibility machinery solely to avoid a justified improvement. Identify affected contracts, document migration and recovery steps, and mark the change with `!` or a `BREAKING CHANGE:` footer. Settings, CLI protocols, installed identity and update/rollback history require deliberate migration; a presentation cleanup must not silently change them.

## Version source and commits

Use [scripts/get-build-version.ps1](scripts/get-build-version.ps1), the pinned GitVersion tool and [GitVersion.yml](GitVersion.yml) as the release-version source. Keep GUI, CLI, assembly metadata, installer and update-feed identities consistent through [Directory.Build.props](Directory.Build.props) and the existing publication workflow. Use `OVERLAY_VERSION` and related build-identity inputs only for explicit local overrides or the workflow's stamped identity.

Commit cohesive, validated increments. Follow Conventional Commits and Semantic Versioning, classify changes by their effect on published contracts, and keep release notes and migration guidance current. Stable versions remain plain `MAJOR.MINOR.PATCH`; Testing previews use the documented suffix. Preserve published tags and packages. Retry an interrupted publication with its original artifact and job rather than rebuilding different bytes for an existing version. See [updates](docs/updates.md).

## Pull request visibility and verification

For changes intended for integration, open a draft PR after the first coherent implementation is pushed. Keep its title, scope, description and validation status current. Mark it ready when implementation and relevant checks are complete.

Start every PR description with a concise summary, followed by `## Changes` listing the changes and `## Verification` listing each completed check and its result. Keep pending or unperformed checks separate from completed verification and explain relevant limits. Use the [PR template](.github/PULL_REQUEST_TEMPLATE.md). Describe the final implementation rather than the conversation history.

## CI economy

Conserve the GitHub Actions allowance. Automatic Windows CI runs on pushes to `main`, normally after a merge, and validates the integrated source before any stable publication. Feature-branch pushes and PR updates must not trigger the Windows pipeline. The lightweight PR-title check runs only when a PR is opened or its title changes.

Batch related changes and use focused local checks during development. Dispatch Windows CI only when a coherent candidate needs Windows-only verification, consolidated pre-merge validation, diagnosis of a platform failure or a Testing preview. Documentation-only and metadata-only changes do not justify a full CI run. A Testing publication already includes full validation; do not dispatch a separate validation run immediately before it. Repeat checks only when new changes, failures or unresolved concerns justify the cost. Preserve checks required for integration and publication.

## Visual evidence

For frontend iterations, show actual WPF screenshots to at least three independent reviewers. Address their actionable findings, generate fresh screenshots after changes, and record their honest scores. Completion requires no outstanding adjustments and at least 9/10 from every reviewer. Screenshots and synthetic tests do not establish compatibility with physical controllers, games or TVs.

## Testing publication

Publish approved previews through the Testing update channel so users can test before merging. PR metadata checks must remain read-only; use the authorized branch publication workflow described in [updates](docs/updates.md).

## Instruction source

These conventions adapt the relevant guidance from [bezoro/framework](https://github.com/bezoro/framework/blob/db872cf8e25efa39f6fcb8b5dc7ef9955f9763ea/AGENTS.md) to this WPF application. Unity package staging and `.meta` GUID rules do not apply.

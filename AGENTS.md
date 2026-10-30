# Repository conventions

Use English for application copy, accessibility text, source comments, documentation, commit messages, pull requests and reviews. Preserve installed settings schemas, command IDs and machine-readable protocol values when changing presentation text.

Follow Conventional Commits and Semantic Versioning. Keep UI coordination separate from native services, preserve cancellation and target identity checks, and use the existing vector and action-content components.

Conserve the GitHub Actions allowance. Do not run CI for every commit or push. Batch related changes, use appropriate local checks during development, and run Windows CI when a coherent candidate needs Windows-only verification, final PR validation or publication. Documentation-only and metadata-only changes do not justify a full CI run. Use GitHub's supported CI-skip mechanism for intermediate pushes that would otherwise trigger unnecessary workflows. Avoid duplicate validation of the same commit: a Testing publication workflow already runs the full validation, so do not dispatch a separate validation run immediately before it. Repeat checks only when new changes, failures or unresolved concerns justify the cost. Preserve the validation required before publishing a preview or release.

For frontend iterations, show actual WPF screenshots to at least three independent reviewers. Address their actionable findings, generate fresh screenshots after changes, and record their honest scores. The requested completion threshold is no outstanding adjustments and at least 9/10 from every reviewer. Screenshots and synthetic tests do not establish compatibility with physical controllers, games or TVs.

Publish approved previews through the testing update channel so users can test before merging. Pull-request events must remain read-only; use the authorized branch publication workflow described in `docs/updates.md`.

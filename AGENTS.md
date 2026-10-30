# Repository conventions

Use English for application copy, accessibility text, source comments, documentation, commit messages, pull requests and reviews. Preserve installed settings schemas, command IDs and machine-readable protocol values when changing presentation text.

Follow Conventional Commits and Semantic Versioning. Keep UI coordination separate from native services, preserve cancellation and target identity checks, and use the existing vector and action-content components.

For frontend iterations, show actual WPF screenshots to at least three independent reviewers. Address their actionable findings, generate fresh screenshots after changes, and record their honest scores. The requested completion threshold is no outstanding adjustments and at least 9/10 from every reviewer. Screenshots and synthetic tests do not establish compatibility with physical controllers, games or TVs.

Publish approved previews through the testing update channel so users can test before merging. Pull-request events must remain read-only; use the authorized branch publication workflow described in `docs/updates.md`.

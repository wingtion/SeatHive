# CLAUDE.md

## Working rules

- Work one step of docs/audit-report.md at a time; do not fix findings outside the current step.
- For bugs: write a failing test first, show it red, then fix and show it green.
- No refactors, renames or package changes beyond the agreed scope; ask first.
- Always run dotnet build at the end and report results.
- While developing, run only the affected tests with --filter.
- Before reporting a step as done, run the full suite once.
- Repeat runs (5x) only for concurrency, locking, expiry or messaging tests, and only when that code changed.
- Run the docker compose smoke test only when Docker, migrations, configuration or cross-service flow changed.
- Never commit; the user commits.
- Do not change README claims until the code supports them.

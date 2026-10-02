# CLAUDE.md

## Working rules

- Work one step of docs/audit-report.md at a time; do not fix findings outside the current step.
- For bugs: write a failing test first, show it red, then fix and show it green.
- No refactors, renames or package changes beyond the agreed scope; ask first.
- Always run dotnet build and dotnet test at the end and report results.
- Never commit; the user commits.
- Do not change README claims until the code supports them.

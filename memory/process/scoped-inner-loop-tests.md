# Scope testing to what the change can actually break

Pick the test scope by what the diff can affect — the full suite is 4+ minutes and running it every step slows everything down. Use discretion (Peter, 2026-08-26):

- **Docs-only / markdown-only** (`*.md`, no code, no resx, no config): no build, no tests. Commit and PR.
- **Single-section edits**: that section's test project is the gate — `dotnet test tests/Humans.<Section>.Tests -v quiet` builds the dependency closure and runs in seconds. That's usually enough for the PR too; CI runs the full suite anyway. A section with no test project (e.g. Tour) falls back to `dotnet build Humans.slnx -v quiet`.
- **Cross-section surface** (contracts, interfaces, Base/Shell, multiple sections): add `tests/Humans.Application.Tests` (the architecture tests), and run the full `dotnet test Humans.slnx -v quiet` gate before the PR.
- **When unsure what the blast radius is**, run the full gate — discretion means judging the radius, not skipping the judgment.

---
name: dotnet build/test always with -v quiet
description: Always pass `-v quiet -clp:ErrorsOnly` to `dotnet build` / `dotnet test`. Never pipe their output through `tail`/`head`/`grep` — truncation hides failure details.
---

Always invoke `dotnet build` and `dotnet test` with `-v quiet` (or `--verbosity quiet`) **plus `-clp:ErrorsOnly`**. Never pipe their output through `tail`, `head`, or `grep` to shorten it.

**`-clp:ErrorsOnly` (temporary, Peter 2026-08-28):** the `HUM_USER_DISPLAYNAME` obsolete-usage warnings (nobodies-collective/Humans#691) print a multi-page wall on every build until the proper fix lands (~2 weeks out). `-clp:ErrorsOnly` silences the MSBuild console logger's warnings only — errors still print, the test-results summary comes from a different logger and is unaffected, warnings still count everywhere else (CI, binlogs, WarningsAsErrors). Once nobodies-collective/Humans#691 closes, drop the flag from this atom.

**Why:** The default verbosity is noisy, which tempts truncation like `| tail -4`. But truncation throws away failure details — when a test fails, the failing test name, assertion message, and stack trace live *above* the final summary. Truncating forces a full re-run just to see what failed. With `-v quiet`, a passing build/test run is already ~3 lines, and failures still surface with their reasons intact.

**How to apply:** On every `dotnet test Humans.slnx ...` or `dotnet build Humans.slnx ...` call, include `-v quiet`. If output is still too long to want in the transcript, redirect to a log file (`> build.log 2>&1`) and Read the log — do not pipe to `tail`/`head`/`grep`.

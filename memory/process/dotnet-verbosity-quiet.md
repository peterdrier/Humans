---
name: dotnet build/test always with -v quiet
description: Always pass `-v quiet` to `dotnet build` / `dotnet test`. Never pipe their output through `tail`/`head`/`grep` — truncation hides failure details.
---

Always invoke `dotnet build` and `dotnet test` with `-v quiet` (or `--verbosity quiet`). Never pipe their output through `tail`, `head`, or `grep` to shorten it.

**Why:** The default verbosity is noisy, which tempts truncation like `| tail -4`. But truncation throws away failure details — when a test fails, the failing test name, assertion message, and stack trace live *above* the final summary. Truncating forces a full re-run just to see what failed. With `-v quiet`, a passing build/test run is already ~3 lines, and failures still surface with their reasons intact.

**How to apply:** On every `dotnet test Humans.slnx ...` or `dotnet build Humans.slnx ...` call, include `-v quiet`. If output is still too long to want in the transcript, redirect to a log file (`> build.log 2>&1`) and Read the log — do not pipe to `tail`/`head`/`grep`.

---
name: Razor errors in files you didn't touch are the toolchain
description: Build reports Razor/CS errors (RZ1021, CS1010, CS0103, mangled identifiers) in `.cshtml` your branch never touched: restart the build server, never edit the views. One build at a time.
---

Errors in `.cshtml` files your branch did not change, often across unrelated sections, are a corrupt compiler state. **Never edit the views it names**; they are valid and compile in CI. Confirm with `git diff origin/main --stat -- <path>`.

**Cause 1: concurrent builds.** `dotnet build`, `dotnet test` and `reforge` share one `VBCSCompiler` server, and two compiling at once corrupt it. One build at a time per checkout: before building, make sure no background command, subagent or reforge scan is compiling, and a dispatcher waits for its executor instead of building alongside.

**Cause 2: a damaged SDK in a cloud container** (`CLAUDE_CODE_REMOTE=true`, seen 2026-09-10 with SDK 10.0.401 dropping MVC tag helpers). The tell is mangled identifiers that are substrings of real ones (`able` from `_Table`) or quoted markup that isn't in the file.

**Recover, in order:**

1. `dotnet build-server shutdown`, then rebuild.
2. Cloud: rebuild with `MSBUILDDISABLENODEREUSE=1` (add `-m:1` to rule out parallelism).
3. Cloud, still broken: install a clean SDK under the scratchpad and point `DOTNET_ROOT` and `PATH` at it for the session.

**In a cloud container never run `dotnet clean` or delete `obj/`/`bin/`:** it removes the prebuilt binaries the container ships with and nothing downstream of `Humans.Base` builds again.

**Related:** [`dotnet-verbosity-quiet`](dotnet-verbosity-quiet.md) · [`no-rm-rf`](no-rm-rf.md)

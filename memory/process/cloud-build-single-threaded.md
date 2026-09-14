---
name: Razor errors on untouched .cshtml files in a cloud container are a toolchain fault, never your views
description: "In a Claude Code cloud container, Razor errors (RZ1021/CS1010/CS0103) on untouched `.cshtml` are a damaged SDK: `dotnet build-server shutdown` + `MSBUILDDISABLENODEREUSE=1`, never edit views or `dotnet clean`."
---

On a `CLAUDE_CODE_REMOTE=true` cloud container the build can fail on `.cshtml` files **your
branch never touched**, across many unrelated sections at once: `RZ1021` on valid self-closing
tags, `CS1010: Newline in constant` on the ordinary `@(x ? "a" : "b")`-inside-an-attribute
pattern views use everywhere, `CS0103` on `model`/`name` inside `<partial …/>`, and mangled
identifiers that are *substrings* of real ones — `able` from `_Table`, `atio` from
`Notification`, `ardRow` from `CardRow`.

**The cause is the toolchain, not the markup.** Observed 2026-09-10/11: the container's
`/usr/local/share/dotnet` SDK 10.0.401 silently drops MVC tag helpers, reproduced on a minimal
two-file Razor project outside this repo; a freshly installed 10.0.401 in the scratchpad
compiles the same project clean, and CI compiles the same commits clean. MSBuild node reuse
then propagates the bad state: after the good SDK was in place, a surviving node reintroduced
the identical errors.

**How to apply:**

- Errors in `.cshtml` files you did not touch are this bug, not a finding. Confirm with
  `git diff origin/main --stat -- <path>` and `git log -1 -- <the .cshtml>` before believing
  the compiler. The compiler quoting markup that is not in the file (`<br>` on a line that
  reads `<br />`) is the tell that you are reading a stale buffer.
- Recover in this order: `dotnet build-server shutdown`, then rebuild with
  `MSBUILDDISABLENODEREUSE=1` (and `-m:1` if you want to rule parallelism out cheaply). If it
  persists, install a clean SDK under the scratchpad and point `DOTNET_ROOT` and `PATH` at it
  for the rest of the session — that is what restored a working gate.
- **Never edit the views to make it go away.** They are valid, and they compile in CI and
  locally. Editing them would be a large unrelated diff against fix-at-the-source.
- **Do not reach for `dotnet clean`,** and do not delete `obj/` or `bin/`. Observed
  2026-09-10: `dotnet clean Humans.slnx` deleted the prebuilt binaries the container ships
  with, turning an intermittent failure into a total inability to build anything downstream of
  `Humans.Base`.
- The repo's canonical commands in `AGENTS.md` stay as they are. This is a broken container to
  be repaired, not a flag every build needs.

Related: [[dotnet-verbosity-quiet]], [[no-rm-rf]], [[always-use-worktree]].

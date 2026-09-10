---
name: In a cloud container, build with -m:1 — parallel MSBuild corrupts Razor source generation
description: On Claude Code cloud runs, `dotnet build Humans.slnx` without `-m:1` intermittently produces truncated Razor-generated C#, surfacing as RZ1021/CS1010/CS0103 errors on valid, untouched .cshtml files across many sections. Add `-m:1`. Do NOT "fix" the views, and do NOT run `dotnet clean` in response.
---

On a `CLAUDE_CODE_REMOTE=true` cloud container, a parallel `dotnet build Humans.slnx` can
corrupt the Razor source generator's output. The symptom is unmistakable once you know it:
compile errors on `.cshtml` files **your branch never touched**, across many unrelated
sections at once, with mangled identifiers that are *substrings* of real ones — `able` from
`_Table`, `atio` from `Notification`, `ardRow` from `CardRow` — plus `RZ1021` on valid
self-closing tags and `CS1010: Newline in constant` on the ordinary
`@(x ? "a" : "b")`-inside-an-attribute pattern that ~133 views in this repo use.

**How to apply:**

- Build and test with `-m:1` in a cloud session: `dotnet build Humans.slnx -v quiet -m:1`,
  `dotnet test tests/Humans.<Section>.Tests -v quiet -m:1`. It is slower and it is correct.
- Errors in `.cshtml` files you did not touch are this bug, not a finding. Confirm with
  `git diff origin/main --stat -- <path>` and `git log -1 -- <the .cshtml>` before believing
  the compiler.
- **Never edit the views to make it go away.** They are valid and they compile in CI and
  locally. Editing them would be a large unrelated diff against fix-at-the-source.
- **Do not reach for `dotnet clean`.** Observed 2026-09-10: `dotnet clean Humans.slnx` in
  response to this deleted the prebuilt binaries the container ships with, turning an
  intermittent failure into a total inability to build anything downstream of
  `Humans.Base` — and it cost an hour and a wrong diagnosis (a supposed
  `global.json` 10.0.100 vs installed 10.0.401 SDK mismatch) before `-m:1` proved the
  toolchain was never at fault. The controlled comparison: same tree,
  `dotnet build src/Humans.Base -v quiet --no-incremental` fails, the same command with
  `-m:1` exits 0.

Related: [[dotnet-verbosity-quiet]], [[no-rm-rf]], [[always-use-worktree]].

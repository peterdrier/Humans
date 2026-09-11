---
name: no-concurrent-roslyn
description: Never run dotnet build while another process (reforge, a second build, a subagent's build) holds the Roslyn compiler server — the shared VBCSCompiler state corrupts and reports phantom Razor errors in files nobody touched. Recover with dotnet build-server shutdown, never by editing the files it names.
---

One worktree, one build at a time. `dotnet build`, `dotnet test` and `reforge` all drive the same
`VBCSCompiler` server process, and two of them compiling at once corrupts its shared state. The
symptom is not a lock error — it is a green-looking run that reports Razor errors in `.cshtml`
files the change never touched, often in a different section entirely.

**Recover by restarting the server, never by editing the files it names:**

```bash
dotnet build-server shutdown
dotnet build Humans.slnx -v quiet
```

The phantom errors are gone after the restart because they were never real. Editing a file the
corrupt server complained about is the `fix-at-the-source` violation this atom exists to prevent —
it changes working code to appease a stale process.

**How to avoid it:** before starting a build, make sure nothing else is building — a background
command, a dispatched subagent that was told to validate, a `reforge` scan. Where a workflow
dispatches an executor that builds, the dispatcher waits for it rather than building alongside it.

Related: [[dotnet-verbosity-quiet]] for the mandatory `-v quiet`.

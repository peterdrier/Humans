---
name: Context discipline in long refactors
description: "During multi-file refactors: read narrow ranges, pipe build/test output to a file, batch edits into one Write, use reforge for symbol queries, commit checkpoints often."
---

In a multi-file refactor the context fills with the same repeat offenders. The levers:

1. **Read narrow.** Grep for the line, then Read 10–30 lines around it. Don't re-Read after a successful Edit; the harness errors on stale state. For a deletion, grep the signature and Edit on it without reading the body.
2. **Build and test to a file.** `dotnet build Humans.slnx -v quiet > build.log 2>&1; grep -c error build.log`, then pull only the lines you need. Counts are nearly free; let the file hold the bulk.
3. **One Write beats many Edits.** Three or fewer changes to a file: `Edit`. Four or more, or a whole-method replacement: one Read, one Write.
4. **Use `/reforge` for the symbol graph:** `reforge references <symbol>`, `implementations <interface>`, `callees <method>`. Plain grep stays right for string literals, comments, `using` lines and file names.
5. **Commit checkpoints** (interfaces, then implementations, tests, docs). Once committed, a file no longer has to stay in your head.
6. **`cd` once in the main loop;** it persists between Bash calls. In a subagent it does not: use absolute paths for every read, edit and build, and run no git. Never `git -C`: [[never-use-git-dash-c]].

**Why:** cost is turns × context. Peter flagged a session that burned its context on 100-line reads, repeated build preambles and four sequential Edits to one file.

Related: [`ci-events-not-polling`](ci-events-not-polling.md)

---
name: Unattended runs never hit a permission prompt
description: Scheduled/unattended runs (dispatcher jobs) must never stall on a prompt: take subagent results from the result, never read task `.output` files; one purpose per Bash call.
---

An unattended run never issues a command that can stop on a permission prompt: nobody is there to answer it, so the run stalls for hours.

**Why:** The 2026-11-24 Gate section-doctor run stalled four hours on one chained Bash call. Its first step copied a background subagent's `/tmp/claude-*/…/tasks/<id>.output` file into `$RUNDIR`. That file is a symlink to the agent's transcript under `~/.claude/projects/`, a protected path: the prompt is forced, no allow rule can pre-approve it, and the other eight harmless commands chained after it waited with it.

**How to apply:**

- **Subagent results come from the result.** Use the text the Agent call returns (or its completion notification). To keep it on disk, write that text to the run's scratch dir with the Write tool. Never `cat`, `cp`, `tail` or `Read` a task's `.output` file, and never read anything under `~/.claude/`.
- **One purpose per Bash call.** Don't chain unrelated commands with `;` or `&&` into one call. A chain is judged by its riskiest part, so one flagged command blocks the rest; separate calls let the harmless ones run.
- **Stay in the worktree and the run's own scratch dir** (`$RUNDIR`, `$TMPDIR`). Nothing outside them, nothing in another session's temp dir.
- **Run repo scripts by their repo path** (`python3 docs/maintenance/…`), not copied into `/tmp` or generated on the fly.
- If a step can only be done through a prompt, skip it, say so in the run's report, and carry on. Never wait for an answer.

**Related:** [`no-scheduled-pr-checkins`](no-scheduled-pr-checkins.md)

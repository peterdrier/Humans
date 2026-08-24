---
name: finish
description: End-of-session cleanup for coding and project work. Use when the user says "finish", "wrap up", "done for now", or any variation of ending a work session.
---

# Finish — Session Closer

Close out a Claude Code session so nothing falls through the cracks when context is lost.

## Scope Rule

Only check repos that were **active in this session**. Use conversation memory to determine which repos were touched. The current working directory always counts.

**Your lane is what THIS session did. Nothing else exists.** Peter runs 5–10 Claudes in parallel, so the repo is full of other sessions' branches, worktrees, PRs, and commits. Reporting any of it is noise he has to read and discard — and worse, it reads as *his* loose ends when it isn't.

Never mention:

- Branches, worktrees, or PRs this session didn't create or work on
- Commits on `main` that landed from elsewhere ("main has moved to X", "N commits came in while we worked")
- Open PRs this session never touched, even to say they're untouched
- Other sessions' running agents or background work
- Anything you noticed in passing and "left alone" — leaving it alone means not mentioning it either

The test for every line: **did this session do it?** If no, cut it. A shorter close that covers only your own work is the correct output, not an incomplete one.

This applies to the git checks too. `git status` and `git log` will surface other sessions' activity; read past it. Report your own uncommitted work, your own unpushed commits, your own branch state — and if this session's work is fully landed and clean, say exactly that and stop.

## Flow

### 1. Git Hygiene (each active repo)

Run `git status`, `git diff --stat`, `git log --oneline -5` as **separate Bash calls** (not chained with `&&`). Report:

- Uncommitted changes (staged/unstaged)
- Untracked files
- Unpushed commits
- Branch state (on main? feature branch that needs merging/pushing?)

Don't auto-fix. Present state and ask if Peter wants to commit now or defer.

### 2. Context Capture

Scan for things that should survive this session:

- Corrections Peter made and durable rules that surfaced — capture as `memory/<bucket>/<name>.md` atoms + `INDEX.md` line per `memory/META.md` (goes on this session's branch, never direct to main)
- Architectural or process decisions not yet recorded

If everything was already captured during the session, say so.

### 3. Session Outcomes

Brief factual summary in chat:
- Shipped/built/fixed
- Started but unfinished
- Discussed but not acted on

### 4. Loose Ends

List open items from this session — tasks deferred, background agents **this session** started that are still running, promises made. Frame as: "These are still open."

If this session left nothing open, say so in one line. Don't pad the section with work that belongs to another lane.

### 5. Clean Close

One-line summary + confirmation it's safe to close. E.g.:
> "3 commits pushed, PR #N open. You're clean — close whenever."

## Tone

- Fast and light. No guilt about unfinished items.
- If energy is clearly low, minimize: git check + loose ends only.
- Acknowledge one win, always.

## Not This

- Not a planning session — don't set tomorrow's priorities
- Not a code review — just check state is clean

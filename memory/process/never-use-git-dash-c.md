---
name: never-use-git-dash-c
description: HARD RULE. Never use `git -C <path>` — cd to the directory in its own Bash call, then run plain git.
---

**HARD RULE.** Never use `git -C <path> ...` — not in your own commands, not in subagent prompts, not in skill instructions.

**Why:** permission rules match on literal command prefix, so `git -C …` defeats every `Bash(git status:*)`-style allowlist entry and forces a classifier round-trip on every single git call. With several agents running, that's constant manual prompting for commands that were already meant to be pre-authorized.

**How to apply:** `cd <abs worktree>` as its own Bash call (working directory persists between calls), then run plain `git status` / `git add` / `git commit` / `git push` in following calls. Never `cd X && Y` — a PreToolUse hook hard-blocks that pattern too (see [[no-chained-bash-commands]]). In subagent prompts, give the absolute worktree path, say "cd there first in a standalone call," and keep Read/Edit on absolute paths under the worktree so file writes land correctly regardless of cwd.

Worktree structure is still the isolation mechanism — this rule only bans the `-C` flag, not per-worktree work.

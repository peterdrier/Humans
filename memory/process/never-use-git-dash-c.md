---
name: never-use-git-dash-c
description: HARD RULE. Never use `git -C <path>` — cd to the directory in its own Bash call, then run plain git. Reaching for `-C` means you're running from the wrong folder.
---

**HARD RULE.** Never use `git -C <path> ...` — not in your own commands, not in subagent prompts, not in skill instructions. No exceptions for swarm/refactor runs or "just this once."

**Why:** wanting `-C` means the shell is in the wrong folder, and if git is aimed at the wrong folder, so is everything else (builds, searches, file edits) — the `-C` masks a real problem instead of fixing it. It also defeats every `Bash(git status:*)`-style allowlist prefix, forcing a manual approval on every git call.

**How to apply:** `cd <abs worktree>` as its own Bash call (working directory persists between calls), then run plain `git status` / `git add` / `git commit` / `git push` in following calls. In subagent prompts, give the absolute worktree path and say "cd there first in a standalone call," and keep Read/Edit on absolute paths under the worktree.

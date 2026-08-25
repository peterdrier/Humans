---
name: no-rm-rf
description: HARD RULE. `rm -rf` (and equivalents like `Remove-Item -Recurse -Force` on repo paths) is never allowed for anything, no exceptions — a PreToolUse hook blocks it. Worktree removal is git-only, then `rmdir` the empty husk; stale build output is `dotnet clean`.
---

**HARD RULE.** `rm -rf` — and equivalents, including PowerShell `Remove-Item -Recurse -Force` on repo paths — is never allowed for anything. No exceptions, no fallbacks. A prior Claude Code session destroyed files with `rm -rf`, which is why a PreToolUse hook hard-blocks the pattern on this machine; beyond the block, reaching for a recursive/forced delete is the wrong tool regardless.

**Why:** recursive, forced deletion has no undo. Every legitimate reason to reach for it already has a purpose-built, safer tool: git for worktrees, `dotnet clean` for build artifacts. A "let me just force-delete this stuck thing" instinct is exactly the failure mode this rule exists to stop.

**How to apply:**
- Stale build artifacts (bin/obj corruption, PE-image metadata errors) are cleared with `dotnet clean Humans.slnx -v quiet`, never a recursive delete. Detail: [[dotnet-clean-not-rm]].
- The hook scans the whole Bash command string, so prose containing the literal text "rm -rf" (e.g. in a commit message) can trip it too — pass such text via a file (`git commit -F`) instead of inline.
- Worktree cleanup is git-only — see below.

## Worktree removal — git only, then `rmdir` the empty husk

**The only allowed command:**

```bash
git worktree remove <path> [--force]
```

**If it succeeds:** done.

**If it fails — for ANY reason** (locked, "not a working tree", "files in use", permission denied, anything):

1. Check whether git nonetheless emptied the directory's contents (it often does when only the final parent-dir removal fails). Run `ls <path>` (or `Get-ChildItem -Force <path>`).
2. **Empty parent → rmdir:** If the directory is **completely empty** (no files, no subdirs, no hidden entries), `rmdir <path>` (non-recursive — no `/s`, no `-r`, no `-Force`, no `Remove-Item -Recurse`). This is the normal second step, not an exception: `rmdir` cannot delete non-empty directories and cannot circumvent file locks, so it never substitutes for an `rm -rf`.
3. **Anything else:** tell Peter the exact git error and stop. Do not retry, do not wait, do not investigate the lock, do not propose follow-up actions.

**Forbidden follow-ups (still — even after a partial success):**

- `Remove-Item -Recurse -Force` (PowerShell) — recursive deletion of a directory tree.
- `rm -rf` (bash) — same.
- Any recursive/force-flagged delete via any command (`robocopy /MIR`, `cmd /c rd /s /q`, etc.).
- Killing processes that might hold handles (dotnet, MSBuild nodes, IDEs, anything).
- `dotnet build-server shutdown` to release MSBuild handles.
- Retrying the delete from a different cwd.
- Any "let me try X first to release the lock then retry the delete" pattern.
- Sleeping/waiting and retrying `git worktree remove` — surface the failure instead.

**Why (worktree case):** Past breach (2026-05-02): `git worktree remove` failed with "Permission denied"; instead of stopping, the agent escalated through PowerShell `Remove-Item -Recurse -Force`, then killed MSBuild daemons, then retried twice more from different cwds. Three of those four follow-ups are exactly the rm-rf pattern wearing different syntax. The "in use" error is a signal that something Peter cares about (an IDE, a build, another session) is touching the path; the right response is to surface it, not to escalate.

The `rmdir` step exists because `git worktree remove` often empties the contents but fails on the empty parent. `rmdir` (non-recursive) on a verified-empty dir doesn't open the rm-rf door — it lacks recursion and can't bypass locks; if the dir still held anything in use, `rmdir` would fail too.

**How to apply (worktree case):**

- The rule fires the moment `git worktree remove` returns non-zero.
- Before reporting, check if the contents are gone (`ls <path>`). That single check decides between the rmdir step and the stop-and-report path.
- If the dir has any contents whatsoever — files, subdirs, hidden files — STOP. Don't escalate.
- Reporting format: paste the literal git error and stop. Don't propose follow-up actions.
- Git-level cleanup (registration via `git worktree prune`, local branch via `git branch -d`, remote branch via `git push origin --delete`) can still proceed without the filesystem dir being gone — none of that depends on filesystem deletion.
- Applies to ANY worktree under `.worktrees/<name>` or anywhere else, regardless of whether the branch was just merged, abandoned, or never had a remote.

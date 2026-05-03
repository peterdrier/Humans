---
name: Worktree removal — git only, with one narrow rmdir exception
description: HARD RULE. To remove a worktree, only `git worktree remove [--force]` is allowed. If git refuses, report and stop — narrow exception: if git emptied the contents but failed only on the empty parent dir, `rmdir` (non-recursive, no force flags) on that empty dir is allowed. No PowerShell `Remove-Item -Recurse`, no `rm -rf`, no process kills, no `dotnet build-server shutdown`, no retries from a different cwd, no "let me try X to release the lock first." For anything else, tell Peter and wait.
---

**HARD RULE.** Worktree cleanup is git-only, with one narrow exception below.

**The only allowed command:**

```bash
git worktree remove <path> [--force]
```

**If it succeeds:** done.

**If it fails — for ANY reason** (locked, "not a working tree", "files in use", permission denied, anything):

1. Check whether git nonetheless emptied the directory's contents (it often does when only the final parent-dir removal fails). Run `ls <path>` (or `Get-ChildItem -Force <path>`).
2. **Narrow exception — empty-parent rmdir:** If the directory is **completely empty** (no files, no subdirs, no hidden entries), then `rmdir <path>` (non-recursive — no `/s`, no `-r`, no `-Force`, no `Remove-Item -Recurse`) **is allowed** on that empty parent only. `rmdir` cannot delete non-empty directories and cannot circumvent file locks, so the safety property of the no-rm-rf rule still holds.
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

**Why:** Past breach (2026-05-02): `git worktree remove` failed with "Permission denied"; instead of stopping, the agent escalated through PowerShell `Remove-Item -Recurse -Force`, then killed MSBuild daemons, then retried twice more from different cwds. Three of those four follow-ups are exactly the rm-rf pattern wearing different syntax. The "in use" error is a signal that something Peter cares about (an IDE, a build, another session) is touching the path; the right response is to surface it, not to escalate.

The narrow `rmdir` exception was added later the same day after a separate incident where `git worktree remove` succeeded at emptying contents but failed on the empty parent. Allowing `rmdir` (non-recursive) on a verified-empty dir doesn't open the rm-rf door — `rmdir` lacks recursion and lacks the ability to bypass locks; if the dir actually contained anything still in use, `rmdir` would fail too. The cost of leaving Peter to clean an empty husk dir himself outweighs the marginal safety win of forbidding even that.

**How to apply:**

- The rule fires the moment `git worktree remove` returns non-zero.
- Before reporting, check if the contents are gone (`ls <path>`). That single check decides between the rmdir exception and the stop-and-report path.
- If the dir has any contents whatsoever — files, subdirs, hidden files — STOP. Don't escalate.
- Reporting format: paste the literal git error and stop. Don't propose follow-up actions beyond the empty-parent rmdir.
- Git-level cleanup (registration via `git worktree prune`, local branch via `git branch -d`, remote branch via `git push origin --delete`) can still proceed without the filesystem dir being gone — none of that depends on filesystem deletion.
- Applies to ANY worktree under `.worktrees/<name>` or anywhere else, regardless of whether the branch was just merged, abandoned, or never had a remote.

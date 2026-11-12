---
name: no-rm-rf
description: HARD RULE. Deleting a directory, clearing bin/obj, or removing a worktree: never `rm -rf` or any recursive forced delete. `dotnet clean` for build output, `git worktree remove` for worktrees.
---

`rm -rf` and every equivalent (`Remove-Item -Recurse -Force`, `rd /s /q`, `robocopy /MIR`, `find … -exec rm`) is never allowed, for anything. A hook blocks it; a past session destroyed files this way.

**Build output.** Stale metadata after a rebase or branch switch (`CS0009 … PE image doesn't contain managed metadata`, corrupt `obj`): `dotnet clean Humans.slnx -v quiet`, then rebuild. Not in a cloud container, where `dotnet clean` removes the prebuilt binaries: see [`phantom-razor-errors`](phantom-razor-errors.md).

**Worktrees.** The only command is `git worktree remove <path> [--force]`.

- It fails, for any reason: run `ls <path>`. Completely empty (no hidden entries): `rmdir <path>`, non-recursive. Anything left: paste the literal git error to Peter and stop.
- Never follow up with a recursive delete, killing processes that may hold handles, `dotnet build-server shutdown` to release locks, retrying from another cwd, or sleep-and-retry. "In use" means something Peter cares about is touching the path.
- `git worktree prune`, `git branch -d` and deleting the remote branch can still proceed.

The hook scans the whole command string, so the literal text in a commit message trips it: pass such text with `git commit -F`.

**Why:** recursive forced deletion has no undo. On 2026-05-02 an agent answered a "Permission denied" by escalating through PowerShell, killing MSBuild and retrying from other directories.

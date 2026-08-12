---
name: Hook commands must use absolute paths
description: Every hook `command` in `.claude/settings.json` locates its script via `"$CLAUDE_PROJECT_DIR/.claude/<script>.sh"`, never a bare relative `.claude/<script>.sh`. A hook fires before the tool command runs, so its cwd is whatever the previous Bash call left behind — a worktree, or a directory that has since been deleted. Scripts that call sibling scripts resolve them from `BASH_SOURCE`, not cwd.
---

Hook `command` entries in `.claude/settings.json` must locate their script absolutely:

```json
"command": "bash \"$CLAUDE_PROJECT_DIR/.claude/check-ef-output-dir.sh\""
```

Not `bash .claude/check-ef-output-dir.sh`.

A hook script that shells out to a sibling script resolves it from its own location:

```bash
HOOK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bash "$HOOK_DIR/razor-lint.sh" --staged --hook
```

**Why:** the Bash tool's cwd persists between calls, and a PreToolUse hook fires *before* the command it guards — including that command's own `cd`. So the hook runs in whatever directory the previous call ended in. When that directory is a worktree the session has since removed, every relative path resolves to nothing and the hook dies with `No such file or directory` instead of doing its job. A guard that silently stops guarding is worse than no guard. Observed 2026-08-11, when `/merged` deleted the worktree the shell was sitting in and all four Bash hooks started failing.

`$CLAUDE_PROJECT_DIR` always points at the session's project root, so hooks resolve against the main checkout even when the command runs in a worktree. That is deliberate: a branch that edits a hook script doesn't get to run its own edited version as a guard.

**How to apply:**

- Fires whenever a hook is added to or edited in `.claude/settings.json`.
- Locating the script is absolute; the script's *cwd* is not changed — `razor-lint-hook.sh` and `block-data-migration-hook.sh` inspect `git diff` in the worktree where the commit is happening, which is correct.
- Test a new hook by piping sample tool JSON to it from an unrelated directory (`cd $(mktemp -d)`), not from the repo root — the repo root is the one cwd that hides this bug.

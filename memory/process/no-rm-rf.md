---
name: no-rm-rf
description: HARD RULE. `rm -rf` (and equivalents like `Remove-Item -Recurse -Force` on repo paths) is never allowed for anything, no exceptions — a PreToolUse hook blocks it. Worktree removal is git-only; stale build output is `dotnet clean`.
---

**HARD RULE.** `rm -rf` — and equivalents, including PowerShell `Remove-Item -Recurse -Force` on repo paths — is never allowed for anything. No exceptions, no fallbacks. A prior Claude Code session destroyed files with `rm -rf`, which is why a PreToolUse hook hard-blocks the pattern on this machine; beyond the block, reaching for a recursive/forced delete is the wrong tool regardless.

**Why:** recursive, forced deletion has no undo. Every legitimate reason to reach for it already has a purpose-built, safer tool: git for worktrees, `dotnet clean` for build artifacts. A "let me just force-delete this stuck thing" instinct is exactly the failure mode this rule exists to stop.

**How to apply:**
- Worktree cleanup is `git worktree remove [--force] <path>` only. If git refuses for any reason, stop and report — do not escalate to a forced filesystem delete, killing processes, or retrying from a different cwd. If git emptied the dir but left the husk, plain `rmdir` (non-recursive) is the normal second step. Full procedure: [[worktree-removal-git-only]].
- Stale build artifacts (bin/obj corruption, PE-image metadata errors) are cleared with `dotnet clean Humans.slnx -v quiet`, never a recursive delete. Detail: [[dotnet-clean-not-rm]].
- The hook scans the whole Bash command string, so prose containing the literal text "rm -rf" (e.g. in a commit message) can trip it too — pass such text via a file (`git commit -F`) instead of inline.

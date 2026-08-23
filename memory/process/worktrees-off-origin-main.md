---
name: worktrees-off-origin-main
description: Feature-work worktrees branch off origin/main (the fork), not upstream/main — only a cherry-pick fix branch destined directly for upstream uses upstream/main as base.
---

For all feature work and issue implementation, create the worktree branch off `origin/main` (the fork's main), NOT `upstream/main`.

**Why:** the dev flow is feature branch → PR to the fork (QA auto-deploys from there) → batched and PR'd to upstream later. A worktree off `upstream/main` misses any commits already merged into the fork that haven't yet been promoted upstream, and the resulting branch can't cleanly PR to the fork without rebase noise. The "branch from upstream/main" guidance applies specifically to a cherry-pick fix branch intended to flow directly to upstream as an isolated PR — a narrow scenario, not the default.

**How to apply:**
- `git fetch origin main && git worktree add -b <branch> .worktrees/<branch> origin/main` — default flow.
- `git fetch upstream main && git worktree add -b <branch> .worktrees/<branch> upstream/main` — only when explicitly creating a hotfix/cherry-pick branch destined directly for upstream.
- If unsure which scenario applies, ask — don't pattern-match the upstream-fix exception onto regular feature work.

Related: [[always-use-worktree]], [[agent-isolation-worktree-base]].

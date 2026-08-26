---
name: worktrees-off-origin-main
description: Feature-work worktrees branch off origin/main (the fork), not upstream/main — only a cherry-pick fix branch destined directly for upstream uses upstream/main as base.
---

For all feature work and issue implementation, branch off `origin/main` (the fork's main), NOT `upstream/main`. This is about the base ref, so it holds either way — off a worktree locally, off the repo root in a cloud run ([[always-use-worktree]]).

**Why:** the dev flow is feature branch → PR to the fork (QA auto-deploys from there) → batched and PR'd to upstream later. A worktree off `upstream/main` misses any commits already merged into the fork that haven't yet been promoted upstream, and the resulting branch can't cleanly PR to the fork without rebase noise. The "branch from upstream/main" guidance applies specifically to a cherry-pick fix branch intended to flow directly to upstream as an isolated PR — a narrow scenario, not the default.

**How to apply:**
- `git fetch origin main && git worktree add -b <branch> .worktrees/<branch> origin/main` — default flow. Cloud run: `git fetch origin main && git checkout -b <branch> origin/main`, in the repo root.
- `git fetch upstream main && git worktree add -b <branch> .worktrees/<branch> upstream/main` — only when explicitly creating a hotfix/cherry-pick branch destined directly for upstream. Cloud run: `git checkout -b <branch> upstream/main`.
- If unsure which scenario applies, ask — don't pattern-match the upstream-fix exception onto regular feature work.

Related: [[always-use-worktree]], [[agent-isolation-worktree-base]].

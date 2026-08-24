---
name: pr-fix-switches-to-pr-branch
description: /pr-fix always switches to the PR's head branch (via its worktree). Never code in main, and never stop to ask just because the current branch differs.
---

When `/pr-fix <N>` runs, always switch to the PR's head branch — use the existing worktree if one exists (`git worktree list` to find it), otherwise create one under `.worktrees/`. Don't stop and ask "should I switch?" when the current branch differs from the PR head, and don't code from `main` or an unrelated branch.

**Why:** Peter: "you should just always switch to the branch from the pr.. NEVER CODE IN MAIN.. and if you're on some other branch, leave it for the pr branch." Switching is always the right move here.

**How to apply:** first action after resolving the PR is `git worktree list` → cd to the matching worktree (or create one). Only ask if the worktree has uncommitted changes that would conflict, or there's no clear way to reach the PR head.

Related: [[always-use-worktree]].

---
name: pr-fix-switches-to-pr-branch
description: /pr-fix always switches to the PR's head branch (via its worktree locally, a plain checkout in a cloud run). Never code in main, and never stop to ask just because the current branch differs.
---

When `/pr-fix <N>` runs, always switch to the PR's head branch — use the existing worktree if one exists (`git worktree list` to find it), otherwise create one under `.worktrees/`. In a cloud run, where [[always-use-worktree]] puts the work in the repo root, that's a plain `git checkout` of the PR head there instead. Don't stop and ask "should I switch?" when the current branch differs from the PR head, and don't code from `main` or an unrelated branch.

**Why:** Peter: "you should just always switch to the branch from the pr.. NEVER CODE IN MAIN.. and if you're on some other branch, leave it for the pr branch." Switching is always the right move here.

**How to apply:** first action after resolving the PR is `git worktree list` → cd to the matching worktree (or create one). In a cloud run it is a plain checkout of the head branch in the repo root — no worktree to list, nothing to cd into. Fetch it from the remote that actually carries it: `origin` for a same-repo PR, the contributor's fork for a cross-repository one — [[cross-repo-pr-push-target]] resolves which and adds the remote, and never hardcode `origin`. Only ask if the checkout has uncommitted changes that would conflict, or there's no clear way to reach the PR head.

Related: [[always-use-worktree]].

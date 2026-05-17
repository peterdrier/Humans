---
name: Cross-repo PRs push to the contributor's fork, not origin
description: When fixing a PR opened from a contributor's fork, `git push origin HEAD:<branch>` lands on peterdrier/Humans and never reaches the PR. Push to the contributor's fork remote instead.
---

Before pushing fixes to any PR, check whether it's cross-repository:

```bash
gh pr view <N> --json isCrossRepository,headRepositoryOwner,headRefName,maintainerCanModify
```

If `isCrossRepository: true`, the PR's head branch lives on `<headRepositoryOwner.login>/Humans`, **not** on `origin` (peterdrier/Humans). Pushing to `origin HEAD:<headRefName>` will succeed but the commit will not appear on the PR — the PR's head SHA stays where it was, and the "fix" is invisible to reviewers.

**Why:** A push that goes nowhere is worse than a push that fails — git reports success, the agent reports "done", and the regression only surfaces when Peter checks the PR page and sees no new commit. Already burned once on peterdrier/Humans#602 (Frank Fanteev's `pie-info-pill` branch).

**How to apply:**

1. Check `isCrossRepository` before composing the push command.
2. If `true` and `maintainerCanModify: true`, ensure a remote exists for the contributor's fork:
   ```bash
   git remote get-url <login> 2>/dev/null || git remote add <login> https://github.com/<login>/Humans.git
   git fetch <login>
   ```
3. Push to that remote, not `origin`:
   ```bash
   git push <login> HEAD:<headRefName>
   ```
4. If `maintainerCanModify: false`, you cannot push directly — stop and ask Peter. Don't push to `origin` as a fallback; the PR won't see it.
5. After pushing, verify with `gh pr view <N> --json headRefOid` that the head SHA matches your new commit.

Most contributor remotes already exist in the worktree (check `git remote -v`) because earlier review rounds added them.

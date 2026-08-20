---
name: Check a PR's base branch before merging it
description: `gh pr merge` reports success when it squashes onto whatever `baseRefName` says — including a stale lane branch. Read base, mergeable and check rollup in one call before every merge.
---

Before merging any PR, confirm what it is merging *into*:

```bash
gh pr view <N> -R peterdrier/Humans --json baseRefName,mergeable,mergeStateStatus,statusCheckRollup \
  -q '.baseRefName+" "+.mergeable+" "+.mergeStateStatus'
```

`baseRefName` must be `main` unless you deliberately intend otherwise.

**Why:** PR #1395 in the nobodies-collective/Humans#1073 epic was opened against `1077-member-nav` (its parent lane) rather than `main`. That lane's own PR had already merged, so the branch was dead. `gh pr merge --squash` succeeded, GitHub reported the PR as `MERGED`, and the work landed on a branch nothing consumes — `main` never received it. Nothing in the merge output, the PR state, or the check rollup indicated a problem; the PR reads as merged to this day.

It surfaced only because a later sweep compared `merge_commit_sha` against `main`'s history. Recovering it cost a replacement PR (#1396), three rebases, and an audit of every other PR in the epic to prove none had the same defect.

A PR reporting `MERGED` is not evidence its content reached `main`.

**How to apply:**

- Run the check above before every `gh pr merge`. It is one call and it also gives you the mergeability and CI state you were about to ask for separately.
- After a batch of merges, verify content rather than status — confirm a distinctive symbol from each PR actually exists on `main`:

  ```bash
  git ls-tree -r --name-only origin/main | grep <file-the-PR-added>
  ```

- If a PR did merge to the wrong base: it cannot be reopened. Rebase the head branch onto `main`, open a replacement PR that references the original, and leave the original closed.
- The root cause is usually stacking — see [`lanes-branch-off-main`](lanes-branch-off-main.md). A lane cut from `origin/main` gets `main` as its base by default and this failure cannot occur.

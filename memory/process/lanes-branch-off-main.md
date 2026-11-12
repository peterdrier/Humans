---
name: Multi-lane epics branch off main, never off each other
description: When splitting an epic into parallel PRs, cut every lane from `origin/main` — never stack lane N on lane N-1 (causes rebase storms, duplicate-line merges, wrong-base merges).
---

An epic split into N PRs cuts N branches from `origin/main`. A lane never branches from another lane and never merges one in to "get its changes". If two lanes need the same new type, land that type first as its own small PR.

```bash
git fetch origin main
git worktree add .claude/worktrees/<lane> -b <lane> origin/main   # every lane, same base
```

Lanes are concurrent writers, so they keep separate worktrees even in a cloud run ([[always-use-worktree]]). Merge in any order and refresh the rest with `git rebase origin/main`.

**Why:** the fork squash-merges, so every merge orphans the parent's commits in each stacked lane. One eleven-PR stacked epic spent 43% of its shell calls on rebases, silently duplicated lines git reported as clean merges, and squashed one PR onto a dead branch.

**Two checks that go with it:**

- Before merging, verify the base: [`verify-pr-base-before-merge`](verify-pr-base-before-merge.md).
- After any merge or rebase into a lane, count anything that fans out, since a clean merge can add the same line twice:

```bash
grep -rn "<vc:chrome-slot" src --include=*.cshtml | sed 's/:[0-9]*:/:/' | sort | uniq -c | awk '$1>1'
grep -rn "DiscoverImplementations<" src --include=*.cs | sed 's/.*DiscoverImplementations</</' | sort | uniq -c | awk '$1>1'
```

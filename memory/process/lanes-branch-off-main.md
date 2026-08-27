---
name: Multi-lane epics branch off main, never off each other
description: When splitting an epic into parallel PRs, cut every lane from `origin/main`. Stacking lane N on lane N-1 is what makes squash-merge produce rebase-per-lane, silent duplicate-line merges, and wrong-base merges.
---

An epic split into N PRs cuts N branches from `origin/main`. A lane never branches from another lane, and never merges another lane into itself to "get its changes."

If two lanes genuinely need the same new type, land that type first as its own small PR and branch both lanes off the result — one extra merge, instead of a stack.

**Why:** The nobodies-collective/Humans#1073 epic (eleven PRs, #1385–#1396) stacked each lane on its predecessor. Because the fork squash-merges, every merge collapsed the parent's commits into one new SHA and orphaned the copies still sitting in every downstream lane. The measured cost, from the session transcript:

- **43% of all shell calls were git bookkeeping** (231 of 532) — rebasing, inspecting the rebase, resolving conflicts, verifying the resolution. Seven rebases, two lanes rebased twice, one three times, one 12-way conflict.
- **Silent duplicate-line merges.** A stacked merge that git reports as clean can add the same line twice when both sides introduced it in different hunks: `<vc:chrome-slot name="member-dashboard" />` was duplicated twice in `Dashboard.cshtml` (every contributing section would have rendered twice), `RegisterContributions(services)` once, and the `Program.cs` contributor `foreach` loops once. `git merge-tree` cannot see this class of bug — only counting occurrences per file finds it.
- **A wrong-base merge.** PR #1395's base was still the parent lane's branch rather than `main`, so `gh pr merge` squashed it onto a dead branch, reported success, and the work never reached `main`. It cost a replacement PR (#1396), three more rebases and a full audit.

None of that was caused by the work. It was caused by the branch topology.

**How to apply:**

```bash
git fetch origin main
git worktree add .worktrees/<lane> -b <lane> origin/main   # every lane, same base
```

Lanes are concurrent writers, so they keep separate worktrees even in a cloud run — the exception [[always-use-worktree]] names, alongside `/refactor-swarm`.

Merge order is then free — pick any lane, merge it, and refresh the rest with `git rebase origin/main`. Lanes that touch disjoint files won't conflict at all.

Two checks that belong to this rule:

- Before merging, verify the base: see [`verify-pr-base-before-merge`](verify-pr-base-before-merge.md).
- After any merge or rebase into a lane, count invocations per file for anything that fans out — view-component slots, discovery loops, DI registration loops:

```bash
grep -rn "<vc:chrome-slot" src --include=*.cshtml | sed 's/:[0-9]*:/:/' | sort | uniq -c | awk '$1>1'
grep -rn "DiscoverImplementations<" src --include=*.cs | sed 's/.*DiscoverImplementations</</' | sort | uniq -c | awk '$1>1'
```

A recurring duplication of this kind is a standing analyzer candidate, not something to keep catching by hand.

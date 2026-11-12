---
name: Agent isolation worktrees fork from origin/main and are reaped when idle
description: "`Agent(isolation:\"worktree\")` forks off `origin/main`, not the controller's feature branch, and reaps an idle worker's worktree before its first commit."
---

`Agent(isolation:"worktree")` branches the worker's worktree from **`origin/main`**, not from your feature branch HEAD, so a worker never sees the branch's earlier commits. Launched in parallel from inside a nested worktree, only some workers actually isolate; the rest commit concurrently on the shared branch.

**Default pattern for a swarm building up one branch:**

1. The orchestrator creates one shared worktree: `git worktree add -b <branch> .claude/worktrees/<n> origin/main`.
2. Workers are **edit-only**: absolute paths for every Read/Edit, no `git`, no `dotnet build`. Parallel is fine for disjoint files.
3. After each batch the orchestrator checks the main checkout is clean and unmoved, builds once, fixes slips and commits.

If you do use `isolation:worktree` (independent tasks off main), dispatch one worker first and confirm it took with `git worktree list` and `git merge-base` before fanning out.

**Reaped when idle.** An isolation worktree with no commits is removed when its agent stops; resuming that agent drops it into the shared main checkout. So every worker's first action after `git checkout -b` is `git commit --allow-empty -m "wip: start #<N>"`. If a worktree is already gone, spawn a fresh agent with the salvaged findings; don't resume the old one.

Related: [[always-use-worktree]], [[lanes-branch-off-main]], [[no-rm-rf]]

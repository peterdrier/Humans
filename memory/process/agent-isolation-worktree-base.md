---
name: agent isolation:worktree bases off origin/main, not your feature HEAD
description: When building up a multi-commit feature branch via subagent swarm, `Agent(isolation:"worktree")` forks each worker off origin/main, not the controller's current branch — don't rely on it for incremental branch work.
---

`Agent(isolation:"worktree")` creates the agent's worktree branched off **origin/main**, NOT off the controller's current (feature-branch) HEAD. Observed 2026-05-25 on a multi-wave analyzer-consolidation branch: a Wave B worker's commit had a merge-base of plain `origin/main`, so it lacked the branch's own earlier commits and re-deleted things already removed — 3-way-merge conflicts on an attempted fast-forward. Separately, spawning several such agents **in parallel** from inside a nested worktree flaked: only a fraction actually isolated, the rest ran in the controller's worktree and committed concurrently on the shared branch, entangling files mid-work.

**Why:** the worktree-swarm model assumes each task is independent off main (its design case: "N test files failing on main"). It does not fit incrementally building one feature branch where each worker must see prior workers' commits.

**How to apply:**
- For a swarm that accumulates onto a multi-commit feature branch, do NOT depend on `isolation:worktree` giving agents the branch state. Either (a) run subagents sequentially in the working worktree without the isolation flag — safe when the controller cwd is already a feature-branch worktree, since commits land there, never main/prod — or (b) pre-create each worktree yourself off the correct base (`git worktree add <path> <feature-HEAD>`) and merge back.
- If you do use `isolation:worktree`, verify it actually took before fanning out: dispatch ONE, then check `git worktree list` and `git merge-base <feature-HEAD> <worker-commit>`. Never launch the full fan-out on faith.

**Proven mechanism for multi-worker branch build-up (PR #782, 2026-05-25).** Orchestrator pre-creates ONE shared worktree (`git worktree add -b <branch> .worktrees/<n> origin/main`); dispatches workers (parallel is fine for disjoint files) that are **edit-only** — absolute paths for every Read/Edit (so cwd confusion can't misdirect writes), no `git`, no `dotnet build` (orchestrator owns both, so a stray commit can't land on the wrong branch and concurrent builds can't corrupt shared bin/obj). After each batch the orchestrator verifies the main checkout is clean and unmoved. Then it builds once, fixes worker slips, and commits. This is the default pattern for agent swarms on this repo.

Related: [[always-use-worktree]], [[worktrees-off-origin-main]], [[worktree-reaped-without-commits]].

---
name: always-use-worktree
description: "HARD RULE (local only). Any branch work or PR fix happens in a `.claude/worktrees/<name>` worktree off `origin/main`; the main checkout is read-only. Cloud runs use the repo root."
---

Decide once, before any exploration: `[ "$CLAUDE_CODE_REMOTE" = "true" ] && echo cloud || echo local`.

## Local machine: always a worktree

All branch work, including work on `main`, happens in `.claude/worktrees/<name>`. Never edit, stage, commit, reset, stash or checkout in the main checkout, and leave any uncommitted changes you find there alone.

**Why:** Peter's machines run 5–10 agent sessions against one clone; switching or resetting the main checkout destroys another session's work. Peter: "you ARE NEVER ALLOWED TO WORK IN MAIN."

- New work: `git fetch origin main && git worktree add -b <slug> .claude/worktrees/<slug> origin/main`. This is step 1, before any code exploration (after the author check in [[issue-fetch-protocol]] for issue work).
- Existing branch or PR: `git worktree list` first, then `git worktree add .claude/worktrees/<slug> <branch>` if none exists.
- Enter with the `EnterWorktree` tool (or `cd` once and stay). 
- **Pass an explicit `path` under the worktree's `src`/`tests` to every Glob/Grep.** They ignore `cd`, and the repo root holds many full checkouts at different commits, so an unscoped search returns inconsistent duplicates.
- Sole exception: the fork reset in [[after-prod-merge-reset]].

## Cloud run: repo root, no worktree

One ephemeral container, one session, fresh clone: nobody to trample. `git checkout -b <branch> origin/main` in the repo root and work there; searches need no `path`. Peter: "that rule is specific to running on any of my machines." Read every "worktree" in other atoms, skills and prompts as the repo root, every `git worktree add` as `git checkout -b`, and never run `git worktree remove`. Worktrees are still right in the cloud for a skill running parallel lanes that must not share a checkout (`/refactor-swarm`, [[lanes-branch-off-main]]).

## Both

- **Base is `origin/main`,** the fork, never `upstream/main`. Only a hotfix branch destined directly for upstream starts from `upstream/main`; if unsure which you have, ask.
- **Fixing a PR: switch to its head branch,** without asking. Peter: "you should just always switch to the branch from the pr.. NEVER CODE IN MAIN." Fetch it from the remote that carries it ([[cross-repo-pr-push-target]]). Ask only if uncommitted changes would conflict.
- Still a feature branch and a PR ([[no-direct-to-main]]).

Related: [[agent-isolation-worktree-base]], [[no-rm-rf]]

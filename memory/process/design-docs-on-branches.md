---
name: design-docs-on-branches
description: Design/spec docs go on the same branch as the implementation and get shared as a GitHub file link — don't open a PR for the spec until implementation is ready.
---

Design/spec documents go on the **same branch as the implementation**, pushed to origin, and shared with Peter as a direct GitHub file link (`https://github.com/peterdrier/Humans/blob/<branch>/<path>`). **Do not open a PR yet** — opening the PR triggers server-side code review (Codex/Claude bots) on a branch that's still just a spec.

**Why:** Peter can't read `.md` files in his terminal, so the spec has to be pushed to GitHub for him to review it. A separate "design PR" plus later "impl PR(s)" produces far too many PRs for one piece of work. Opening the PR before impl is ready also burns review-bot quota on incomplete work. This combines with [[one-branch-for-phased-plans]]: one branch, one PR, phase-tagged commits — the spec is just phase 0.

**How to apply:**
1. Create a worktree at `.worktrees/<name>` branched from `origin/main`.
2. Write and commit the spec in the worktree.
3. Push the branch to `origin` — do NOT open a PR.
4. Give Peter a direct GitHub link to the spec file on that branch.
5. After Peter approves the spec, proceed with implementation on the same branch.
6. Open the PR only when implementation is ready for review.

Applies to design specs, proposals, RIPs, and anything under `docs/superpowers/specs/` or `docs/superpowers/plans/` that needs human review. Does NOT apply to small doc updates (maintenance log, typo fixes) that don't need review — those can be plain commits on a regular branch.

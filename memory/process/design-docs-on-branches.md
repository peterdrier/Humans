---
name: design-docs-on-branches
description: Design/spec docs go on the same branch as the implementation, delivered as a draft PR — no separate "design PR", and the PR leaves draft only when implementation is ready.
---

Design/spec documents go on the **same branch as the implementation**, pushed to origin and opened as a **draft PR** ([[always-open-a-pr]], [[wip-prs-as-draft]]). Share the spec with Peter as the draft PR plus a direct GitHub file link (`https://github.com/peterdrier/Humans/blob/<branch>/<path>`). The PR leaves draft only when implementation is ready for review.

**Why:** Peter can't read `.md` files in his terminal, so the spec has to be on GitHub for him to review it. A separate "design PR" plus later "impl PR(s)" produces far too many PRs for one piece of work, and review bots (Codex) trigger on ready-for-review, not on drafts — so a draft carries the spec through review without burning bot passes on incomplete work. This combines with [[one-branch-for-phased-plans]]: one branch, one PR, phase-tagged commits — the spec is just phase 0.

**How to apply:**
1. Create a worktree at `.worktrees/<name>` branched from `origin/main`.
2. Write and commit the spec in the worktree.
3. Push the branch and open a **draft** PR; give Peter the PR and the direct file link to the spec.
4. After Peter approves the spec, implement on the same branch.
5. Mark the PR ready for review only when implementation is done.

Applies to design specs, proposals, RIPs, and anything under `docs/superpowers/specs/` or `docs/superpowers/plans/` that needs human review. Does NOT apply to small doc updates (maintenance log, typo fixes) that don't need review — those are plain commits on a regular branch.

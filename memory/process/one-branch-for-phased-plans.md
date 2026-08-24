---
name: one-branch-for-phased-plans
description: For multi-phase implementation plans where a later phase depends on an earlier one, use one branch with phase-tagged commits and one PR — don't stack PRs across phases.
---

When a plan has multiple phases that depend on each other (phase 2 needs phase 1's code, etc.), use ONE branch with one commit (or a small group of commits) per phase, and open ONE PR. Don't create branch-per-phase with stacked PRs.

**Why:** stacked PRs add review overhead — if a downstream review surfaces a change to an earlier phase, every dependent branch needs rebasing. Phase-tagged commits in a single branch already give reviewable granularity (`git log --oneline` shows the phase boundaries) without that tax. Squash-merging a stacked base PR also orphans the descendants' ancestry, requiring serial rebase-and-merge work that adds no value. Peter, after a build produced 3 stacked PRs: "it's fine, could have just been one branch though."

**How to apply:**
- Plans with N phases where phase N depends on phase N−1: ONE branch, ONE PR. Use clear phase-tagged commit messages so reviewers can navigate ("Phase 1: …", "Phase 2: …").
- Plans with truly independent phases (rare): each phase can be its own branch+PR off `origin/main`.
- When dispatching subagents per phase, point them at the SAME branch — each implementer commits and pushes onto the existing branch, doesn't create a new one.
- The one-PR-per-issue rule still applies: if the plan touches genuinely separable features, those go in their own PRs.

Related: [[design-docs-on-branches]] — the design spec is phase 0 of the same branch.

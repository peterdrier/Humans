---
name: refactor-runs-origin-main-only
description: For refactor-swarm / Reforge refactor runs, plan against origin/main only — open PRs are not lane-exclusion constraints; never derive a conflict map from in-flight PRs without asking first.
---

For `refactor-swarm` and similar Reforge-guided refactor runs, the plan is based on **origin/main only**. Open PRs — even ones touching a section's service/interface — are NOT lane-exclusion constraints; Peter treats them as "they rebase around the refactor," not blockers. The conflict map is built only across the refactor's own lanes, never against pre-existing PRs.

**Why:** one run spent a large amount of effort deriving a section-exclusion/conflict map from several open fork PRs, concluding nearly every cross-section candidate was "in-flight/blocked." Peter: "your inflight analysis is completely wrong … ignore all of them. this is based on origin/main only." The "exclude in-flight sections" instinct over-fired badly.

**How to apply:** when a refactor-swarm prompt says "exclude in-flight work," confirm scope in ONE line before spending any recon on it ("treat open PRs as blockers, or origin/main-only?") and default to origin/main-only. Don't `gh pr view --files` a pile of PRs to build an exclusion matrix.

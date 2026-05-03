---
name: Don't flag cheap DB reads on [AllowAnonymous] pages as perf issues
description: At 500-user scale with no data-leak risk, an auth guard on a cheap `[AllowAnonymous]` DB read is dead defensive code. Don't flag in code review.
---

Don't flag "method runs unconditionally on `[AllowAnonymous]` page even for anonymous users" as an IMPORTANT code-review finding when the only cost is a cheap DB read AND there is no data-leak risk (e.g. result is only rendered for authenticated users).

**Why:** The project explicitly targets ~500 users on a single server, and `CLAUDE.md` says "Prefer in-memory caching over query optimization" and "Don't over-engineer for scale." Many `[AllowAnonymous]` pages are primarily reached from authenticated flows in practice (e.g. `/Barrios/{slug}` is mostly hit by logged-in users via the CityPlanning map). An extra settings query on rare truly-anonymous traffic is not a real problem; an auth guard is dead defensive code for a scenario that doesn't matter. Contradicts Peter's rule "don't add validation for scenarios that can't happen" — the scenario can happen but the consequences are negligible.

**How to apply:** When reviewing PRs in this project, before flagging an unconditional call on an `[AllowAnonymous]` route, check:

1. Is there a data-leak risk (does the result get rendered/exposed to the anonymous user)? If yes, real finding.
2. Is the call expensive (large query, external API, heavy computation)? If yes, maybe worth mentioning.
3. Otherwise: don't flag it. At 500 users, a cheap extra DB read on a rare code path is fine.

Valid perf findings still include: N+1 queries, missing `.Include()` causing lazy loads, expensive loops, hot-path external calls. This rule is specifically about the "guard anonymous execution" category.

**Cross-reference:** This is a *don't-flag* anti-rule for `code-review-rules.md` reviewers — make sure that file's reviewer-handoff section also notes it.

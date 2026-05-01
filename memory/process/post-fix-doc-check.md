---
name: Check feature spec / section invariant docs after a fix, before commit
description: After completing a fix or feature, review the relevant `docs/features/*.md` and `docs/sections/*.md`. Update inline if behavior, auth, workflow, data model, or routes changed. Reduces churn from doc-only follow-up commits.
---

**After completing a fix or feature but before committing**, check the relevant BRDs in `docs/features/` and section invariants in `docs/sections/`, and update them if the change affects:

- Documented behavior
- Authorization rules
- Workflows / state machines
- Data model
- Routes / URLs
- Section invariants

**Why:** Reduces churn from separate doc-only commits, keeps docs in sync with code at the SHA level (so `git blame` on a doc line tells you which feature change it came from), and forces the author to think about whether the change has documented invariants that need updating.

**How to apply:**

- Before staging your final commit, scan `docs/features/<related>.md` and `docs/sections/<owning-section>.md` for invariants that the change touches.
- Update inline. If the change intentionally alters an invariant, update the doc to reflect the new state — don't leave stale rules.
- If the change has no doc-level effect, no update needed (don't manufacture doc churn).

**Related:** [`docs/sections/SECTION-TEMPLATE.md`](../../docs/sections/SECTION-TEMPLATE.md) for section invariant doc structure.

---
name: Check feature spec / section invariant docs after a fix, before commit
description: After completing a fix or feature, review the owning section's `Docs/features/*.md` specs and its `Docs/<Section>.md` invariants, plus any `docs/features/global/` spec it touches. Update inline if behavior, auth, workflow, data model, or routes changed. Reduces churn from doc-only follow-up commits.
---

**After completing a fix or feature but before committing**, check the owning section's `Docs/features/` folder for the spec and `Docs/<Section>.md` for the invariants, plus `docs/features/global/` for a cross-section spec and `docs/sections/` for a section not yet moved into its own project. Update them if the change affects:

- Documented behavior
- Authorization rules
- Workflows / state machines
- Data model
- Routes / URLs
- Section invariants

**Why:** Reduces churn from separate doc-only commits, keeps docs in sync with code at the SHA level (so `git blame` on a doc line tells you which feature change it came from), and forces the author to think about whether the change has documented invariants that need updating.

**How to apply:**

- Before staging your final commit, scan `src/Sections/Humans.<Section>/Docs/features/` for the related spec and `Docs/<Section>.md` for the invariants, and check for invariants that the change touches.
- Update inline. If the change intentionally alters an invariant, update the doc to reflect the new state — don't leave stale rules.
- If the change has no doc-level effect, no update needed (don't manufacture doc churn).

**Related:** [`docs/sections/SECTION-TEMPLATE.md`](../../docs/sections/SECTION-TEMPLATE.md) for section invariant doc structure.

---
name: Don't drop columns for decoupling work — the property override IS the migration
description: HARD RULE. When decoupling code from a column, the property override + read sweeps complete the work. Column drops are separate, optional, and waited until the full sequence is verified end-to-end in production.
---

When the goal is "stop relying on a column as the source of truth," the **property override + read sweeps are the migration.** The column drop is separate, optional, and dangerous — defer it until the entire multi-PR sequence has run through production successfully.

**Why:** For email-identity decoupling specifically, the `User.Email` override that returns first-verified-`UserEmail` (with `?? base.Email` fallback) means new code paths use UserEmails as canonical, while legacy LINQ filters that hit the column continue working unchanged. **No code is broken. No schema is changed. The architectural goal is achieved.** When an earlier PR 361 had attempted the column drop / `Ignore()` path, every existing user on the preview environment disappeared from the UI and Peter had to reset the PR environment. The "What's deferred" section in PR 361's body was the walk-back. Concrete fingerprint of getting this wrong: cloned QA users present in the DB but invisible to the app.

Peter's verbatim from 2026-04-29: "we can stop using columns of data and not drop them. you're overly complicating things again. we can be fully migrated past pr6 before we drop anything if need be. Stop doing dangerous things."

**How to apply:**

A "decouple X from Y" PR is **complete** when:
1. The property override (or equivalent shim) reads from the new source of truth.
2. Read sweeps redirect new/active code paths through the canonical service.
3. Writes to the old column are stopped (architecture test enforcement).
4. Tests pass.

A "decouple X from Y" PR is **NOT** required to:
- Drop the column.
- Add `Ignore()` to the EF mapping.
- Add a custom Identity store that hides the column from EF entirely.
- Refactor every legacy LINQ filter that still reads the column.

If the override masks the column without breaking anything, **leave the column alone** — indefinitely if need be. Schema changes are forward-only and dangerous; in-memory routing through an override is reversible by deleting the override.

Spec items like "PR 2 drops the column" can be revised down to "PR 2 makes the column unused; drop is optional, separate, scheduled when the full sequence is verified end-to-end in production."

When you find LINQ filter sites that read the column during decoupling work, that is **not a blocker** — those sites continue working because the column is still there. The thick-repo refactor for those sites is its own concern, untied to the decoupling effort.

"Stop doing dangerous things" is the canonical Peter feedback for: forcing column drops, hand-editing migrations, expanding scope past what's safe. When uncertain: leave the column, ship the override, move on.

**Related:** [`no-drops-until-prod-verified`](no-drops-until-prod-verified.md) — broader hard-storage rule.

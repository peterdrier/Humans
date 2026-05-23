---
name: SurfaceBudget is owner-applied only — never add it, never suggest it
description: HARD RULE. `[SurfaceBudget(N)]` is placed by the repo owner by hand on the surfaces they choose. Agents NEVER add it to a type and NEVER suggest adding it — not in a PR, review, or aside. It lives predominantly on read interfaces. An agent's only job is to keep an already-present N accurate when it edits that type.
---

`[SurfaceBudget(N)]` is **owner-applied only**. Peter decides where it goes and places it by hand. Agents never add the attribute and never suggest adding it — not in a PR, a review, or in passing.

**Why:** Agents over-applied it, spraying it onto healthy classes one "good candidate, +1" at a time. That broke builds (HUM0016 slack failures) and the cleanup cost outran any benefit. It's a deliberately narrow consolidation ratchet, not a quality badge to spread.

**How to apply:** Never add it to a type; never propose it; when asked what it does, describe HUM0015/HUM0016 and stop. The one thing you DO: when editing a type that already carries it, keep `N` exact — lower it when you legitimately remove a method (never raise it, never add the attribute to a fresh type). Per-type mechanics (no raises/splits/bag-of-flags) live in the `<remarks>` on `SurfaceBudgetAttribute`.

**Where it lives now:** the read-side `I…ServiceRead` interfaces (`ITeamServiceRead`, `IUserServiceRead`, `IConsentServiceRead`).

**Related:**
- [`interface-method-additions-are-debt`](../architecture/interface-method-additions-are-debt.md) — every method add is debt; the principle behind the budget.
- [`no-analyzer-suppressions`](../process/no-analyzer-suppressions.md) — don't silence HUM0015/HUM0016.

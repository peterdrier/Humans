---
name: SurfaceBudget is owner-applied only — never add it, never suggest it
description: HARD RULE. The `[SurfaceBudget(N)]` attribute is placed by the repo owner by hand on the specific surfaces they choose. Agents NEVER add it to a type and NEVER suggest adding it — not in a PR, not in a review, not in passing. It predominantly lives on read interfaces. An agent's only job is to keep an already-present budget number accurate when it edits that type.
---

`[SurfaceBudget(N)]` is **owner-applied only**. Peter decides when and where it goes, and places it by hand. Agents must never add the attribute to a type and never suggest adding it — not in a PR description, not in a code review, not as an aside.

**Why:** Agents over-applied the attribute, spraying it onto healthy classes one "this seems like a good candidate, +1" suggestion at a time. That caused build problems (HUM0016 slack failures on types that shouldn't have been budgeted) and became counter-productive — the cleanup cost exceeded any benefit. The attribute is a deliberately narrow consolidation ratchet, not a code-quality badge to spread. The mechanism only works when the owner controls adoption.

**How to apply:**

1. **Never add `[SurfaceBudget]` to a new type.** Even if the type looks like a perfect candidate (10+ methods, growing surface), do not add it and do not propose it.
2. **Never suggest expanding adoption.** No "consider adding SurfaceBudget to X", no review comments recommending it, no maintenance-sweep tickets to "budget more interfaces".
3. **When asked what it does, report and stop.** Describe what HUM0015 (over-budget) / HUM0016 (slack) enforce, where it's currently applied, then stop — don't pivot to recommending more.
4. **Keep already-present numbers accurate.** The one thing you DO when editing a type that already carries the attribute: if you legitimately remove a method (within the [`interface-method-additions-are-debt`](../architecture/interface-method-additions-are-debt.md) rules), lower `N` to match the new exact count. You still never *raise* it (that requires removing a method in the same PR per the attribute's own agent rules) and never *add* the attribute to a fresh type.

**Where it currently lives:** predominantly on read interfaces — the `I…ServiceRead` boundary types (`ITeamServiceRead`, `IUserServiceRead`, `IConsentServiceRead`). It is valid on classes/structs too, but the owner has kept adoption narrow on purpose.

**Scope:** All work in this repo, including agent-authored PRs and reviews. The full per-type rules (no raises, no splits, lower on net-negative, hit-a-wall-ask-the-owner) live in the XML `<remarks>` on `SurfaceBudgetAttribute` itself.

**Related:**
- [`interface-method-additions-are-debt`](../architecture/interface-method-additions-are-debt.md) — the principle behind SurfaceBudget; every method add is debt.
- [`surface-budget-history-trim`](surface-budget-history-trim.md) — when bumping a budget, trim the XML-doc history to the 3 newest entries.
- [`no-analyzer-suppressions`](../process/no-analyzer-suppressions.md) — don't silence HUM0015/HUM0016; fix the surface (or, here, leave the attribute off entirely).

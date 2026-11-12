---
name: interface-method-additions-are-debt
description: Adding a method to any interface is durable debt. Audit existing methods first — prefer a LINQ chain at the call site over a new method. Stop and ask Peter before adding one.
---

Adding a method to an interface is technical debt, not a refactor. Methods accrete one justified
increment at a time and never decay. The default is **reuse**. Applies to every interface: services,
repositories, cross-section contracts.

**How to apply — in order:**
1. **Audit the existing methods.** List every one whose return shape already covers the need; the
   list-returning and by-ids methods almost always do.
2. **Compose at the call site.** A short LINQ chain over an existing list-returning method beats a
   dedicated method. Choosing which field to show is view-model assembly and belongs at the
   presentation layer.
3. **Discount review-bot requests for new methods.** Reply on the thread with the reason instead of
   complying.
4. **Still need one? Stop and ask Peter**, presenting the methods you considered, why each didn't fit,
   and the alternative shapes you ruled out. That holds even for unbudgeted interfaces.

**Pre-approved** (listed in the PR): an optional parameter or tightened signature on an existing method,
and one owner-section method replacing two or more identical cross-section caller sequences —
[`debt-sweep-standing-policy`](../process/debt-sweep-standing-policy.md) D1.

**Enforcement:** `[SurfaceBudget(N)]` on the interface with analyzers HUM0015/HUM0016 caps the count;
this atom is the principle behind it.

**Why:** agent PRs grew the service interfaces method by method, and consolidating them afterwards
became the dominant maintenance cost.

**Related:** [`reuse-first-change-discipline`](../process/reuse-first-change-discipline.md) ·
[`no-business-logic-in-controllers`](no-business-logic-in-controllers.md)

---
name: Every interface-method addition is long-term technical debt — default to reuse, not add
description: Adding a method to ANY interface (budgeted or not) is durable technical debt, not a refactor. Methods accrete; they compound across interfaces; they take weeks to consolidate. Before adding any method, audit existing methods on that interface for one whose return shape already covers the need — most of the time a list-returning method + a short LINQ chain at the call site is the right answer. The long-term cost of every additional method outweighs the short-term gain of "a cleaner-feeling call site" nearly every time. Stop and ask Peter before adding.
---

Adding a method to an interface is **technical debt**, not a refactor. New methods accrete — they don't decay — and they compound across `IUserService` + `IUserEmailService` + `IProfileService` + `ITeamService` + `ICampService` + … into surfaces that take weeks of focused work to audit and consolidate.

**Why:** Past instance (the durable lesson behind this atom) — over the two weeks leading to 2026-05-09, multiple agent-generated PRs added methods one well-justified increment at a time across budgeted and unbudgeted service interfaces. Cleaning that up has been the dominant maintenance cost since: the `audit-surface` skill, the `interface-method-budget-ratchet` rule, and the `peterdrier#673` person-search consolidation (`-3` net) all exist because of this exact accretion pattern. The PR that produced this atom (#468 / issue #690) added an 11th email-lookup method to `IUserEmailService` (which already exposed ~20) just to satisfy a review-bot warning; reverted same session.

**The default is REUSE, not add.** Treat new-method requests with strong skepticism, including (especially) your own.

**How to apply — order of preference:**

1. **Audit existing methods first.** Before reaching for a new one, list every existing method on the interface whose return shape covers what you need. Look hard at `Get…ListAsync`, `Get…ByIdsAsync`, `GetEntities…ByUserIdAsync` — they almost always already return enough.
2. **Inline a chain at the call site.** A short LINQ chain (`.Where(…).OrderByDescending(…).FirstOrDefault()`) on an existing list-returning method is preferable to a dedicated new method. Picking which field to display is view-model assembly — it belongs at the presentation layer.
3. **Discount review-bot warnings that ask for new methods.** Bot warnings are advisory; surface-area discipline wins when they conflict. Reply on the thread explaining why, instead of complying mechanically.
4. **If you genuinely need to add one — STOP and ask Peter.** Present: which existing methods you considered, why each didn't fit, the long-term cost, and what alternative shapes (e.g. tightening an existing method's signature) you ruled out. Don't add unilaterally even on unbudgeted interfaces.

**Scope:** All interfaces, not just the budgeted set. The `interface-method-budget-ratchet` rule is the hard-enforcement version for `ITeamService` / `ICampService` / `IShiftManagementService` / `IProfileService` / `IUserService`. This atom is the broader principle that applies to every interface in the codebase, including `IUserEmailService`, `IAuditLogService`, every `I…Repository`, every cross-section interface — adding a method to any of them is debt and requires the same skepticism.

**Related:**
- [`interface-method-budget-ratchet`](interface-method-budget-ratchet.md) — strict down-only ratchet for the budgeted set; same principle, hard-enforced via test
- [`no-business-logic-in-controllers`](no-business-logic-in-controllers.md) — explicitly excludes view-model assembly, which is what most "extract this to a service" warnings are flagging

---
name: SurfaceBudget history — keep 3 most recent entries, max 50 tokens each
description: When editing the recent-history `<list type="bullet">` above a `[SurfaceBudget(N)]` interface, keep ONLY the 3 newest entries and compress each to ≤50 tokens. Older entries get dropped (the git log is the long-term record). Applies every time you bump the budget.
---

The XML-doc recent-history bullets above any `[SurfaceBudget(N)]` interface are a short-term aid for the next reviewer — not a changelog. The git log is the durable record.

**Why:** Histories were growing unbounded (`IProfileService` accumulated 10 entries, several >100 tokens each), eating context every time the interface is read for surface-budget work. Long entries also become stale faster — the recent rationale is what matters, not the multi-year accretion story.

**How to apply:**

1. When bumping `[SurfaceBudget(N)]` on any interface, **before** adding the new bullet, trim the existing list to the 2 most recent entries — so after adding yours it has 3.
2. Compress your new entry to **≤50 tokens** (~35 words). Lead with the budget transition (`32→27`), state what you removed or added, name the callers if useful. No exhaustive method lists, no decision rationale prose — that belongs in the commit body.
3. Older entries are NOT preserved. `git log -p` on the interface file reaches every removed entry by definition.

**Shape:**

```xml
/// <list type="bullet">
///   <item>27→24 — drained 3 cross-section accessors; callers moved to IUserService.GetAllUserInfos.</item>
///   <item>32→27 — UserInfo snapshot consolidation; 5 single-caller readers inlined, 3 multi-caller swap-impls.</item>
///   <item>2026-05-13 — 31→32: GetProfilePictureMigrationSnapshotAsync added for DB→FS migration verification page.</item>
/// </list>
```

**Scope:** Every interface with `[SurfaceBudget(N)]`. Currently 18 interfaces; trim on touch, not in a sweep.

**Related:**
- [`interface-method-additions-are-debt`](../architecture/interface-method-additions-are-debt.md) — the principle behind SurfaceBudget itself.

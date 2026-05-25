---
name: cached-reads-no-shape-variants
description: When a read surface is served from an in-memory cache that holds the fully-populated payload, do not offer "lighter" shape variants (`WithEmails`, `IncludeFoo`, `withChildren=false`). The cache only knows one shape — variants split the contract for no benefit and reintroduce EF-shaped thinking into a cache-first surface.
---

When a read method serves from an in-memory cache of a fully-populated DTO (e.g. `UserInfo`, `TeamInfo`, `FullProfile`), the surface returns one shape: whatever the cache holds. **Do not** add `WithEmails` / `WithChildren` / `includeFoo` variants or boolean flags that gate which navigations are populated.

**Why:** Variants are an EF-shaped optimization — at the DB layer skipping `.Include(x => x.Foo)` saves a join. Once the cache is the source of truth, the navigation is already in RAM; the "lighter" call path saves only an allocation, at the cost of two methods doing almost the same thing and a flag that callers misread as "the cache might not have emails." It doesn't. The cache holds the canonical shape. Peter caught this on PR #625 ([nobodies-collective/Humans#744](https://github.com/nobodies-collective/Humans/issues/744)) — `CachingUserService` was preserving `GetByIdsAsync` / `GetByIdsWithEmailsAsync` as two methods routed through a private `GetByIdsInternalAsync(..., includeEmails, ...)` helper that "broke the caching pattern fundamentally": there is nothing else the cache can serve.

**How to apply:**

- New read methods on a cached service surface (`IUserService`, `IProfileService`, `ITeamService`, ...): return the full DTO/entity payload. No `includeFoo` parameters.
- When collapsing pre-existing EF-shaped variants into a single cache-first method, also collapse the underlying repository surface — the inner repo's `.Include(...)` becomes unconditional. The two-method split was only meaningful when the source was EF.
- If a future caller genuinely needs a slimmer shape (rare — usually the answer is "use `UserInfo` directly"), introduce a *different* method with a clearly different purpose, not a flag-gated variant.
- Exception: paging/filter parameters (`since`, `limit`, predicate) are not shape variants; they change *which rows* return, not *which fields* are populated per row.

**Related:**

- [`caching-transparent`](caching-transparent.md) — the cache shouldn't leak into type names; this rule says it also shouldn't leak into method-shape variants.
- [`iuserservice-onestop-userinfo`](iuserservice-onestop-userinfo.md) — long-term direction for the User-section surface.
- [`interface-method-additions-are-debt`](interface-method-additions-are-debt.md) — adding a second method is itself debt; collapsing toward one is the default.

---
name: decorators-talk-only-to-inner
description: HARD RULE. A caching/wrapping decorator over interface I may only depend on I (via its keyed inner registration) and the cache plumbing. No sideways repository, service, or sibling-section injections — ever.
---

A class that decorates interface `I` (e.g. `CachingXService : IX, IHostedService` over a keyed-Scoped inner `IX`) is allowed exactly two kinds of collaborator:

1. The inner `IX` it wraps (resolved via `IServiceScopeFactory` per call for Singleton-over-Scoped decorators).
2. The cache plumbing it composes or inherits (`TrackedCache<TKey,TValue>`, `ICacheStats`, `IMemoryCache`, loggers).

It does **not** inject `IUserRepository`, sibling repos, `IUserService`, or any other section's surface — not for warmup, not for "I just need the user list," not for anything. If the decorator needs more data than its inner can produce, the **inner** is the right place to add it (the inner is a regular service that's allowed to talk to repos). The decorator stays a thin caching shell.

**Why:** When this rule slips, the decorator silently becomes a second copy of the section's data-assembly logic, dependency-injected with cross-section repos and reaching across ownership boundaries. The next person reading the code can't tell what the cache wraps vs. what it *is*. Concretely, this rule was articulated after a /Admin first-hit perf fix attempt injected `IUserRepository` into `CachingShiftViewService` to drive a startup warm-up — the right answer was to fix the inner's `GetUsersAsync` to bulk-load and let the decorator just batch cache-misses through it.

**How to apply:**

- Adding a new method to a `Caching*` decorator? Its body should be: cache-lookup, maybe a single delegation to the inner, cache the result, return. Anything else is a smell.
- Need bulk semantics? Add them to the **inner's** interface method (`IX.GetUsersAsync(IReadOnlyCollection<Guid>)`) and implement them in the inner using whatever repos the inner already injects. The decorator's batch method collapses cache-misses into one inner call.
- Need data from outside the section to warm? Don't. The caller already has the universe of ids (e.g. `AdminDashboardService` already snapshots users via `IUserService`). Push that responsibility to the caller, not into the decorator.
- DI registration smell: if your decorator's constructor signature drifts away from `(IServiceScopeFactory scopeFactory, ILogger<Self> logger)` plus optional cache primitives, you've broken this rule.

**Related:**

- `caching-transparent` (no `Cached*` types in the domain surface).
- `cached-reads-no-shape-variants` (one shape per cache, not `IncludeFoo` variants).
- `docs/architecture/design-rules.md` §15 (caching pattern).

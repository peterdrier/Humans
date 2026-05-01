---
name: Caching is transparent to the app — no `Cached*` types in the public surface
description: When introducing caching, public-facing type names must not leak the cache. Consumers shouldn't know whether data came from memory or the database. `Full<Section>` is the §15 stitched-DTO convention.
---

When adding a caching layer (decorator, dict cache, projection DTO), the public-facing type names must not advertise the cache. Consumers should not know whether their data came from memory or the database — they call `IXService` and get the result.

**Why:** Peter explicitly rejected `CachedUser` as a DTO name during the User §15 migration (PR #243): "do not call the object cached*. caching is transparent to the app." The type-naming leakage would tie consumers to the caching implementation and make future decorator changes painful. It also makes the "no caching is a valid outcome" path awkward — Governance (#242) and User (#243) both ended up without decorators because caching wasn't warranted; a `Cached*` naming convention would push every section toward cache-when-you-shouldn't.

**How to apply:**

- DTO types for cached projections follow the `Full<Section>` pattern when they stitch multiple entities (`FullProfile` = Profile + User + CV). Peter doesn't love the `Full` prefix because it breaks alphabetical sorting, but accepts it as the §15 convention.
- If a DTO would only wrap a single entity's fields (no stitching), ask whether the DTO is needed at all — pass-through reads via the service interface returning the entity directly may be fine.
- **Never introduce a type named `Cached*` for domain data.** Cross-section invalidator interfaces (`IFullProfileInvalidator`) are fine because they name the concept, not the cache.
- Caching decorators themselves (`CachingProfileService`) are an exception — they live in Infrastructure and consumers never reference them; the `Caching` prefix is the implementation class name, not exposed through DI.

**Related:** [design-rules.md §15f](../../docs/architecture/design-rules.md#15f-projection-dto-naming) — projection DTO naming convention.

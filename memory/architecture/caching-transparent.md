---
name: Caching is transparent to the app - no `Cached*` types in the public surface
description: When introducing caching, public-facing type names must not leak the cache. Consumers should not know whether data came from memory or the database. Use canonical read-model names such as `TeamInfo` or established stitched names such as `FullProfile`.
---

When adding a caching layer (decorator, dict cache, projection DTO), the public-facing type names must not advertise the cache. Consumers should not know whether their data came from memory or the database; they call `IXService` and get the result.

**Why:** Peter explicitly rejected `CachedUser` as a DTO name during the User section migration (PR #243): "do not call the object cached*. caching is transparent to the app." The type-naming leakage would tie consumers to the caching implementation and make future decorator changes painful. It also makes the "no caching is a valid outcome" path awkward: Governance (#242) and User (#243) both ended up without decorators because caching was not warranted; a `Cached*` naming convention would push every section toward cache-when-you-should-not.

**How to apply:**

- DTO types for cached projections use canonical read-model names that do not imply EF entity identity. Prefer `<Section>Info` for section-owned read models (`TeamInfo`) and keep established stitched names where they already exist (`FullProfile` = Profile + User + CV).
- If a DTO would only wrap a single entity's fields, ask whether the DTO is needed at all. For service boundaries, prefer a read model over returning an EF/domain entity when callers are outside the owning section.
- **Never introduce a type named `Cached*` for domain data.** Cross-section invalidator interfaces (`IFullProfileInvalidator`) are fine because they name the concept, not the cache.
- Caching decorators themselves (`CachingProfileService`) are an exception. They live in Infrastructure and consumers never reference them; the `Caching` prefix is the implementation class name, not exposed through DI.

**Related:** [design-rules.md section 15f](../../docs/architecture/design-rules.md#15f-canonical-read-model-naming) - canonical read-model naming convention.

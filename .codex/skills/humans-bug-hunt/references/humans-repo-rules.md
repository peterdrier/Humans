# Humans Repo Rules

Use this reference to guide bug-hunt passes in the Humans codebase without re-reading a long prompt each time.

## Mission

Find and fix real bugs. Do not refactor for cleanliness, improve style, or add features.

A good bug candidate usually causes one of these:

- incorrect runtime behavior
- broken rendering or invalid HTML behavior
- lost form state on validation errors
- stale or poisoned cache data
- bad redirects, broken links, or missing error handling
- null-driven failures or silently missing related data
- drift between the app and external providers
- time or range logic using the wrong boundary

## Repo Map

- `src/Humans.Domain/`: entities, enums, value objects
- `src/Humans.Application/`: interfaces, DTOs, constants
- `src/Humans.Infrastructure/`: EF Core, external integrations, jobs, caching
- `src/Humans.Web/`: controllers, Razor views, authorization, UI flow
- `tests/Humans.Application.Tests/`: fast regression coverage

Start with controllers, services, and views. Prefer application tests when adding coverage.

## Forbidden Areas

Never change how data is stored or migrated.

Avoid these paths entirely:

- `src/Humans.Infrastructure/Data/HumansDbContext.cs`
- `src/Humans.Infrastructure/Data/EntityConfigurations/**`
- `src/Humans.Infrastructure/Migrations/**`

Also avoid entity-shape cleanup, JSON serialization attribute changes, and any schema-adjacent edits.

## High-Yield Search Patterns

Search for issues like these, but use judgment instead of treating them as a mandatory checklist:

- Razor boolean attributes that stay present when false
- tables or layouts whose conditional columns get out of sync
- controller POST paths that redisplay a view without restoring needed view data
- redirect targets or route names that do not match real actions
- login or external-auth paths that lose `returnUrl` or hide the actual error
- cache entries that are not evicted after mutation, or transient failures that get cached too aggressively
- service queries that materialize entities and then access unloaded navigation properties
- date and time checks that use start when they should use end, or vice versa
- string comparisons that rely on culture or case accidentally
- external-service error paths that replace real data with misleading placeholder values
- sticky-table CSS or view logic that breaks on horizontal scroll or conditional columns

## Hunt Discipline

- Prefer bugs you can explain concretely from code and verify locally.
- Keep fixes small and isolated.
- Add regression tests when the behavior is easy to lock down.
- Commit one fix per commit.
- Push after verified progress so the branch is resumable.
- Stop when only low-confidence or forbidden changes remain.

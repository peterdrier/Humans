# Humans Tech Debt Rules

Use this reference to guide autonomous tech-debt reduction passes in the Humans codebase.

## Mission

Reduce impactful tech debt. Favor consolidation, clearer ownership, shared patterns, and simpler code paths. Do not add features or chase style-only cleanup.

Good candidates usually look like:

- duplicated logic that should be shared
- the same concern implemented with conflicting patterns
- controllers carrying business logic that should live in services
- services owning responsibilities from the wrong domain
- repeated magic strings or route names that should use constants or `nameof()`
- missing abstractions where repetition is already real
- stale abstractions that create indirection without value

## Repo Map

- `src/Humans.Domain/`: entities, enums, value objects
- `src/Humans.Application/`: interfaces, DTOs, constants
- `src/Humans.Infrastructure/`: EF Core, external integrations, jobs, caching
- `src/Humans.Web/`: controllers, Razor views, authorization, UI flow
- `tests/Humans.Application.Tests/`: fast regression coverage

## Forbidden Areas

Never change how data is stored or migrated.

Avoid these paths entirely:

- `src/Humans.Infrastructure/Data/HumansDbContext.cs`
- `src/Humans.Infrastructure/Data/EntityConfigurations/**`
- `src/Humans.Infrastructure/Migrations/**`

Also avoid entity-shape cleanup, serialization attribute changes, and other schema-adjacent edits.

## Tech-Debt Priorities

Prioritize items with clear payoff:

- layer-separation violations: EF/repository/service code doing presentation sorting, UI filtering, arbitrary caps, navigation joins, or cross-section DB reads
- one concern implemented several different ways
- copy-pasted controller or service logic with the same business meaning
- cross-domain methods that belong in a different service
- error-handling, caching, or authorization patterns that should be standardized
- route names, roles, or repeated strings that should use constants or `nameof()`

Deprioritize:

- cosmetic naming-only cleanups
- large rewrites with weak behavioral payoff
- breaking interface churn unless the call graph is fully understood
- file splitting done only to reduce line count

## Layer-Separation Rules

Treat these as the first-pass search rules for new tech-debt runs.

Repositories are persistence adapters. They may apply storage predicates needed to fetch the owned data set, but they must not own screen behavior:

- no display sorting or user-facing ordering in repositories
- no screen/page caps such as `Take(50)`, "recent", "top", or dashboard limits in repositories
- no query-string, tab, or screen-specific filtering in repositories
- no joins/includes added only to shape a view model

Controllers and views own UI-specific shaping:

- final display sort order
- screen-specific filters
- paging/window sizes and `Take(...)` limits
- grouping and secondary ordering for tables/cards
- dashboard-card "recent" or "top N" choices

Services own reusable application behavior, not display choices. If a rule is truly domain/application behavior, put it in the owning service. If it is just how one screen wants to sort, filter, group, cap, or show data, keep it at the controller/view boundary.

Sections must not reach across each other's persistence:

- no cross-section DB calls or joins from repositories or services
- no EF navigation joins across section boundaries
- no repository method returning another section's entity graph
- call the other section's public service/interface by IDs instead
- merge data in memory at the controller/application boundary when a screen needs multiple sections

Prefer typed foreign-key queries and narrow projections over navigation-property graph loading. A slower but explicit service call boundary is better than a hidden cross-section join.

## Safety Checks

- Verify every change preserves behavior except for the intended simplification.
- When touching interfaces in `Application/`, check all implementations and callers.
- When touching authorization, preserve the exact access level.
- Keep controllers thin and services cohesive, but do not move code across boundaries unless the ownership problem is obvious and local.
- Stop if the next step drifts into database, migration, or entity-shape changes.

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

## Safety Checks

- Verify every change preserves behavior except for the intended simplification.
- When touching interfaces in `Application/`, check all implementations and callers.
- When touching authorization, preserve the exact access level.
- Keep controllers thin and services cohesive, but do not move code across boundaries unless the ownership problem is obvious and local.
- Stop if the next step drifts into database, migration, or entity-shape changes.

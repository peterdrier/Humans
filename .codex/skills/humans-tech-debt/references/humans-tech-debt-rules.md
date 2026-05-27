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

Repositories are persistence adapters. They may apply storage predicates needed to fetch the owned data set, but they must not own screen behavior. Cross-section DB joins/includes are more important than display-sort cleanup and should be fixed first:

- no display sorting or user-facing ordering in repositories
- no screen/page caps such as `Take(50)`, "recent", "top", or dashboard limits in repositories when the source is a bounded/cached set
- no query-string, tab, or screen-specific filtering in repositories
- no joins/includes added only to shape a view model

Exception: do not load unbounded operational/history tables just to sort/filter/take in a service or controller. Audit, email outbox, feedback, issue lists, and similarly growing tables should use explicit repository methods that perform DB-side ordering/paging/windowing and return only the bounded slice. Mark those intentional exceptions inline with `// arch:db-sort-ok top-N selector`, `// arch:db-sort-ok admin page window`, or a similarly specific reason.

Controllers and views own UI-specific shaping for finite/cached data sets such as profiles, teams, camps, and option lists:

- final display sort order
- screen-specific filters
- paging/window sizes and `Take(...)` limits
- grouping and secondary ordering for tables/cards
- dashboard-card "recent" or "top N" choices

Services own reusable application behavior, not display choices. If a rule is truly domain/application behavior, put it in the owning service. If it is just how one screen wants to sort, filter, group, cap, or show data over already-bounded data, keep it at the controller/view boundary. For unbounded tables, services may orchestrate after a repository-selected slice, but must not load the full table and then apply top-N/windowing in memory.

Sections must not reach across each other's persistence:

- no cross-section DB calls or joins from repositories or services
- no EF navigation joins across section boundaries
- no repository method returning another section's entity graph
- call the other section's public service/interface by IDs instead
- merge data in memory at the controller/application boundary when a screen needs multiple sections

Users, Profiles, and UserEmail are one ownership section: Humans. Do not move code between `Services.Users` and `Services.Profile`, and do not add wrapper service methods, just to satisfy a section-boundary cleanup. Treat `IUserRepository`, `IProfileRepository`, and `IUserEmailRepository` as same-section repositories for this rule.

Prefer typed foreign-key queries and narrow projections over navigation-property graph loading. A slower but explicit service call boundary is better than a hidden cross-section join.

## Interface Consolidation Rules

Large interfaces should not grow one convenience method per caller. Prefer smaller, composable contracts.

Bad pattern:

- `GetActiveProfilesAsync()`
- `GetSuspendedProfilesAsync()`
- `GetProfilesForDashboardAsync()`
- `GetRecentOrdersAsync()`
- `GetTopTicketsAsync()`

Better pattern, when the result set is safe to materialize and the shaping is screen-specific:

- `GetProfilesAsync()` plus caller-side `.Where(p => p.IsActive)`
- `GetProfilesAsync()` plus caller-side `.Where(p => p.IsSuspended)`
- `GetProfilesAsync()` plus controller/view `.OrderBy(...).Take(20)`

Start interface-consolidation passes with the largest interfaces and work down by method count. This gives the best payoff and catches the contracts most likely to accumulate AI-generated helper methods.

Fixing strategy:

- Rank `src/Humans.Application/Interfaces/**/*.cs` interfaces by public method count.
- Inspect the largest interfaces first.
- Find method families that differ only by status, filter, sort, window, dashboard, or screen.
- Collapse them into one broader section-owned method when behavior remains clear and result size is safe.
- Move screen-specific `.Where(...)`, `.OrderBy(...)`, `.Take(...)`, grouping, and dashboard shaping to controllers/views.
- Update callers and tests in the same commit.

Do not collapse methods that represent real domain concepts, authorization boundaries, operational queue semantics, expensive server-side queries that cannot safely materialize, or intentionally bounded search/tool APIs.

## Read-Model Enrichment Rule

When a caller needs a stable fact about an aggregate already exposed through a bounded or cached canonical read model, prefer adding scalar fields to that DTO over adding a new interface, repository, service method, or one-off query implementation.

Good pattern:

- add `StripeFee` and `ApplicationFee` to `TicketOrderInfo`
- let callers derive totals from `ITicketServiceRead.GetTicketOrdersAsync()`
- keep the existing cached/read boundary as the one source of ticket-order facts

Bad pattern:

- add `ITicketingBudgetRepository`
- add `TicketingBudgetRepository`
- duplicate a `TicketOrders` query only to project two fee values and a count

New durable surfaces are justified when the data is unbounded, permission-specific, expensive to materialize through the read model, transactional, sensitive, not a stable fact of the aggregate, or when adding the fields would pollute the canonical DTO with screen-only concerns. Otherwise, a few scalar DTO properties are lower weight than another interface, implementation, DI registration, test fixture, cache path, and HUM0025 ownership surface.

## Section Read-Service Rules

When a section exposes both a full service interface and a `*ServiceRead` interface, code outside the owning section should depend on the read interface unless it truly writes, invalidates caches, performs an owning-section workflow, or needs an entity-only method that has no read-model equivalent yet.

Read interfaces are the preferred cross-section boundary:

- `ITeamServiceRead` returns `TeamInfo` / `TeamSearchHit` projections for external Teams reads; only Teams-owned write flows and entity-specific operations should inject `ITeamService`.
- `IUserServiceRead` returns `UserInfo`, `HumanSearchResult`, `OnsiteUserRow`, and merge-chain read projections for external Users reads; write flows, identity mutations, account deletion, merge, and entity-only operations may keep `IUserService`.
- `IConsentServiceRead` is the external Consent read surface; consent submission and consent-owned writes keep `IConsentService`.
- When adding the same split to another section, first define a narrow `ISectionServiceRead` around the canonical cached projection, make the full service inherit it, register both interfaces to the same caching decorator, and migrate external read-only callers to the read interface.

Migration strategy:

- Search production code for full-service injections such as `IUserService`, `ITeamService`, `IConsentService`, and future `I*Service` interfaces that also have a `*ServiceRead`.
- Ignore implementations, caching decorators, DI registrations, tests that intentionally exercise full-service behavior, and same-section write/orchestration flows.
- For each external caller, inspect every method used. If all calls are available on the read interface, change the constructor/field/base class/helper parameter and update tests to substitute the read interface.
- If a caller uses an entity-returning legacy method only to render display data, migrate the caller to the read projection (`UserInfo`, `TeamInfo`, `CampInfo`, etc.) instead of keeping the full interface.
- Leave TODO-free notes only when a full-service dependency is intentionally retained because it writes or still needs an unmigrated entity-only operation.

## Safety Checks

- Verify every change preserves behavior except for the intended simplification.
- When touching interfaces in `Application/`, check all implementations and callers.
- When touching authorization, preserve the exact access level.
- Keep controllers thin and services cohesive, but do not move code across boundaries unless the ownership problem is obvious and local.
- Stop if the next step drifts into database, migration, or entity-shape changes.

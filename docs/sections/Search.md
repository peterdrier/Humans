<!-- freshness:triggers
  src/Humans.Application/Services/Search/**
  src/Humans.Application/Interfaces/Search/**
  src/Humans.Web/Controllers/SearchController.cs
  src/Humans.Web/Views/Search/**
  src/Humans.Application/DTOs/GlobalSearchResults.cs
-->
<!-- freshness:flag-on-change
  The GUID-vs-text visibility split, the five-section fan-out list, and the Search-vs-HumanSearchViewComponent naming boundary — review when SearchService's dependencies change or when a destination page's own visibility gate changes.
-->

# Search — Section Invariants

Orchestrator behind the global `/Search` page. Fans out to five sections' read surfaces, scores each independently, owns no tables.

## Concepts

- The **Search** section is the global `/Search` page and its backing orchestrator, `SearchService`. It matches each entity's own public fields (name, plus a public bio bucket for humans) and returns five independently-ranked, unsorted buckets — Humans, Teams, Camps, Shifts, Events. There is no cross-modal traversal (a person hit never pulls in their teams) and no cross-type ranking.
- A **query** shorter than 2 characters after trim returns all five buckets empty — not an error, not the full dataset.
- **`onlyType`** short-circuits the fan-out: when set, `SearchService` calls only the one matching section instead of all five and discarding four. Backs the per-type filter chips.
- **The naming trap this doc exists to record:** `/Search` (this section, `SearchService`/`SearchController`) is unrelated to `HumanSearchViewComponent` (the `<vc:human-search>` inline person-picker). Both share the word "search," but `HumanSearchViewComponent` is **Users-owned** — it calls `IUserServiceRead.SearchUsersAsync` directly for in-form pickers (camp role assignment, team-admin member picker, ticket-transfer recipient lookup) and is correctly excluded from this section's `reforge.surface-score.json` paths. See [`person-search`](../../memory/architecture/person-search.md). A reader going only from the shared name would reasonably assume both belong here — they don't; only the `/Search` page and `SearchService` do.

## Data Model

None — Search owns no tables. It is a pure read/fan-out orchestrator over five other sections' service interfaces (see Cross-Section Dependencies).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | Full `/Search`: a text query returns the public-visibility surface across all five types; a GUID query resolves any searchable entity unfiltered (see Invariants) |
| Admin / any admin role | Same surface as any authenticated human — Search has no privileged/admin search mode (tracked at nobodies-collective/Humans#693) |
| Anonymous | Cannot reach `/Search` — `[Authorize]` redirects to sign-in |

## Invariants

- A query shorter than 2 characters after trim short-circuits to an empty `GlobalSearchResults` — no call to any of the five sections.
- `onlyType` skips the fan-out to the other four sections entirely, not just their display.
- Each bucket is scored independently on name-match strength (exact = 100, prefix = 80, contains = 60); there is no cross-type ranking and no result cap — every match returns (capping was tried and reverted: at ~500-user scale it hid people users were looking for). Display ordering (score desc, then title/burner-name asc) is a `SearchController` concern, never pushed into `SearchService` ([`display-sort-in-controllers`](../../memory/architecture/display-sort-in-controllers.md)).
- **Text queries are visibility-filtered per section, role-blind.** Hidden teams, non-public camp seasons (outside `Active`/`Full`), admin-only rotas, and admin-only profile fields are excluded for every viewer — there is no admin-privileged text-search bucket. The filters are constants applied inside each section's own search call (`CachingTeamService`, `CachingCampService`, `ShiftManagementService`, `IUserServiceRead.SearchUsersAsync(..., PersonSearchFields.PublicAll, ...)`), not a role check `SearchService` performs itself.
- **GUID queries are NOT visibility-filtered — this is a routing convenience, not an authorization statement.** Pasting a searchable entity's own id (Team, Camp, Shift/Rota, Human) resolves it directly, bypassing the text-query visibility filters, on the reasoning that a caller can only use an id they already hold. Search may therefore return a hit the caller cannot open; enforcement is entirely the destination page's job (Peter's ruling on nobodies-collective/Humans#985, 2026-08-07). **Do not re-add a visibility check on the GUID path** — that was the pre-ruling behavior and was deliberately removed.
- There are no hidden users, so the Humans bucket applies the same `PersonSearchFields.PublicAll` mask to both text and GUID queries — humans are unconditionally resolvable either way.
- **Known destination-page gap:** `/Camps/{slug}` and `/Camps/{slug}/Season/{year}` do not yet hold up the GUID-resolves-but-destination-refuses contract — `CampController.Details`/`SeasonDetails` have no season-status gate, so a non-public season's detail page currently renders for any viewer, anonymous included. Tracked at nobodies-collective/Humans#993 (filed 2026-08-07); the Search layer is not what needs to change.
- `Features:Events` gates the Events bucket end to end — the fan-out call, the filter chip, and the results section are all skipped together when the flag is off.

## Negative Access Rules

- Anonymous visitors **cannot** reach `/Search` — `[Authorize]` redirects to sign-in.
- No viewer, Admin included, **cannot** get admin-only profile fields, a hidden team, a non-public camp season, or an admin-only rota to surface via a **text** query — the exclusion is structural (a constant mask/filter), not a per-role branch, so there is no privileged variant to grant.
- `SearchService` **cannot** query any section's tables directly — every bucket comes from that section's public service interface, never a repository or `HumansDbContext` (enforced: `SearchArchitectureTests`, analyzers HUM0026/HUM0027 on its `IOrchestrator` marker).
- A search hit for an entity the viewer isn't entitled to open is **not** treated as a Search-layer privacy bug — refusal belongs to the destination page. The one currently known exception is the `/Camps/{slug}` gap above, tracked separately.

## Triggers

None — this section is a pure read/fan-out surface with no side effects: no writes, no audit entries, no notifications, no cache invalidation of its own (each fan-out target manages its own cache independently).

## Cross-Section Dependencies

- **Users/Identity**: `IUserServiceRead.SearchUsersAsync(query, PersonSearchFields.PublicAll, limit)` — Humans bucket.
- **Teams**: `ITeamServiceRead.SearchAsync` — Teams bucket, `Team.Name` only, filtered to non-hidden.
- **Camps**: `ICampServiceRead.SearchAsync` — Camps bucket, public-year `CampSeason.Name` only, filtered to `Active`/`Full` season status.
- **Shifts**: `IShiftManagementService.SearchAsync` — Shifts bucket (Rota names), filtered to `IsVisibleToVolunteers`. Full service interface, not a `Read`-suffixed one — Shifts has not split a read interface for this call.
- **Events**: `IEventServiceRead.GetApprovedEventsAsync(...)` — Events bucket, gated by `Features:Events`; reuses the public Browse query (`Status = Approved` only, matched on Title or Description).
- **Configuration**: `IConfiguration` — reads the `Features:Events` flag directly (not a section dependency, listed for completeness of the constructor).

## Architecture

**Owning services:** `SearchService` (orchestrator)
**Owned tables:** None — orchestrator over `IUserServiceRead`, `ITeamServiceRead`, `ICampServiceRead`, `IShiftManagementService`, `IEventServiceRead`.
**Status:** (A) Migrated — orchestrator from inception. Its marker was corrected `IApplicationService` → `IOrchestrator` in nobodies-collective/Humans#987 (PR peterdrier/Humans#1197, 2026-08-07) to match its actual shape: no table, no repository injection, coordinates five sections through their public interfaces.

- `SearchService` lives in `Humans.Application.Services.Search/` and never imports `Microsoft.EntityFrameworkCore`.
- **No repository.** `SearchService` carries `IOrchestrator`, not `IApplicationService` — it injects no `I*Repository` and no `HumansDbContext`, enforced by analyzers HUM0026 (no repo/DbContext injection) and HUM0027 (`IOrchestrator` xor `IApplicationService`), plus `SearchArchitectureTests`.
- **Decorator decision — no caching decorator on `SearchService` itself.** It composes results already served from each source section's own caching layer: Humans/Teams/Camps are read from those sections' warm in-memory snapshots (`CachingUserService`/`CachingTeamService`/`CachingCampService`); Shifts and Events still run a case-insensitive Postgres `ILike` at the DB layer through their own repositories. Adding a Search-level cache on top would duplicate invalidation the owning sections already do correctly.
- **Cross-domain navs** — none; the section owns no entities.
- **Cross-section calls** — `IUserServiceRead`, `ITeamServiceRead`, `ICampServiceRead`, `IShiftManagementService`, `IEventServiceRead` (see Cross-Section Dependencies).
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/SearchArchitectureTests.cs` pins `ISearchService` to `IOrchestrator` (and off `IApplicationService`) and asserts `SearchService`'s constructor injects no repository. Behavioral coverage (query-length gate, `onlyType` short-circuit, score tiers, the `Features:Events` gate, GUID-vs-text resolution) lives in `tests/Humans.Application.Tests/Services/Search/SearchServiceTests.cs` and `tests/Humans.Web.Tests/Controllers/SearchControllerTests.cs`.

See [`docs/features/global/global-search.md`](../features/global/global-search.md) for the full user-facing workflow and DTO reference; this doc states the invariants a PR is checked against, not the feature narrative.

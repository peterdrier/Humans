<!-- freshness:triggers
  src/Sections/Humans.Search/**
  src/Sections/Humans.Events/Services/CachingEventService.cs
  src/Sections/Humans.Events/Section.cs
  src/Sections/Humans.Users/Data/CachingUserService.cs
  src/Sections/Humans.Teams/Services/CachingTeamService.cs
  src/Sections/Humans.Camps/Services/CachingCampService.cs
  src/Sections/Humans.Shifts/Services/ShiftManagementService.cs
  src/Sections/Humans.Shifts/Data/ShiftRepository.Management.cs
  src/Sections/Humans.Shifts.Contracts/IShiftManagementServiceRead.cs
  src/Sections/Humans.Users/Services/PersonSearchMatcher.cs
  src/Sections/Humans.Camps/Controllers/CampController.cs
  src/Sections/Humans.Teams/Controllers/TeamController.cs
  src/Sections/Humans.Shifts/Controllers/ShiftsController.cs
  src/Sections/Humans.Shifts/Models/ShiftBrowsePageBuilder.cs
  src/Sections/Humans.Teams/ViewComponents/TeamsSearchResultViewComponent.cs
  src/Sections/Humans.Camps/ViewComponents/CampsSearchResultViewComponent.cs
  src/Sections/Humans.Shifts/ViewComponents/ShiftsSearchResultViewComponent.cs
  src/Sections/Humans.Events/ViewComponents/EventsSearchResultViewComponent.cs
-->
<!-- freshness:flag-on-change
  The GUID-vs-text visibility split, the five-section fan-out list, and the Search-vs-HumanSearchViewComponent naming boundary — review when SearchService's dependencies change or when a destination page's own visibility gate changes.

  The trigger list reaches outside the section because Search owns no logic of its own: every
  invariant below is implemented in someone else's file. The per-bucket visibility filters and GUID
  branches live in the four Caching*Service classes and ShiftManagementService (whose text branch
  delegates the active-event, IsVisibleToVolunteers and ILike predicates to
  ShiftRepository.Management.cs). The Humans score tiers live in PersonSearchMatcher. The
  destination-page guarantee lives in CampController, TeamController and — because a rota hit links
  to /Shifts?departmentId=, not to the rota — ShiftsController plus ShiftBrowsePageBuilder. Events'
  Section.cs is listed because "four buckets never touch the DB" rests on IEventServiceRead being
  bound to CachingEventService.
-->

# Search — Section Invariants

Orchestrator behind the global `/Search` page. Fans out to five sections' read surfaces, owns no tables.

## Concepts

- The **Search** section is the global `/Search` page and its orchestrator, `SearchService`. It matches each entity's own public fields (name, plus a public bio bucket for humans) and returns five independently-scored, unsorted buckets — Humans, Teams, Camps, Shifts, Events. No cross-modal traversal (a person hit never pulls in their teams), no cross-type ranking.
- A **query** shorter than 2 characters after trim returns all five buckets empty — not an error, not the full dataset.
- **`onlyType`** short-circuits the fan-out: `SearchService` calls only the matching section. Backs the per-type filter chips.
- **Naming trap:** `/Search` (this section) is unrelated to `HumanSearchViewComponent` (`<vc:human-search>`, the inline person picker), which is Users-owned and calls `IUserServiceRead.SearchUsersAsync` directly from in-form pickers across several sections. See [`person-search`](../../../../memory/architecture/person-search.md). Only the `/Search` page and `SearchService` belong here.

## Data Model

None — Search owns no tables. It is a read-only fan-out over five other sections' service interfaces (see Cross-Section Dependencies).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | Full `/Search`: a text query returns the public-visibility surface across all five types; a GUID query resolves a searchable entity past those filters (see Invariants). No active profile required — the nav item and the page gate on authentication only. |
| Admin / any admin role | Same surface — Search has no privileged mode (nobodies-collective/Humans#693) |
| Anonymous | Cannot reach `/Search` — `[Authorize]` redirects to sign-in |

## Invariants

- A query shorter than 2 characters after trim short-circuits to an empty `GlobalSearchResults` — no section is called.
- `onlyType` skips the other four sections entirely — not called, rather than called and discarded.
- **Every hit arrives pre-scored by its owning section; `SearchService` never rescores.** Teams/Camps/Shifts/Events score against `StringSearchExtensions.NameMatchScore` in `Humans.Base` (exact 100, prefix 80, contains 60; Events halves the tiers for a Description-only match). Humans arrive from `PersonSearchMatcher`, which adds a whole-name-prefix tier (85) and a non-name public-field tier (40) — so a Humans score of 85 or 40 is correct, not a defect. A GUID hit scores 100 in every bucket.
- **No result cap.** Every match is returned; the orchestrator passes an effectively unbounded `max` to each section. Capping was tried and hid people.
- **Display order is the controller's, never the service's** ([`display-sort-in-controllers`](../../../../memory/architecture/display-sort-in-controllers.md)): non-human buckets by score desc then `SortKey` asc (case-insensitive); humans by the shared `OrderByRelevance()`.
- **Text queries are visibility-filtered per section, role-blind.** Hidden teams, non-public camp seasons (outside `Active`/`Full`), admin-only rotas and admin-only profile fields are excluded for every viewer. The filters are constants inside each section's own search call (`CachingTeamService`, `CachingCampService`, `ShiftManagementService`, `IUserServiceRead.SearchUsersAsync(..., PersonSearchFields.PublicAll, ...)`) — not a role check `SearchService` performs.
- **GUID queries are NOT visibility-filtered — a routing convenience, not an authorization statement.** Pasting an entity's own id (Team, Camp, Rota, Human) resolves it past the text-query filters, on the reasoning that a caller can only use an id they already hold. Search may return a hit the caller cannot open; enforcement is the destination page's job (ruling on nobodies-collective/Humans#985). **Do not re-add a visibility check on the GUID path.**
- The Humans bucket has no visibility filter for the GUID path to bypass, but resolution is not unconditional: `CachingUserService.SearchUsersAsync` returns an id hit only for a human with a non-rejected `Profile`, the same eligibility gate the text loop applies per candidate. The GUID path skips only the `PersonSearchFields.PublicAll` field mask.
- **The destination page's own scoping wins, including when it shows less than the GUID resolved.** Both known cases are settled behavior, closed as designed: `/Camps/{slug}` renders a non-public season's detail page to any viewer (nobodies-collective/Humans#993), and a rota GUID from a past event resolves to a `/Shifts?departmentId=` link that lists only the current event's rotas (nobodies-collective/Humans#998). Neither is a Search-layer change.
- `Features:Events` gates the Events bucket end to end — the fan-out call (service), the filter chip and the results heading (view) are all skipped when the flag is off.
- A service exception renders the page shell with the query preserved, never a 500; a cancelled request propagates.

## Negative Access Rules

- Anonymous visitors **cannot** reach `/Search` — `[Authorize]` redirects to sign-in.
- No viewer, Admin included, **can** surface admin-only profile fields, a hidden team, a non-public camp season or an admin-only rota through a **text** query — the exclusion is a constant mask/filter, so there is no privileged variant to grant.
- `SearchService` **cannot** query any section's tables — every bucket comes from that section's public read interface. Enforced by analyzers HUM0026/HUM0027 (see Architecture).
- A hit for an entity the viewer is not entitled to open is **not** a Search-layer privacy bug — refusal belongs to the destination page.

## Triggers

None — this section is a pure read surface with no side effects: no writes, no audit entries, no notifications, no cache of its own.

## Cross-Section Dependencies

- **Users**: `IUserServiceRead.SearchUsersAsync(query, PersonSearchFields.PublicAll, limit)` — Humans bucket.
- **Teams**: `ITeamServiceRead.SearchAsync` — Teams bucket, `Team.Name` only, non-hidden teams.
- **Camps**: `ICampServiceRead.SearchAsync` — Camps bucket, public-year `CampSeason.Name` only, `Active`/`Full` seasons.
- **Shifts**: `IShiftManagementServiceRead.SearchAsync` — Shifts bucket (rota names), `IsVisibleToVolunteers` rotas of the active event.
- **Events**: `IEventServiceRead.SearchAsync` — Events bucket, approved events by Title or Description; gated by `Features:Events`.
- **Configuration**: `IConfiguration` — the `Features:Events` flag, read by both the service and `Index.cshtml`.
- **Project references**: all five publishers whole, not just their `.Contracts` leaves — `@addTagHelper` binds only against a referenced assembly, and every result row is the owning section's view component. Only `Contracts/` surface and the components the framework needs public are reachable through them (HUM0034).

## Architecture

**Owning services:** `SearchService` (orchestrator)
**Owned tables:** None — orchestrator over `IUserServiceRead`, `ITeamServiceRead`, `ICampServiceRead`, `IShiftManagementServiceRead`, `IEventServiceRead`.
**Status:** (A) Migrated — own project at `src/Sections/Humans.Search`; `ISearchService : IOrchestrator` ([`orchestrator-marker`](../../../../memory/architecture/orchestrator-marker.md)).

### Cross-section read interface

| Read interface | Methods | Notes |
|---|---:|---|
| — | — | Nothing outside the section names a Search type |

- **Public surface: none.** `Contracts/` is empty (see its `README.md`). `ISearchService`, `GlobalSearchResults`, `GlobalSearchResult`, `SearchResultType`, `GlobalSearchViewModel` and `SearchController` are `internal`; the assembly exports only `Section` and `SearchResource`. The member-nav item is the section's own `SectionNav : ISectionNav`, visible to any authenticated user (no `AppAccess` policy), so Shell references nothing here.
- **No repository, no `DbContext`, no `Data/` folder.** The `IOrchestrator` marker makes that enforceable: HUM0026 (an orchestrator may not inject a repository or `DbContext`) and HUM0027 (`IOrchestrator` xor `IApplicationService`) fire as errors. `ISearchService` is `internal` and exists for the marker and for `SearchControllerTests` to substitute it.
- **Decorator decision — no caching decorator.** Humans, Teams, Camps and Events are served from their owning sections' warm in-memory snapshots (`CachingUserService`, `CachingTeamService`, `CachingCampService`, `CachingEventService` — the last bound to `IEventServiceRead` in Events' `Section.cs`). **Shifts is the only DB-backed bucket:** `ShiftManagementService.SearchAsync` runs a case-insensitive Postgres `ILike` for text and a rota lookup for a GUID. A Search-level cache would cache a cache and duplicate invalidation the owners already do.
- **Resource set — split by renderer.** `SearchResource` owns the `Search_Filter*` and `Search_Global*` keys: all of `/Search`'s own copy. Keys with the `Search_` prefix whose renderer is elsewhere stay in `SharedResource`: `Search_Title`/`Search_Placeholder` belong to `/Profile/Search`, `Search_NoResults`/`Search_MatchedIn` to `Humans.Users`, and `Search_MinChars` is rendered by `/Profile/Search` **and** by this section's `Index.cshtml` — so the section binds `SharedLocalizer` for that one call rather than splitting the person-search page's message set. A prefix is not an owner.
- **Rendering is the owner's.** Every row is the owning section's own view component, invoked with the key this section holds: `<vc:user-search-result>` for humans (id plus the match context Users produced), and `_GlobalSearchSection` switching on `GlobalSearchResult.Type` to `<vc:teams-search-result>`, `<vc:camps-search-result>`, `<vc:shifts-search-result>`, `<vc:events-search-result>`. `GlobalSearchResult` is `(Type, Key, SortKey, Score)` with a `Guid` key and no display fields; `SortKey` is sorted on and never rendered. `Views/_ViewImports.cshtml` carries one `@addTagHelper` per publisher — drop one and that bucket ships as inert literal markup on a green build; `ViewComponentTagHelperBindingTests` (static) and `GlobalSearchSectionRenderTests` (rendered) guard it.
- **Cross-domain navs** — none; the section owns no entities.
- **Cross-section calls** — the five read interfaces above.
- **Test map.** Orchestration (`tests/Humans.Search.Tests/Services/SearchServiceTests.cs`): the length gate and its boundary, trimming, `onlyType`, pass-through of each section's score/key/sort-key, the GUID path, the `PublicAll` mask on both paths, the unbounded cap, `Features:Events` off. Controller (`tests/Humans.Search.Tests/Controllers/SearchControllerTests.cs`): both sort orders, view-model assembly, the shell-not-500 path, cancellation. Structure (`tests/Humans.Search.Tests/Architecture/SearchArchitectureTests.cs`): the `IOrchestrator` marker. Per-section filters and scoring are pinned where they live: `CachingUserServiceTests` and `PersonSearchMatcherTests` (`tests/Humans.Users.Tests`), `CachingTeamServiceTests`, `CachingCampServiceTests`, `CachingEventServiceTests`, and for Shifts `ShiftManagementServiceTests` plus the local-only `ShiftRepositoryRotaSearchTests`. Rendering (local-only `Humans.Integration.Tests`): `SearchPageRenderTests` and `GlobalSearchSectionRenderTests`.

See [`docs/features/global/global-search.md`](../../../../docs/features/global/global-search.md) for the user-facing workflow and DTO reference; this doc states the invariants a PR is checked against.

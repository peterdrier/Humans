<!-- freshness:triggers
  src/Sections/Humans.Search/**
  src/Humans.Base/Interfaces/ISearchContributor.cs
  src/Sections/Humans.Users/Section.cs
  src/Sections/Humans.Teams/Section.cs
  src/Sections/Humans.Camps/Section.cs
  src/Sections/Humans.Shifts/Section.cs
  src/Sections/Humans.Events/Services/CachingEventService.cs
  src/Sections/Humans.Events/Section.cs
  src/Sections/Humans.Users/Data/CachingUserService.cs
  src/Sections/Humans.Teams/Services/CachingTeamService.cs
  src/Sections/Humans.Camps/Services/CachingCampService.cs
  src/Sections/Humans.Shifts/Services/ShiftManagementService.cs
  src/Sections/Humans.Shifts/Data/ShiftRepository.Management.cs
  src/Sections/Humans.Shifts/Contracts/IShiftService.cs
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
  The GUID-vs-text visibility split, the contributed-category seam, and the Search-vs-HumanSearchViewComponent naming boundary — review when a contributor's category changes or when a destination page's own visibility gate changes.

  The trigger list reaches outside the section because Search owns no logic of its own: every
  invariant below is implemented in someone else's file. Each category is its section's ISearchContributor
  in that section's Section.cs (the field mask, the unbounded cap and the Events flag live there). The per-bucket visibility filters and GUID
  branches live in the four Caching*Service classes and ShiftManagementService (whose text branch
  delegates the active-event, IsVisibleToVolunteers and ILike predicates to
  ShiftRepository.Management.cs). The Humans score tiers live in PersonSearchMatcher. The
  destination-page guarantee lives in CampController, TeamController and — because a rota hit links
  to /Shifts?departmentId=, not to the rota — ShiftsController plus ShiftBrowsePageBuilder. Events'
  Section.cs is listed because "four buckets never touch the DB" rests on IEventService being
  bound to CachingEventService.
-->

# Search — Section Invariants

Orchestrator behind the global `/Search` page. Fans out to every section's contributed `ISearchContributor` categories, owns no tables, names no section.

## Concepts

- The **Search** section is the global `/Search` page and its orchestrator, `SearchService`. It collects every enabled **search category** (Base's `SearchCategory`, contributed by a section's `ISearchContributor`) and returns one independently-scored, unsorted bucket per category — Humans, Teams, Camps, Shifts, Events today. Each category matches its own entity's public fields (name, plus a public bio bucket for humans). No cross-modal traversal (a person hit never pulls in their teams), no cross-type ranking.
- A **query** shorter than 2 characters after trim returns every category with an empty bucket — not an error, not the full dataset.
- **`filter`** (a category key, case-insensitive) short-circuits the fan-out: `SearchService` searches only the matching category. Backs the per-type filter chips; an unknown key searches everything.
- **Naming trap:** `/Search` (this section) is unrelated to `HumanSearchViewComponent` (`<vc:human-search>`, the inline person picker), which is Users-owned and calls `IUserService.SearchUsersAsync` directly from in-form pickers across several sections. See [`person-search`](../../../../memory/architecture/person-search.md). Only the `/Search` page and `SearchService` belong here.

## Data Model

None — Search owns no tables. It is a read-only fan-out over the contributed categories (see Cross-Section Dependencies).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | Full `/Search`: a text query returns the public-visibility surface across every enabled category; a GUID query resolves a searchable entity past those filters (see Invariants). No active profile required — the nav item and the page gate on authentication only. |
| Admin / any admin role | Same surface — Search has no privileged mode (nobodies-collective/Humans#693) |
| Anonymous | Cannot reach `/Search` — `[Authorize]` redirects to sign-in |

## Invariants

- A query shorter than 2 characters after trim lists every enabled category with an empty bucket — no category's search is called (labels still resolve, for the hint).
- `filter` skips every other category entirely — not called, rather than called and discarded.
- Categories render in ascending `Weight` (Humans 0, Teams 10, Camps 20, Shifts 30, Events 40), then key.
- **Every hit arrives pre-scored by its owning section; `SearchService` never rescores.** Teams/Camps/Shifts/Events score against `StringSearchExtensions.NameMatchScore` in `Humans.Base` (exact 100, prefix 80, contains 60; Events halves the tiers for a Description-only match). Humans arrive from `PersonSearchMatcher`, which adds a whole-name-prefix tier (85) and a non-name public-field tier (40) — so a Humans score of 85 or 40 is correct, not a defect. A GUID hit scores 100 in every bucket.
- Human-result match badges use Users’ localized built-in field labels in the current UI culture. Custom contact labels remain verbatim.
- **No result cap.** Every match is returned; each contributor passes `int.MaxValue` to its section's search. Capping was tried and hid people.
- **Display order is the controller's, never the service's** ([`display-sort-in-controllers`](../../../../memory/architecture/display-sort-in-controllers.md)): categories by weight then ordinal key, and hits within every bucket by score desc then `SortKey` asc (case-insensitive). The service returns categories unordered with their contributed weights. Humans' sort key is the burner name, so that is the person-search `OrderByRelevance()` order.
- **Text queries are visibility-filtered per section, role-blind.** Hidden teams, non-public camp seasons (outside `Active`/`Full`), admin-only rotas and admin-only profile fields are excluded for every viewer. The filters are constants inside each section's own search call (`CachingTeamService`, `CachingCampService`, `ShiftManagementService`, and Users' contributor calling `IUserService.SearchUsersAsync(..., PersonSearchFields.PublicAll, ...)`) — not a role check `SearchService` performs.
- **GUID queries are NOT visibility-filtered — a routing convenience, not an authorization statement.** Pasting an entity's own id (Team, Camp, Rota, Human) resolves it past the text-query filters, on the reasoning that a caller can only use an id they already hold. Search may return a hit the caller cannot open; enforcement is the destination page's job (ruling on nobodies-collective/Humans#985). **Do not re-add a visibility check on the GUID path.**
- The Humans bucket has no visibility filter for the GUID path to bypass, but resolution is not unconditional: `CachingUserService.SearchUsersAsync` returns an id hit only for a human with a non-rejected `Profile`, the same eligibility gate the text loop applies per candidate. The GUID path skips only the `PersonSearchFields.PublicAll` field mask.
- **The destination page's own scoping wins, including when it shows less than the GUID resolved.** Both known cases are settled behavior, closed as designed: `/Camps/{slug}` renders a non-public season's detail page to any viewer (nobodies-collective/Humans#993), and a rota GUID from a past event resolves to a `/Shifts?departmentId=` link that lists only the current event's rotas (nobodies-collective/Humans#998). Neither is a Search-layer change.
- `Features:Events` gates the Events category end to end through its `IsEnabled`: with the flag off it is not listed, so the search call, the filter chip, the hint label and the results heading are all absent.
- A service exception, including dependency cancellation while the request is live, is logged at Error and renders the form with the query and filter preserved plus localized unavailable feedback. It never displays zero-count result chips or the no-matches message for a failed search. Cancellation of the request token propagates and is logged at Warning without an exception stack.

## Negative Access Rules

- Anonymous visitors **cannot** reach `/Search` — `[Authorize]` redirects to sign-in.
- No viewer, Admin included, **can** surface admin-only profile fields, a hidden team, a non-public camp season or an admin-only rota through a **text** query — the exclusion is a constant mask/filter, so there is no privileged variant to grant.
- `SearchService` **cannot** query any section's tables — every bucket comes from the owning section's contributed category, which calls its own public read interface. Search references only `Humans.Base`. Enforced by analyzers HUM0026/HUM0027 (see Architecture).
- A hit for an entity the viewer is not entitled to open is **not** a Search-layer privacy bug — refusal belongs to the destination page.

## Triggers

None — this section is a pure read surface with no side effects: no writes, no audit entries, no notifications, no cache of its own.

## Cross-Section Dependencies

Search names no section. Each category is an `ISearchContributor` on its owner's `Section.cs`:

- **Users** (`Human`): `IUserService.SearchUsersAsync(query, PersonSearchFields.PublicAll, int.MaxValue)`; rows by `UserSearchResultViewComponent` with the match context.
- **Teams** (`Team`): `ITeamService.SearchAsync` — `Team.Name` only, non-hidden teams.
- **Camps** (`Camp`): `ICampService.SearchAsync` — public-year `CampSeason.Name` only, `Active`/`Full` seasons.
- **Shifts** (`Shift`): `IShiftService.SearchAsync` — rota names, `IsVisibleToVolunteers` rotas of the active event.
- **Events** (`Event`): `IEventService.SearchAsync` — approved events by Title or Description; enabled by `Features:Events`.
- **Project references**: `Humans.Base` only. Rows are invoked by component `Type` through `Component.InvokeAsync`, so no publisher's `@addTagHelper` or assembly is needed.

## Architecture

**Owning services:** `SearchService` (orchestrator)
**Owned tables:** None — orchestrator over `IEnumerable<ISearchContributor>`.
**Status:** (A) Migrated — own project at `src/Sections/Humans.Search`; `ISearchService : IOrchestrator` ([`orchestrator-marker`](../../../../memory/architecture/orchestrator-marker.md)).

### Cross-section read interface

| Read interface | Methods | Notes |
|---|---:|---|
| — | — | Nothing outside the section names a Search type |

- **Public surface: none.** `Contracts/` is empty (see its `README.md`). `ISearchService`, `GlobalSearchResults`, `SearchBucket`, `GlobalSearchViewModel` and `SearchController` are `internal`; the assembly exports only `Section` and `SearchResource`. The member-nav item is `Section`'s own `ISectionNav` implementation, visible to any authenticated user (no `AppAccess` policy), so Shell references nothing here.
- **No repository, no `DbContext`, no `Data/` folder.** The `IOrchestrator` marker makes that enforceable: HUM0026 (an orchestrator may not inject a repository or `DbContext`) and HUM0027 (`IOrchestrator` xor `IApplicationService`) fire as errors. `ISearchService` is `internal` and exists for the marker and for `SearchControllerTests` to substitute it.
- **The seam.** `ISearchContributor` (Base) returns `SearchCategory` records: a stable `Key` (the `filter` value), a `Label` the contributor localizes from its own resources (`<Section>_Search_Category`), an icon, the row `ResultComponent` type, a `SearchAsync` delegate, a `Weight` and an optional `IsEnabled`. `[ViewComponentSlot(typeof(SearchHit))]` makes `ViewComponentSlotContractTests` check every row component binds from a `SearchHit` (`Guid key`, optional `matchField`/`matchSnippet`). `SearchService` takes `IServiceProvider` only to hand it to those delegates, as the things-to-do and admin-tile hosts do.
- **Decorator decision — no caching decorator.** Humans, Teams, Camps and Events are served from their owning sections' warm in-memory snapshots (`CachingUserService`, `CachingTeamService`, `CachingCampService`, `CachingEventService` — the last bound to `IEventService` in Events' `Section.cs`). **Shifts is the only DB-backed bucket:** `ShiftManagementService.SearchAsync` runs a case-insensitive Postgres `ILike` for text and a rota lookup for a GUID. A Search-level cache would cache a cache and duplicate invalidation the owners already do.
- **Resource set — split by renderer.** `SearchResource` owns every key that only `/Search` renders (its title, hint, placeholder, the All chip, aria-labels and empty state); category labels are the contributors' own. `Search_GlobalHint` takes the joined labels as `{0}`. Keys with the `Search_` prefix whose renderer is elsewhere stay in `SharedResource`: `Search_Title` is a Camps placeholder and `Search_MatchedIn` belongs to `Humans.Users`' result row. A prefix is not an owner.
- **Rendering is the owner's.** Every row is the owning section's own view component, invoked by the `ResultComponent` type its category declares with the `SearchHit` as arguments. `SearchHit` is `(Key, SortKey, Score, MatchField?, MatchSnippet?)` with a `Guid` key and no display fields; `SortKey` is sorted on and never rendered. `GlobalSearchSectionRenderTests` and `SearchPageRenderTests` (rendered) guard the rows.
- **Cross-domain navs** — none; the section owns no entities.
- **Cross-section calls** — none of its own; the contributed delegates call their sections' read interfaces.
- **Test map.** Orchestration (`tests/Humans.Search.Tests/Services/SearchServiceTests.cs`): the length gate and its boundary, trimming, `filter` (case-insensitive, unknown, disabled), weight order, enablement, pass-through of each category's hits and label. Contributors (`tests/Humans.<Section>.Tests/Contributions/SearchContributorTests.cs` in Users, Teams, Camps, Shifts, Events): the `PublicAll` mask on text and GUID queries, the unbounded cap, the key/sort-key/score/match-context mapping, the `Features:Events` gate. Controller (`tests/Humans.Search.Tests/Controllers/SearchControllerTests.cs`): the sort order, view-model assembly, the shell-not-500 path, cancellation. Structure (`tests/Humans.Search.Tests/Architecture/SearchArchitectureTests.cs`): the `IOrchestrator` marker. Per-section filters and scoring are pinned where they live: `CachingUserServiceTests` and `PersonSearchMatcherTests` (`tests/Humans.Users.Tests`), `CachingTeamServiceTests`, `CachingCampServiceTests`, `CachingEventServiceTests`, and for Shifts `ShiftManagementServiceTests` plus `ShiftRepositoryRotaSearchTests` in `Humans.Integration.Tests`. Rendering (`Humans.Integration.Tests`, a PR check): `SearchPageRenderTests` and `GlobalSearchSectionRenderTests`.

See [`docs/features/global/global-search.md`](../../../../docs/features/global/global-search.md) for the user-facing workflow and DTO reference; this doc states the invariants a PR is checked against.

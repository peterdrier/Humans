<!-- freshness:triggers
  src/Sections/Humans.Search/**
  src/Sections/Humans.Events/Services/CachingEventService.cs
  src/Sections/Humans.Events/Section.cs
  src/Sections/Humans.Users/Data/CachingUserService.cs
  src/Sections/Humans.Teams/Services/CachingTeamService.cs
  src/Sections/Humans.Camps/Services/CachingCampService.cs
  src/Sections/Humans.Shifts/Services/ShiftManagementService.cs
  src/Sections/Humans.Shifts/Data/ShiftRepository.Management.cs
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

  Why the trigger list reaches outside src/**/Search/**: this section owns no logic of its own. Every
  invariant it states is implemented in someone else's file — the per-bucket visibility filters and
  GUID branches live in the four Caching*Service classes and ShiftManagementService, whose text branch
  in turn only delegates: the active-event, IsVisibleToVolunteers and ILike predicates behind the
  "Shifts is the only DB-backed bucket" claim are in ShiftRepository.Management.cs, not the service.
  The Humans score tiers live in PersonSearchMatcher, and the ruling on nobodies-collective/Humans#985 moved the whole
  privacy guarantee onto the destinations: CampController, TeamController, and — because a rota hit
  links to /Shifts?departmentId=, not to the rota itself — ShiftsController plus
  ShiftBrowsePageBuilder, where the IncludeAdminOnly/IncludeHidden flags actually decide it.
  Humans.Events' Section.cs is in the list for a different reason: the "four buckets never touch the
  DB" claim rests on IEventServiceRead being bound to CachingEventService (a one-line DI
  registration), so a lifetime change or a rebind to EventService falsifies it without editing any
  service. A change to any of these can falsify this doc without touching a single file under Search.
-->

# Search — Section Invariants

Orchestrator behind the global `/Search` page. Fans out to five sections' read surfaces, scores each independently, owns no tables.

## Concepts

- The **Search** section is the global `/Search` page and its backing orchestrator, `SearchService`. It matches each entity's own public fields (name, plus a public bio bucket for humans) and returns five independently-ranked, unsorted buckets — Humans, Teams, Camps, Shifts, Events. There is no cross-modal traversal (a person hit never pulls in their teams) and no cross-type ranking.
- A **query** shorter than 2 characters after trim returns all five buckets empty — not an error, not the full dataset.
- **`onlyType`** short-circuits the fan-out: when set, `SearchService` calls only the one matching section instead of all five and discarding four. Backs the per-type filter chips.
- **The naming trap this doc exists to record:** `/Search` (this section, `SearchService`/`SearchController`) is unrelated to `HumanSearchViewComponent` (the `<vc:human-search>` inline person-picker). Both share the word "search," but `HumanSearchViewComponent` is **Users-owned** — it calls `IUserServiceRead.SearchUsersAsync` directly for in-form pickers and is correctly excluded from this section's `reforge.surface-score.json` paths. Its reach is wider than the three examples usually cited: seven production views use `<vc:human-search>` today — `Camp/Members`, `Gate/Admin`, `Gate/Claim`, `Google/Accounts`, `TeamAdmin/EarlyEntry`, `TeamAdmin/Members`, `TicketTransfer/Index` — plus `WidgetGallery/Index` as a demo. A change to the picker has a blast radius across four sections, none of them this one. See [`person-search`](../../../../memory/architecture/person-search.md). A reader going only from the shared name would reasonably assume both belong here — they don't; only the `/Search` page and `SearchService` do.

## Data Model

None — Search owns no tables. It is a pure read/fan-out orchestrator over five other sections' service interfaces (see Cross-Section Dependencies).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | Full `/Search`: a text query returns the public-visibility surface across all five types; a GUID query resolves a searchable entity past those visibility filters (see Invariants) |
| Admin / any admin role | Same surface as any authenticated human — Search has no privileged/admin search mode (tracked at nobodies-collective/Humans#693) |
| Anonymous | Cannot reach `/Search` — `[Authorize]` redirects to sign-in |

## Invariants

- A query shorter than 2 characters after trim short-circuits to an empty `GlobalSearchResults` — no call to any of the five sections.
- `onlyType` skips the fan-out to the other four sections entirely, not just their display.
- Each bucket is scored independently, but **the rubric is not shared — Humans use a finer one.** Teams/Camps/Shifts score themselves against `StringSearchExtensions.NameMatchScore` in `Humans.Base` (exact = 100, prefix = 80, contains = 60) and the orchestrator carries the number through untouched; Events is the one bucket `SearchService` still scores, because it publishes no scored hit type (nobodies-collective/Humans#1062). Humans arrive **pre-scored** from `PersonSearchMatcher` (`PersonSearchMatcher.cs:27-31`), which adds two tiers the orchestrator has no concept of — exact name = 100, whole-name prefix = **85**, token prefix = 80, name contains = 60, and any non-name public field (bio/city/contact) = **40**. A GUID hit scores 100 in every bucket. So do not check the Humans bucket against 100/80/60: a whole-name-prefix hit at 85 and a bio hit at 40 are both correct. There is no cross-type ranking and no result cap — every match returns (capping was tried and reverted: at ~500-user scale it hid people users were looking for). Display ordering (score desc, then sort-key/burner-name asc) is a `SearchController` concern, never pushed into `SearchService` ([`display-sort-in-controllers`](../../../../memory/architecture/display-sort-in-controllers.md)).
- **Text queries are visibility-filtered per section, role-blind.** Hidden teams, non-public camp seasons (outside `Active`/`Full`), admin-only rotas, and admin-only profile fields are excluded for every viewer — there is no admin-privileged text-search bucket. The filters are constants applied inside each section's own search call (`CachingTeamService`, `CachingCampService`, `ShiftManagementService`, `IUserServiceRead.SearchUsersAsync(..., PersonSearchFields.PublicAll, ...)`), not a role check `SearchService` performs itself.
- **GUID queries are NOT visibility-filtered — this is a routing convenience, not an authorization statement.** Pasting a searchable entity's own id (Team, Camp, Shift/Rota, Human) resolves it directly, bypassing the text-query visibility filters, on the reasoning that a caller can only use an id they already hold. Search may therefore return a hit the caller cannot open; enforcement is entirely the destination page's job (Peter's ruling on nobodies-collective/Humans#985, 2026-08-07). **Do not re-add a visibility check on the GUID path** — that was the pre-ruling behavior and was deliberately removed.
- There are no hidden users, so the Humans bucket has no visibility filter for the GUID path to bypass — but it is not unconditional resolution either. `CachingUserService.SearchUsersAsync` returns an id hit only when the human has a `Profile` and `Profile.RejectedAt` is null (`CachingUserService.cs:132-150`), the same eligibility gate the text loop applies per candidate; a profile-less or rejected human returns an empty bucket from both paths. What the GUID path does skip is the `PersonSearchFields.PublicAll` mask, which only governs which fields *text* matching compares.
- **The destination page's own scoping wins, including when it shows less than the GUID resolved.** Both known cases were ruled as designed on 2026-08-07 and are settled behavior, not tracked gaps:
  - `CampController.Details`/`SeasonDetails` have no season-status gate, so a non-public season's detail page renders for any viewer, anonymous included (nobodies-collective/Humans#993, closed as designed). The Search layer is not what needs to change.
  - Worked example — a rota GUID from a *previous* event resolves and links to `/Shifts?departmentId=`, which lists the **current** event's rotas and so does not contain it (nobodies-collective/Humans#998, closed as designed). The id did its routing job; the destination is scoped more narrowly than the search, and that scoping wins.
- `Features:Events` gates the Events bucket end to end — the fan-out call, the filter chip, and the results section are all skipped together when the flag is off.

## Negative Access Rules

- Anonymous visitors **cannot** reach `/Search` — `[Authorize]` redirects to sign-in.
- No viewer, Admin included, **can** get admin-only profile fields, a hidden team, a non-public camp season, or an admin-only rota to surface via a **text** query — the exclusion is structural (a constant mask/filter), not a per-role branch, so there is no privileged variant to grant.
- `SearchService` **cannot** query any section's tables directly — every bucket comes from that section's public service interface, never a repository or `HumansDbContext`. Enforced since #1197 by analyzers HUM0026/HUM0027 (both errors) — see Architecture.
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
- **Project references**: all five publishers whole, not just their `.Contracts` leaves — `@addTagHelper` binds only against a referenced assembly, and every result row is now the owning section's view component. Only `Contracts/` surface and the components the framework needs public are reachable through them (HUM0034).

## Architecture

**Owning services:** `SearchService` (orchestrator)
**Owned tables:** None — orchestrator over `IUserServiceRead`, `ITeamServiceRead`, `ICampServiceRead`, `IShiftManagementService`, `IEventServiceRead`.
**Status:** (A) Migrated, and since nobodies-collective/Humans#866 (G5) the section is its own project at `src/Sections/Humans.Search`. Orchestrator in shape from inception, and the marker says so: `ISearchService : IOrchestrator` (`Services/ISearchService.cs`), corrected from `IApplicationService` in nobodies-collective/Humans#987 (PR peterdrier/Humans#1197, merged 2026-08-07). Roster and rationale: [`orchestrator-marker`](../../../../memory/architecture/orchestrator-marker.md).

**Public surface: none.** `Contracts/` is empty (see its `README.md`) — after the move nothing outside the section names a Search type, so `ISearchService`, `GlobalSearchResults`, `GlobalSearchResult`, `SearchResultType`, `GlobalSearchViewModel` and `SearchController` are all `internal`, and the assembly exports only `Section` and `SearchResource`. `_Layout` reaches `/Search` by controller *name* through the route table, which needs no reference.

- `SearchService` lives in `Humans.Search.Services` and the section project has no `Data/` folder, no `DbContext` and no repository reference at all — Scanner's and Cantina's table-less shape. No type in the section takes a `DbContext`, an `IDbContextFactory<>`, a repository or a `Stores` type — the orchestrator claim stated over the whole section rather than over one constructor. [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md): this is documentation, not a pinned assertion.
- **No repository.** `SearchService` injects no `I*Repository` and no `HumansDbContext`, and since #1197 the marker makes that enforceable: HUM0026 (an `IOrchestrator` may not inject a repository/`DbContext`) and HUM0027 (`IOrchestrator` xor `IApplicationService`) both fire on it, as errors. Note what they still do **not** catch — per [`orchestrator-marker`](../../../../memory/architecture/orchestrator-marker.md), neither rule inspects an `IApplicationService` implementer's shape, so nothing at build time would have flagged the pre-#1197 state. That mismatch was found by hand audit, and the next one will be too.
- **Decorator decision — no caching decorator on `SearchService` itself.** Four of the five buckets are already served from their owning section's warm in-memory snapshot, with no DB round trip per search: Humans/Teams/Camps via `CachingUserService`/`CachingTeamService`/`CachingCampService`, and **Events too** — `IEventServiceRead` is registered as the `CachingEventService` singleton (`Humans.Events`' `Section.cs`), whose `GetApprovedEventsAsync` filters the approved-event cache in memory (`Contains(…, OrdinalIgnoreCase)`), not in SQL. **Shifts is the only DB-backed bucket:** `ShiftManagementService.SearchAsync` calls `repo.SearchVolunteerVisibleRotasAsync` (case-insensitive Postgres `ILike`) for text and `repo.GetRotaAsync` for a GUID. So a Search-level cache would, for four of five buckets, cache a cache — and duplicate invalidation the owning sections already do correctly.
- **Resource set — 12 keys of 17, and the split is by renderer.** `SearchResource` owns the six `Search_Filter*` and six `Search_Global*` keys, i.e. all of `/Search`'s own copy. Five keys keep the `Search_` prefix and stayed in `SharedResource` because their renderers are elsewhere: `Search_Title` / `Search_Placeholder` belong to Shell's `/Profile/Search` person-search page, `Search_NoResults` / `Search_MatchedIn` to `Humans.Users` (that page's empty state and the `<vc:user-search-result>` row), and `Search_MinChars` is read by `/Profile/Search` **and** by this section's `Index.cshtml` — so the section binds `SharedLocalizer` for that one call site rather than splitting the person-search page's four-key message set. A prefix is not an owner.
- **Presentation of a human is not this section's — and since nobodies-collective/Humans#1062 it does not name it either.** The Humans bucket renders one `<vc:user-search-result>` per hit: `Humans.Users` owns the component and the markup, this section passes the id plus the match context Users' own `HumanSearchResult` produced. `HumanSearchResultViewModel`, `SearchResultMappingExtensions.ToHumanSearchViewModel` and the `_HumanSearchResults.cshtml` partial they existed for are all deleted; there is no shared person-row view model left to keep in step. The cost is one `ProjectReference` to `Humans.Users` — a tag helper binds only against an assembly the section references, and an orchestrator referencing what it orchestrates is expected (only cycles are forbidden). `Views/_ViewImports.cshtml` carries the matching `@addTagHelper *, Humans.Users`; without it every row ships as inert literal markup on a green 200, which `SearchPageRenderTests` pins by asserting on the rendered match badge.
- **Teams / Camps / Shifts / Events render the same way (nobodies-collective/Humans#1062).** `_GlobalSearchSection` switches on `GlobalSearchResult.Type` and emits that section's own component with the key and nothing else — `<vc:teams-search-result team-id>`, `<vc:camps-search-result camp-id>`, `<vc:shifts-search-result rota-id>`, `<vc:events-search-result event-id>`. `GlobalSearchResult` is `(Type, Key, SortKey, Score)` where `Key` is a `Guid`: no `Title`, no `Subtitle`, no `Url`. Every bucket keys by the entity's id — `TeamSearchHit`, `CampSearchHit` and `RotaSearchHit` all carry one — and a component that needs a slug for its link fetches it from the entity it just loaded. `SortKey` is a name the controller sorts on and no view ever renders. The cost is a `ProjectReference` per publisher and one `@addTagHelper` line each; `GlobalSearchSectionRenderTests` pins all four against real seeded rows, and measures that tripling the rows adds no query.
- **One rubric, four scorers.** Teams/Camps/Shifts score their own hits with `StringSearchExtensions.NameMatchScore`, which lives in `Humans.Base` precisely so the buckets stay comparable without a section owning another's rubric. Events is the open end: it has no scored hit type, so `SearchService` still scores that bucket off `ApprovedEventView.Title` — the last display field this section names.
- **Cross-domain navs** — none; the section owns no entities.
- **Cross-section calls** — `IUserServiceRead`, `ITeamServiceRead`, `ICampServiceRead`, `IShiftManagementService`, `IEventServiceRead` (see Cross-Section Dependencies).
- **Test coverage — structure and behaviour pinned as of #1197/#1198, with one hole left.** That hole is the Events cache filter (last bullet below), and it is what still holds G3 predicate 3 at **FAIL** in the audit. Structure: `tests/Humans.Search.Tests/Architecture/SearchArchitectureTests.cs` (#1197, moved and trimmed at G5) — `ISearchService_ImplementsOrchestratorNotApplicationService`. (The former `SearchService_HasNoRepositoryDependency` and `TheSectionExportsOnlyItsSectionAndResourceMarkers` were retired once HUM0026/HUM0027 took over enforcing the same claims at build time — see [`peters-hard-rules.md`](../../../../docs/architecture/peters-hard-rules.md)'s "tests are not acceptable" preference for analyzer-shaped rules. `SearchService_DependsOnlyOnServiceInterfaces` and `NoTypeInTheSectionTouchesDataAccess` were retired for asserting an absence — [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md).) Rendering: `tests/Humans.Integration.Tests/Controllers/SearchPageRenderTests.cs` (8 tests) — the page in English and Spanish, both empty-query branches, the no-results path, the `filter=` bind, the anonymous redirect, and Shell's nav still linking to the moved controller — plus `GlobalSearchSectionRenderTests.cs` (2 tests), which seeds one real row per non-human bucket and asserts on markers only each section's component writes, then measures that tripling the rows adds no query. Behaviour (#1198):
  - **Orchestration** — `tests/Humans.Search.Tests/Services/SearchServiceTests.cs`, 17 tests: the `<2`-char gate and its boundary, query trimming, `onlyType` querying one section and skipping four, the no-`onlyType` fan-out to all five, the 100/80/60 tiers with case-insensitivity and empty-name drop, GUID hits for Team/Camp/Rota, `PersonSearchFields.PublicAll` on both text and GUID paths, the unbounded cap, the `Features:Events` off path, and the Events description-fallback tier.
  - **Controller** — `tests/Humans.Search.Tests/Controllers/SearchControllerTests.cs`, 7 tests: non-human buckets sorted score-desc-then-sort-key-asc, humans by relevance then burner name, view-model projection, and the shell-not-500 path on a service throw.
  - **Humans** — `CachingUserServiceTests.cs:659-892`, 18 tests, including `SearchUsersAsync_PublicAll_ExcludesRejected`, `…_GuidShortCircuitsById`, and its `ExactName` carve-out.
  - **Teams** — `CachingTeamServiceTests.SearchAsync_ServesFromCache_MatchesByName_ExcludesHidden` (`:468`) seeds "Kitchen" and a hidden "Kitchenette" and asserts a single hit.
  - **Camps** — the `Active`/`Full` filter is now genuinely pinned: `SearchAsync_TextQuery_ExcludesNonPublicSeasonStatuses` (`:444`) is a `[Theory]` over `Pending`/`Rejected`/`Withdrawn` asserting an empty result, and `SearchAsync_GuidQuery_ResolvesACampWithANonPublicSeason` (`:458`) pins the GUID carve-out. The older `…_MatchesPublicYearSeasonName` (`:418`) pins only the cache path — it seeds one `Active` season and asserts the positive match, so on its own it never protected the status predicate.
  - **Shifts** — `tests/Humans.Integration.Tests/Repositories/Shifts/ShiftRepositoryRotaSearchTests.cs`: `SearchVolunteerVisibleRotasAsync_ExcludesRotasHiddenFromVolunteers` seeds a visible and a hidden rota and asserts only the visible one returns; a second test pins case-insensitivity and event scoping.
  - **Humans score tiers** — `PersonSearchMatcherTests.cs`: 18 test methods (10 `[Fact]`, 8 `[Theory]`), 40 executed cases once `[InlineData]` expands.
  - **Still not covered:** `CachingEventService`'s in-memory `MatchesQuery` — the Events *cache* filter. `SearchServiceTests` exercises the orchestrator's scoring of event rows against a mocked `IEventServiceRead`, and `EventRepositoryTests` covers the *repository*'s status/category/venue filters; neither runs the cache path Search actually calls. `CachingEventServiceTests` does not touch `GetApprovedEventsAsync`.

  History in G3 gap #1 of [`docs/plans/2026-08-03-g0-first-audit/Search.md`](../../../../docs/plans/2026-08-03-g0-first-audit/Search.md). **Check the list above before writing a test — most of this is now covered, and the one real hole is the Events cache filter.**

See [`docs/features/global/global-search.md`](../../../../docs/features/global/global-search.md) for the full user-facing workflow and DTO reference; this doc states the invariants a PR is checked against, not the feature narrative.

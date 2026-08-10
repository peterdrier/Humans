<!-- freshness:triggers
  src/Sections/Humans.Events/Services/CachingEventService.cs
  src/Sections/Humans.Events/Section.cs
  src/Humans.Application/Services/Search/**
  src/Humans.Application/Interfaces/Search/**
  src/Humans.Web/Controllers/SearchController.cs
  src/Humans.Web/Views/Search/**
  src/Humans.Application/DTOs/GlobalSearchResults.cs
  src/Humans.Application/DTOs/SectionSearchHits.cs
  src/Humans.Infrastructure/Services/Users/CachingUserService.cs
  src/Humans.Infrastructure/Services/Teams/CachingTeamService.cs
  src/Humans.Infrastructure/Services/Camps/CachingCampService.cs
  src/Humans.Application/Services/Shifts/ShiftManagementService.cs
  src/Humans.Infrastructure/Repositories/Shifts/ShiftRepository.Management.cs
  src/Humans.Application/Services/Profiles/PersonSearchMatcher.cs
  src/Humans.Web/Controllers/CampController.cs
  src/Humans.Web/Controllers/TeamController.cs
  src/Humans.Web/Controllers/ShiftsController.cs
  src/Humans.Web/Models/Shifts/ShiftBrowsePageBuilder.cs
-->
<!-- freshness:flag-on-change
  Search scope (which fields are searched per section), the authorization model (role-blind on both paths; public-only on text queries), the US-GS.4 GUID exception, and per-section SearchAsync contracts — review when search code, the auth-conventions atom, or the person-search atom change.

  The trigger list reaches outside src/**/Search/** on purpose. Since the nobodies-collective/Humans#985
  ruling, the visibility guarantee is enforced by the destination pages, not by Search — so the
  US-GS.4 GUID exception and its known-gap note depend on CampController/TeamController, and the
  per-bucket filters and GUID branches live in the Caching*Service classes and ShiftManagementService —
  and for Shifts, one level deeper still: the service's text branch delegates to
  ShiftRepository.Management.cs, where the active-event, IsVisibleToVolunteers and EF.Functions.ILike
  predicates behind this doc's Shifts claims actually live.
  Rotas are the non-obvious one: a rota hit links to /Shifts?departmentId=, not to the rota, so its
  destination gate is ShiftBrowsePageBuilder's IncludeAdminOnly/IncludeHidden flags behind
  ShiftsController — that is where an admin-only rota would be exposed, not in Shifts' search call.
  Fixing nobodies-collective/Humans#993 in CampController must force a review of this doc; without
  these paths it would not.
-->

# Global Search (`/Search`)

## Business Context

Members regularly want to find a person, team, camp, shift, or event without first guessing which list page to start from. As membership grows and camps/teams multiply, the friction of "which area do I look in?" gets worse. A single magnifying-glass entry point in the top nav routes to `/Search`, which fans out across the searchable sections and renders type-grouped results. The Events bucket is only included when the `Features:Events` flag is on (it gates the section's nav and routes the same way).

The feature is deliberately scoped to matching **confined to each entity's own public fields** — no cross-modal traversal. Earlier drafts proposed cross-modal pull-ins (a person → their teams; a team → its rotas) and a unified ranked list, but those were dropped:

- Cross-modal traversal invited 2nd- and 3rd-order links the user didn't ask for (e.g. "camps you lead" surfaced when matching a person), and the orchestration code was disproportionate to the value.
- Names are what users actually type when they remember "I think it was called Foo." Matching on adjacency leaves Foo at the top instead of burying it under loosely-related rows.

## User Stories

### US-GS.1: Open the global search
**As an** authenticated user
**I want to** click a magnifying-glass in the top nav
**So that** I can search the whole app from any page

**Acceptance Criteria:**
- A magnifying-glass icon appears in the top nav for any authenticated user.
- Clicking it routes to `/Search` with an empty query.
- Empty / single-character query renders an instructional placeholder, not a 500 or wall of every record.

### US-GS.2: Search by name across sections
**As an** authenticated user
**I want to** type a query and see ranked hits for humans, teams, camps, and shifts
**So that** I can jump to the right entity without remembering which list page owns it

**Acceptance Criteria:**
- `/Search?q=<query>` returns type-grouped sections: Humans, Teams, Camps, Shifts, and (when `Features:Events` is enabled) Events.
- Each section is independently ranked by score within itself; no cross-type ranking.
- All matches are returned per section — there is no result cap (the dataset is small enough at ~500-user scale that capping only hid people users were looking for).
- Each result clearly shows its type via section header + icon, and links to the canonical detail page (for Events, the link is `/Events/Browse?q=<title>` — there is no per-event detail page).
- Per-type filter chips (All | Humans | Teams | Camps | Shifts | Events) hide the other sections. The Events chip is hidden when `Features:Events` is off.
- A query with no matches renders "No results for <query>." (not 500).

### US-GS.3: Match by the right fields
**As an** authenticated user
**I want** matches based on the entity's name
**So that** I find what I'm looking for without typing exactly the right field

**Acceptance Criteria:**
- **Humans** match via `IUserServiceRead.SearchUsersAsync` with `PersonSearchFields.PublicAll` (`Name | Bio`, per `memory/architecture/person-search.md`) — the resolved display name plus the `Bio` bucket: bio, city, contribution-interests, CV, pronouns, `AllActiveProfiles`-visible ContactFields, and publicly-exposed emails. The bit-flag's existing fields are unchanged — humans inherit the same scope as `/Profile/Search` for non-admin viewers. Emergency-contact data and admin-only fields are never searchable.
- **Teams** match on `Team.Name` only.
- **Camps** match on the public-year `CampSeason.Name` only.
- **Shifts** (rotas) match on `Rota.Name` only.
- Teams, Camps, Shifts, and Humans additionally match by pasting the entity's own id (a `Guid.TryParse` fast-path scored as an exact match). For humans the pasted UserId resolves against the cached snapshot (`CachingUserService`) and is reported as a `User ID` match; rejected profiles are excluded, and `ExactName` queries skip id resolution so a GUID-shaped burner name matches by name, never by id collision.
- **Events** match on `Event.Title` or `Event.Description` and are filtered to `Status = Approved` only. Events are the one deliberate exception to matching on the name/title field alone: the orchestrator reuses `IEventServiceRead.GetApprovedEventsAsync` (the same call the public Browse page makes), which filters Title + Description in memory over the approved-event cache, because event copy is short and free-form so description text is often the load-bearing name signal users remember. Rows are still scored by Title via the standard exact/prefix/contains rubric; rows that only matched via Description fall through to a contains-tier score so they're still surfaced (just ranked below title hits).
- Humans, Teams, Camps **and Events** match in-memory against the cached snapshots (`CachingUserService` / `CachingTeamService` / `CachingCampService` / `CachingEventService`) — case-insensitive contains, accent-folded for humans; search never hits the DB for these four buckets. **Shifts is the only DB-backed bucket**, running case-insensitive Postgres `EF.Functions.ILike` per `memory/feedback_ef_ilike_not_toupper.md`.

### US-GS.4: A text query surfaces the public-visibility set, never more
**As an** authenticated viewer (any role)
**I want** a name search to surface only what a regular volunteer would see from list pages
**So that** the search affordance can't be a privilege escalation, and admins never see surprise data through this path

*(Scope: text queries. Resolving an entity by pasting its GUID is a separate, ruled-on path — see US-GS.5.)*

**Acceptance Criteria — these govern *text* queries. GUID queries are a sanctioned exception; see US-GS.5.**
- Hidden teams (`Team.IsHidden = true`) are excluded for everyone.
- Camps are filtered to the public-status set (`CampSeasonStatus.Active` or `Full`) for the public year — same gate as the public camp directory.
- Rotas are filtered to `IsVisibleToVolunteers = true` for everyone.
- Events are filtered to `Status = Approved`; submissions in `Draft`, `Pending`, `Rejected`, `ResubmitRequested`, or `Withdrawn` are never returned, matching the public `/Events/Browse` surface.
- Admin-only profile fields (verified emails, non-public ContactFields) are never returned through `/Search`, regardless of role. Admins use the existing per-section admin pages (`/Teams` admin, `/Camps` admin, `/Users/Admin`) for privileged views.

### US-GS.5: A GUID query resolves straight to the entity
**As** someone who already holds an entity id (from a log line, an audit row, a support thread)
**I want** pasting it into search to find that entity
**So that** I don't have to know which section's admin page owns it

**Acceptance Criteria:**
- The Teams, Camps and Rotas buckets treat a parseable GUID as an id lookup and skip the visibility filters in US-GS.4 — the hit comes back for a hidden team, a non-public camp season, or a rota hidden from volunteers.
- Humans are the exception: the id path skips only the `PersonSearchFields` mask, not the eligibility gate. `CachingUserService.SearchUsersAsync` requires `Profile is not null && Profile.RejectedAt is null` on the GUID branch exactly as it does per-row on the text branch, so a profile-less or rejected user resolves to nothing either way.
- The hit is scored as an exact match and its URL is the entity's normal page. Opening it re-runs that page's own access checks — a detail page refuses (`/Teams/{slug}` 404s a hidden team), and a rota's `/Shifts?departmentId={teamId}` listing opens but omits the hidden rota. `/Camps/{slug}` is the one that does not yet hold up its end (see Authorization Model).
- Rotas carry one further exception, and it is about reach rather than visibility. The GUID branch of `ShiftManagementService.SearchAsync` has no event filter, but `/Shifts` always builds from the active event — so a rota belonging to a **past** event resolves to a link that cannot show it, even when it is volunteer-visible. Tracked as nobodies-collective/Humans#998. The text branch does not have this problem: it is already scoped to the active event.

## Authorization Model

`/Search` is gated by `[Authorize]` — anonymous viewers can't reach it. Beyond that, **search is not an authorization boundary**: a hit says a URL exists, not that the caller may open it. Visibility is enforced at the destination, in whatever shape that destination has — a detail page refuses outright, a listing page renders and omits the row (ruling on nobodies-collective/Humans#985, 2026-08-07). Text queries are still filtered to the public surface per US-GS.4; the GUID path in US-GS.5 is deliberately unfiltered, because you can only use it if you already hold the id.

That destination-page guarantee has one known hole: `CampController.Details` (and `SeasonDetails`) has no season-status gate, so a camp whose public-year season is `Pending`, `Rejected` or `Withdrawn` renders its detail page to anyone, signed-out included. Tracked as nobodies-collective/Humans#993; `CampControllerTests.Details_NonPublicSeason_AnonymousViewer_IsRefused` is skipped until that lands.

There is no scope parameter on `ISearchService` and no role check in `SearchController`.

This is a deliberate descope. An earlier draft had a `SearchScope { Public, Admin }` parameter threaded through every search service that promoted `Admin` / `HumanAdmin` / `Board` callers to a wider surface (hidden teams, non-public camp seasons, admin-only profile fields). It was removed because:

- A single global scope can't honor the admin-superset rule (`memory/code/admin-role-superset.md`) for `TeamsAdmin` / `CampAdmin` / `TicketAdmin` without leaking admin profile fields cross-domain — see the discussion in nobodies-collective/Humans#693.
- Privileged search isn't a basic-feature requirement. The basics are "find a person/team/camp/rota by name from any page." Admins still have section-specific admin pages for the privileged view.

If privileged search is added later, the right shape is per-bucket scope (TeamsAdmin gets the admin Teams surface but the public Humans surface), not a single global enum. Tracked at #693.

## Architecture

`ISearchService` is a thin orchestrator in the Application layer. It owns no tables, has no repository, and reaches every other section through public service interfaces only — no direct repository fan-out, no cross-section table access. Per design-rules §6, the section that owns a table owns the query against it; the orchestrator just merges and ranks within each type bucket.

```
SearchController
   └── ISearchService.SearchAsync(query, onlyType)
         ├── IUserServiceRead.SearchUsersAsync(query, PersonSearchFields.PublicAll, limit)   → IReadOnlyList<HumanSearchResult>
         ├── ITeamServiceRead.SearchAsync(query, max)                                         → IReadOnlyList<TeamSearchHit>
         ├── ICampServiceRead.SearchAsync(query, max)                                         → IReadOnlyList<CampSearchHit>
         ├── IShiftManagementService.SearchAsync(query, max)                                  → IReadOnlyList<RotaSearchHit>
         └── IEventServiceRead.GetApprovedEventsAsync(…, q: query, …)  (skipped when Features:Events is off)  → IReadOnlyList<Event>
```

Humans, Teams, and Camps are served entirely from their caching decorators' warm in-memory snapshots — the inner `TeamService` / `CampService` `SearchAsync` throw `NotSupportedException` and the DB-search repository methods are gone. **Events is cache-backed too:** `IEventServiceRead` is registered as the `CachingEventService` singleton (`EventsSectionExtensions.cs:48`), whose `GetApprovedEventsAsync` filters the approved-event cache in memory with `Contains(…, OrdinalIgnoreCase)` — no DB round trip per search. **Shifts is the only bucket that reaches Postgres**, running the case-insensitive `ILike` filter against the name field with `EscapeLikePattern` to defang `%` / `_` / `\` in user input. Section services map their domain entities to type-specific search-hit DTOs (`TeamSearchHit`, `CampSearchHit`, `RotaSearchHit`) so the orchestrator never has to traverse cross-domain navigation properties to render a row.

The orchestrator scores each non-human hit by name-match strength (humans arrive pre-scored by `PersonSearchMatcher`, which adds tiers for token-prefix and non-name-field matches):

| Match shape     | Score |
|-----------------|-------|
| Name (exact)    |  100  |
| Name (prefix)   |   80  |
| Name (contains) |   60  |

Display ordering is a presentation concern and lives in `SearchController.BuildViewModel` per `memory/architecture/display-sort-in-controllers.md` — the service returns scored but unsorted buckets. Each non-human bucket sorts by `Score desc, Title asc` at the controller; the humans bucket sorts by relevance (`OrderByRelevance()`: `Score` desc, then `BurnerName`), matching `/Profile/Search`.

Counts reflect every match — there is no cap, so the chip count is the true number of hits the user can scroll to. There is no separate `CountMatchingAsync` per section; the buckets are already the full result set.

## DTOs

| DTO | Returned by | Used by |
|---|---|---|
| `HumanSearchResult` | `IUserServiceRead.SearchUsersAsync` | View renders via `_HumanSearchResults` partial |
| `TeamSearchHit (Name, Slug)` | `ITeamServiceRead.SearchAsync` | Orchestrator scores → `GlobalSearchResult` |
| `CampSearchHit (Slug, Name)` | `ICampServiceRead.SearchAsync` | Orchestrator scores → `GlobalSearchResult` |
| `RotaSearchHit (Name, TeamId, TeamName)` | `IShiftManagementService.SearchAsync` | Orchestrator scores → `GlobalSearchResult` |
| `GlobalSearchResult (Type, Title, Subtitle, Url, Score)` | Orchestrator | View renders simple list rows for Teams / Camps / Shifts / Events |
| `GlobalSearchResults (Query, Humans, Teams, Camps, Shifts, Events)` | `ISearchService` | View-model / view |

## UI

`/Search` renders type-grouped sections, in order: **Humans**, **Teams**, **Camps**, **Shifts**, **Events**. Each section is hidden when its bucket is empty. The Events section and chip are also hidden when `Features:Events` is off (the view reads `IConfiguration` directly for this gate).

- **Humans** are rendered by the canonical `_HumanSearchResults` partial (see `memory/architecture/person-search.md`). The controller projects each `HumanSearchResult` to `HumanSearchResultViewModel` via the existing `ToHumanSearchViewModel` extension, matching `/Profile/Search` and `/Users/Admin`.
- **Teams / Camps / Shifts / Events** are rendered by `_GlobalSearchSection` — a small, deliberately-minimal partial. This is not a third person-search surface (the `_HumanSearchResults` rule applies only to person rendering); it's a generic list-row template for the simpler types.

A type-filter chip row at the top (All | Humans | Teams | Camps | Shifts | Events) preserves the query and toggles the active filter. Counts on each chip reflect the full match count.

## Out of Scope

- **Cross-modal / relational pull-ins** (person → their teams; team → its rotas; camp → its leads). Earlier draft included these; dropped after spec review.
- **Cross-modal "as-you-type" autocomplete** from the navbar input. Separate issue.
- **Full-text Postgres `tsvector` indexing** / search-as-you-type latency optimization. Revisit if `ILike` becomes slow at the project's ~500-user scale.
- **External / public search.** Search is gated behind `[Authorize]`.

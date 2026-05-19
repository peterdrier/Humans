<!-- freshness:triggers
  src/Humans.Application/Services/Search/**
  src/Humans.Application/Interfaces/Search/**
  src/Humans.Web/Controllers/SearchController.cs
  src/Humans.Web/Views/Search/**
  src/Humans.Application/DTOs/GlobalSearchResults.cs
  src/Humans.Application/DTOs/SectionSearchHits.cs
-->
<!-- freshness:flag-on-change
  Search scope (which fields are searched per section), the public-only authorization model, and per-section SearchAsync contracts — review when search code, the auth-conventions atom, or the person-search atom change.
-->

# Global Search (`/Search`)

## Business Context

Members regularly want to find a person, team, camp, shift, or event without first guessing which list page to start from. As membership grows and camps/teams multiply, the friction of "which area do I look in?" gets worse. A single magnifying-glass entry point in the top nav routes to `/Search`, which fans out across the searchable sections and renders type-grouped results. The Events bucket is only included when the `Features:Events` flag is on (it gates the section's nav and routes the same way).

The feature is deliberately scoped to **name-only matching**. Earlier drafts proposed cross-modal pull-ins (a person → their teams; a team → its rotas) and a unified ranked list, but those were dropped:

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
- Each section is capped at 10 results in the unified view; 50 when a single-type filter chip is active.
- Each result clearly shows its type via section header + icon, and links to the canonical detail page (for Events, the link is `/Events/Browse?q=<title>` — there is no per-event detail page).
- Per-type filter chips (All | Humans | Teams | Camps | Shifts | Events) hide the other sections and bump the cap. The Events chip is hidden when `Features:Events` is off.
- A query with no matches renders "No results for <query>." (not 500).

### US-GS.3: Match by the right fields
**As an** authenticated user
**I want** matches based on the entity's name
**So that** I find what I'm looking for without typing exactly the right field

**Acceptance Criteria:**
- **Humans** match via `IProfileService.SearchProfilesAsync` with `PersonSearchFields.PublicAll` (per `memory/architecture/person-search.md`). The bit-flag's existing fields are unchanged — humans inherit the same scope as `/Profile/Search` for non-admin viewers. Emergency-contact data is never searchable.
- **Teams** match on `Team.Name` only.
- **Camps** match on the public-year `CampSeason.Name` only.
- **Shifts** (rotas) match on `Rota.Name` only.
- **Events** match on `Event.Title` or `Event.Description` and are filtered to `Status = Approved` only. Events are the one deliberate exception to names-only: the orchestrator reuses `IEventService.GetApprovedEventsAsync` (the same call the public Browse page makes), which filters Title + Description with ILike, because event copy is short and free-form so description text is often the load-bearing name signal users remember. Rows are still scored by Title via the standard exact/prefix/contains rubric; rows that only matched via Description fall through to a contains-tier score so they're still surfaced (just ranked below title hits).
- All matchers run case-insensitive Postgres `EF.Functions.ILike` at the DB layer per `memory/feedback_ef_ilike_not_toupper.md`.

### US-GS.4: Search surfaces the public-visibility set, never more
**As an** authenticated viewer (any role)
**I want** search to surface only what a regular volunteer would see from list pages
**So that** the search affordance can't be a privilege escalation, and admins never see surprise data through this path

**Acceptance Criteria:**
- Hidden teams (`Team.IsHidden = true`) are excluded for everyone.
- Camps are filtered to the public-status set (`CampSeasonStatus.Active` or `Full`) for the public year — same gate as the public camp directory.
- Rotas are filtered to `IsVisibleToVolunteers = true` for everyone.
- Events are filtered to `Status = Approved`; submissions in `Draft`, `Pending`, `Rejected`, `ResubmitRequested`, or `Withdrawn` are never returned, matching the public `/Events/Browse` surface.
- Admin-only profile fields (verified emails, non-public ContactFields) are never returned through `/Search`, regardless of role. Admins use the existing per-section admin pages (`/Teams` admin, `/Camps` admin, `/Profile/Admin`) for privileged views.

## Authorization Model

`/Search` is gated by `[Authorize]` — anonymous viewers can't reach it. Beyond that, **every authenticated viewer sees the same public-visibility surface**, regardless of role. There is no scope parameter on `ISearchService` and no role check in `SearchController`.

This is a deliberate descope. An earlier draft had a `SearchScope { Public, Admin }` parameter threaded through every search service that promoted `Admin` / `HumanAdmin` / `Board` callers to a wider surface (hidden teams, non-public camp seasons, admin-only profile fields). It was removed because:

- A single global scope can't honor the admin-superset rule (`memory/code/admin-role-superset.md`) for `TeamsAdmin` / `CampAdmin` / `TicketAdmin` without leaking admin profile fields cross-domain — see the discussion in nobodies-collective/Humans#693.
- Privileged search isn't a basic-feature requirement. The basics are "find a person/team/camp/rota by name from any page." Admins still have section-specific admin pages for the privileged view.

If privileged search is added later, the right shape is per-bucket scope (TeamsAdmin gets the admin Teams surface but the public Humans surface), not a single global enum. Tracked at #693.

## Architecture

`ISearchService` is a thin orchestrator in the Application layer. It owns no tables, has no repository, and reaches every other section through public service interfaces only — no direct repository fan-out, no cross-section table access. Per design-rules §6, the section that owns a table owns the query against it; the orchestrator just merges and ranks within each type bucket.

```
SearchController
   └── ISearchService.SearchAsync(query, onlyType, limit)
         ├── IProfileService.SearchProfilesAsync(query, PersonSearchFields.PublicAll, limit) → IReadOnlyList<HumanSearchResult>
         ├── ITeamService.SearchAsync(query, max)                                             → IReadOnlyList<TeamSearchHit>
         ├── ICampService.SearchAsync(query, max)                                             → IReadOnlyList<CampSearchHit>
         ├── IShiftManagementService.SearchAsync(query, max)                                  → IReadOnlyList<RotaSearchHit>
         └── IEventService.GetApprovedEventsAsync(…, q: query, …)  (skipped when Features:Events is off)  → IReadOnlyList<Event>
```

Each section's repository runs the case-insensitive Postgres `ILike` filter against the entity's name field at the DB layer with `EscapeLikePattern` to defang `%` / `_` / `\` in user input. Section services map their domain entities to type-specific search-hit DTOs (`TeamSearchHit`, `CampSearchHit`, `RotaSearchHit`) so the orchestrator never has to traverse cross-domain navigation properties to render a row.

The orchestrator scores each hit by name-match strength:

| Match shape     | Score |
|-----------------|-------|
| Name (exact)    |  100  |
| Name (prefix)   |   80  |
| Name (contains) |   60  |

Display ordering is a presentation concern and lives in `SearchController.BuildViewModel` per `memory/architecture/display-sort-in-controllers.md` — the service returns scored but unsorted buckets. Each non-human bucket sorts by `Score desc, Title asc` at the controller; the humans bucket sorts by `BurnerName asc`, matching `/Profile/Search`.

Counts are post-cap by design — they reflect what the user actually sees in the page. There is no separate `CountMatchingAsync` per section; total-match counts at scale (~500 users) aren't worth the second query.

## DTOs

| DTO | Returned by | Used by |
|---|---|---|
| `HumanSearchResult` | `IProfileService` (existing) | View renders via `_HumanSearchResults` partial |
| `TeamSearchHit (Name, Slug)` | `ITeamService.SearchAsync` | Orchestrator scores → `GlobalSearchResult` |
| `CampSearchHit (Slug, Name)` | `ICampService.SearchAsync` | Orchestrator scores → `GlobalSearchResult` |
| `RotaSearchHit (Name, TeamId, TeamName)` | `IShiftManagementService.SearchAsync` | Orchestrator scores → `GlobalSearchResult` |
| `GlobalSearchResult (Type, Title, Subtitle, Url, Score)` | Orchestrator | View renders simple list rows for Teams / Camps / Shifts / Events |
| `GlobalSearchResults (Query, Humans, Teams, Camps, Shifts, Events)` | `ISearchService` | View-model / view |

## UI

`/Search` renders type-grouped sections, in order: **Humans**, **Teams**, **Camps**, **Shifts**, **Events**. Each section is hidden when its bucket is empty. The Events section and chip are also hidden when `Features:Events` is off (the view reads `IConfiguration` directly for this gate).

- **Humans** are rendered by the canonical `_HumanSearchResults` partial (see `memory/architecture/person-search.md`). The controller projects each `HumanSearchResult` to `HumanSearchResultViewModel` via the existing `ToHumanSearchViewModel` extension, matching `/Profile/Search` and `/Profile/Admin`.
- **Teams / Camps / Shifts / Events** are rendered by `_GlobalSearchSection` — a small, deliberately-minimal partial. This is not a third person-search surface (the `_HumanSearchResults` rule applies only to person rendering); it's a generic list-row template for the simpler types.

A type-filter chip row at the top (All | Humans | Teams | Camps | Shifts | Events) preserves the query and toggles the active filter. Counts on each chip reflect the post-cap result count.

## Out of Scope

- **Cross-modal / relational pull-ins** (person → their teams; team → its rotas; camp → its leads). Earlier draft included these; dropped after spec review.
- **Cross-modal "as-you-type" autocomplete** from the navbar input. Separate issue.
- **Full-text Postgres `tsvector` indexing** / search-as-you-type latency optimization. Revisit if `ILike` becomes slow at the project's ~500-user scale.
- **External / public search.** Search is gated behind `[Authorize]`.

<!-- freshness:triggers
  src/Humans.Application/Services/Search/**
  src/Humans.Application/Interfaces/Search/**
  src/Humans.Web/Controllers/SearchController.cs
  src/Humans.Web/Views/Search/**
  src/Humans.Application/DTOs/GlobalSearchResults.cs
  src/Humans.Application/DTOs/SectionSearchHits.cs
  src/Humans.Application/DTOs/SearchScope.cs
-->
<!-- freshness:flag-on-change
  Search scope (which fields are searched per section), authorization model (SearchScope enum vs PersonSearchFields bit-flag), and per-section SearchAsync contracts — review when search code, the auth-conventions atom, or the person-search atom change.
-->

# Global Search (`/Search`)

## Business Context

Members regularly want to find a person, team, camp, or shift without first guessing which list page to start from. As membership grows and camps/teams multiply, the friction of "which area do I look in?" gets worse. A single magnifying-glass entry point in the top nav routes to `/Search`, which fans out across the four searchable sections and renders type-grouped results.

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
- `/Search?q=<query>` returns four type-grouped sections: Humans, Teams, Camps, Shifts.
- Each section is independently ranked by score within itself; no cross-type ranking.
- Each section is capped at 10 results in the unified view; 50 when a single-type filter chip is active.
- Each result clearly shows its type via section header + icon, and links to the canonical detail page.
- Per-type filter chips (All | Humans | Teams | Camps | Shifts) hide the other sections and bump the cap.
- A query with no matches renders "No results for <query>." (not 500).

### US-GS.3: Match by the right fields
**As an** authenticated user
**I want** matches based on what I'd actually remember about the entity
**So that** I find what I'm looking for without typing exactly the right field

**Acceptance Criteria:**
- **Humans** match via `IProfileService.SearchProfilesAsync` with `PersonSearchFields.PublicAll` for non-admins and `PersonSearchFields.AdminAll` for admins (per `memory/architecture/person-search.md`). Emergency-contact data is never searchable, regardless of scope.
- **Teams** match on `Team.Name`, `Team.Slug`, and `Team.Description`.
- **Camps** match on `Camp.Slug`, `CampSeason.Name` for the public year, and `CampSeason.BlurbShort` for the public year.
- **Shifts** (rotas) match on `Rota.Name` and `Rota.Description` within the active event.
- All matchers run case-insensitive Postgres `EF.Functions.ILike` at the DB layer per `memory/feedback_ef_ilike_not_toupper.md`.

### US-GS.4: Search respects existing visibility
**As a** non-admin viewer
**I want** search to surface only what I'd already see from list pages
**So that** the search affordance can't be a privilege escalation

**Acceptance Criteria:**
- Hidden teams (`Team.IsHidden = true`) are excluded for non-admins.
- Camps are filtered to the public-status set (`CampSeasonStatus.Active` or `Full`) for the public year — same gate as the public camp directory.
- Rotas are filtered to `IsVisibleToVolunteers = true` for non-admins.
- Admin-only profile fields (verified emails, non-public ContactFields) are returned only when the controller has verified an admin-shaped role (`Admin` / `HumanAdmin` / `Board`).

### US-GS.5: Admins see what they'd see elsewhere
**As an** admin viewer
**I want** search to surface the admin-visible set
**So that** I can find hidden teams, draft camps, and admin-only rotas without leaving the search page

**Acceptance Criteria:**
- Admins see hidden teams in team results.
- Admins see camps with any season status (pending / rejected / withdrawn included) for the public year.
- Admins see rotas regardless of `IsVisibleToVolunteers`.
- Admins see human results that include the `Admin` profile-field bit (verified emails, non-public ContactFields).

## Authorization Model

The auth boundary is the controller. Services are auth-free per design-rules §11.

`SearchController.Index` resolves the viewer's role to a single `SearchScope` value:

```csharp
private static readonly string[] AdminViewerRoles = { Admin, HumanAdmin, Board };
var scope = AdminViewerRoles.Any(role => User.IsInRole(role))
    ? SearchScope.Admin
    : SearchScope.Public;
```

The `SearchScope` enum (`Public` / `Admin`) is then threaded through `ISearchService` and to each section's `SearchAsync`. Every call site reads `SearchScope.X` literally and is auditable at a glance — same auditability as the `PersonSearchFields.X` bit-flag in `memory/architecture/person-search.md`. There are no privilege booleans (`isAdmin`, `includeHidden`, `includeNonPublic`) on any service interface; those are an explicitly prohibited pattern (`memory/code/authorization-conventions.md`).

For humans specifically, the orchestrator translates `SearchScope` to the existing `PersonSearchFields` bit-flag — `Public` → `PublicAll`, `Admin` → `AdminAll` — preserving the canonical person-search contract.

## Architecture

`ISearchService` is a thin orchestrator in the Application layer. It owns no tables, has no repository, and reaches every other section through public service interfaces only — no direct repository fan-out, no cross-section table access. Per design-rules §6, the section that owns a table owns the query against it; the orchestrator just merges and ranks within each type bucket.

```
SearchController
   └── ISearchService.SearchAsync(query, scope, onlyType, limit)
         ├── IProfileService.SearchProfilesAsync(query, PersonSearchFields, limit) → IReadOnlyList<HumanSearchResult>
         ├── ITeamService.SearchAsync(query, SearchScope, max)                      → IReadOnlyList<TeamSearchHit>
         ├── ICampService.SearchAsync(query, SearchScope, max)                      → IReadOnlyList<CampSearchHit>
         └── IShiftManagementService.SearchAsync(query, SearchScope, max)           → IReadOnlyList<RotaSearchHit>
```

Each section's repository runs the case-insensitive Postgres `ILike` filter at the DB layer with `EscapeLikePattern` to defang `%` / `_` / `\` in user input. Section services map their domain entities to type-specific search-hit DTOs (`TeamSearchHit`, `CampSearchHit`, `RotaSearchHit`) so the orchestrator never has to traverse cross-domain navigation properties to render a row.

The orchestrator scores within each type using:

| Field hit               | Score |
|-------------------------|-------|
| Name (exact)            |  100  |
| Name (prefix)           |   80  |
| Slug (contains)         |   70  |
| Name (contains)         |   60  |
| Description (contains)  |   30  |
| Multi-field bonus (×N)  |  +15  |

Counts are post-cap by design — they reflect what the user actually sees in the page. There is no separate `CountMatchingAsync` per section; total-match counts at scale (~500 users) aren't worth the second query.

## DTOs

| DTO | Returned by | Used by |
|---|---|---|
| `HumanSearchResult` | `IProfileService` (existing) | View renders via `_HumanSearchResults` partial |
| `TeamSearchHit (Name, Slug, Description?)` | `ITeamService.SearchAsync` | Orchestrator scores → `GlobalSearchResult` |
| `CampSearchHit (Slug, Name, Blurb?)` | `ICampService.SearchAsync` | Orchestrator scores → `GlobalSearchResult` |
| `RotaSearchHit (Name, Description?, TeamId, TeamName)` | `IShiftManagementService.SearchAsync` | Orchestrator scores → `GlobalSearchResult` |
| `GlobalSearchResult (Type, Title, Subtitle?, Url, Score, MatchField?)` | Orchestrator | View renders simple list rows for Teams / Camps / Shifts |
| `GlobalSearchResults (Query, Humans, Teams, Camps, Shifts)` | `ISearchService` | View-model / view |
| `SearchScope { Public, Admin }` | enum | Controller → service → section services |

## UI

`/Search` renders four type-grouped sections, in order: **Humans**, **Teams**, **Camps**, **Shifts**. Each section is hidden when its bucket is empty.

- **Humans** are rendered by the canonical `_HumanSearchResults` partial (see `memory/architecture/person-search.md`). The controller projects each `HumanSearchResult` to `HumanSearchResultViewModel` via the existing `ToHumanSearchViewModel` extension, matching `/Profile/Search` and `/Profile/Admin`.
- **Teams / Camps / Shifts** are rendered by `_GlobalSearchSection` — a small, deliberately-minimal partial. This is not a third person-search surface (the `_HumanSearchResults` rule applies only to person rendering); it's a generic list-row template for the simpler types.

A type-filter chip row at the top (All | Humans | Teams | Camps | Shifts) preserves the query and toggles the active filter. Counts on each chip reflect the post-cap result count.

## Out of Scope

- **Cross-modal / relational pull-ins** (person → their teams; team → its rotas; camp → its leads). Earlier draft included these; dropped after spec review.
- **Cross-modal "as-you-type" autocomplete** from the navbar input. Separate issue.
- **Full-text Postgres `tsvector` indexing** / search-as-you-type latency optimization. Revisit if `ILike` becomes slow at the project's ~500-user scale.
- **External / public search.** Search is gated behind `[Authorize]`.

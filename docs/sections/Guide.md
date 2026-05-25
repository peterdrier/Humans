<!-- freshness:triggers
  src/Humans.Application/Services/GuideFilter.cs
  src/Humans.Application/Services/GuideRolePrivilegeMap.cs
  src/Humans.Application/Constants/GuideFiles.cs
  src/Humans.Application/Interfaces/IGuideContentService.cs
  src/Humans.Application/Interfaces/IGuideRoleResolver.cs
  src/Humans.Infrastructure/Services/GuideContentService.cs
  src/Humans.Infrastructure/Services/GuideRoleResolver.cs
  src/Humans.Infrastructure/Services/GuideRenderer.cs
  src/Humans.Infrastructure/Services/GitHubGuideContentSource.cs
  src/Humans.Web/Controllers/GuideController.cs
  src/Humans.Web/Views/Guide/**
  docs/guide/**
-->
<!-- freshness:flag-on-change
  Guide page role-scoped block visibility, cache TTL, refresh trigger rules, or file set — review when GuideController/GuideFilter/GuideRolePrivilegeMap/GuideFiles or guide markdown content changes.
-->

# Guide — Section Invariants

<!-- Stateless read section: serves markdown from docs/guide/ via a GitHub content source, cached in-memory, with role-scoped filtering per request. -->

## Concepts

- A **guide page** is one markdown file under `docs/guide/` in the Humans
  repo, rendered at `/Guide/<FileStem>`.
- A **role-scoped block** is a `## As a …` heading and the content under
  it, wrapped in `<div data-guide-role="…" data-guide-roles="…">` by the
  renderer and optionally stripped at request time by `GuideFilter`.
- A **parenthetical** is text in parens after `## As a …` (e.g. `## As a
  Board member / Admin (Teams Admin)`) that specifies which domain admin
  role sees that block.

## Data Model

None. Guide owns no database tables. All content is fetched from GitHub and cached in-memory via `IMemoryCache`. No migrations, no EF entities.

## Routing

| Method | Route | Handler | Auth |
|--------|-------|---------|------|
| GET | `/Guide` | `GuideController.Index` → renders `README` | `[AllowAnonymous]` |
| GET | `/Guide/{name}` | `GuideController.Document` → renders named stem | `[AllowAnonymous]` |
| POST | `/Guide/Refresh` | `GuideController.Refresh` → re-fetches all 28 files | `[Authorize(AdminOnly)]` |

Unknown stems return 404 (`NotFound.cshtml`). GitHub unavailability on cold cache returns 503 (`Unavailable.cshtml`).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anonymous | View Volunteer-scoped blocks only |
| Any authenticated human | All Anonymous capabilities |
| Team coordinator (`TeamMember.Role == Coordinator`) | Additionally view Coordinator-scoped blocks |
| Domain admin (system role named in a block's parenthetical) | Additionally view blocks whose parenthetical names their role |
| Board, Admin | View all blocks on all pages (including all Coordinator blocks via within-file superset rule) |
| Admin | All Board capabilities. Additionally trigger `POST /Guide/Refresh` |

## Invariants

- All content above the first `## As a …` heading in a file is always visible regardless of role.
- All content at or below a non-`As a …` `##` heading (e.g. `## Related sections`) is always visible.
- Anonymous users see only Volunteer-scoped blocks; never Coordinator or Board/Admin blocks.
- If a file contains any Board/Admin block visible to the current user, all Coordinator blocks in that file are also shown (within-file superset rule, enforced in `GuideFilter.Apply`).
- Guide content is the 28 files in `docs/guide/`: `README`, `GettingStarted`, `Glossary`, the 19 sections enumerated in `GuideFiles.Sections`, plus the 6 plain-language pages enumerated in `GuideFiles.CommonQuestions` (rendered as the "Common questions" sidebar group). Nothing is authored in-app.
- Cache key is `guide:<FileStem>`. TTL is sliding, configured via `Guide:CacheTtlHours` (default 6 hours, floor 1 hour).
- Only `GuideContentService` reads or writes `guide:*` cache entries. No other service touches guide content.

## Negative Access Rules

- Non-Admin users **cannot** trigger `POST /Guide/Refresh`.
- Anonymous users **cannot** see Coordinator-scoped or Board/Admin-scoped blocks.
- A domain admin **cannot** see blocks for parentheticals that do not name their role.
- No user **can** author or edit guide content in-app; GitHub PR is the only authoring path.

## Triggers

- First `GET /Guide/*` on cold cache → `GuideContentService` fetches and renders all 28 files; entries populated with sliding TTL.
- `POST /Guide/Refresh` (Admin) → re-fetches and re-renders all 28 files; existing cache entries overwritten.
- GitHub fetch failure on warm cache → stale content served; warning logged; TTL preserved.
- GitHub fetch failure on cold cache → `GuideContentUnavailableException` thrown; controller returns 503 `Unavailable.cshtml`.

## Cross-Section Dependencies

- **Teams**: `GuideRoleResolver` queries `TeamMembers` directly via `HumansDbContext` to determine `IsTeamCoordinator` (`tm.Role == TeamMemberRole.Coordinator && tm.LeftAt == null`).
- **Auth/Roles**: `GuideRoleResolver` reads `ClaimsPrincipal.IsInRole` against `RoleNames` constants to build `SystemRoles` set.

## Architecture

**Owning services:** `GuideContentService` (Infrastructure), `GuideRoleResolver` (Infrastructure), `GuideRenderer` (Infrastructure), `GitHubGuideContentSource` (Infrastructure), `GuideFilter` (Application, static), `GuideRolePrivilegeMap` (Application, static)
**Owned tables:** None — orchestrator over GitHub content source + `IMemoryCache`
**Status:** (B) Partially migrated — interfaces in `Humans.Application/Interfaces/` (`IGuideContentService`, `IGuideRoleResolver`, `IGuideContentSource`, `IGuideRenderer`); implementations in `Humans.Infrastructure/Services/`; no repository (no DB tables); static utility classes (`GuideFilter`, `GuideRolePrivilegeMap`) in `Humans.Application/Services/`

#### Current violations

- **Inline `IMemoryCache` usage in service methods:**
  - `src/Humans.Infrastructure/Services/GuideContentService.cs` — `_cache.TryGetValue` / `_cache.Set` inline (§2d/§4b); no Store or decorator; acceptable given content-not-entity shape, but does not follow §15 pattern
- **Cross-section direct DbContext reads:**
  - `src/Humans.Infrastructure/Services/GuideRoleResolver.cs:57` — `_db.TeamMembers.AnyAsync(...)` reads Teams table directly instead of calling `ITeamService` or a Teams read interface

#### Touch-and-clean guidance

- When touching `GuideRoleResolver.cs`: replace `_db.TeamMembers` query with a call to a Teams read interface (e.g. `ITeamMemberReadService.IsCoordinatorAsync(userId)`) to remove the cross-section DB read.
- No architecture test file exists for Guide (`tests/Humans.Application.Tests/Architecture/GuideArchitectureTests.cs` is absent). Add one when migrating.

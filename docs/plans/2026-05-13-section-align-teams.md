# Section Align - Teams
**Run started:** 2026-05-13 | **Mode:** existing-section | **Worktree:** `H:\source\humans\.worktrees\section-align-teams`
**Branch:** `align/teams` (off `origin/main`)
**Canonical section name proposal:** Teams

## Axis 1 - Boundary integrity

### 1.1 Section name consistency - clean
Canonical name `Teams` is consistent across:
- `docs/sections/Teams.md`
- Controllers:
  - `src/Humans.Web/Controllers/TeamController.cs`
  - `src/Humans.Web/Controllers/TeamAdminController.cs`
- Views:
  - `src/Humans.Web/Views/Team/`
  - `src/Humans.Web/Views/TeamAdmin/`
- ViewModels:
  - `src/Humans.Web/Models/TeamViewModels.cs`
  - `src/Humans.Web/Models/TeamResourceViewModels.cs`
  - `src/Humans.Web/Models/TeamSyncViewModels.cs`
  - `src/Humans.Web/Models/TeamFormViewModelBase.cs`
- DI extension:
  - `src/Humans.Web/Extensions/Sections/TeamsSectionExtensions.cs`

### 1.2 Controller existence/placement - clean
Team UI and admin actions are in section-local controllers (`Team*`). No `Teams` actions were identified in unrelated controllers.

### 1.3 URL surface - mostly clean
Known routes are primarily:
- `/Teams/*`
- `/Team/Admin/*` (documented canonical path for team admin flow)

Note: runtime route currently appears as `TeamAdminController` mounted at `Teams/{slug}` in implementation; if canonical docs require `/Team/Admin/*`, route shape should be reconciled before calling this clean.

No clear section-collision was found in this pass.

### 1.4 Views folder - clean
`Team` and `TeamAdmin` view folders exist and align with controller locations.

### 1.5 ViewModel placement - clean
Team view models are section-local in `src/Humans.Web/Models`.

### 1.6 Controller-base leak - clean
No Teams-specific helpers or models were found added to `HumansControllerBase`.

### 1.7 Extensions placement - clean
`TeamsSectionExtensions` exists and contains Teams registrations in `src/Humans.Web/Extensions/Sections`.

### 1.8 Role surface - partial review
No obvious public role anomalies were found from route-level inspection.
Needs direct review against `docs/sections/Teams.md` during runtime pass.

### 1.9 / 1.10 Inbound cross-section DB access and EF navigations - action required
Direct reads/writes to Teams-owned tables from outside the section were found:
- `src/Humans.Infrastructure/Repositories/AuditLog/AuditLogRepository.cs` uses `ctx.Teams` for display names.
- `src/Humans.Infrastructure/Services/HumansMetricsService.cs` reads `db.Teams` and `db.TeamJoinRequests`.

Cross-section inbound Team entity navigation usage observed:
- `src/Humans.Application/Models/Team/...` has obsolete Team references in other sections (`OwningTeam`, `Team`, `AssignedToTeam` navs documented as obsolete).

These should be migrated to interface contracts or explicitly accepted/approved in follow-up section work.
For each follow-up, validate first that each caller needs exactly what Teams currently provides, and that caller requirements are available on the Teams API surface:
- caller field or object requirements should map to existing methods on `ITeamService`/other `Humans.Application.Interfaces.Teams` contracts
- if missing, extend Teams service contracts before removing DB-level reads

### 1.11 Outbound cross-section access - mostly clean
No obvious Teams service direct repository access outside its section was found during this pass.
Where cross-section data is needed, Teams services appear to use section interfaces (to verify further in Phase 2 if gaps remain).

### 1.12 Controller -> DbContext - clean
No Teams controllers inject `HumansDbContext` directly.

### 1.13 Migrations - clean
No hand-edited Teams migration paths were identified in this pass.

### 1.14 Section doc shape - clean
`docs/sections/Teams.md` exists and covers routing, ownership, and current workflow.

### 1.15 Operational docs/routing gaps - none identified
No explicit undocumented major routing exception was confirmed in this pass.

## Axis 2 - Internal cohesion

### 2.1 EF leakage from service layer - clean
Team service types (`TeamService`, `TeamResourceService`, `TeamPageService`) are in `src/Humans.Application/Services/Teams` and do not take direct EF `DbContext` dependencies.

### 2.2 Caching placement - clean
Caching exists in `src/Humans.Infrastructure/Services/Teams/CachingTeamService.cs` as a decorator around primary service.

### 2.3 DI lifetimes - clean
Team registrations in `TeamsSectionExtensions` and related infrastructure registrations follow existing singleton/scoped/service-decorator pattern.

### 2.4 Repository pattern - clean
Teams repositories are owned in `src/Humans.Infrastructure/Repositories/Teams`, interfaces are section-local, and implementation (`TeamRepository`) is a sealed concrete class.

### 2.5 Shared visual components - review needed
No obvious Teams-specific shared components were identified in this pass; need follow-up confirmation on future cross-page rendering surfaces.

### 2.6 Interface budget + consolidation - action required
Surface budget status for Teams public interfaces was not confirmed in this pass.
Need to review for `[SurfaceBudget]` compliance and exact budget alignment.

### 2.7 Architecture tests - partial coverage
Existing architecture tests are present:
- `tests/Humans.Application.Tests/Architecture/TeamsArchitectureTests.cs`
- `tests/Humans.Application.Tests/Architecture/TeamResourceArchitectureTests.cs`
- `tests/Humans.Application.Tests/Architecture/TeamPageArchitectureTests.cs`

Boundary coverage should be revalidated after any interface or composition changes.

## Axis 3 - Test focus

### 3.1 Test placement
Tests exist in:
- `tests/Humans.Application.Tests/Architecture/*Teams*`
- `tests/Humans.Application.Tests/Services/*Team*`
- `tests/e2e/tests/teams*.spec.ts`

### 3.2 Coverage map
Service, repository, and e2e coverage exists in targeted tests.

### 3.3 Redundancy
No immediate redundant tests were flagged in initial scan.

### 3.4 Mutation testing
Baseline from `docs/testing/mutation-testing.md`: `2139` attributes.
Teams-specific `local/stryker-runs/teams` report not confirmed; no delta recorded in this pass.

## Test-attribute gate
- Baseline from `docs/testing/mutation-testing.md`: 2139 attributes.
- Phase 0 net delta: +0 / -0 = 0.

## Stop conditions tripped

None.

## Follow-up /section-align targets

1. [`section-align-auditLog`](../../docs/plans/2026-05-12-section-align-auditLog.md)
   - Remove/replace `AuditLogRepository` direct `ctx.Teams` reads.
   - Verify `AuditLogRepository` callers only need display fields already exposed by Teams service interfaces (`team name`, `slug`, `id`, and any other referenced Team metadata).
2. [`section-align-scanner` or metrics-adjacent pass](../../docs/plans/2026-05-12-section-align-scanner.md)
   - Remove/replace `HumansMetricsService` direct `Teams`/`TeamJoinRequests` access.
   - Confirm `HumansMetricsService` can be satisfied via Teams API contracts (`ITeamService` reads needed by metrics) before changing any EF reads.
3. `[section-align-docs for section owning obsolete Team navs]`
   - Consolidate deprecated Team navigation usage in non-Team entities (`BudgetCategory.Team`, `CalendarEvent.OwningTeam`, `FeedbackReport.AssignedToTeam`) to section-owned service contracts.

## Phase plan

### Phase 1 - Surface alignment
1. [ ] Validate Teams controller/model/view locations are still canonical after latest commits.
2. [ ] Re-check all Team routes in `TeamController` and `TeamAdminController` versus `docs/sections/Teams.md`; update docs if needed.
3. [ ] Revalidate role surface and unauthorized route exposure in section admin/user pathways.

### Phase 2 - Architecture and boundary cleanup
1. [ ] Check Teams interfaces for `[SurfaceBudget]` coverage and exactness.
2. [ ] Verify all cross-section Team access is via section services/interfaces.
3. [ ] Resolve or document remaining inbound Teams table reads in non-Team sections (audit log/metrics items above).

### Phase 3 - Simplify / prune
1. [ ] Reduce/decouple remaining obsolete Team nav exposures where practical.
2. [ ] Eliminate duplicate/legacy Team-specific coupling code introduced by obsolete navs.
3. [ ] Rework obvious `Teams` boundary leakage to service contracts.

### Phase 4 - Docs
1. [ ] Confirm `docs/sections/Teams.md` includes any intentional boundary exception found above.
2. [ ] Add explicit follow-up references for `section-align` targets created in 1.10/2.6.

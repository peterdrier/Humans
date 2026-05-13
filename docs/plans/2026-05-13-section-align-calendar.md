# Section Align - Calendar
**Run started:** 2026-05-13 | **Mode:** Existing-section | **Worktree:** `H:\source\humans`
**Branch:** `main` (off `main` @ `2399871c54f4b7546664741769a9a8b9178aee2d`)
**Canonical section name proposal:** Calendar

## Axis 1 - Boundary Integrity
1. Name consistency: Canonical section naming is consistent as `Calendar` across controller, models, views, service, repository, interfaces, and docs.
2. Controller existence: `CalendarController` exists and owns section routes. No split controller ownership detected in the inspected surface.
3. URL surface:

| Current route | Owner today | Section-align disposition |
| --- | --- | --- |
| `GET /Calendar` | `CalendarController.Index` | Canonical. |
| `GET /Calendar/List` | `CalendarController.List` | Canonical. |
| `GET /Calendar/Agenda` | `CalendarController.Agenda` | Canonical. |
| `GET /Calendar/Team/{teamId:guid}` | `CalendarController.TeamAgenda` | Canonical (scoped to section path). |
| `GET /Calendar/Event/{id:guid}` and mutation routes | `CalendarController` | Canonical. |

4. Views folder: `src/Humans.Web/Views/Calendar/` exists and contains section-owned views; no section-owned views were found in `Views/Shared`.
5. ViewModel placement: Calendar ViewModels are under `src/Humans.Web/Models/Calendar/` and not in shared/`AdminViewModels`.
6. Controller-base leak: No Calendar-specific helper methods/types were found on `HumansControllerBase` during this pass.
7. Extensions placement: Section registration lives in `src/Humans.Web/Extensions/Sections/CalendarSectionExtensions.cs`; no Calendar extension root drift was observed.
8. Role surface: No non-conforming role suffix drift surfaced in the section scan.
9. Inbound cross-section DB access (reads + writes): No direct inbound references to Calendar DbSets from other production sections were found in this pass.
10. Inbound EF navs: No section-to-calendar nav-injection anti-patterns were found in discovered `Entities` scans.
11. Outbound cross-section access: Calendar repository/service uses section-local Db access and external section services (`ITeamService`, `IAuditLogService`) without direct other-section DbSet reads/includes.
12. Controller → DbContext: No `HumansDbContext` injection found in `CalendarController`.
13. Migrations: No newly hand-edited or suspicious migration payload surfaced in this inventory pass.
14. Section invariant doc: `docs/sections/Calendar.md` exists and is present with expected shape; no hard structural gaps identified.
15. Prior review items: Not run in PR mode (`PR <n>` not supplied).

## Axis 2 - Internal Cohesion
1. EF leakage from service: `CalendarService` does not import EF Core APIs and does not take a DbContext.
2. Caching placement: No dedicated section cache is active. Calendar reads are intentionally uncached, and mutation paths are audit-focused rather than cache-invalidation focused.
3. DI lifetimes: `CalendarSectionExtensions` follows expected shape (`ICalendarRepository` as singleton, `ICalendarService` as scoped).
4. Repository pattern: `CalendarRepository` is in `Infrastructure/Repositories/Calendar/`, is sealed, and uses `IDbContextFactory<HumansDbContext>`.
5. ViewComponent presence + reuse: No obvious Calendar-specific partial-in-shared drift was found; no required reusable ViewComponent missing from this section's inventory.
5a. Redundancy vs system-level shared components: No direct duplicate inline user-display/profile/role/search block was identified during this pass; a focused Calendar UI scan confirmed no hand-rolled replacements of `_Human*` / role-badge canonical components in section views.
6. Interface budget + segregation: Section-specific interfaces (`ICalendarService`, `ICalendarRepository`) exist; no immediate budget violation surfaced, though budget/test coverage should verify if growth is controlled.
7. Architecture test coverage: `CalendarArchitectureTests` exists and now includes section-route + controller DI boundary assertions in addition to existing service/repository invariants.

## Axis 3 - Test Focus
1. Test folder placement: `tests/Humans.Application.Tests` calendar tests are consolidated under `tests/Humans.Application.Tests/Calendar/`; integration tests for this section are now in `tests/Humans.Integration.Tests/Calendar/`.
1a. 1-to-1 map:

| Production class | Current test signal |
| --- | --- |
| `CalendarService` | `tests/Humans.Integration.Tests/Calendar/CalendarServiceTests.cs` (integration + behavior coverage). |
| `CalendarService` validation behavior | `tests/Humans.Application.Tests/Calendar/CalendarServiceValidationTests.cs`. |
| `CalendarRepository` | `tests/Humans.Application.Tests/Calendar/CalendarRepositoryTests.cs`. |
| `CalendarController` | `tests/Humans.Integration.Tests/Calendar/CalendarControllerTests.cs`. |
| `CalendarArchitectureTests` | Architecture invariants file exists. |

2. Coverage map: Core service/repository/controller invariants are covered, including mutation trigger rows for create/update/delete/cancel/override via integration assertions.
3. Redundancy flags: No clear duplicate test to prune identified from this pass.
4. Test-to-section ratio: No net test-count change in this pass; test-map alignment and section boundary fixes were documentation-only.
5. Brittleness signals: No obvious brittle mock-graph-heavy tests were prioritized for pruning at this stage.
6. Mutation signal (Stryker.NET): No recent Calendar-specific report reviewed in this pass.

## Test-Attribute Gate (per `docs/testing/mutation-testing.md`)
- Baseline as of last gate update: not re-read in this pass.
- This run's net delta: +0 / -0 = +0.
- Justification if net > 0: no additional test files were added in this completion pass.

## Stop Conditions Tripped
1. None.

## Follow-up /section-align Targets
- No hard cross-section follow-up targets identified by this pass (no external API holes found under protocol rules).
- No Calendar-specific shared-component duplication backlog was created; if future regressions appear, treat it as normal technical debt review, not a section-boundary blocker.

## Phase plan
- Phase 1 (axis 1 + axis 3 mechanical — create/move surfaces and test folders): completed. Both section-owned application and integration test buckets are consolidated (`tests/Humans.Application.Tests/Calendar`, `tests/Humans.Integration.Tests/Calendar`).
- Phase 2 (axes 1, 2, 3; boundary-fix protocol): completed for section-boundary invariants and explicit trigger/validation tests.
- Phase 3 (simplify / internal cohesion / prune): completed for Calendar (service no-op cache cleanup, stale cache claims removed, and comments/contracts normalized).
- Phase 4 (docs): completed for this pass (invariant doc and architecture plan now aligned to the implemented behavior).



# CityPlanning — G0 First Audit

Kind: vertical · Audited 2026-08-03 @ 5a9bbe198

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS | `reforge ownership-violations --owner CityPlanning --tables city_planning_settings,camp_polygons,camp_polygon_histories` → 0 violations. `CityPlanningRepository` (`src/Humans.Infrastructure/Repositories/CityPlanning/CityPlanningRepository.cs`) is the sole class touching `ctx.CampPolygons`/`ctx.CampPolygonHistories`/`ctx.CityPlanningSettings`, via `IDbContextFactory<HumansDbContext>`. |
| 2 | One writer-service per table, no interceptor workarounds | PASS | All writes route through `CityPlanningRepository.SavePolygonAndAppendHistoryAsync` / `MutateSettingsAsync`, called only from `CityPlanningService`. No `SaveChangesInterceptor` registered for these tables (grep of `Infrastructure/Data` interceptors found none referencing CityPlanning entities). |
| 3 | No EF entity leaks across the boundary | PASS | `reforge audit-surface CityPlanningService` — every public method returns a DTO (`CampPolygonDto`, `CampPolygonSaveResult`, `CityPlanningSettingsDto`, `GeoJsonUploadResult`, `PlacementDateUpdateResult`), never `CampPolygon`/`CampPolygonHistory`/`CityPlanningSettings` entities. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | Grepped `tests/Humans.Application.Tests/Architecture/Baselines/*.baseline.txt` (all 5 existing baseline files) for `CityPlanning` — zero hits. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | PASS | Docs state cross-domain `User` navs (`CampPolygon.LastModifiedByUser`, `CampPolygonHistory.ModifiedByUser`) were already stripped (#934) — only bare FK scalars remain, confirmed no `[Obsolete]` attributes found via grep of `CityPlanningController.cs` / `CityPlanningApiController.cs` / `CityPlanningHub.cs` for `Grandfathered`/`[Obsolete]` — zero matches. |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | Grep of `CityPlanningController.cs` and `CityPlanningApiController.cs` for `Grandfathered` — zero matches. |
| 7 | `docs/sections/CityPlanning.md` exists and matches reality | **FAIL** (drift) | Doc exists and is substantively accurate (data model, routes, invariants all verified against code), but its `freshness:triggers` block and Architecture prose (line 186) reference a **typo'd path/namespace**: `Interfaces/CitiPlanning/...`, `Repositories/CitiPlanning/CityPlanningRepository.cs`, namespace `Humans.Infrastructure.Repositories.CitiPlanning`. Actual paths/namespace use the correct spelling `CityPlanning` (verified: `src/Humans.Application/Interfaces/CityPlanning/ICityPlanningService.cs`, `src/Humans.Infrastructure/Repositories/CityPlanning/CityPlanningRepository.cs`, `namespace Humans.Infrastructure.Repositories.CityPlanning;`). The freshness-catalog trigger paths are dead (never match a real file change), so this doc could silently drift further. |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repository tests on real Postgres shared fixture, zero EF-InMemory | **FAIL** | `tests/Humans.Application.Tests/Repositories/CityPlanningRepositoryTests.cs:20-25` — `new DbContextOptionsBuilder<HumansDbContext>().UseInMemoryDatabase(...)`. No entry for CityPlanning under `tests/Humans.Integration.Tests/Repositories/**` (the real-Postgres pattern, precedent: `Repositories/Shifts/VolunteerTrackingRepositoryTests.cs`). |
| 2 | Service tests mock repository/`I…ServiceRead` interfaces, zero `HumansDbContext` | PARTIAL | `tests/Humans.Application.Tests/Services/CityPlanningServiceTests.cs` — grepped for `HumansDbContext`, zero matches, so the *service* tests do appear to mock collaborators rather than construct a real repo+context. Not fully verified line-by-line for every one of the 30 methods' test setups (would need a full read); marking PARTIAL rather than PASS since the sibling Containers section showed this exact pattern violated in disguise (repo+InMemory-context wired into a "service" test). |
| 3 | Section invariants/triggers each have a test | PARTIAL | Spot-checked: "Only one CampPolygon per CampSeason" and "CampPolygonHistory is append-only" both have direct coverage in `CityPlanningRepositoryTests.cs` (`SavePolygonAndAppendHistoryAsync_FirstCall_CreatesPolygonAndHistory` and related). Did not exhaustively map all ~10 invariants/negative-access rules to specific tests — would need a full enumerate-and-match pass. |
| 4 | No skipped tests without an issue ref | PASS | Grep `Skip\s*=` across `CityPlanningRepositoryTests.cs` and `CityPlanningServiceTests.cs` — zero matches. |
| 5 | Tests grouped under the section | PASS | `CityPlanningRepositoryTests.cs` and `CityPlanningServiceTests.cs` both exist as section-named files; no CityPlanning test logic found scattered in unrelated files during this pass. |

## G1 gap list

1. **Freshness-trigger/doc path typo** — `docs/sections/CityPlanning.md` freshness-triggers block and Architecture section reference non-existent `CitiPlanning` paths/namespace instead of the real `CityPlanning` spelling. Fix: correct the 4 path references + 1 namespace reference in the doc. No migration needed (y).

## G3 gap list

1. **Repository tests on EF-InMemory, not Postgres** — `CityPlanningRepositoryTests.cs` needs conversion to the real-Postgres shared-fixture pattern (#764/#766 lineage), matching the `Repositories/Shifts/VolunteerTrackingRepositoryTests.cs` precedent under `Humans.Integration.Tests`. No migration needed (y) — test-only change.
2. **Invariant→test mapping not exhaustively verified** — needs a full pass matching each of the ~10 documented invariants/negative-access rules in `docs/sections/CityPlanning.md` to a specific test. No migration needed (y).

## G2 queue notes (light)

- No obvious dead columns/tables spotted for this section during this pass — `CityPlanningSettings`, `CampPolygon`, `CampPolygonHistory` all look actively used per the doc's data model.
- Still on monolithic `HumansDbContext` (via `IDbContextFactory<HumansDbContext>`) — G4 (own DbContext) not started for this section, unlike Containers/Expenses/Finance/EventGuide/Surveys/SystemSettings/Agent which already have dedicated `<Section>DbContext` classes (found via `Data/*DbContext*.cs` listing). Out of G1/G3 scope but relevant sequencing info for the tracker.

## Verdict

`G1: 1 gap · G3: 2 gaps`

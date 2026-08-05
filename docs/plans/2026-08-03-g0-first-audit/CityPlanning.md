# CityPlanning — G0 First Audit

Kind: vertical · Audited 2026-08-03 @ 5a9bbe198

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS | `reforge ownership-violations --owner CityPlanning --tables city_planning_settings,camp_polygons,camp_polygon_histories` → 0 violations. `CityPlanningRepository` (`src/Humans.Infrastructure/Repositories/CityPlanning/CityPlanningRepository.cs`) is the sole class touching `ctx.CampPolygons`/`ctx.CampPolygonHistories`/`ctx.CityPlanningSettings`, via `IDbContextFactory<HumansDbContext>`. |
| 2 | One writer-service per table, no interceptor workarounds | PASS | All writes route through `CityPlanningRepository.SavePolygonAndAppendHistoryAsync` / `MutateSettingsAsync`, called only from `CityPlanningService`. No `SaveChangesInterceptor` registered for these tables (grep of `Infrastructure/Data` interceptors found none referencing CityPlanning entities). |
| 3 | No EF entity leaks across the boundary | PASS | `reforge audit-surface CityPlanningService` — every public method returns a DTO (`CampPolygonDto`, `CampPolygonSaveResult`, `CityPlanningSettingsDto`, `GeoJsonUploadResult`, `PlacementDateUpdateResult`), never `CampPolygon`/`CampPolygonHistory`/`CityPlanningSettings` entities. |
| 4 | No cross-section EF joins (zero baseline entries) | **FAIL — corrected 2026-08-03** | The zero baseline-file hits are correct but prove nothing: HUM0024 is **attribute**-allowlisted, not baseline-file-based, so grepping `Baselines/*.txt` cannot establish a pass. `CampPolygonConfiguration.cs` and `CampPolygonHistoryConfiguration.cs` both carry active `[Grandfathered("HUM0024", …)]` markers over cross-section relationships — `HasOne(p => p.CampSeason)` → Camps, and `HasOne<User>()` on `LastModifiedByUserId`/`ModifiedByUserId` → Users. The demolition inventory in this same commit records the same relationships. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | **FAIL — corrected 2026-08-03** | The nav half holds: cross-domain `User` navs (`CampPolygon.LastModifiedByUser`, `CampPolygonHistory.ModifiedByUser`) were stripped (#934), only bare FK scalars remain. But the original grep covered only `CityPlanningController.cs`/`CityPlanningApiController.cs`/`CityPlanningHub.cs` — not `Infrastructure/Data/Configurations/`, where both configs carry active `[Grandfathered("HUM0024", …)]` attributes (see predicate 4). Same blind spot as the original Camps and Auth passes. |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | Grep of `CityPlanningController.cs` and `CityPlanningApiController.cs` for `Grandfathered` — zero matches. |
| 7 | `docs/sections/CityPlanning.md` exists and matches reality | **FAIL** (drift) | Doc exists and is substantively accurate (data model, routes, invariants all verified against code), but its `freshness:triggers` block and Architecture prose (line 186) reference a **typo'd path/namespace**: `Interfaces/CitiPlanning/...`, `Repositories/CitiPlanning/CityPlanningRepository.cs`, namespace `Humans.Infrastructure.Repositories.CitiPlanning`. Actual paths/namespace use the correct spelling `CityPlanning` (verified: `src/Humans.Application/Interfaces/CityPlanning/ICityPlanningService.cs`, `src/Humans.Infrastructure/Repositories/CityPlanning/CityPlanningRepository.cs`, `namespace Humans.Infrastructure.Repositories.CityPlanning;`). The freshness-catalog trigger paths are dead (never match a real file change), so this doc could silently drift further. |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repository tests on real Postgres shared fixture, zero EF-InMemory | **FAIL** | `tests/Humans.Application.Tests/Repositories/CityPlanningRepositoryTests.cs:20-25` — `new DbContextOptionsBuilder<HumansDbContext>().UseInMemoryDatabase(...)`. No entry for CityPlanning under `tests/Humans.Integration.Tests/Repositories/**` (the real-Postgres pattern, precedent: `Repositories/Shifts/VolunteerTrackingRepositoryTests.cs`). |
| 2 | Service tests mock repository/`I…ServiceRead` interfaces, zero `HumansDbContext` | **FAIL — corrected 2026-08-03** | Grepping these files for the literal `HumansDbContext` is a false negative: they inherit their EF setup. `CityPlanningServiceTests` extends `ServiceTestHarness` and constructs a concrete repository — the PARTIAL hedge was right to suspect "the same pattern violated in disguise", and this is it. `ServiceTestHarness` (`tests/Humans.Application.Tests/Infrastructure/ServiceTestHarness.cs:54,71`) builds a real `HumansDbContext` over `.UseInMemoryDatabase(...)` and exposes it as `Db`/`DbFactory`, so a harness-derived test constructing a concrete repository is exactly the EF-InMemory service test this predicate forbids. Same false-negative pattern corrected across Auth, Budget, Camps, CityPlanning, Feedback, Governance and Consent in this pass. |
| 3 | Section invariants/triggers each have a test | PARTIAL | Spot-checked: "Only one CampPolygon per CampSeason" and "CampPolygonHistory is append-only" both have direct coverage in `CityPlanningRepositoryTests.cs` (`SavePolygonAndAppendHistoryAsync_FirstCall_CreatesPolygonAndHistory` and related). Did not exhaustively map all ~10 invariants/negative-access rules to specific tests — would need a full enumerate-and-match pass. |
| 4 | No skipped tests without an issue ref | PASS | Grep `Skip\s*=` across `CityPlanningRepositoryTests.cs` and `CityPlanningServiceTests.cs` — zero matches. |
| 5 | Tests grouped under the section | PASS | `CityPlanningRepositoryTests.cs` and `CityPlanningServiceTests.cs` both exist as section-named files; no CityPlanning test logic found scattered in unrelated files during this pass. |

## G1 gap list

1. **Freshness-trigger/doc path typo** — `docs/sections/CityPlanning.md` freshness-triggers block and Architecture section reference non-existent `CitiPlanning` paths/namespace instead of the real `CityPlanning` spelling. Fix: correct the 4 path references + 1 namespace reference in the doc. No migration needed (y).
2. **Added 2026-08-03: 2 HUM0024 cross-section EF join grandfathers** (`CampPolygonConfiguration.cs`, `CampPolygonHistoryConfiguration.cs`) covering the CampSeason → Camps and `LastModifiedByUserId`/`ModifiedByUserId` → Users relationships (see predicates 4 and 5). Tracked only to a generic doc anchor, no specific issue. Fix: verify liveness and either retire the attributes or file a tracking issue; the physical FK cuts are schema-queue work. No migration needed: **y** (pending verification).

## G3 gap list

1. **Repository tests on EF-InMemory, not Postgres** — `CityPlanningRepositoryTests.cs` needs conversion to the real-Postgres shared-fixture pattern (#764/#766 lineage), matching the `Repositories/Shifts/VolunteerTrackingRepositoryTests.cs` precedent under `Humans.Integration.Tests`. No migration needed (y) — test-only change.
2. **Invariant→test mapping not exhaustively verified** — needs a full pass matching each of the ~10 documented invariants/negative-access rules in `docs/sections/CityPlanning.md` to a specific test. No migration needed (y).


**Added 2026-08-03 — harness-inherited EF-InMemory (G3.2).** `CityPlanningServiceTests` extend `ServiceTestHarness`, which stands up a real `HumansDbContext` over `.UseInMemoryDatabase(...)`; the original pass missed this because it grepped for a literal `HumansDbContext` the files never name. Fix: convert to `Substitute.For<ICityPlanningRepository>()` per #766, or move these off the harness. No-migration-needed: **y**.

## Schema demolition queue (light)

- No obvious dead columns/tables spotted for this section during this pass — `CityPlanningSettings`, `CampPolygon`, `CampPolygonHistory` all look actively used per the doc's data model.
- Still on monolithic `HumansDbContext` (via `IDbContextFactory<HumansDbContext>`) — G4 (own DbContext) not started for this section, unlike Containers/Expenses/Finance/EventGuide/Surveys/SystemSettings/Agent which already have dedicated `<Section>DbContext` classes (found via `Data/*DbContext*.cs` listing). Out of G1/G3 scope but relevant sequencing info for the tracker.


**Added 2026-08-03 — cross-section FK cuts belong in this queue.** Retiring `[Obsolete]` navs or `[Grandfathered(HUM0024)]` markers is a code-shape change; it does **not** drop the physical constraint. Per the demolition inventory, this section owns **4** cross-section FKs across 2 tables: `camp_polygons` and `camp_polygon_histories` → `camp_seasons` (Camps) and `AspNetUsers` (Users), via `CampPolygonConfiguration.cs:24,29` and `CampPolygonHistoryConfiguration.cs:24,29`. All are cross-section FK cuts — without them listed here, a schema batch driven by this scorecard can complete while every cross-section database dependency survives.

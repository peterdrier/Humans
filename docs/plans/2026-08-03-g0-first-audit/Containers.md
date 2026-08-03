# Containers — G0 First Audit

Kind: vertical · Audited 2026-08-03 @ 5a9bbe198

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | PASS | `reforge ownership-violations --owner Containers --tables containers,container_placements` → 0 violations. `ContainerRepository` (`src/Humans.Infrastructure/Repositories/Containers/ContainerRepository.cs`) is the sole class touching `ctx.Containers`/`ctx.ContainerPlacements`, via `IDbContextFactory<ContainersDbContext>` — this section **already has its own dedicated DbContext** (`src/Humans.Infrastructure/Data/ContainersDbContext.cs` + `ContainersDbContextFactory.cs`), ahead of most sections in the G4 ladder. |
| 2 | One writer-service per table, no interceptor workarounds | PASS | All writes route through `ContainerRepository`, called only from `ContainerService`. No interceptor found for `containers`/`container_placements`. |
| 3 | No EF entity leaks across the boundary | PASS | `reforge audit-surface ContainerService` — every public method returns `ContainerDto`/`ContainerPlacementDto`/`ContainerAdminOverview`, never the `Container`/`ContainerPlacement` entities. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | Grepped all 5 baseline files under `tests/Humans.Application.Tests/Architecture/Baselines/` for `Container` — zero hits. Doc confirms bare-Guid FKs (`Container.CampId`, `ContainerPlacement.ContainerId`) with no EF nav, per `memory/architecture/no-cross-section-ef-joins.md`. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | PASS | Grep of `ContainerController.cs` for `Grandfathered`/`[Obsolete]` — zero matches. Doc confirms no EF navs cross-section at all (bare Guid pattern from day one). |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | Grep of `ContainerController.cs` for `Grandfathered` — zero matches. |
| 7 | `docs/sections/Containers.md` exists and matches reality | PASS | Doc's data model (Container/ContainerPlacement columns, composite PK, indexes), architecture section (`ContainerService`, `IContainerRepository`/`ContainerRepository` in `Humans.Infrastructure.Repositories.Containers`), and cross-section dependency list (Camps via `ICampServiceRead`, CityPlanning via `ICityPlanningServiceRead`) all verified against actual code — paths and namespaces match exactly (no typo drift, unlike CityPlanning's sibling doc). |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repository tests on real Postgres shared fixture, zero EF-InMemory | **FAIL** | No dedicated `ContainerRepositoryTests.cs` exists anywhere. The only exercise of `ContainerRepository` is inside `tests/Humans.Application.Tests/Services/Containers/ContainerImageServiceTests.cs` and `ContainerPlacementServiceTests.cs`, both of which build `ContainersDbContext` via `NewSectionDbOptions<ContainersDbContext>()` → `ServiceTestHarness.cs:71` → `.UseInMemoryDatabase(...)`. No entry for Containers under `tests/Humans.Integration.Tests/Repositories/**` (the real-Postgres pattern precedent: `Repositories/Shifts/VolunteerTrackingRepositoryTests.cs`). |
| 2 | Service tests mock repository interfaces, zero `HumansDbContext`/DbContext | **FAIL** | `ContainerImageServiceTests.cs:23-35` and `ContainerPlacementServiceTests.cs:23-49` construct a **real** `ContainerRepository` wired to a real (in-memory) `ContainersDbContext`, then instantiate `ContainerService` with that real repo — not a mock of `IContainerRepository`. This conflates service-layer and repository-layer testing and is the same underlying InMemory-DbContext issue as predicate 1, just surfaced from the other direction. |
| 3 | Section invariants/triggers each have a test | PARTIAL | Spot-checked: image-replace-on-upload and placement upsert/clear triggers have direct test coverage (`CreateAsync_WithMainImage_SavesUnderContainersPrefix`, placement save/clear tests referenced in `reforge audit-surface` output). Did not exhaustively map every documented invariant (e.g. cascade-delete of placements, orphaned-image-file documented limitation) to a specific test. |
| 4 | No skipped tests without an issue ref | PASS | Grep `Skip\s*=` across both `Services/Containers/*.cs` test files — zero matches. |
| 5 | Tests grouped under the section | PASS | Both test files live under `tests/Humans.Application.Tests/Services/Containers/`. |

## G1 gap list

None — Containers is the cleanest of this batch on G1; ahead of most sections by already having its own `ContainersDbContext` (a G4 item, out of scope here but worth flagging to the tracker).

## G3 gap list

1. **No true repository-layer test, and the closest substitute uses EF-InMemory** — `ContainerImageServiceTests.cs`/`ContainerPlacementServiceTests.cs` should be split: (a) a proper `ContainerRepositoryTests.cs` under the real-Postgres integration pattern, and (b) service tests rewritten to mock `IContainerRepository` (e.g. via `NSubstitute`, already a project dependency per the `using NSubstitute;` import seen in `ContainerImageServiceTests.cs`) instead of standing up a real DbContext. No migration needed (y) — test-only change.
2. **Invariant→test mapping not exhaustively verified** — needs a full pass against `docs/sections/Containers.md` Invariants/Negative-Access-Rules/Triggers sections. No migration needed (y).

## G2 queue notes (light)

- Doc explicitly documents an accepted limitation (orphaned placement-image files on delete) rather than a bug — not a demolition candidate, just a known tradeoff at current scale.
- Already has its own `ContainersDbContext` — a head start on G4 once G1/G3 close.

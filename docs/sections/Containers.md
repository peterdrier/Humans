<!-- freshness:triggers
  src/Humans.Application/Services/Containers/**
  src/Humans.Domain/Entities/Container.cs
  src/Humans.Infrastructure/Data/Configurations/Containers/**
  src/Humans.Infrastructure/Repositories/Containers/ContainerRepository.cs
  src/Humans.Web/Controllers/ContainerController.cs
  src/Humans.Web/Authorization/Requirements/ContainerAuthorizationHandler.cs
  src/Humans.Web/Authorization/Requirements/ContainerOperationRequirement.cs
-->
<!-- freshness:flag-on-change
  Container CRUD authorization (lead vs CampAdmin vs city-planning team), placement phase gating, image storage path, and org-level vs barrio distinction — review when Container services/entities/controllers/auth handlers change.
-->

# Containers — Section Invariants

Physical shipping containers managed per-barrio or at org level, placed on the City Planning map.

## Concepts

- A **Container** is a single shipping container tracked by name, optional description, optional image, and a year. It may be **barrio-scoped** (owned by a specific CampSeason) or **org-level** (not tied to any barrio; `CampSeasonId == null`).
- **Barrio container**: `CampSeasonId` is set. Managed by the owning camp's leads and by Map Admins.
- **Org-level container**: `CampSeasonId` is null. Managed exclusively by Map Admins.
- **Container placement** is the act of positioning a container on the City Planning map by storing a GeoJSON Feature in `LocationGeoJson`. Placement is separate from creation and is gated by `CityPlanningSettings.IsContainerPlacementOpen` for non-admins.
- **Container placement phase** is the toggle (`IsContainerPlacementOpen` on `CityPlanningSettings`) that controls whether barrio leads can place containers. Map Admins (CampAdmin role or city-planning team) are never gated.

## Data Model

### Container

**Table:** `containers`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| CampSeasonId | Guid? | FK → `camp_seasons.Id` (nullable; null = org-level); `OnDelete(Cascade)` — **FK only**, nav declared but not read by Containers code (design-rules §15i) |
| Year | int | Season year; denormalised from CampSeason.Year for barrio containers; set directly for org-level |
| Name | string | max 256; required |
| Description | string? | max 2000 |
| ImageStoragePath | string? | max 512; relative path from `wwwroot/` |
| ImageContentType | string? | max 64 |
| ImageFileName | string? | max 256; original upload filename |
| LocationGeoJson | text? | GeoJSON Feature; null = unplaced |
| CreatedAt | Instant | Set on create |
| UpdatedAt | Instant | Updated on every write |

**Indexes:** `IX_containers_CampSeasonId`, `IX_containers_Year`

**Cross-section FKs:** `CampSeasonId` → `camp_seasons.Id` (Camps section) — **FK only**, no navigation property read by this section.

**SortOrder removed:** the initial migration added `SortOrder`; migration `20260426140854_RemoveContainerSortOrder` dropped it. Containers are ordered by `Name` in all views and queries.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View containers on the map overview (`/CityPlanning/`) |
| Camp lead (own camp, placement phase open) | Create, edit, delete, and place their own barrio's containers; upload/delete images |
| CampAdmin role | All camp lead capabilities on all barrio containers. All org-level container management. Placement phase toggle |
| City-planning team member (team slug: `city-planning`) | Same as CampAdmin on containers |

## Invariants

- A container with `CampSeasonId == null` is org-level; only Map Admins (CampAdmin or city-planning team) may create, edit, delete, or manage images for it.
- A container with `CampSeasonId` set belongs to a specific season; the owning camp's leads and Map Admins may manage it.
- Write access for barrio leads is gated by `CityPlanningSettings.IsContainerPlacementOpen`. Map Admins are never gated (enforced in `ContainerController.CheckPlacementPhaseAsync` and `CityPlanningApiController`).
- Image storage uses `IContainerImageStorage`; files are written to `wwwroot/uploads/containers/{containerId}/`. One image per container; uploading a second image replaces the first.
- Resource-based authorization per design-rules §11: `ContainerAuthorizationHandler` + `ContainerOperationRequirement` gate barrio container writes. Org-level container writes in `CityPlanningController` use inline `IsMapAdminAsync` checks (no resource object needed — org containers have no camp owner to verify).
- `CampSeason.CampSeason` nav is declared on `Container` but is never read by this section's code (design-rules §15i).

## Negative Access Rules

- Barrio leads **cannot** manage org-level containers (`CampSeasonId == null`).
- Barrio leads **cannot** manage another barrio's containers — `ContainerAuthorizationHandler` verifies `IsUserCampLeadAsync` for the container's `CampSeasonId`.
- Barrio leads **cannot** create, edit, delete, or place containers when the placement phase is closed (`IsContainerPlacementOpen == false`).
- Non-admins **cannot** toggle the placement phase open or closed.
- Non-admins **cannot** access `/CityPlanning/ContainerMap/{year}` when the placement phase is closed (controller returns 403 for non-admins who are not barrio leads or when the phase is closed).
- Regular authenticated humans **cannot** write any container data — `ContainerController` requires camp lead status or Map Admin role.

## Triggers

- When a container image is uploaded, the previous image file is deleted from disk before writing the new one; `ImageStoragePath`, `ImageContentType`, and `ImageFileName` are updated. Storage path pattern: `uploads/containers/{containerId}/{guid}.{ext}`.
- When a container image is deleted, the image file is removed from disk and the three image fields are set to null.
- When a container is deleted (`DeleteAsync`), its image (if any) is removed from disk as part of `IContainerService.DeleteAsync`.
- Placement save updates `LocationGeoJson` and `UpdatedAt` only; no other side effects.
- Placement clear sets `LocationGeoJson` to null.

## Cross-Section Dependencies

- **Camps:** `ICampService` — `GetCampBySlugAsync`, `IsUserCampLeadAsync`, `GetCampSeasonInfoAsync`, `GetCampLeadSeasonIdForYearAsync`, `GetCampSeasonBriefsForYearAsync`, `GetCampSeasonDisplayDataForYearAsync` — camp/season lookups and lead verification for authorization. FK to `camp_seasons.Id`.
- **City Planning:** `ICityPlanningService` — `GetSettingsAsync` (placement phase gate), `IsCityPlanningTeamMemberAsync` (Map Admin check). The container placement API endpoints (`PUT/DELETE /api/city-planning/containers/{id}/placement`, `GET /api/city-planning/containers/{year}`) are hosted in `CityPlanningApiController` because the placement editing experience is a City Planning concern.

## Architecture

**Owning services:** `ContainerService` (`Humans.Application.Services.Containers`)
**Owned tables:** `containers`
**Status:** (A) Migrated — introduced in (A) shape from day one per PR peterdrier/Humans#389 (2026-04-26). New sections must be (A) per design-rules §15h(1).

- `ContainerService` lives in `Humans.Application.Services.Containers` and never imports `Microsoft.EntityFrameworkCore` — enforced structurally by `Humans.Application.csproj`'s reference graph.
- `IContainerRepository` / `ContainerRepository` (`Humans.Infrastructure.Repositories.Containers`) is the only code path that touches the `containers` table via `DbContext`.
- `IContainerImageStorage` / `ContainerImageStorage` (Application interface + Infrastructure impl) handles filesystem writes, rooted at `wwwroot/`. Follows the same pattern as `CampImageStorage`.
- **Decorator decision — no caching decorator.** Small dataset (containers per season), admin/lead facing, low write frequency. Plain pass-through. Add `IMemoryCache` later if read patterns dominate.
- **Cross-domain navs:** `Container.CampSeason` is declared on the entity but never read by Containers code. Target for the cross-cutting nav strip per §15i.
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/` — no dedicated `ContainersArchitectureTests.cs` at time of initial PR; follow-up to add per design-rules §15.
- DI bundle: `ContainersSectionExtensions.AddContainersSection` (Web layer) registers `IContainerRepository` (Singleton), `IContainerImageStorage` (Singleton), `IContainerService` (Scoped).

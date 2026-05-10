<!-- freshness:triggers
  src/Humans.Application/Services/Containers/**
  src/Humans.Domain/Entities/Container.cs
  src/Humans.Domain/Entities/ContainerPlacement.cs
  src/Humans.Domain/Constants/SystemCampIds.cs
  src/Humans.Infrastructure/Data/Configurations/Containers/**
  src/Humans.Infrastructure/Repositories/Containers/ContainerRepository.cs
  src/Humans.Web/Controllers/ContainerController.cs
  src/Humans.Web/Authorization/Requirements/ContainerAuthorizationHandler.cs
  src/Humans.Web/Authorization/Requirements/ContainerOperationRequirement.cs
-->
<!-- freshness:flag-on-change
  Container CRUD authorization (lead vs CampAdmin vs city-planning team), placement phase gating, image storage path, and org-level vs barrio distinction (Organization camp) — review when Container services/entities/controllers/auth handlers change.
-->

# Containers — Section Invariants

Physical shipping containers managed per-barrio or at org level, placed on the City Planning map.

## Concepts

- A **Container** is a physical asset owned by a `Camp` (`CampId` non-null). Containers persist year-over-year — they are NOT scoped to a season.
- A **ContainerPlacement** is a per-year placement of a container on the city map. Composite primary key on `(ContainerId, Year)`. Placement-only metadata (notes, placement image) lives here, since it varies year over year.
- The **Organization camp** is a real `Camp` row with the well-known id `SystemCampIds.Organization` (`00000000-0000-0000-0011-000000000001`). It exists so `Container.CampId` can be non-nullable: org-level containers are owned by this camp. The row is created manually in production (no seed migration) and persists across years.
- **Barrio container**: `CampId != SystemCampIds.Organization`. Managed by the owning camp's leads and by Map Admins.
- **Org-level container**: `CampId == SystemCampIds.Organization`. Managed exclusively by Map Admins.
- **Container placement** is the act of positioning a container on the City Planning map for a specific year by upserting a `ContainerPlacement` row with a GeoJSON Feature in `LocationGeoJson`. Placement is gated by `CityPlanningSettings.IsContainerPlacementOpen` for non-admins.
- **Container placement phase** is the toggle (`IsContainerPlacementOpen` on `CityPlanningSettings`) that controls whether barrio leads can place containers. Map Admins (CampAdmin role or city-planning team) are never gated.

## Data Model

### Container

**Table:** `containers`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| CampId | Guid | **Non-null**. Bare FK (no nav) — Camp lives in a different section (no-cross-section-ef-joins). Org-level containers use `SystemCampIds.Organization`. |
| Name | string | max 256; required |
| Description | string? | max 2000 |
| ImageStoragePath | string? | max 512; relative path from `wwwroot/` |
| ImageContentType | string? | max 64 |
| ImageFileName | string? | max 256 |
| CreatedAt | Instant | Set on create |
| UpdatedAt | Instant | Updated on every write |

**Indexes:** `IX_containers_CampId`

### ContainerPlacement

**Table:** `container_placements`

| Property | Type | Notes |
|----------|------|-------|
| ContainerId | Guid | Composite PK part 1; bare FK to `containers.Id` (no FK constraint at DB layer either; cleanup is application-driven on container delete) |
| Year | int | Composite PK part 2 |
| LocationGeoJson | text? | GeoJSON Feature; null = unplaced (row may still exist if notes/image present) |
| PlacementNotes | string? | max 5000 |
| PlacementImageStoragePath | string? | max 512 |
| PlacementImageContentType | string? | max 64 |
| PlacementImageFileName | string? | max 256 |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Indexes:** `IX_container_placements_Year`

**Cross-section FKs:** none with EF navs. `Container.CampId` and `ContainerPlacement.ContainerId` are bare Guids per `memory/architecture/no-cross-section-ef-joins.md`. There is no DB FK on `ContainerPlacement.ContainerId` — `ContainerService.DeleteAsync` cascades placement deletion explicitly via `ContainerRepository.DeleteAsync`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View containers on the map overview (`/CityPlanning/`) |
| Camp lead (own camp, placement phase open) | Create, edit, delete containers for their camp; place / clear / annotate placements for their camp's containers |
| CampAdmin role | All camp lead capabilities on all barrio containers. All org-level container management. Placement phase toggle |
| City-planning team member (team slug: `city-planning`) | Same as CampAdmin on containers |

## Invariants

- A container with `CampId == SystemCampIds.Organization` is org-level; only Map Admins (CampAdmin or city-planning team) may create, edit, delete, or place it.
- A container with any other `CampId` belongs to that camp; the owning camp's leads and Map Admins may manage it.
- Write access for barrio leads is gated by `CityPlanningSettings.IsContainerPlacementOpen`. Map Admins are never gated.
- `Container` is year-agnostic; `ContainerPlacement` carries the year. There is no `Year` column on `containers`.
- Image storage uses `IContainerImageStorage`; container main images live at `wwwroot/uploads/containers/{containerId}/main-{guid}.{ext}`; placement images at `wwwroot/uploads/containers/{containerId}/placement-{guid}.{ext}`. Uploading a new image of a given kind deletes the prior file of that kind only.
- Resource-based authorization per design-rules §11: `ContainerAuthorizationHandler` + `ContainerOperationRequirement` gate barrio container writes. Org-level container writes in `CityPlanningController` use inline `IsMapAdminAsync` checks (the resource handler also denies non-admins for org containers via the `CampId != SystemCampIds.Organization` branch).
- Deleting a container deletes all its `ContainerPlacement` rows in the same transaction.
- Documented limitation: when a container is deleted, placement-image files on disk for years other than the deleted-row scan window may be orphaned. At ~500-user scale this is acceptable; a periodic disk sweep can reclaim space.

## Negative Access Rules

- Barrio leads **cannot** manage org-level containers (`CampId == SystemCampIds.Organization`).
- Barrio leads **cannot** manage another barrio's containers — `ContainerAuthorizationHandler` verifies `IsUserCampLeadAsync` for the container's `CampId`.
- Barrio leads **cannot** create, edit, delete, or place containers when the placement phase is closed (`IsContainerPlacementOpen == false`).
- Non-admins **cannot** toggle the placement phase open or closed.
- Non-admins **cannot** access `/CityPlanning/ContainerMap/{year}` when the placement phase is closed (controller returns 403 for non-admins who are not barrio leads or when the phase is closed).
- Regular authenticated humans **cannot** write any container data.

## Triggers

- When a Main image is uploaded during Create/Edit, the previous Main file is deleted from disk before writing the new one.
- When a Main image is removed via the "Remove image" checkbox, the file is deleted from disk and the corresponding three fields are set to null.
- When a container is deleted, the Main image (if any) is removed from disk; all `ContainerPlacement` rows for that container are removed in the same transaction.
- Placement save (`SavePlacementAsync(containerId, year, geoJson)`) upserts a `ContainerPlacement` row, preserving any existing notes/image.
- Placement clear (`ClearPlacementAsync(containerId, year)`): if notes/image are absent, the row is deleted; otherwise `LocationGeoJson` is set to null and the row is preserved.
- Placement metadata upsert (`UpsertPlacementMetadataAsync`) handles notes + placement image upload/removal independently of the geoJson location.

## Cross-Section Dependencies

- **Camps:** `ICampService` — `GetCampBySlugAsync`, `IsUserCampLeadAsync`, `GetCampDisplayDataForYearAsync`, `GetCampLeadCampIdForYearAsync` — camp lookups and lead verification for authorization. `Container.CampId` is a bare Guid pointing at `camps.Id`.
- **City Planning:** `ICityPlanningService` — `GetSettingsAsync` (placement phase gate), `IsCityPlanningTeamMemberAsync` (Map Admin check). The container placement API endpoints (`PUT/DELETE /api/city-planning/containers/{id}/placement/{year}`, `GET /api/city-planning/containers/{year}`) are hosted in `CityPlanningApiController` because the placement editing experience is a City Planning concern.

## Architecture

**Owning services:** `ContainerService` (`Humans.Application.Services.Containers`)
**Owned tables:** `containers`, `container_placements`
**Status:** (A) Migrated — introduced in (A) shape from day one per PR peterdrier/Humans#389 (2026-04-26), reshaped pre-merge to the `Container` + `ContainerPlacement` split with the `SystemCampIds.Organization` virtual camp (2026-05-10). New sections must be (A) per design-rules §15h(1).

- `ContainerService` lives in `Humans.Application.Services.Containers` and never imports `Microsoft.EntityFrameworkCore`.
- `IContainerRepository` / `ContainerRepository` (`Humans.Infrastructure.Repositories.Containers`) is the only code path that touches `containers` and `container_placements` via `DbContext`.
- `IContainerImageStorage` / `ContainerImageStorage` (Application interface + Infrastructure impl) handles filesystem writes, rooted at `wwwroot/`.
- **Decorator decision — no caching decorator.** Small dataset, admin/lead facing, low write frequency.
- DI bundle: `ContainersSectionExtensions.AddContainersSection` (Web layer) registers `IContainerRepository` (Singleton), `IContainerImageStorage` (Singleton), `IContainerService` (Scoped).

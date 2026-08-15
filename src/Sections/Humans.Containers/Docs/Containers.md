<!-- freshness:triggers
  src/Sections/Humans.Containers/**
-->
<!-- freshness:flag-on-change
  Container CRUD authorization (lead vs CampAdmin vs city-planning team), placement phase gating, and image storage path — review when Container services/entities/controllers/auth handlers change.
-->

# Containers — Section Invariants

Physical shipping containers managed per-barrio or at org level, placed on the City Planning map.

## Concepts

- A **Container** is a physical asset owned by a `Camp` (`CampId` non-null). Containers persist year-over-year — they are NOT scoped to a season. Every container belongs to a real camp; there is no system-managed or virtual camp.
- **Org-level containers** (not tied to any barrio) are owned by an ordinary, production-created camp like any other; admin-only access falls out naturally because that camp has no assigned leads. (Rejected alternative: a virtual `SystemCampIds.Organization` sentinel camp — implemented and removed pre-merge 2026-05-14 because it forced special-case branches in the auth handlers.)
- A **ContainerPlacement** is a per-year placement of a container on the city map. Composite primary key on `(ContainerId, Year)`. Placement-only metadata (notes, placement image) lives here, since it varies year over year.
- **Container placement** is the act of positioning a container on the City Planning map for a specific year by upserting a `ContainerPlacement` row with a GeoJSON Feature in `LocationGeoJson`. Placement is gated by `CityPlanningSettings.IsContainerPlacementOpen` for non-admins.
- **Container placement phase** is the toggle (`IsContainerPlacementOpen` on `CityPlanningSettings`) that controls whether barrio leads can place containers. Map Admins (CampAdmin role or city-planning team) are never gated.

## Data Model

### Container

**Table:** `containers`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| CampId | Guid | **Non-null**. Bare FK (no nav) — Camp lives in a different section (no-cross-section-ef-joins). |
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

**Cross-section FKs:** none with EF navs. `Container.CampId` and `ContainerPlacement.ContainerId` are bare Guids per `memory/architecture/no-cross-section-ef-joins.md`. There is no DB FK on `ContainerPlacement.ContainerId` — `Service.DeleteAsync` cascades placement deletion explicitly via `Repository.DeleteAsync`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View containers on the map overview (`/CityPlanning/`) |
| Camp lead (own camp, placement phase open) | Create, edit, delete containers for their camp; place / clear / annotate placements for their camp's containers |
| CampAdmin role | All camp lead capabilities on every camp's containers. Placement phase toggle |
| City-planning team member (team slug: `city-planning`) | Same as CampAdmin on containers |

## Invariants

- A container belongs to its `CampId`; the owning camp's leads and Map Admins (CampAdmin or city-planning team) may manage it.
- Write access for barrio leads is gated by `CityPlanningSettings.IsContainerPlacementOpen`. Map Admins are never gated.
- `Container` is year-agnostic; `ContainerPlacement` carries the year. There is no `Year` column on `containers`.
- `ContainerPlacement.LocationGeoJson` stores a GeoJSON Feature whose Polygon is the container footprint (20 ft container: 6 m × 2.4 m body plus a door triangle whose tip marks the door bearing — a 7-vertex ring built client-side with Turf.js, tip at vertex index 4) and whose `properties` must carry `center_lng`, `center_lat`, `rotation_degrees` (presence enforced server-side by `CityPlanningApiController` on placement PUT). Rotation deliberately lives inside the Feature — there is no separate rotation column — so the client rebuilds the drag/rotate handle state without re-deriving orientation from vertex geometry.
- Image storage uses the shared `IFileStorage`; both main and placement images are saved by the same `SaveImageAsync` helper under `wwwroot/uploads/containers/{containerId}/{guid}.{ext}` — the two kinds are distinguished by which entity field holds the key (`Container.ImageStoragePath` vs `ContainerPlacement.PlacementImageStoragePath`), not by a filename prefix. Uploading a new image of a given kind deletes the prior file of that kind only.
- Resource-based authorization per design-rules §11: `ContainerAuthorizationHandler` + `ContainerOperationRequirement` gate container writes (lead branch derives lead status via LINQ over `ICampServiceRead.GetCampsForYearAsync`, matching the resource's `CampId` and a `Season.IsLead(userId)` for the settings year).
- Deleting a container deletes all its `ContainerPlacement` rows in the same transaction.
- Documented limitation: when a container is deleted, placement-image files on disk for years other than the deleted-row scan window may be orphaned. At ~500-user scale this is acceptable; a periodic disk sweep can reclaim space.

## Negative Access Rules

- Camp leads **cannot** manage another camp's containers — `ContainerAuthorizationHandler` verifies the user leads a season of the container's `CampId` (via `ICampServiceRead.GetCampsForYearAsync` + `Season.IsLead`).
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

## Cross-Section Dependencies

- **Camps:** `ICampServiceRead` — `GetCampBySlugAsync`, `GetCampsForYearAsync`, `GetCampsWithLeadsForYearAsync` — camp lookups and lead verification for authorization (lead status derived via LINQ over `GetCampsForYearAsync` + `Season.IsLead`). `Container.CampId` is a bare Guid pointing at `camps.Id`.
- **City Planning:** `ICityPlanningServiceRead` — `GetSettingsAsync` (placement phase gate), `IsCityPlanningTeamMemberAsync` (Map Admin check). The container placement API endpoints (`PUT/DELETE /api/city-planning/containers/{id}/placement/{year}`, `GET /api/city-planning/containers/{year}`) are hosted in `CityPlanningApiController` because the placement editing experience is a City Planning concern.

## Architecture

**Owning services:** `Service` (`Humans.Containers.Services`)
**Owned tables:** `containers`, `container_placements`
**Status:** (A) Migrated — introduced in (A) shape from day one per PR peterdrier/Humans#389 (2026-04-26), reshaped pre-merge to the `Container` + `ContainerPlacement` split (2026-05-10) and stripped of the virtual-org-camp sentinel pre-merge (2026-05-14). New sections must be (A) per design-rules §15h(1). **G5 (own project, `src/Sections/Humans.Containers`) — 2026-08-09.**

- `Service` (`Humans.Containers.Services`) never imports `Microsoft.EntityFrameworkCore`.
- `IContainerRepository` / `Repository` (`Humans.Containers.Data`, `IDbContextFactory<ContainersDbContext>`) is the only code path that touches `containers` and `container_placements` via `DbContext`.
- **DbContext** — `ContainersDbContext` (`Data/ContainersDbContext.cs`, `internal sealed`) is the section's own per-section EF model (nobodies-collective/Humans#858 split): maps only `containers` and `container_placements`, with its own `__EFMigrationsHistory_Containers` table and migrations under `Data/Migrations/`. Same database and connection as `HumansDbContext` — the split partitions the EF model, not the database. The `holded_*`-style name mismatch does not arise here: both tables match the section name.
- Images are written through the shared `IFileStorage` under the `uploads/containers/` key prefix (`memory/architecture/one-ifilestorage`). There is no container-specific storage interface.
- **Decorator decision — no caching decorator.** Small dataset, admin/lead facing, low write frequency.
- DI: `Section.Register` (project root, discovered by Shell) registers `IContainerRepository` (Singleton), `IContainerService` (Scoped) and `ContainerAuthorizationHandler`. Nothing is added to Shell.
- **`Contracts/` is a folder, not a project.** Every consumer outside the section — `CityPlanningController` and `CityPlanningApiController` — sits in `Humans.CityPlanning`, which references this project directly, so no downward carve is needed (design §15 step 5b). The reverse edge is the leaf only: this project references `Humans.CityPlanning.Contracts` (the authorization handler and the list page gate on the placement phase), and that leaf references `Humans.Interfaces` alone, so the pair stays acyclic. The folder is unusually wide for the same reason: City Planning drives container CRUD from its own controllers and views, so the service interface, its DTOs, the authorization requirement/target and the shared card view models are all cross-section surface. Narrowing it means moving the `/CityPlanning/BarrioMap/Admin/Containers` pages into this section, which is a URL change and out of scope for a G5 move.
- **Shared card partials live here.** `Views/Shared/_ContainerCardRow.cshtml`, `_ContainerCardModals.cshtml` and `_ContainerFormFields.cshtml` are rendered by both this section's `Container/Index` and Shell's `CityPlanning/Containers`. They stay in the section rather than in `Humans.UI` precisely so they can localize from `ContainersResource`; a Base partial cannot see a section's resource set and would have to take every label in on its model (the `_FavouriteButton` lesson — that partial has since moved into `Humans.Events` at G5 lane 4b-i, nobodies-collective/Humans#866, but keeps its caller-supplies-labels shape because Shell's EventsCard renders it too; design §15 step 3b).
- **Resources.** The 20 `Container_*` keys and the 9 `ContainerMap_*` keys are both `ContainersResource` — the map page is City Planning's URL over Containers' data, so the vocabulary stays in this set. `Humans.CityPlanning`'s `_ViewImports` binds an `IStringLocalizer<ContainersResource>` alongside its own, so which set a key comes from is visible at the call site.
- **Audit discriminators are literals** (`Services/AuditEntityTypes.cs`), not `nameof`. `Camp` in particular is not nameable from this assembly after the move.

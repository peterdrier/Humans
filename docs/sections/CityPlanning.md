<!-- freshness:triggers
  src/Humans.Application/Services/CityPlanning/**
  src/Humans.Application/Interfaces/CitiPlanning/ICityPlanningService.cs
  src/Humans.Application/Interfaces/Repositories/ICityPlanningRepository.cs
  src/Humans.Domain/Entities/CityPlanningSettings.cs
  src/Humans.Domain/Entities/CampPolygon.cs
  src/Humans.Domain/Entities/CampPolygonHistory.cs
  src/Humans.Infrastructure/Data/Configurations/CityPlanning/**
  src/Humans.Infrastructure/Repositories/CitiPlanning/CityPlanningRepository.cs
  src/Humans.Web/Controllers/CityPlanningController.cs
  src/Humans.Web/Controllers/CityPlanningApiController.cs
  src/Humans.Web/Hubs/CityPlanningHub.cs
-->
<!-- freshness:flag-on-change
  Polygon edit authorization (lead vs city-planning team vs CampAdmin), placement-open gating, and append-only history rules — review when CityPlanning service/entities/controllers change.
-->

# City Planning — Section Invariants

Interactive map surface with three screens: read-only overview, barrio polygon editing, and container placement. Owns placement phase control and append-only polygon history.

## Concepts

- **City Planning** is an interactive map for camp barrio placement. Camp leads draw polygons to claim their barrio's physical footprint on the site.
- **CityPlanningSettings** is a per-year singleton controlling the barrio placement phase (open/closed), the container placement phase (open/closed), site boundary (limit zone), and informational overlays (official zones).
- **Container placement phase** gates whether barrio leads can add/edit/delete containers for their camp. Toggled by map admins from the containers admin sub-page. Camp admins and city planning team members are always exempt.
- **CampPolygon** is a single polygon per CampSeason representing the camp's placed area.
- **CampPolygonHistory** is an append-only audit trail of polygon edits and restores.

## Data Model

### CityPlanningSettings

Per-year singleton controlling the placement phase and map overlays. Auto-created from `CampSettings.PublicYear`.

**Table:** `city_planning_settings`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Year | int | Season year (unique) |
| IsPlacementOpen | bool | Whether camp leads can edit polygons |
| OpenedAt | Instant? | When barrio placement was last opened |
| ClosedAt | Instant? | When barrio placement was last closed |
| IsContainerPlacementOpen | bool | Whether barrio leads can manage their containers |
| ContainerPlacementOpenedAt | Instant? | When container placement was last opened |
| ContainerPlacementClosedAt | Instant? | When container placement was last closed |
| PlacementOpensAt | LocalDateTime? | Informational scheduled open (not enforced) |
| PlacementClosesAt | LocalDateTime? | Informational scheduled close (not enforced) |
| RegistrationInfo | text? | Admin-editable markdown shown at the top of `/Barrios/Register`. Null/empty = hidden. Keyed to the highest open season year (falling back to `PublicYear`), not to `CampSettings.PublicYear` like the other fields. |
| LimitZoneGeoJson | text? | GeoJSON FeatureCollection — site boundary |
| OfficialZonesGeoJson | text? | GeoJSON FeatureCollection — named overlay zones |
| UpdatedAt | Instant | Last modification |

### CampPolygon

One polygon per CampSeason representing the camp's placed barrio area.

**Table:** `camp_polygons`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| CampSeasonId | Guid | FK → CampSeason (unique — one polygon per season) — **FK only**, no nav read by this section |
| GeoJson | text | GeoJSON Feature with Polygon geometry |
| AreaSqm | double | Computed area in square meters |
| LastModifiedByUserId | Guid | FK → User — **FK only**, no nav read by this section |
| LastModifiedAt | Instant | Last modification |

### CampPolygonHistory

Append-only per design-rules §12. The repository exposes no `UpdateAsync` / `RemoveAsync` — restores call `SavePolygonAndAppendHistoryAsync` with a `"Restored from ..."` note, which both updates the polygon and appends a new history row.

**Table:** `camp_polygon_histories`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| CampSeasonId | Guid | FK → CampSeason — **FK only**, no nav read by this section |
| GeoJson | text | GeoJSON snapshot |
| AreaSqm | double | Area at time of snapshot |
| ModifiedByUserId | Guid | FK → User — **FK only**, no nav read by this section |
| ModifiedAt | Instant | When this version was saved |
| Note | string (512) | "Saved" or "Restored from {timestamp}" |

Cross-domain navs (`CampPolygon.CampSeason`, `CampPolygon.LastModifiedByUser`, `CampPolygonHistory.CampSeason`, `CampPolygonHistory.ModifiedByUser`) remain declared on the entities but are no longer read from this section's code. Stripping them at the entity boundary is a follow-up item consistent with §15i — new code must use `ICampService` / `IUserService` instead.

## Routing

Three distinct pages served by `CityPlanningController` (`[Route("CityPlanning")]`):

| Route | Purpose | Access |
|-------|---------|--------|
| `/CityPlanning/` | Read-only overview map — all placed barrios, all placed containers for the year | Any authenticated human |
| `/CityPlanning/BarrioMap` | Barrio polygon editing — draw/edit own polygon (leads) or any polygon (admins) | Camp leads + Map Admins |
| `/CityPlanning/ContainerMap/{year}` | Container placement map — drag-to-place containers within the site boundary | Camp leads (phase open) + Map Admins |

Admin sub-pages hosted on `CityPlanningController` under `/CityPlanning/BarrioMap/Admin/*`:

| Route | Purpose |
|-------|---------|
| `/CityPlanning/BarrioMap/Admin` | Settings panel: toggle barrio placement, upload limit zone and official zones, set placement dates |
| `/CityPlanning/BarrioMap/Admin/Containers/{year}` | Org-level + all-barrio container admin: CRUD, image management, container placement phase toggle |
| `POST /CityPlanning/BarrioMap/Admin/OpenPlacement` | Open barrio placement phase |
| `POST /CityPlanning/BarrioMap/Admin/ClosePlacement` | Close barrio placement phase |
| `POST /CityPlanning/BarrioMap/Admin/UpdatePlacementDates` | Set informational open/close datetimes |
| `POST /CityPlanning/BarrioMap/Admin/UploadLimitZone` | Upload limit zone GeoJSON |
| `GET /CityPlanning/BarrioMap/Admin/DownloadLimitZone` | Download limit zone GeoJSON |
| `POST /CityPlanning/BarrioMap/Admin/DeleteLimitZone` | Delete limit zone |
| `POST /CityPlanning/BarrioMap/Admin/UploadOfficialZones` | Upload official zones GeoJSON |
| `GET /CityPlanning/BarrioMap/Admin/DownloadOfficialZones` | Download official zones GeoJSON |
| `POST /CityPlanning/BarrioMap/Admin/DeleteOfficialZones` | Delete official zones |

The container entity CRUD for barrio leads is served by `ContainerController` at `/Camp/{slug}/Containers`. The placement API for all containers is served by `CityPlanningApiController` at `/api/city-planning/containers/*` — placement is a City Planning concern even though the container entity belongs to the Containers section.

**API — `CityPlanningApiController` (`[Route("api/city-planning")]`)**

| Route | Action |
|-------|--------|
| `GET /api/city-planning/state` | Map state: settings + all polygons + unmapped seasons |
| `PUT /api/city-planning/camp-polygons/{campSeasonId}` | Save or update a polygon |
| `GET /api/city-planning/camp-polygons/{campSeasonId}/history` | Version history (newest first) |
| `POST /api/city-planning/camp-polygons/{campSeasonId}/restore/{historyId}` | Restore historical version (map admin only) |
| `GET /api/city-planning/export.geojson?year={year}` | Export all polygons as GeoJSON (map admin only) |
| `GET /api/city-planning/containers/{year}` | Container placement map state for the year |
| `GET /api/city-planning/containers/{year}/export.geojson` | Export all container placements as GeoJSON |
| `PUT /api/city-planning/containers/{id}/placement/{year}` | Save or update a container placement |
| `DELETE /api/city-planning/containers/{id}/placement/{year}` | Clear a container placement |

**SignalR — `CityPlanningHub` (`/hubs/city-planning`)**

Broadcasts `CampPolygonUpdated(campSeasonId, geoJson, areaSqm, soundZone, campName)` after every polygon save. Receives `CursorMoved(lng, lat)` from clients.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View the map and all placed barrios |
| Camp lead (own camp, placement open) | Draw or edit their own camp's polygon |
| Camp lead (own camp, container placement open) | Add/edit/delete their own camp's containers |
| City-planning team member (team slug: `city-planning`) | Full admin access always (any polygon, containers, settings, exports) |
| CampAdmin role | Full admin access always |

## Invariants

- Only one CampPolygon per CampSeason (unique constraint on `CampSeasonId`).
- CampPolygonHistory is append-only — edits and restores always create a new history entry (design-rules §12).
- Camp leads can only edit their own camp's polygon when barrio placement is open. City-planning team members and CampAdmin are exempt.
- Camp leads can only add/edit/delete their camp's containers when container placement is open. City-planning team members and CampAdmin are exempt.
- CityPlanningSettings row is auto-created per year from `CampSettings.PublicYear`.
- SignalR broadcasts polygon updates to all connected clients in real time.
- Limit zone and official zones are stored as GeoJSON on CityPlanningSettings; out-of-bounds and overlap detection is client-side.
- Enforced by `CityPlanningArchitectureTests` (no-decorator shape, append-only repository surface).

## Negative Access Rules

- Regular humans **cannot** edit polygons for camps they do not lead.
- Camp leads **cannot** edit their polygon when barrio placement is closed.
- Camp leads **cannot** add/edit/delete their containers when container placement is closed.
- Non-admin humans **cannot** access the admin panel (placement toggles, zone uploads, export).

## Triggers

- Saving a polygon creates a CampPolygonHistory entry with note `"Saved"`.
- Restoring a historical version saves the current polygon state to history first (note: `"Restored from {timestamp}"`), then overwrites the polygon with the restored version.
- SignalR broadcasts `CampPolygonUpdated` to all connected clients after every save.

## Cross-Section Dependencies

- **Camps:** `ICampService` — CampSeason is the anchor entity; CampLead determines who can edit which polygon; `GetCampLeadSeasonIdForYearAsync`, `GetCampSeasonDisplayDataForYearAsync`, `GetCampSeasonBriefsForYearAsync` used by container admin and map pages.
- **Containers:** `IContainerService` — placement API and container admin pages (`CityPlanningApiController`, `CityPlanningController`) read and write container placement via `IContainerService.GetAllAsync`, `GetPlacementsByYearAsync`, `SavePlacementAsync`, `ClearPlacementAsync`, plus per-camp container CRUD. City Planning hosts the placement API endpoints for an entity owned by the Containers section.
- **Teams:** `ITeamService` — membership in the city-planning team (slug: `city-planning`) grants admin access.
- **Profiles:** `IProfileService` — display data for polygon edit attribution.
- **Users/Identity:** `IUserService.GetByIdsAsync` — `LastModifiedByUser` / `ModifiedByUser` display names (replaces prior cross-domain `.Include`).

## Architecture

**Owning services:** `CityPlanningService` (`Humans.Application.Services.CityPlanning`)
**Owned tables:** `city_planning_settings`, `camp_polygons`, `camp_polygon_histories`
**Status:** (A) Migrated (peterdrier/Humans PR #543, 2026-04-22).

- `CityPlanningService` lives in `Humans.Application.Services.CityPlanning` and never imports `Microsoft.EntityFrameworkCore` — enforced structurally by `Humans.Application.csproj`'s reference graph.
- `ICityPlanningRepository` (`Humans.Application.Interfaces.Repositories`) / `CityPlanningRepository` (`Humans.Infrastructure.Repositories.CitiPlanning`) is the only code path that touches this section's tables via `DbContext`.
- **Decorator decision — no caching decorator.** Admin-facing, low-traffic (same rationale as Governance / User / Feedback).
- **Cross-section reads** route through `ICampService`, `ITeamService`, `IProfileService`, and `IUserService`. The previous cross-domain `.Include(h => h.ModifiedByUser)` on `CampPolygonHistories` is replaced by a batched `IUserService.GetByIdsAsync` lookup at the service layer.
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/CityPlanningArchitectureTests.cs` pins the non-decorator shape and the append-only repository surface.
- **Per-map screens, not generic layers.** Issue #521 originally proposed a generic `MapFeature` entity with toggleable map layers; the implementation pivoted to dedicated per-map screens (overview / barrio placement / container placement) after thread discussion — see #521 for the rationale.

### Repository surface

`ICityPlanningRepository` exposes:

- Polygon reads by camp season ids (`GetPolygonsByCampSeasonIdsAsync`, `GetCampSeasonIdsWithPolygonAsync`).
- Polygon-history reads for a camp season (`GetHistoryForCampSeasonAsync`, `GetHistoryEntryAsync`).
- Atomic "save polygon + append history" write (`SavePolygonAndAppendHistoryAsync`). Polygon upsert and history insert happen in one unit of work.
- Settings read/upsert (`GetSettingsByYearAsync`, `GetOrCreateSettingsAsync`, `MutateSettingsAsync`). All field-level mutations (placement open/close, limit zone, official zones, placement dates, registration info) flow through `MutateSettingsAsync` at the service layer.

Per §12, `camp_polygon_histories` is append-only — the repository intentionally exposes no `UpdateHistoryAsync` / `RemoveHistoryAsync`.

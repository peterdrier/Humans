<!-- freshness:triggers
  src/Sections/Humans.CityPlanning/**
  src/Sections/Humans.CityPlanning.Contracts/**
-->
<!-- freshness:flag-on-change
  Polygon edit authorization (lead vs city-planning team vs CampAdmin), placement-open gating, and append-only history rules — review when CityPlanning service/entities/controllers change.
-->

# City Planning — Section Invariants

Interactive map surface: a read-only overview, barrio polygon editing, and container placement. Owns placement phase control and append-only polygon history.

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
| CampSeasonId | Guid | Bare reference id for the CampSeason (unique — one polygon per season). **No FK constraint and no navigation** — see the note under this table. |
| GeoJson | text | GeoJSON Feature with Polygon geometry |
| AreaSqm | double | Computed area in square meters |
| LastModifiedByUserId | Guid | Bare reference id for the User. **No FK constraint and no navigation.** |
| LastModifiedAt | Instant | Last modification |

### CampPolygonHistory

Append-only per design-rules §12. The repository exposes no `UpdateAsync` / `RemoveAsync` — restores call `SavePolygonAndAppendHistoryAsync` with a `"Restored from ..."` note, which both updates the polygon and appends a new history row.

**Table:** `camp_polygon_histories`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| CampSeasonId | Guid | Bare reference id for the CampSeason. **No FK constraint and no navigation.** |
| GeoJson | text | GeoJSON snapshot |
| AreaSqm | double | Area at time of snapshot |
| ModifiedByUserId | Guid | Bare reference id for the User. **No FK constraint and no navigation.** |
| ModifiedAt | Instant | When this version was saved |
| Note | string (512) | Open-ended — the polygon `PUT` persists whatever the caller sends. Examples: `"Saved"` (the fallback when the caller sends none), `"Restored from {timestamp} UTC"` (composed server-side by a restore), `"Imported {timestamp}"` (what the bulk import sends) |

`CampSeasonId`, `LastModifiedByUserId` and `ModifiedByUserId` are bare `Guid` columns: neither configuration declares a relationship of any kind, so `CampSeason` and `User` are absent from this section's EF model entirely. New code resolves them through `ICampServiceRead` / `IUserServiceRead`, never a nav.

## Routing

The pages served by `CityPlanningController` (`[Route("CityPlanning")]`):

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
| `POST /CityPlanning/BarrioMap/Admin/OpenContainerPlacement` | Open container placement phase |
| `POST /CityPlanning/BarrioMap/Admin/CloseContainerPlacement` | Close container placement phase |
| `POST /CityPlanning/BarrioMap/Admin/UpdatePlacementDates` | Set informational open/close datetimes |
| `POST /CityPlanning/BarrioMap/Admin/UploadLimitZone` | Upload limit zone GeoJSON |
| `GET /CityPlanning/BarrioMap/Admin/DownloadLimitZone` | Download limit zone GeoJSON |
| `POST /CityPlanning/BarrioMap/Admin/DeleteLimitZone` | Delete limit zone |
| `POST /CityPlanning/BarrioMap/Admin/UploadOfficialZones` | Upload official zones GeoJSON |
| `GET /CityPlanning/BarrioMap/Admin/DownloadOfficialZones` | Download official zones GeoJSON |
| `POST /CityPlanning/BarrioMap/Admin/DeleteOfficialZones` | Delete official zones |
| `POST /CityPlanning/BarrioMap/Admin/Containers/Barrios/{campId}/Create` | Create a container for a barrio |
| `POST /CityPlanning/BarrioMap/Admin/Containers/{id}/Edit` | Edit a container |
| `POST /CityPlanning/BarrioMap/Admin/Containers/{id}/Delete` | Delete a container |

The admin page also carries a **bulk polygon import**: `barrio-map/admin-import.js` reads
`GET /api/city-planning/state`, matches the uploaded FeatureCollection's features to camps by
lower-cased name or slug, previews the matches, and then issues one ordinary
`PUT /api/city-planning/camp-polygons/{campSeasonId}` per match with the note
`Imported {timestamp}`. There is no server-side import endpoint — an import is N saves and
gets N history rows for free.

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
| `PUT /api/city-planning/containers/{id}/placement/{year}/notes` | Set placement notes and/or sketch image (multipart form) |
| `DELETE /api/city-planning/containers/{id}/placement/{year}` | Clear a container placement |

**SignalR — `CityPlanningHub` (`/hubs/city-planning`)**

Broadcasts `CampPolygonUpdated(campSeasonId, geoJson, areaSqm, soundZone, campName)` after every polygon save, and `CursorMoved(connectionId, displayName, lat, lng)` / `CursorLeft(connectionId)` to the other connected clients. Receives `UpdateCursor(lat, lng)` from clients.

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
- The container placement map deliberately has **no SignalR channel and no MapboxDraw control** — placement saves are fire-and-forget per drop (single-user workflow is sufficient at this scale) and containers use a custom drag-to-move / drag-handle-to-rotate interaction. Only the barrio polygon map broadcasts real-time updates via `CityPlanningHub`.
- Limit zone and official zones are stored as GeoJSON on CityPlanningSettings; out-of-bounds and overlap detection is client-side.
- GeoJSON is stored in **`text`** columns (`CampPolygon.GeoJson`, `CampPolygonHistory.GeoJson`, `CityPlanningSettings.LimitZoneGeoJson` / `OfficialZonesGeoJson`), deliberately **not `jsonb`** — the app never queries inside the JSON structure; it round-trips whole FeatureCollections to the MapLibre client, so `jsonb`'s parse/index overhead buys nothing. (Contrast sibling Camp columns `Links` / `Vibes` / `OpenSeasons`, which use `jsonb`.)
- No caching decorator; `CampPolygonHistory` is append-only — see the Architecture test note below.
- Every settings write that takes a `userId` — both placement phases, the zone uploads and the zone deletes — appends an `AuditLog` entry naming that actor, after the save, and a request aborted mid-write does not drop it — the row id is resolved before the save, and the save itself runs on `CancellationToken.None`, so nothing cancellable sits between the committing write and the token-less `LogAsync`. The settings row records *when* a value changed; the audit log is the only record of *who*. A rejected upload never reaches the row and writes no entry. `UpdatePlacementDatesAsync` and `UpdateRegistrationInfoAsync` take no `userId` and are not audited.
- The city-planning team slug is lower-cased on **both** sides before comparing — the configured `CityPlanningTeamSlug` and the stored `Team.Slug` / `Team.CustomSlug` — so case never decides the map-admin exemption. A blank configured slug matches nothing.

## Negative Access Rules

- Regular humans **cannot** edit polygons for camps they do not lead.
- Camp leads **cannot** edit their polygon when barrio placement is closed.
- Camp leads **cannot** add/edit/delete their containers when container placement is closed.
- Non-admin humans **cannot** access the admin panel (placement toggles, zone uploads, export).

## Triggers

- Saving a polygon creates a CampPolygonHistory entry with note `"Saved"`, or the note the client supplied — the bulk import sends `"Imported {timestamp}"`.
- Restoring a historical version saves the current polygon state to history first (note: `"Restored from {timestamp}"`), then overwrites the polygon with the restored version.
- SignalR broadcasts `CampPolygonUpdated` to all connected clients after every save.

## Cross-Section Dependencies

- **Camps:** `ICampServiceRead` — CampSeason is the anchor entity; CampLead determines who can edit which polygon; `GetCampsForYearAsync`, `GetCampSeasonByIdAsync`, `GetSettingsAsync` used by container admin and map pages. Lead/display data is derived via LINQ over the cached `GetCampsForYearAsync` projection (`CampInfo.IsLead` / `GetLeadSeasonIdForYear`).
- **Containers:** `IContainerService` — placement API and container admin pages (`CityPlanningApiController`, `CityPlanningController`) read and write container placement via `IContainerService.GetAllAsync`, `GetPlacementsByYearAsync`, `SavePlacementAsync`, `ClearPlacementAsync`, plus per-camp container CRUD. City Planning hosts the placement API endpoints for an entity owned by the Containers section.
- **Teams:** `ITeamServiceRead` — membership in the city-planning team (slug: `city-planning`) grants admin access.
- **Containers:** `ContainerController` and `ContainerAuthorizationHandler` inject `ICityPlanningServiceRead` (placement phase gate and city-planning team check). This is the correct read-only cross-section surface — not `ICityPlanningService`.
- **Users/Identity:** `IUserServiceRead.GetUserInfosAsync` — `LastModifiedByUser` / `ModifiedByUser` display names (replaces prior cross-domain `.Include`).
- **AuditLog (crosscut):** `IAuditLogService.LogAsync` — the `CityPlanning*` actions on every settings write that names an actor.

## Architecture

**Owning services:** `CityPlanningService` (`Humans.CityPlanning.Services`)
**Owned tables:** `city_planning_settings`, `camp_polygons`, `camp_polygon_histories`
**Status:** (A) Migrated (peterdrier/Humans PR #543, 2026-04-22). Own project since G5 (nobodies-collective/Humans#866).

- `CityPlanningService` lives in `Humans.CityPlanning.Services` and never imports `Microsoft.EntityFrameworkCore` — the repository is the only EF consumer, pinned by `CityPlanningArchitectureTests`.
- `ICityPlanningRepository` / `CityPlanningRepository` (`Humans.CityPlanning.Data`) is the only code path that touches this section's tables via `CityPlanningDbContext`.
- **Decorator decision — no caching decorator.** Admin-facing, low-traffic (same rationale as Governance / User / Feedback).
- **Read/write interface split.** `ICityPlanningServiceRead` (`GetSettingsAsync`, `GetRegistrationInfoAsync`, `IsCityPlanningTeamMemberAsync`) is the cross-section read surface. External sections inject `ICityPlanningServiceRead`; `ICityPlanningService : ICityPlanningServiceRead` adds writes. `ContainerAuthorizationHandler` and `ContainerController` inject `ICityPlanningServiceRead` — not `ICityPlanningService`. The service exposes no display-name read; `CityPlanningHub` resolves the burner name directly via `IUserServiceRead.GetUserInfoAsync`, and lives at `Services/CityPlanningHub.cs` in this section — `internal`, mapped by the section's own `SectionEndpoints : ISectionEndpoints` rather than by Shell's `MapHub<T>` on the concrete type. See `memory/architecture/section-read-write-split.md`.
- **Save/restore return type.** `SaveCampPolygonAsync` and `RestoreCampPolygonVersionAsync` return `CampPolygonSaveResult(GeoJson, AreaSqm)`, a DTO — keeping EF entities inside the service boundary.
- **Upload pipeline.** `UpdateLimitZoneFromUploadAsync` / `UpdateOfficialZonesFromUploadAsync` accept `IFormFile?` directly — file read, size limit and JSON validation all live in the service — and return `GeoJsonUploadResult`. `UpdatePlacementDatesAsync` accepts raw `string?` date inputs, parses them internally, and returns `PlacementDateUpdateResult`; the `LocalDateTime` parse logic and `DateFormattingExtensions` are not the controller's.
- **No year-keyed settings read on `ICityPlanningRepository`.** All settings access routes through `GetOrCreateSettingsAsync`, which creates the row with `IsPlacementOpen = false` when absent.
- **`UpdatePlacementDatesAsync` is off the contract.** It is overloaded on the concrete `CityPlanningService`: the `string?`-taking entry point the controller calls is public, the `LocalDateTime?`-taking one that writes is private. Neither is on `ICityPlanningService` or `ICityPlanningServiceRead`.
- **Cross-section reads** route through `ICampServiceRead`, `ITeamServiceRead`, and `IUserServiceRead`. History rows carry no cross-domain navigation: `CampPolygonHistories` stores `ModifiedByUserId` only, and the service resolves names through a batched `IUserServiceRead.GetUserInfosAsync` lookup.
- **Architecture test** — `tests/Humans.CityPlanning.Tests/CityPlanningArchitectureTests.cs` enforces one thing: the API controller's route prefix stays `api/city-planning` (the city-planning JavaScript hard-codes this URL). The non-decorator shape and append-only repository surface above are documentation, not assertions: a test that a section *lacks* something is forbidden by [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md). The page controller's routes and the `Views/_ViewImports.cshtml` set are exercised by `CityPlanningPageRenderTests`, which lives in `tests/Humans.Integration.Tests` and therefore **does not run in CI** — `build.yml` filters that assembly out deliberately ([`integration-tests-are-not-ci-tests`](../../../../memory/process/integration-tests-are-not-ci-tests.md)). Treat it as a local check, not a gate. The gate for those routes is `tests/e2e/tests/city-planning.spec.ts`, which loads the map screens (and their deny paths) against the deployed QA site; `e2e-qa.yml` triggers it on push to main, so it catches a broken route or a missing `_ViewImports` line after the merge, not on the PR.
- **Cross-section surface** — `Humans.CityPlanning.Contracts` is its own project, not a `Contracts/` folder, because of **Containers alone**: `Humans.Containers` needs `ICityPlanningServiceRead` while this section references `Humans.Containers`, so that pair is mutual and a folder would cycle it. `Humans.Camps` consumes the leaf too — `CampService` clears a deleted camp's polygons through `ICityPlanningService` — but is not a reason it must exist: Camps already references `Humans.CityPlanning` outright and this section references only `Humans.Camps.Contracts` back, so that pair is acyclic either way. It holds `ICityPlanningServiceRead`, `ICityPlanningService` (adds `DeleteCampPolygonsForSeasonsAsync` and `UpdateRegistrationInfoAsync`), `CityPlanningSettingsDto` and `CityPlanningOptions`. Everything else in the section is `internal`.
- **Resources** — `CityPlanningResource`, every supported culture at key parity. `Container_*` / `ContainerMap_*` on the barrio container pages are Containers' vocabulary and are bound through `ContainersLocalizer`; `Common_*` stays in `SharedResource`.
- **Per-map screens, not generic layers.** Each map is a purpose-built screen — overview, barrio placement, container placement. There is no generic `MapFeature` entity and no toggleable-layer system; `Docs/health.md` records that alternative as declined and why.

### Repository surface

`ICityPlanningRepository` exposes:

- Polygon reads by camp season ids (`GetPolygonsByCampSeasonIdsAsync`, `GetCampSeasonIdsWithPolygonAsync`).
- Polygon-history reads for a camp season (`GetHistoryForCampSeasonAsync`, `GetHistoryEntryAsync`).
- Atomic "save polygon + append history" write (`SavePolygonAndAppendHistoryAsync`). Returns the persisted `CampPolygon` only; the history row is a side effect readable via `GetHistoryForCampSeasonAsync`. Polygon upsert and history insert happen in one unit of work.
- Season-scoped cascade delete (`DeletePolygonsForCampSeasonsAsync`) — removes a season's polygon and its history rows in one unit of work, and is the one exception to append-only. Reached from Camps through `ICityPlanningService.DeleteCampPolygonsForSeasonsAsync` when a camp is deleted.
- Settings read/upsert (`GetOrCreateSettingsAsync`, `MutateSettingsAsync`). All field-level mutations (placement open/close, limit zone, official zones, placement dates, registration info) flow through `MutateSettingsAsync` at the service layer. It returns `Task` — a caller that needs the written row reads it back through `GetOrCreateSettingsAsync`, so no EF entity leaves the repository. There is no year-keyed settings read; every path goes through `GetOrCreateSettingsAsync`.

Per §12, `camp_polygon_histories` is append-only — the repository intentionally exposes no `UpdateHistoryAsync` / `RemoveHistoryAsync`.

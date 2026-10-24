<!-- freshness:triggers
  src/Sections/Humans.CityPlanning/**
  src/Sections/Humans.CityPlanning.Contracts/**
  src/Sections/Humans.Containers/Authorization/ContainerAuthorizationHandler.cs
  src/Sections/Humans.Containers/Contracts/ContainerOperationRequirement.cs
  src/Sections/Humans.Containers/Controllers/ContainerController.cs
  src/Sections/Humans.Camps/Humans.Camps.csproj
-->
<!-- freshness:flag-on-change
  Polygon edit authorization (lead vs city-planning team vs CampAdmin), placement-open gating, and append-only history rules — review when CityPlanning service/entities/controllers change.
-->

# City Planning — Section Invariants

Interactive map surface: a read-only overview, barrio polygon editing, and container placement. Owns placement phase control and append-only polygon history. Barrio map popup and drawing warnings, history browsing, save failures and discard confirmations use the viewer’s language in all six supported cultures; map-admin-only restore controls remain operator copy. Polygon saves cannot overlap; draw updates keep Save disabled until the request finishes. A successful save closes editing only when the active camp and polygon still match its captured snapshot, preserving subsequent edits. Restores cannot overlap and close their history panel and draft only if both still match the state at request start. Failed save/restore requests retain the current edit and show their error message; failed history requests or JSON decoding show the history failure panel. A history load started during the panel’s close transition waits for cleanup before fetching and reopening; only the latest waiting load resumes.

## Concepts

- **City Planning** is an interactive map for camp barrio placement. Camp leads draw polygons to claim their barrio's physical footprint on the site.
- **CityPlanningSettings** is a per-year singleton controlling the barrio placement window (a day-offset schedule plus a manual override), the container placement phase (open/closed), site boundary (limit zone), and informational overlays (official zones).
- Container creation delegates real-camp validation to Containers. A missing camp returns the owner’s localized refusal before files or rows are written, including from the map-admin route.
- **Container placement phase** gates whether barrio leads can place, annotate or clear containers for their camp. The container map page gate reads the route year’s settings, the same year its placement writes authorize against. Container CRUD uses the separate Manage operation and is not phase-gated; the admin create/edit/delete posts carry the route year and redirect back to that year’s list. Toggled by map admins from the containers admin sub-page. Placement save, notes/image update and clear share one container lookup and Place authorization gate against the route year’s settings and camp leads; the container list’s edit flags use the same year. Camp admins and city planning team members are always exempt.
- **CampPolygon** is a single polygon per CampSeason representing the camp's placed area.
- **CampPolygonHistory** is an append-only audit trail of polygon edits and restores.

## Data Model

### CityPlanningSettings

Per-year singleton controlling the placement phase and map overlays. The current year is the active event's (`ISettingsService.GetActiveEventSettingsAsync().Year`). Reads never create the row: a year with no row reads as the unpersisted defaults; the first admin write creates it.

**Table:** `city_planning_settings`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Year | int | Season year (unique) |
| PlacementOverride | bool? | Manual override of the barrio schedule: null = follow it, true = forced open, false = forced closed. Stored in the column that held the old manual `IsPlacementOpen` flag, so pre-#2102 rows read as forced open/closed |
| OpenedAt | Instant? | When an admin last forced barrio placement open |
| ClosedAt | Instant? | When an admin last forced barrio placement closed |
| PlacementOpensOffset | int? | First open day, in days from the active event's gate opening (negative = before) |
| PlacementClosesOffset | int? | Last open day (inclusive), days from gate opening |
| PlacementChangeableUntilOffset | int? | Last day (inclusive) a lead may still adjust an existing polygon after the window closes |
| IsContainerPlacementOpen | bool | Whether barrio leads can manage their containers |
| ContainerPlacementOpenedAt | Instant? | When container placement was last opened |
| ContainerPlacementClosedAt | Instant? | When container placement was last closed |
| PlacementOpensAt / PlacementClosesAt | LocalDateTime? | **Retired** (#2102) display-only dates, never enforced. `[Obsolete]`, unread and unwritten; the columns stay until their drop is approved (`debt.yml`) |
| RegistrationInfo | text? | Admin-editable markdown shown at the top of `/Barrios/Register`. Null/empty = hidden. Keyed to the highest open season year (falling back to the active event's year), not to the active event's year like the other fields. |
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
| GeoJson | text | GeoJSON Polygon/MultiPolygon, normally wrapped in a Feature |
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

`CampSeasonId`, `LastModifiedByUserId` and `ModifiedByUserId` are bare `Guid` columns: neither configuration declares a relationship of any kind, so `CampSeason` and `User` are absent from this section's EF model entirely. New code resolves them through `ICampService` / `IUserService`, never a nav.

## Routing

The pages served by `CityPlanningController` (`[Route("CityPlanning")]`):

| Route | Purpose | Access |
|-------|---------|--------|
| `/CityPlanning/` | Read-only overview map — all placed barrios, all placed containers for the year | Any authenticated human |
| `/CityPlanning/BarrioMap` | Barrio polygon editing — draw/edit own polygon (leads) or any polygon (admins) | Any authenticated human (editing is gated to camp leads and Map Admins) |
| `/CityPlanning/ContainerMap/{year}` | Container placement map — drag-to-place containers within the site boundary | Camp leads (phase open) + Map Admins |

Admin sub-pages hosted on `CityPlanningController` under `/CityPlanning/BarrioMap/Admin/*`:

| Route | Purpose |
|-------|---------|
| `/CityPlanning/BarrioMap/Admin` | Upload limit zone and official zones, containers, export/import — placement override, placement schedule, and registration info moved to `/Settings#city-planning` (peterdrier/Humans#1634) |
| `/CityPlanning/BarrioMap/Admin/Containers/{year}` | Org-level + all-barrio container admin: CRUD, image management, container placement phase toggle |
| `POST /CityPlanning/BarrioMap/Admin/OpenPlacement` | Force barrio placement open — manual override (redirects to `/Settings#city-planning`) |
| `POST /CityPlanning/BarrioMap/Admin/ClosePlacement` | Force barrio placement closed — manual override (redirects to `/Settings#city-planning`) |
| `POST /CityPlanning/BarrioMap/Admin/FollowPlacementSchedule` | Clear the override so barrio placement follows the schedule (redirects to `/Settings#city-planning`) |
| `POST /CityPlanning/BarrioMap/Admin/OpenContainerPlacement` | Open container placement phase |
| `POST /CityPlanning/BarrioMap/Admin/CloseContainerPlacement` | Close container placement phase |
| `POST /CityPlanning/BarrioMap/Admin/UpdatePlacementSchedule` | Set the barrio schedule's three day offsets (redirects to `/Settings#city-planning`) |
| `POST /CityPlanning/BarrioMap/Admin/UpdateRegistrationInfo` | Set the barrio registration page's markdown from its sole editor (redirects to `/Settings#city-planning`) |
| `POST /CityPlanning/BarrioMap/Admin/UploadLimitZone` | Upload limit zone GeoJSON |
| `GET /CityPlanning/BarrioMap/Admin/DownloadLimitZone` | Download limit zone GeoJSON |
| `POST /CityPlanning/BarrioMap/Admin/DeleteLimitZone` | Delete limit zone |
| `POST /CityPlanning/BarrioMap/Admin/UploadOfficialZones` | Upload official zones GeoJSON |
| `GET /CityPlanning/BarrioMap/Admin/DownloadOfficialZones` | Download official zones GeoJSON |
| `POST /CityPlanning/BarrioMap/Admin/DeleteOfficialZones` | Delete official zones |
| `POST /CityPlanning/BarrioMap/Admin/Containers/{year}/Barrios/{campId}/Create` | Create a container for a barrio |
| `POST /CityPlanning/BarrioMap/Admin/Containers/{year}/{id}/Edit` | Edit a container |
| `POST /CityPlanning/BarrioMap/Admin/Containers/{year}/{id}/Delete` | Delete a container |

The admin page also carries a **bulk polygon import**: `barrio-map/admin-import.js` reads
`GET /api/city-planning/state`, matches the uploaded FeatureCollection's features to camps by
lower-cased name or slug, previews the matches only while the file that started loading is still selected (obsolete previews and errors are discarded; malformed feature metadata or geometry raises an operator preview error and clears any previous pending import), and then issues one ordinary
`PUT /api/city-planning/camp-polygons/{campSeasonId}` per match with the note
`Imported {timestamp}`. There is no server-side import endpoint — an import is N saves and
gets one history row per successful save. HTTP failures and network exceptions are collected per camp; remaining reviewed matches are attempted, and the result reports both successes and failed camp names.

The container entity CRUD for barrio leads is served by `ContainerController` at `/Camp/{slug}/Containers`. The placement API for all containers is served by `CityPlanningApiController` at `/api/city-planning/containers/*` — placement is a City Planning concern even though the container entity belongs to the Containers section.

**API — `CityPlanningApiController` (`[Route("api/city-planning")]`)**

| Route | Action |
|-------|--------|
| `GET /api/city-planning/state` | Map state: settings + all polygons + unmapped seasons |
| `PUT /api/city-planning/camp-polygons/{campSeasonId}` | Save or update a polygon |
| `GET /api/city-planning/camp-polygons/{campSeasonId}/history` | Version history (newest first) |
| `POST /api/city-planning/camp-polygons/{campSeasonId}/restore/{historyId}` | Restore historical version (map admin only; 404 when the history id is not the season's) |
| `GET /api/city-planning/export.geojson?year={year}` | Export all polygons as GeoJSON (map admin only) |
| `GET /api/city-planning/containers/{year}` | Container placement map state for the year |
| `GET /api/city-planning/containers/{year}/export.geojson` | Export all container placements as GeoJSON |
| `PUT /api/city-planning/containers/{id}/placement/{year}` | Save or update a container placement |
| `PUT /api/city-planning/containers/{id}/placement/{year}/notes` | Set placement notes and/or sketch image (multipart form) |
| `DELETE /api/city-planning/containers/{id}/placement/{year}` | Clear a container placement |

**SignalR — `CityPlanningHub` (`/hubs/city-planning`)**

Negotiation and connection require both authentication and `AppAccess` (stored `UserState.Active`), including for admins. SignalR does not run the MVC membership filter, so the hub enforces that gate explicitly.

Broadcasts `CampPolygonUpdated(campSeasonId, geoJson, areaSqm, soundZone, campName)` after every polygon save, and `CursorMoved(connectionId, displayName, lat, lng)` / `CursorLeft(connectionId)` to the other connected clients. Receives `UpdateCursor(lat, lng)` from clients.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View the map and all placed barrios |
| Camp lead (own camp, placement Open) | Draw or edit their own camp's polygon |
| Camp lead (own camp, placement Changeable) | Edit their own camp's existing polygon — not draw a new one |
| Camp lead (own camp, container placement open) | Add/edit/delete their own camp's containers |
| City-planning team member (team slug: `city-planning`) | Full admin access always (any polygon, containers, settings, exports) |
| CampAdmin role | Full admin access always |

## Invariants

- Only one CampPolygon per CampSeason (unique constraint on `CampSeasonId`).
- Barrio history displays only the latest requested camp season. Switching barrios clears prior preview/restore controls immediately; late success or failure cannot overwrite a newer request or reopen a dismissed panel. One panel-close handler clears history previews while preserving active edits.
- CampPolygonHistory is append-only — edits and restores always create a new history entry (design-rules §12).
- Barrio placement has one effective state, computed on every read by `CityPlanningService` (peterdrier/Humans#2102): the manual override when set (true = Open, false = Closed); otherwise the schedule, by today's date in the active event's time zone (`EventSettingsInfo.TimeZoneId`) against `GateOpeningDate` + offset — **Open** from the opening day through the closing day, **Changeable** through the changeable-until day, then **Closed**. No opening day, no active event, or a row for another year than the active event's means Closed unless forced open. No job flips anything: the date decides at read time.
- Camp leads can create or edit their own camp's polygon only while placement is Open, and only edit one that already exists while it is Changeable. City-planning team members and CampAdmin are exempt.
- The schedule's days must run open ≤ close ≤ changeable-until, within 366 days of gate opening, and a changeable-until day needs a closing day; anything else is refused without a write or an audit entry.
- Camp leads can only add/edit/delete their camp's containers when container placement is open. City-planning team members and CampAdmin are exempt.
- City planning's current year is the active event's year. With no active event there is no current year: the map pages (index, barrio map, container map), the admin page and the state API answer 404, the `/Settings#city-planning` tab says so in place of the placement cards — the registration-info card stays while a season is open, since that copy is keyed to the open season — the Camps details page shows placement as closed, and a settings write throws.
- Settings reads are side-effect free (peterdrier/Humans#2102): `GetSettingsAsync` and `GetRegistrationInfoAsync` never write. A year with no row reads as the defaults (closed, no dates, no zones) with `Id = Guid.Empty`; the row is created by the first admin write (`GetOrCreateSettingsAsync` on the write paths only).
- SignalR broadcasts polygon updates to all connected clients in real time.
- Container-placement save, notes, and clear HTTP failures use localized prefixes while retaining status codes and server details; invalid-placement GeoJSON errors are localized in all six cultures. Containers validates placement geometry before writing; the API localizes its typed refusal as HTTP 422.
- Member-facing container map centering and notes buttons have localized titles and accessible names; the notes modal close button reuses the localized shared label.
- Container drag and rotation saves include the final pointer movement even before its animation frame. Changing or clearing the selected container discards unfinished gestures and queued movement.
- Placement-note saves update the submitted container in local map state even if another container is opened while saving. A completed save or error cannot close or alter a newer notes-editing session.
- The container placement map deliberately has **no SignalR channel and no MapboxDraw control** — placement saves are fire-and-forget per drop (single-user workflow is sufficient at this scale) and containers use a custom drag-to-move / drag-handle-to-rotate interaction. Only the barrio polygon map broadcasts real-time updates via `CityPlanningHub`.
- Limit zone and official zones are stored as GeoJSON on CityPlanningSettings; out-of-bounds and overlap detection is client-side.
- GeoJSON is stored in **`text`** columns (`CampPolygon.GeoJson`, `CampPolygonHistory.GeoJson`, `CityPlanningSettings.LimitZoneGeoJson` / `OfficialZonesGeoJson`), deliberately **not `jsonb`** — the app never queries inside the JSON structure; it round-trips whole FeatureCollections to the MapLibre client, so `jsonb`'s parse/index overhead buys nothing. (Contrast sibling Camp columns `Links` / `Vibes` / `OpenSeasons`, which use `jsonb`.)
- No caching decorator; `CampPolygonHistory` is append-only — see the Architecture test note below.
- Every settings write that takes a `userId` — the barrio override (forced open, forced closed, cleared), the barrio schedule, the container phase, the zone uploads and the zone deletes — appends an `AuditLog` entry naming that actor, after the save, and a request aborted mid-write does not drop it — the row id is resolved before the save, and the save itself runs on `CancellationToken.None`, so nothing cancellable sits between the committing write and the token-less `LogAsync`. The settings row records *when* a value changed; the audit log is the only record of *who*. A rejected upload never reaches the row and writes no entry. `UpdatePlacementDatesAsync` and `UpdateRegistrationInfoAsync` audit the same way (`CityPlanningPlacementDatesUpdated`, `CityPlanningRegistrationInfoUpdated`); the registration-info entry names the highest-open-season row it wrote, not the active event year's. Rejected date input writes nothing.
- The city-planning team slug is lower-cased on **both** sides before comparing — the configured `CityPlanningTeamSlug` and the stored `Team.Slug` / `Team.CustomSlug` — so case never decides the map-admin exemption. A blank configured slug matches nothing.

## Negative Access Rules

- Anonymous and non-active accounts cannot negotiate or establish a new live map connection, even with an admin role; this includes rejected, deleted and merged accounts.

- Regular humans **cannot** edit polygons for camps they do not lead.
- Camp leads **cannot** edit their polygon when barrio placement is Closed, and **cannot** create one while it is Changeable.
- Camp leads **cannot** add/edit/delete their containers when container placement is closed.
- Non-admin humans **cannot** access the admin panel (zone uploads, container management and its placement-phase toggle, export/import; barrio placement toggle lives on `/Settings#city-planning`).

## Triggers

- Saving a polygon creates a CampPolygonHistory entry with note `"Saved"`, or the note the client supplied — the bulk import sends `"Imported {timestamp}"`.
- Restoring a historical version overwrites the current polygon with the restored version and appends a history entry for it (note: `"Restored from {timestamp}"`).
- SignalR broadcasts `CampPolygonUpdated` to all connected clients after every save. Broadcast preparation (including camp detail lookup) and delivery failures are logged while returning the saved polygon; an aborted request instead propagates cancellation.

## Cross-Section Dependencies

- **Settings:** `ISettingsService.GetActiveEventSettingsAsync` — the active event's year is city planning's current year.
- **Camps:** `ICampService` — CampSeason is the anchor entity; CampLead determines who can edit which polygon; `GetCampsForYearAsync`, `GetCampSeasonByIdAsync`, `GetSettingsAsync` used by container admin and map pages. Lead/display data is derived via LINQ over the cached `GetCampsForYearAsync` projection (`CampInfo.IsLead` / `GetLeadSeasonIdForYear`).
- **Containers:** `IContainerService` — placement API and container admin pages (`CityPlanningApiController`, `CityPlanningController`) read and write container placement via `IContainerService.GetAllAsync`, `GetPlacementsByYearAsync`, `SavePlacementAsync`, `ClearPlacementAsync`, plus per-camp container CRUD. City Planning hosts the placement API endpoints for an entity owned by the Containers section.
- **Teams:** `ITeamService` — membership in the city-planning team (slug: `city-planning`) grants admin access.
- **Containers:** `ContainerController` and `ContainerAuthorizationHandler` inject `ICityPlanningService` (placement phase gate and city-planning team check). This is the correct read-only cross-section surface — not `ICityPlanningWrite`.
- **Users/Identity:** `IUserService.GetUserInfosAsync` — `LastModifiedByUser` / `ModifiedByUser` display names (replaces prior cross-domain `.Include`).
- **AuditLog (crosscut):** `IAuditLogService.LogAsync` — the `CityPlanning*` actions on every settings write that names an actor.

## Architecture

**Owning services:** `CityPlanningService` (`Humans.CityPlanning.Services`)
**Owned tables:** `city_planning_settings`, `camp_polygons`, `camp_polygon_histories`
**Status:** (A) Migrated (peterdrier/Humans PR #543, 2026-04-22). Own project since G5 (nobodies-collective/Humans#866).

- `CityPlanningService` lives in `Humans.CityPlanning.Services` and never imports `Microsoft.EntityFrameworkCore` — the repository is the only EF consumer. `CityPlanningArchitectureTests` pins the API controller's route prefix only, not this EF boundary.
- `ICityPlanningRepository` / `CityPlanningRepository` (`Humans.CityPlanning.Data`) is the only code path that touches this section's tables via `CityPlanningDbContext`.
- **Decorator decision — no caching decorator.** Admin-facing, low-traffic (same rationale as Governance / User / Feedback).
- **Public contract.** `ICityPlanningService` (`GetSettingsAsync`, `GetRegistrationInfoAsync`, `IsCityPlanningTeamMemberAsync`) is the public read contract. External sections inject `ICityPlanningService`; the one cross-section write, Camps' polygon cleanup on camp deletion, is the separate `ICityPlanningWrite` (`DeleteCampPolygonsForSeasonsAsync`). `ContainerAuthorizationHandler` and `ContainerController` inject `ICityPlanningService` — not `ICityPlanningWrite`. `UpdateRegistrationInfoAsync` is on neither: it is section-internal, on the concrete `CityPlanningService`. The service exposes no display-name read; `CityPlanningHub` resolves the burner name directly via `IUserService.GetUserInfoAsync`, and lives at `Services/CityPlanningHub.cs` in this section — `internal`, mapped by the section's own `SectionEndpoints : ISectionEndpoints` rather than by Shell's `MapHub<T>` on the concrete type.
- **Camp geometry validation.** Save and restore reject non-area GeoJSON before writing the polygon or history. Polygon and MultiPolygon coordinates must contain nonempty polygons and closed rings of at least four positions; positions need finite numeric coordinates, latitude within −90–90 and longitude within −180–180. Feature-wrapped and bare geometry, holes and altitude coordinates round-trip unchanged. Area must be finite and non-negative; zero remains valid. Invalid geometry or area returns HTTP 400 on save and restore before any write or broadcast, and logs a warning with the reason without an exception object. The map displays its existing localized save-failure message. Overlay FeatureCollection uploads retain their separate JSON validation.
- **Save/restore return type.** `SaveCampPolygonAsync` and `RestoreCampPolygonVersionAsync` return `CampPolygonSaveResult(GeoJson, AreaSqm)`, a DTO — keeping EF entities inside the service boundary.
- **Upload pipeline.** `UpdateLimitZoneFromUploadAsync` / `UpdateOfficialZonesFromUploadAsync` accept `IFormFile?` directly — file read, 10 MB size limit and JSON validation all live in the service — and return `GeoJsonUploadResult`. Their routes, and the placement-image API route, allow 11 MB to cover multipart overhead without allowing the server default to buffer a larger upload. `UpdatePlacementDatesAsync` accepts raw `string?` date inputs, parses them internally, and returns `PlacementDateUpdateResult`; the `LocalDateTime` parse logic and `DateFormattingExtensions` are not the controller's.
- **Reads and writes take different repository paths.** Reads use `GetSettingsAsync` (null when absent, never writes); writes use `GetOrCreateSettingsAsync` / `MutateSettingsAsync`, which create the row with no override and no schedule when absent.
- **`UpdatePlacementScheduleAsync` and `ClearPlacementOverrideAsync` are off the contract.** They live only on the concrete `CityPlanningService`, which the controller injects; they are on neither `ICityPlanningWrite` nor `ICityPlanningService`. Cross-section readers (Camps' Details page, Containers) see the effective state and the schedule's calendar dates on `CityPlanningSettingsDto` (`PlacementState`, `PlacementOverride`, the `*Offset` and `*On` fields).
- **Container placement stays manual.** Its open/close is a plain flag (`IsContainerPlacementOpen`) with no schedule; #2102 scheduled only barrio placement.
- Index, barrio-map, and container-map GETs propagate request cancellation from the initial member lookup through their existing data reads.
- **Cross-section reads** route through `ICampService`, `ITeamService`, and `IUserService`. History rows carry no cross-domain navigation: `CampPolygonHistories` stores `ModifiedByUserId` only, and the service resolves names through a batched `IUserService.GetUserInfosAsync` lookup.
- **Architecture test** — `tests/Humans.CityPlanning.Tests/CityPlanningArchitectureTests.cs` enforces one thing: the API controller's route prefix stays `api/city-planning` (the city-planning JavaScript hard-codes this URL). The non-decorator shape and append-only repository surface above are documentation, not assertions: a test that a section *lacks* something is forbidden by [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md). The page controller's routes and the `Views/_ViewImports.cshtml` set are exercised by `CityPlanningPageRenderTests`, which lives in `tests/Humans.Integration.Tests` and therefore **does not run in CI** — `build.yml` filters that assembly out deliberately ([`integration-tests-are-not-ci-tests`](../../../../memory/process/integration-tests-are-not-ci-tests.md)). Treat it as a local check, not a gate. The gate for those routes is `tests/e2e/tests/city-planning.spec.ts`, which loads the map screens (and their deny paths) against the deployed QA site; `e2e-qa.yml` triggers it on push to main, so it catches a broken route or a missing `_ViewImports` line after the merge, not on the PR.
- **Cross-section surface** — `Humans.CityPlanning.Contracts` is its own project, not a `Contracts/` folder, because of **Containers alone**: `Humans.Containers` needs `ICityPlanningService` while this section references `Humans.Containers`, so that pair is mutual and a folder would cycle it. `Humans.Camps` consumes the leaf too — `CampService` clears a deleted camp's polygons through `ICityPlanningWrite` — but is not a reason it must exist: Camps already references `Humans.CityPlanning` outright and this section references only `Humans.Camps.Contracts` back, so that pair is acyclic either way. It holds `ICityPlanningService`, `ICityPlanningWrite` (`DeleteCampPolygonsForSeasonsAsync` only), `CityPlanningSettingsDto` and `CityPlanningOptions`. Everything else in the section is `internal`.
- **Resources** — `CityPlanningResource`, every supported culture at key parity. `Containers_*` keys on the barrio container pages are Containers' vocabulary and are bound through `ContainersLocalizer`; `Common_*` stays in `SharedResource`. Container form and placement APIs localize typed owner refusals through `ContainersResource`, with warning logs and HTTP 422 for API refusals. Dependency exceptions propagate and are never classified by message or shown as validation feedback.
- **Per-map screens, not generic layers.** Each map is a purpose-built screen — overview, barrio placement, container placement. There is no generic `MapFeature` entity and no toggleable-layer system; `Docs/health.md` records that alternative as declined and why.

### Repository surface

`ICityPlanningRepository` exposes:

- Polygon reads by camp season ids (`GetPolygonsByCampSeasonIdsAsync`, `GetCampSeasonIdsWithPolygonAsync`).
- Polygon-history reads for a camp season (`GetHistoryForCampSeasonAsync`, `GetHistoryEntryAsync`).
- Atomic "save polygon + append history" write (`SavePolygonAndAppendHistoryAsync`). Returns the persisted `CampPolygon` only; the history row is a side effect readable via `GetHistoryForCampSeasonAsync`. Polygon upsert and history insert happen in one unit of work.
- Season-scoped cascade delete (`DeletePolygonsForCampSeasonsAsync`) — removes a season's polygon and its history rows in one unit of work, and is the one exception to append-only. Reached from Camps through `ICityPlanningWrite.DeleteCampPolygonsForSeasonsAsync` when a camp is deleted.
- Settings read (`GetSettingsAsync` — null when the year has no row; never writes) and upsert (`GetOrCreateSettingsAsync`, `MutateSettingsAsync` — write paths only). All field-level mutations (barrio override, barrio schedule, container phase, limit zone, official zones, registration info) flow through `MutateSettingsAsync` at the service layer. It returns `Task`, so no EF entity leaves the repository.

Per §12, `camp_polygon_histories` is append-only — the repository intentionally exposes no `UpdateHistoryAsync` / `RemoveHistoryAsync`.

## Issue queue

`Section` also declares this queue’s Area-label resource key and page-path aliases; Issues discovers both with the queue.

CityPlanning owns the `CityPlanning` issue queue: it implements `IIssueQueueOwner` (Issues' `Contracts/`
folder) on its `Section` entry point, declaring the queue key and the roles that handle
issues filed against it — `CampAdmin`, plus `Admin`, which handles every queue. Issues
discovers the declaration through DI and holds no list of sections; dropping the seam
sends this section's stored issues to the Admin-only queue.

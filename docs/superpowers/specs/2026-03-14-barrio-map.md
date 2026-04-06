# Barrio Map Feature Design

## Business Context

City planning at Nowhere needs to know where each barrio intends to camp before the event. Today this is handled informally — leads email a map request, a city planner manually updates a layout. This feature replaces that workflow with a self-service interactive map where barrio leads place their own camp footprint on the festival site, and city planners have live visibility into the layout as it develops.

The system centers on a **placement phase** concept: CampAdmin opens and closes a window during which leads can edit their polygons. Outside the placement phase, the map is read-only. City planners have edit access at all times to make corrections.

## Scope

**This spec:**
- Interactive satellite map centered on the festival site
- Polygon drawing/editing for each barrio's camp footprint
- Live area display in square metres
- Admin-controlled placement phase (open/close)
- Full polygon version history with preview and restore
- Collaborative editing: all barrio leads + city planning team
- Real-time cursor presence via SignalR
- CampAdmin panel for uploading a GeoJSON limit zone (visual boundary overlay)

**Out of scope (future):**
- Server-side enforcement of the limit zone (currently visual only)
- Conflict detection between overlapping barrio polygons
- Export of the full site map as an image

## Data Model

### New Entities

#### CampPolygon

One record per camp season. Updated in-place when the polygon changes; history is separately tracked in `CampPolygonHistory`. Each year's festival layout is independent.

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | PK, init |
| CampSeasonId | Guid (FK → CampSeason) | Unique (1:1 with CampSeason) |
| GeoJson | string | GeoJSON Feature string. Stored as PostgreSQL `text`. |
| AreaSqm | double | Pre-calculated area in m². Computed client-side by Turf.js. |
| LastModifiedByUserId | Guid (FK → User) | Non-nullable. DeleteBehavior.Restrict. |
| LastModifiedAt | Instant | NodaTime. Updated on every save. |

**Navigation:** `CampSeason`, `LastModifiedByUser`

#### CampPolygonHistory

Append-only log of every polygon version for a camp season. Never updated or deleted. One entry written each time a polygon is saved or restored.

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | PK, init |
| CampSeasonId | Guid (FK → CampSeason) | init. DeleteBehavior.Restrict. |
| GeoJson | string | Snapshot of the polygon at this point in time. init. |
| AreaSqm | double | Area at time of save. init. |
| ModifiedByUserId | Guid (FK → User) | Who saved. init. DeleteBehavior.Restrict. |
| ModifiedAt | Instant | NodaTime. init. |
| Note | string | Human-readable label. Default: `"Saved"`. Restore entries: `"Restored from 2026-03-10 14:32 UTC"`. init. |

**Navigation:** `CampSeason`, `ModifiedByUser`

#### CampMapSettings

One row per year. Created on demand when a CampAdmin or city planning team member first interacts with a year's map settings. The service always operates on the row matching `CampSettings.PublicYear`.

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | PK, init |
| Year | int | The season year this row applies to. Unique. |
| IsPlacementOpen | bool | Whether the placement phase is currently open for this year. Default: `false`. |
| OpenedAt | Instant? | When placement was last opened. Null if never opened. |
| ClosedAt | Instant? | When placement was last closed. |
| LimitZoneGeoJson | string? | GeoJSON FeatureCollection defining the visual boundary for this year. Null until uploaded. |
| UpdatedAt | Instant | Last write timestamp. |

### Relationships

```
CampSeason 1──0..1 CampPolygon
CampSeason 1──∞    CampPolygonHistory (append-only)
```

### Storage

GeoJSON is stored as PostgreSQL `text` columns (not `jsonb`) — the system never queries inside the JSON structure, so jsonb's overhead is unnecessary.

## Authorization

### Roles

| Role | Map access |
|------|-----------|
| **CampAdmin** | Full access at all times. Can open/close placement, upload limit zone, restore any camp's history. Can edit any polygon even when placement is closed. Full admin panel access. |
| **City planning team member** | Full admin panel access: can open/close placement, upload/delete limit zone. Can edit any camp's polygon at all times (including after placement closes). |
| **Camp lead (primary or co-lead)** | Can edit their own camp's polygon when placement is open. Cannot edit other camps. Read-only when closed. |
| **Active volunteer** | Read-only map view. |
| **Anonymous** | Not permitted — map requires authentication. |

**City planning team** is identified by a slug configured in `appsettings.json`:

```json
"CampMap": {
  "CityPlanningTeamSlug": "city-planning"
}
```

This is a manually-managed team (not auto-synced) whose members get map-wide edit access.

### Edit Permission Logic (`CanUserEditAsync`)

```
if user.IsInRole(CampAdmin) → always allowed
if user is member of city planning team → always allowed
if placement is closed → denied
if user is active lead of the camp that owns the target CampSeason → allowed
otherwise → denied
```

### Per-Season Ownership Check

Non-admin, non-city-planning leads can only save polygons for their own camp's season. Attempting to save to a different camp's season polygon returns `403 Forbidden`.

## Workflows

### Placement Phase Lifecycle

```
Closed (default)
    │ CampAdmin or city planning team member opens placement
    ▼
Open
    │ CampAdmin or city planning team member closes placement
    ▼
Closed
```

When placement is open:
- Camp leads whose barrio **has no polygon yet** see an **"Add my barrio on the map"** button. Clicking it enters draw mode locked to their camp season.
- Camp leads whose barrio **already has a polygon** see no button — instead their polygon is highlighted in a distinct colour. Clicking it enters edit mode.
- CampAdmin and city planning team see **"Add a barrio on the map"** followed by a dropdown of all camp seasons that do not yet have a polygon. Selecting one enters draw mode for that season.
- CampAdmin and city planning team can click any existing polygon to edit it.

When placement is closed:
- No add/edit controls for camp leads and volunteers — map is read-only.
- Camp leads still see their own polygon highlighted, but clicking it does not enter edit mode.
- CampAdmin and city planning team retain full edit access regardless.
- A banner indicates placement is closed.

### Polygon Add Workflow (first time)

1. Lead clicks "Add my barrio on the map" (or admin selects a barrio from the dropdown)
2. Draw mode activates — user draws a polygon on the map
3. Turf.js calculates and displays area in m² in real time while drawing
4. User clicks "Save"
5. Client sends PUT `/api/camp-map/polygons/{campSeasonId}` with GeoJSON + area
6. Server checks edit permission, creates `CampPolygon`, appends first `CampPolygonHistory` entry
7. Server broadcasts polygon to all connected clients — polygon appears on everyone's map
8. The "Add" button disappears for that lead; their polygon is now shown highlighted

### Polygon Edit Workflow (subsequent edits)

1. Lead clicks their highlighted polygon (or admin clicks any polygon)
2. Edit mode activates — existing polygon becomes editable via maplibre-gl-draw
3. Turf.js updates area display in real time as the shape changes
4. User clicks "Save"
5. Same as steps 5–7 above — polygon updated in place, history entry appended, broadcast sent

### Polygon History & Restore

- Each save creates an immutable `CampPolygonHistory` entry
- History panel shows: who, when, area, note — sorted newest first
- "Preview" shows the historical polygon overlaid on the current state (not persisted)
- "Restore" calls POST `/api/camp-map/polygons/{campSeasonId}/restore/{historyId}`, which:
  1. Calls `SavePolygonAsync` with the historical GeoJSON
  2. Creates a new history entry with note `"Restored from {iso8601} UTC"`
  3. Broadcasts the restored polygon to all connected users

### Real-Time Cursor Presence

- On connecting to the SignalR hub, users receive a list of currently connected users
- As the user moves their mouse over the map, cursor coordinates are sent to the hub
- The hub relays cursor updates to all OTHER connected users (excluding sender)
- Each remote cursor is rendered as a colored marker with the user's display name
- On disconnect, the hub broadcasts `CursorLeft` with the user's connection ID so others remove the cursor

### Limit Zone

- CampAdmin or city planning team uploads a GeoJSON file via the admin panel
- The limit zone is stored in `CampMapSettings.LimitZoneGeoJson` for the current year
- On the map, the limit zone is rendered as a semi-transparent overlay showing the allowed placement area
- Polygons placed outside the limit zone are visually highlighted (e.g., red outline) but not server-side rejected
- CampAdmin or city planning team can delete the limit zone (sets to null)

## Routes

### Map Page

| Method | Route | Auth | Purpose |
|--------|-------|------|---------|
| GET | `/BarrioMap` | Authenticated | Interactive map for all logged-in users |
| GET | `/BarrioMap/Admin` | CampAdmin or city planning team | Admin panel: placement toggle + limit zone |

### API (AJAX, anti-forgery validated)

| Method | Route | Auth | Purpose |
|--------|-------|------|---------|
| GET | `/api/camp-map/state` | Authenticated | Current settings + all polygons for the active year + list of camp seasons without a polygon |
| GET | `/api/camp-map/polygons/{campSeasonId}/history` | Authenticated | Polygon history for one camp season |
| PUT | `/api/camp-map/polygons/{campSeasonId}` | Authenticated | Save/update polygon for a camp season |
| POST | `/api/camp-map/polygons/{campSeasonId}/restore/{historyId}` | Authenticated | Restore a historical version |

### Export

| Method | Route | Auth | Purpose |
|--------|-------|------|---------|
| GET | `/api/camp-map/export.geojson?year={year}` | CampAdmin or city planning team | Download all polygons for a given year as a GeoJSON FeatureCollection. Each feature includes camp name, slug, year, and area in its `properties`. Defaults to the current active year. |

### Admin Actions (POST, anti-forgery validated)

| Method | Route | Auth | Purpose |
|--------|-------|------|---------|
| POST | `/BarrioMap/Admin/OpenPlacement` | CampAdmin or city planning team | Open the placement phase |
| POST | `/BarrioMap/Admin/ClosePlacement` | CampAdmin or city planning team | Close the placement phase |
| POST | `/BarrioMap/Admin/UploadLimitZone` | CampAdmin or city planning team | Upload GeoJSON limit zone file |
| POST | `/BarrioMap/Admin/DeleteLimitZone` | CampAdmin or city planning team | Remove the limit zone |

### SignalR Hub

| Endpoint | Purpose |
|----------|---------|
| `/hubs/camp-map` | Real-time cursor presence + polygon broadcast |

## Technical Implementation

### Frontend Libraries (CDN)

All loaded from CDN. Added to `About.cshtml` with version and license:

| Library | Version | License | Purpose |
|---------|---------|---------|---------|
| MapLibre GL JS | 4.x | BSD-3 | Interactive map rendering |
| @maplibre/maplibre-gl-draw | 1.x | ISC | Polygon drawing/editing tool |
| Turf.js | 7.x | MIT | Client-side area calculation |
| @microsoft/signalr | (bundled) | MIT | Real-time WebSocket client |

**Satellite tiles:** ESRI World Imagery — `https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}` — free, no API key required.

**Map center:** `[-0.13717, 41.69964]`, zoom 17

### Content Security Policy

`CspNonceMiddleware` must be updated to add:
- `worker-src blob:` — required for MapLibre's web workers
- `https://server.arcgisonline.com` in `connect-src` — for ESRI tile fetching

### Service Layer

`ICampMapService` / `CampMapService` in Application/Infrastructure layers:

```
GetPolygonsAsync(year)                    → all CampPolygon records for CampSeasons in the given year
GetCampSeasonsWithoutPolygonAsync(year)   → list of CampSeason (id, name, slug) that have no CampPolygon yet
GetPolygonHistoryAsync(campSeasonId)      → CampPolygonHistory ordered by ModifiedAt desc
SavePolygonAsync(campSeasonId, geoJson, areaSqm, userId, note = "Saved")
RestorePolygonVersionAsync(campSeasonId, historyId, restoredByUserId)
CanUserEditAsync(userId, campSeasonId)    → bool  // CampAdmin + city planning always true; leads check camp ownership + placement open
IsUserMapAdminAsync(userId)               → bool  // true for CampAdmin or city planning team member
ExportAsGeoJsonAsync(year)                → string (GeoJSON FeatureCollection; properties: campName, slug, year, areaSqm)
GetSettingsAsync()                        → CampMapSettings  // for the current PublicYear; creates row if missing
OpenPlacementAsync(userId)
ClosePlacementAsync(userId)
UpdateLimitZoneAsync(geoJson, userId)
DeleteLimitZoneAsync(userId)
```

`SavePolygonAsync` is the single write path used by both saves and restores. It:
1. Upserts `CampPolygon` (creates on first save, updates thereafter)
2. Appends to `CampPolygonHistory`

### SignalR Hub (`CampMapHub`)

- `[Authorize]` — only authenticated users can connect
- `UpdateCursor(lat, lng)` — client sends cursor position; hub relays to `Others`
- `PolygonUpdated(campSeasonId, geoJson, areaSqm)` — broadcast from `SavePolygonAsync` (via `IHubContext<CampMapHub>`)
- `OnDisconnectedAsync` — broadcasts `CursorLeft(connectionId)` to group

### UI Patterns

**Anti-forgery:** AJAX PUT/POST requests send the `RequestVerificationToken` header (read from the hidden `@Html.AntiForgeryToken()` input in the page). Both `SavePolygon` and `RestorePolygonVersion` are decorated with `[ValidateAntiForgeryToken]`.

**Own camp highlight:** The `Index.cshtml` view receives `USER_CAMP_SEASON_ID` from the server (the CampSeason.Id for the user's camp in the active year, null if none). The user's own camp polygon is rendered in a distinct colour (e.g., `#00bfff`) while other camps use the default colour (e.g., `#ff6600`).

**"Add my barrio" button (leads):** Shown only when placement is open AND `USER_CAMP_SEASON_ID` is set AND that season has no polygon yet. Hidden once the polygon exists — the highlighted shape is the entry point for editing instead.

**"Add a barrio" button + dropdown (CampAdmin / city planning):** Always visible when placement is open (or user is map admin). The dropdown lists all camp seasons for the active year that have no polygon. Selecting one activates draw mode for that session, setting the target `campSeasonId` client-side before the Save call.

**Click-to-edit:** Clicking a polygon that the current user is allowed to edit activates edit mode via `draw.add()` and `draw.changeMode('direct_select')`. For leads, only their own highlighted polygon is clickable for editing.

**History panel XSS safety:** The history list renders using data attributes + event listeners instead of inline `onclick` handlers with embedded GeoJSON strings.

**Save button:** Disabled until a polygon has been drawn or an existing polygon is in edit mode. Always sends to the `campSeasonId` that was set when draw/edit mode was activated.

### EF Core Configuration

- `CampPolygonConfiguration` — unique index on `CampSeasonId`, `DeleteBehavior.Restrict` on both FKs (non-nullable Guids cannot use SetNull)
- `CampPolygonHistoryConfiguration` — index on `(CampSeasonId, ModifiedAt)`, `DeleteBehavior.Restrict` on both FKs
- `CampMapSettingsConfiguration` — unique index on `Year`. No seeded data; rows are created on demand.

### DI Registration

- `ICampMapService` registered as scoped in `InfrastructureServiceCollectionExtensions`
- `builder.Services.AddSignalR()` in `Program.cs`
- `app.MapHub<CampMapHub>("/hubs/camp-map")` in `Program.cs`

## Related Features

| Feature | Relationship |
|---------|-------------|
| Camps Phase 1 | **Prerequisite** — provides `Camp`, `CampSeason`, `CampLead`, `CampSettings`, `RoleNames.CampAdmin` |
| Teams | City planning team is a standard `Team` — no special entity needed |
| Google Sync | Not involved — no Google resources provisioned for the map |
| GDPR | No personal data exported by map APIs. GeoJSON polygons are spatial data only. |

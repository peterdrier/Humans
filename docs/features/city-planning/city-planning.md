<!-- freshness:triggers
  src/Humans.Application/Services/CityPlanning/**
  src/Humans.Web/Controllers/CityPlanningController.cs
  src/Humans.Web/Controllers/CityPlanningApiController.cs
  src/Humans.Web/Controllers/ContainerController.cs
  src/Humans.Web/Hubs/CityPlanningHub.cs
  src/Humans.Web/Authorization/RoleChecks.cs
  src/Humans.Domain/Entities/CityPlanningSettings.cs
  src/Humans.Domain/Entities/CampPolygon.cs
  src/Humans.Domain/Entities/CampPolygonHistory.cs
  src/Humans.Domain/Entities/Container.cs
  src/Humans.Infrastructure/Data/Configurations/CityPlanning/**
  src/Humans.Web/wwwroot/js/city-planning/**
-->
<!-- freshness:flag-on-change
  Polygon entities, container entity, MVC/API/SignalR routes, sound-zone color encoding, or map-admin authorization may have shifted.
-->

# City Planning

## Business Context

City Planning organizes the physical layout of the event site across three distinct phases and three screens:

1. **Main map** (`/CityPlanning`) — read-only overview available to all authenticated users. Shows official zones, barrio polygons, and optionally containers and the limit zone (both togglable).
2. **Barrio placement map** (`/CityPlanning/BarrioMap`) — collaborative real-time tool for barrio leads and map admins to draw and adjust camp polygons. Only meaningful while the barrio placement phase is open.
3. **Container placement** (`/CityPlanning/ContainerMap/{year}`) — map where barrio leads and admins place physical containers on the site. Containers are first created and managed (with description and photos) via a list view, then geo-placed during the container placement phase.

**Goals:**
- Give everyone a live view of the evolving site layout
- Let camp leads stake out their own barrio without manual back-and-forth with organizers
- Detect and flag spatial problems early (out-of-bounds placements, overlaps)
- Let barrio leads and admins manage physical containers and place them on the map
- Give admins tools to manage both placement lifecycles and site data

## User Stories

### Main map
- **US-38.1: Read-only overview** — Any authenticated user sees official zones and barrio polygons always visible. Containers and barrio zones (limit zone) can be toggled on/off. Measure tool available.
- **US-38.2: Navigation shortcuts** — Map admins always see links to the barrio placement map and container placement map. Barrio leads see the barrio placement link when `IsPlacementOpen`, and the container placement link when `IsContainerPlacementOpen`.

### Barrio placement map
- **US-38.3: View the Barrio Map** — Barrio leads and map admins see all placed barrios on a full-screen aerial map, color-coded by sound zone, with name labels and warning indicators
- **US-38.4: Place My Barrio** — Camp leads draw their polygon during the placement phase; area and edge lengths shown in real time
- **US-38.5: Adjust an Existing Barrio** — Camp leads (own polygon, while open) and map admins (any polygon, always) can edit vertices, reposition, and reshape
- **US-38.6: Out-of-Bounds and Overlap Warnings** — Red crosshatch when outside the limit zone; orange stripes when overlapping another barrio; ⚠️ prepended to labels
- **US-38.7: View Polygon History** — Full version history per polygon; map admins can restore any past version
- **US-38.8: Real-Time Collaborative View** — Remote cursors visible live; polygon saves broadcast to all connected clients via SignalR
- **US-38.9: Placement Phase Card** — Shows open/closed status badge and a help modal with scheduled dates (informational, Spain time)
- **US-38.10: Admin — Manage Placement Phase** — Toggle barrio placement open/closed; timestamps recorded
- **US-38.11: Admin — Set Placement Dates** — Set informational open/close datetimes shown in the help modal; not enforced
- **US-38.12: Admin — Upload/Delete Limit Zone** — GeoJSON boundary for allowed placement area; rendered as dashed colored outline (color-coded by sound zone); download/delete supported
- **US-38.13: Admin — Upload/Delete Official Zones** — Read-only named overlay (dark gray, labeled); each Feature requires a `name` property; download/delete supported
- **US-38.14: Admin — Export All Placements** — Download all polygons as a GeoJSON FeatureCollection
- **US-38.15: Admin — Add Polygon on Behalf of a Camp** — Place a polygon for any unmapped camp season via admin dropdown
- **US-38.16: Distance Measuring Tool** — Any authenticated user can toggle a two-point ruler. First click drops a point and starts a live rubber-band line with a distance label; second click locks the measurement; a third click starts a new one. Distance shows as `NNN m` under 1 km and `N.NN km` at or above. Escape or re-clicking the button exits and clears all measure layers.

### Containers
- **US-38.17: Manage Containers (admin)** — Map admins create, edit, and delete containers for the whole event year via `/CityPlanning/BarrioMap/Admin/Containers/{year}`. Each container has a name, description, and an optional photo.
- **US-38.18: Manage Containers (barrio lead)** — Barrio leads manage the containers assigned to their camp via `/Camp/{slug}/Season/{year}/Containers`. Same fields; scoped to own camp season.
- **US-38.19: Container Photos** — Each container can have one image uploaded/replaced/deleted. Stored on the filesystem with DB fallback.
- **US-38.20: Placed Badge** — Container list views show a "Placed" badge next to containers that have a location set.
- **US-38.21: Export Containers as GeoJSON** — Map admins can download all placed containers for the year as a GeoJSON FeatureCollection. Barrio leads download only their own camp's containers.
- **US-38.22: Place Containers on Map** — During the container placement phase, barrio leads (own containers) and map admins (all containers) place containers on the map by dragging from the sidebar. Containers are represented as rotatable rectangles with configurable dimensions.
- **US-38.23: Admin — Manage Container Placement Phase** — Toggle container placement open/closed independently of barrio placement.

## Data Model

### CampPolygon
```
CampPolygon
├── Id: Guid
├── CampSeasonId: Guid (FK → CampSeason, unique — one polygon per season)
├── GeoJson: string (GeoJSON Feature, single Polygon geometry)
├── AreaSqm: double
├── LastModifiedByUserId: Guid (FK → User)
└── LastModifiedAt: Instant
```

### CampPolygonHistory
```
CampPolygonHistory
├── Id: Guid
├── CampSeasonId: Guid (FK → CampSeason)
├── GeoJson: string
├── AreaSqm: double
├── ModifiedByUserId: Guid (FK → User)
├── ModifiedAt: Instant
└── Note: string ("Saved" by default; "Restored from {ISO timestamp}" for restores)
```

### CityPlanningSettings (singleton per year)
```
CityPlanningSettings
├── Id: Guid
├── Year: int [unique]
├── IsPlacementOpen: bool
├── OpenedAt: Instant?
├── ClosedAt: Instant?
├── PlacementOpensAt: LocalDateTime? (informational scheduled open; not enforced)
├── PlacementClosesAt: LocalDateTime? (informational scheduled close; not enforced)
├── IsContainerPlacementOpen: bool
├── ContainerPlacementOpenedAt: Instant?
├── LimitZoneGeoJson: string? (GeoJSON FeatureCollection — site boundary)
├── OfficialZonesGeoJson: string? (GeoJSON FeatureCollection — named overlay zones)
└── UpdatedAt: Instant
```

### Container
```
Container
├── Id: Guid
├── Year: int
├── CampSeasonId: Guid? (FK → CampSeason — null means org-level container)
├── Name: string
├── Description: string?
├── ImageStoragePath: string?
├── ImageContentType: string?
├── ImageFileName: string?
├── LocationGeoJson: string? (GeoJSON Feature, Polygon geometry — null when unplaced)
├── CreatedAt: Instant
└── UpdatedAt: Instant
```

## Frontend Architecture

### Main map (`/js/city-planning/main.js` + `config.js`)
Minimal read-only map. Fetches `/api/city-planning/state` and renders layers. No draw library. Imports `measure.js` from the container-map module for the measure tool.

| Layer | Always visible | Togglable |
|-------|---------------|-----------|
| Official zones | ✓ | |
| Barrio polygons | ✓ | |
| Limit zone (barrio zones) | | ✓ (off by default) |
| Containers | | ✓ (off by default) |

### Barrio placement map (`/js/city-planning/barrio-map/`)

| Module | Responsibility |
|--------|---------------|
| `main.js` | Entry point: map init, draw setup, button handlers |
| `state.js` | Shared mutable state (map, draw, campMap data, active edit session) |
| `layers.js` | Layer/source definitions, `renderMap()`, pattern generators |
| `geometry.js` | Turf.js helpers: `isOutsideZone`, `overlapsOtherCamps`, feature builders |
| `edit.js` | Edit mode lifecycle, draw event handlers, popup, history offcanvas |
| `signalr.js` | SignalR connection, cursor broadcast, polygon update handler |
| `config.js` | Server-rendered config values read from DOM data attributes |
| `measure.js` | Distance measuring tool — sources/layers, click state machine, rubber-band preview |

**State flow:**
1. Page loads → `GET /api/city-planning/state` fetches settings + all polygons
2. Map renders via `renderMap()` using fetched data
3. Edit actions call `PUT /api/city-planning/camp-polygons/{id}`
4. Server broadcasts `CampPolygonUpdated` via SignalR → all clients re-render that polygon

**Visual encoding (sound zones — barrio map):**

| Zone | Fill color | Outline color |
|------|-----------|--------------|
| Blue (0) | `#88aadd` | `#2266cc` |
| Green (1) | `#88bb88` | `#229944` |
| Yellow (2) | `#ddcc66` | `#cc9900` |
| Orange (3) | `#ddaa66` | `#cc6600` |
| Red (4) | `#dd8888` | `#cc1111` |
| Surprise (5) | Rainbow stripe pattern | `#cc00cc` |
| Unknown | `#aaaaaa` | `#666666` |

Own camp polygon uses 2× outline width and higher fill opacity. Active edit polygon is dimmed across all others.

**Warning overlays (barrio map):**
- Out-of-bounds: red crosshatch pattern (`#ff2222`)
- Overlap: orange dashed horizontal stripes (`#ff8800`)

### Container placement map (`/js/container-map/`)
Full-screen map with a sidebar listing placed/unplaced containers. Containers are dragged from the sidebar onto the map as rotatable rectangles. Uses CSS custom properties `--container-fill` (unselected) and `--container-fill-selected` (selected) for colors, defined in `ContainerMap.cshtml`.

## Authorization

| Action | Required |
|--------|----------|
| View main map | Authenticated |
| View barrio placement map | Authenticated |
| Place / edit own barrio polygon | Authenticated + own camp season exists + `IsPlacementOpen` |
| Edit any barrio polygon | Map admin |
| Restore polygon version | Map admin |
| Export barrio GeoJSON | Map admin |
| Admin panel (placement toggle, dates, zone uploads) | Map admin |
| View container placement map | Map admin, or barrio lead + `IsContainerPlacementOpen` |
| Manage containers (create/edit/delete/image) | Map admin (all), barrio lead (own camp) |
| Place / remove containers on map | Map admin (all), barrio lead (own camp) + `IsContainerPlacementOpen` |
| Export container GeoJSON | Map admin (all), barrio lead (own camp) |

Map admin = `RoleChecks.IsCampAdmin(User)` **or** member of the City Planning team (`ICityPlanningService.IsCityPlanningTeamMemberAsync`).

## URL Structure

### MVC Routes

| Route | Description |
|-------|-------------|
| `GET /CityPlanning` | Read-only overview map |
| `GET /CityPlanning/BarrioMap` | Barrio placement map |
| `GET /CityPlanning/BarrioMap/Admin` | Admin settings panel |
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
| `GET /CityPlanning/BarrioMap/Admin/Containers/{year}` | Admin container list |
| `POST /CityPlanning/BarrioMap/Admin/Containers/{year}/Create` | Create org-level container |
| `POST /CityPlanning/BarrioMap/Admin/Containers/{year}/Barrios/{seasonId}/Create` | Create barrio container |
| `POST /CityPlanning/BarrioMap/Admin/Containers/{id}/Edit` | Edit container |
| `POST /CityPlanning/BarrioMap/Admin/Containers/{id}/Delete` | Delete container |
| `POST /CityPlanning/BarrioMap/Admin/Containers/{id}/Image/Upload` | Upload container image |
| `POST /CityPlanning/BarrioMap/Admin/Containers/{id}/Image/Delete` | Delete container image |
| `GET /CityPlanning/ContainerMap/{year}` | Container placement map |
| `GET /Camp/{slug}/Season/{year}/Containers` | Barrio lead container list |
| `POST /Camp/{slug}/Season/{year}/Containers/Create` | Create container (barrio lead) |
| `POST /Camp/{slug}/Season/{year}/Containers/{id}/Edit` | Edit container (barrio lead) |
| `POST /Camp/{slug}/Season/{year}/Containers/{id}/Delete` | Delete container (barrio lead) |
| `POST /Camp/{slug}/Season/{year}/Containers/{id}/Image/Upload` | Upload image (barrio lead) |
| `POST /Camp/{slug}/Season/{year}/Containers/{id}/Image/Delete` | Delete image (barrio lead) |

### API Routes

| Route | Description |
|-------|-------------|
| `GET /api/city-planning/state` | Map state: settings + all polygons + unmapped seasons |
| `PUT /api/city-planning/camp-polygons/{campSeasonId}` | Save or update a polygon |
| `GET /api/city-planning/camp-polygons/{campSeasonId}/history` | Version history for a polygon |
| `POST /api/city-planning/camp-polygons/{campSeasonId}/restore/{historyId}` | Restore a historical version |
| `GET /api/city-planning/export.geojson?year={year}` | Export all barrio polygons as GeoJSON |
| `GET /api/city-planning/containers/{year}` | All containers for year with `canEdit` flag |
| `PUT /api/city-planning/containers/{id}/placement` | Set container location GeoJSON |
| `DELETE /api/city-planning/containers/{id}/placement` | Remove container from map |
| `GET /api/city-planning/containers/{year}/export.geojson` | Export placed containers as GeoJSON |

### SignalR Hub

`/hubs/city-planning` — broadcasts `CampPolygonUpdated(campSeasonId, geoJson, areaSqm, soundZone, campName)` and receives `CursorMoved(lng, lat)` from clients.

## Related Features

- [Camps](../camps/camps.md) — `CampSeason` is the anchor entity; placement requires an approved camp season for the current year
- [Authentication](../auth/authentication.md) — All map routes require authentication
- [Administration](../global/administration.md) — Admin role gates map admin actions

## Future

- Containers will be assigned to map zones in a third phase (zone-assignment feature, follows container placement).
- Hypothetical: other teams could use this map to collaborate on placement for art, power lines, …

# City Planning

## Business Context

City Planning gives camps a collaborative, real-time tool to stake out and refine their polygon on an aerial map of the site before the event. Camp leads draw their own shapes during the placement phase; map admins oversee the process, manage the site boundary and informational overlays, and export the final placement as GeoJSON for use in logistics and public materials.

**Goals:**
- Let camp leads place their own barrio without manual back-and-forth with organizers
- Give everyone a live view of the evolving site layout
- Detect and flag spatial problems early (out-of-bounds placements, overlaps)
- Give admins tools to manage the placement lifecycle and site data

## User Stories

- **US-38.1: View the Barrio Map** — Members see all placed barrios on a full-screen aerial map, color-coded by sound zone, with name labels and warning indicators
- **US-38.2: Place My Barrio** — Camp leads draw their polygon during the placement phase; area and edge lengths shown in real time
- **US-38.3: Adjust an Existing Barrio** — Camp leads (own polygon, while open) and map admins (any polygon, always) can edit vertices, reposition, and reshape
- **US-38.4: Out-of-Bounds and Overlap Warnings** — Red crosshatch when outside the limit zone; orange stripes when overlapping another barrio; ⚠️ prepended to labels
- **US-38.5: View Polygon History** — Full version history per polygon; map admins can restore any past version
- **US-38.6: Real-Time Collaborative View** — Remote cursors visible live; polygon saves broadcast to all connected clients via SignalR
- **US-38.7: Placement Phase Card** — Shows open/closed status badge and a help modal with scheduled dates (informational, Spain time)
- **US-38.8: Admin — Manage Placement Phase** — Toggle placement open/closed; timestamps recorded
- **US-38.9: Admin — Set Placement Dates** — Set informational open/close datetimes shown in the help modal; not enforced
- **US-38.10: Admin — Upload/Delete Limit Zone** — GeoJSON boundary for allowed placement area; rendered as dashed white outline; download/delete supported
- **US-38.11: Admin — Upload/Delete Official Zones** — Read-only named overlay (dark gray, labeled); each Feature requires a `name` property; download/delete supported
- **US-38.12: Admin — Export All Placements** — Download all polygons as a GeoJSON FeatureCollection
- **US-38.13: Admin — Add Polygon on Behalf of a Camp** — Place a polygon for any unmapped camp season via admin dropdown

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
├── LimitZoneGeoJson: string? (GeoJSON FeatureCollection — site boundary)
├── OfficialZonesGeoJson: string? (GeoJSON FeatureCollection — named overlay zones)
└── UpdatedAt: Instant
```

## Frontend Architecture

The map page is a full-screen MapLibre GL JS map with a vanilla ES module frontend (`/js/barrio-map/`). Files:

| Module | Responsibility |
|--------|---------------|
| `main.js` | Entry point: map init, draw setup, button handlers |
| `state.js` | Shared mutable state (map, draw, campMap data, active edit session) |
| `layers.js` | Layer/source definitions, `renderMap()`, pattern generators |
| `geometry.js` | Turf.js helpers: `isOutsideZone`, `overlapsOtherCamps`, feature builders |
| `edit.js` | Edit mode lifecycle, draw event handlers, popup, history offcanvas |
| `signalr.js` | SignalR connection, cursor broadcast, polygon update handler |
| `config.js` | Server-rendered config values read from DOM data attributes |

**State flow:**
1. Page loads → `GET /api/city-planning/state` fetches settings + all polygons
2. Map renders via `renderMap()` using fetched data
3. Edit actions call `PUT /api/city-planning/camp-polygons/{id}`
4. Server broadcasts `CampPolygonUpdated` via SignalR → all clients re-render that polygon

**Visual encoding (sound zones):**

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

**Warning overlays:**
- Out-of-bounds: red crosshatch pattern (`#ff2222`)
- Overlap: orange dashed horizontal stripes (`#ff8800`)

## Authorization

| Action | Required |
|--------|----------|
| View map | Authenticated |
| Place / edit own polygon | Authenticated + own camp season exists + placement open |
| Edit any polygon | Map admin |
| Restore polygon version | Map admin |
| Export GeoJSON | Map admin |
| Admin panel (placement toggle, dates, zone uploads) | Map admin |

Map admin is determined at the controller level via claims-first pattern: `RoleChecks.IsCampAdmin(User)` for global roles, then `ICityPlanningService.IsCityPlanningTeamMemberAsync` for team-specific access.

## URL Structure

### MVC Routes

| Route | Description |
|-------|-------------|
| `GET /CityPlanning` | Full-screen map page |
| `GET /CityPlanning/Admin` | Admin settings panel |
| `POST /CityPlanning/Admin/OpenPlacement` | Open placement phase |
| `POST /CityPlanning/Admin/ClosePlacement` | Close placement phase |
| `POST /CityPlanning/Admin/UpdatePlacementDates` | Set informational open/close datetimes |
| `POST /CityPlanning/Admin/UploadLimitZone` | Upload limit zone GeoJSON |
| `GET /CityPlanning/Admin/DownloadLimitZone` | Download limit zone GeoJSON |
| `POST /CityPlanning/Admin/DeleteLimitZone` | Delete limit zone |
| `POST /CityPlanning/Admin/UploadOfficialZones` | Upload official zones GeoJSON |
| `GET /CityPlanning/Admin/DownloadOfficialZones` | Download official zones GeoJSON |
| `POST /CityPlanning/Admin/DeleteOfficialZones` | Delete official zones |

### API Routes

| Route | Description |
|-------|-------------|
| `GET /api/city-planning/state` | Map state: settings + all polygons + unmapped seasons |
| `PUT /api/city-planning/camp-polygons/{campSeasonId}` | Save or update a polygon |
| `GET /api/city-planning/camp-polygons/{campSeasonId}/history` | Version history for a polygon |
| `POST /api/city-planning/camp-polygons/{campSeasonId}/restore/{historyId}` | Restore a historical version |
| `GET /api/city-planning/export.geojson?year={year}` | Export all polygons as GeoJSON |

### SignalR Hub

`/hubs/city-planning` — broadcasts `CampPolygonUpdated(campSeasonId, geoJson, areaSqm, soundZone, campName)` and receives `CursorMoved(lng, lat)` from clients.

## Related Features

- [Camps](20-camps.md) — `CampSeason` is the anchor entity; placement requires an approved camp season for the current year
- [Authentication](01-authentication.md) — All map routes require authentication
- [Administration](09-administration.md) — Admin role gates map admin actions

## Future

- Certain: in a second phase, barrios will be able to place their container.
- Hypothetical: other teams could use this map to collaborate on placement for art, power lines, ...

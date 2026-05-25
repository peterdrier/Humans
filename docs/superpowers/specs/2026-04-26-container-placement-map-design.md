# Container Placement Map — Design Spec

**Date:** 2026-04-26
**Status:** Implemented in PR peterdrier/Humans#389 — see also `docs/sections/Containers.md` and `docs/sections/CityPlanning.md` for the section invariants.

## Overview

A dedicated full-screen map page where authorised users can position shipping containers within the festival site. City-planning team members and admins have permanent access; barrio leads can only place when the container placement phase is open.

The page shows a single unified map for all containers the current user can edit, rather than one map per container. Users click a container in the sidebar to activate it, drag it to position, rotate it via a drag handle, and each drop auto-saves.

---

## 1. Data Model

### Container entity — new field

Add one nullable column to the `containers` table:

| Column | Type | Nullable | Notes |
|--------|------|----------|-------|
| `location_geo_json` | `TEXT` | YES | GeoJSON Feature string; null = unplaced |

No separate `rotation_degrees` column. The rotation is stored inside the GeoJSON Feature's `properties` object (see below) to keep everything together.

**Placed vs unplaced:** `location_geo_json IS NULL` → unplaced.

### GeoJSON Feature structure

The stored Feature is a **pentagon polygon** — a 20 ft shipping container (6 m × 2.4 m rectangle) with a 1.2 m equilateral triangle protruding from the door end. The triangle's tip direction is the door bearing; no separate field is needed.

```json
{
  "type": "Feature",
  "geometry": {
    "type": "Polygon",
    "coordinates": [[ /* 6 vertices + closing point, WGS-84 */ ]]
  },
  "properties": {
    "center_lng": -0.1372,
    "center_lat": 41.6998,
    "rotation_degrees": 37.5
  }
}
```

`center_lng`, `center_lat`, and `rotation_degrees` are stored alongside the polygon so the JS can reconstruct the rotation handle position on reload without re-deriving it from vertex geometry.

### Pentagon geometry (canonical construction)

All coordinates are derived in JS using Turf.js from `(centerLng, centerLat, rotationDegrees)`:

1. Build an unrotated rectangle: half-length = 3 m east/west, half-width = 1.2 m north/south (in metres, converted to degrees via `turf.destination`)
2. Append triangle: tip at 4.2 m east of center (3 m body + 1.2 m protrusion), base matches the door-end short edge
3. Close the polygon
4. Apply `turf.transformRotate(feature, rotationDegrees, { pivot: center })`

The resulting polygon has 6 vertices (+ closing): `back-left → back-right → door-right → triangle-tip → door-left → back-left` (anti-clockwise, matching GeoJSON convention). The triangle tip is always vertex index 3 before closing.

### EF Core migration

- Add `string? LocationGeoJson` to `Container`
- Migration adds `location_geo_json TEXT NULL` to `containers`
- No index needed (not queried by this column)

### ContainerDto

Add `string? LocationGeoJson` to the existing `ContainerDto` record.

---

## 2. Service layer

`IContainerService` gains one new method:

```csharp
Task<ContainerDto> SavePlacementAsync(Guid id, string geoJson, CancellationToken ct);
```

Implementation validates that `geoJson` is valid JSON (existing `IsValidJson` helper in the controller can move to a shared utility), sets `LocationGeoJson`, updates `UpdatedAt`, and returns the updated DTO. No other business logic — placement validity (phase open, barrio match) is enforced in the controller/API layer.

---

## 3. API

Two new endpoints on `CityPlanningApiController`:

### `GET /api/city-planning/containers/{year}`

Returns all containers for the year. Used by the placement map on load.

**Authorization:** any authenticated user who is either a map admin or a barrio lead (camp lead role). All containers for the year are returned; `canEdit` filters what the client may interact with.

**Response:**

```json
[
  {
    "id": "...",
    "name": "Container A",
    "description": "...",
    "campSeasonId": "..." ,
    "locationGeoJson": "{ ... }" ,
    "canEdit": true
  }
]
```

`canEdit` is `true` if the current user is a map admin, or is the barrio lead for this container's camp and the placement phase is open. `locationGeoJson` is null if unplaced.

### `PUT /api/city-planning/containers/{id}/placement`

Saves or updates the placement GeoJSON for one container.

**Authorization:**
- Map admin: always
- Barrio lead: only when `IsContainerPlacementOpen` and container belongs to their camp season

**Request body:** `{ "geoJson": "..." }`

**Validation:**
- `geoJson` must be valid JSON
- `geoJson` must parse as a GeoJSON Feature with Polygon geometry and the three required properties (`center_lng`, `center_lat`, `rotation_degrees`)

**Response:** the updated `ContainerDto` (200) or 403/422 on error.

### `DELETE /api/city-planning/containers/{id}/placement`

Clears the placement (sets `LocationGeoJson` to null). Same authorization rules as PUT.

**Response:** 204.

---

## 4. Map page

### Route

`GET /CityPlanning/ContainerMap/{year}`

**Authorization:**
- Map admin (CampAdmin role or city-planning team member): always accessible
- Barrio lead: only when `IsContainerPlacementOpen`; returns 403 otherwise

**ViewModel:** `ContainerMapViewModel` containing `Year`, `IsMapAdmin`, `UserCampSeasonId` (null for admins), `MapBounds` (JSON), `EsriTilesUrl`.

### Nav link

- On `Containers.cshtml` (admin page): a "Place on map" button at the top of the page linking to `/CityPlanning/ContainerMap/{year}`
- On `Container/Index.cshtml` (barrio page): a "Place on map" button visible when placement is open or user is map admin

### View

Full-screen layout matching `CityPlanning/Index.cshtml`. Includes:
- MapLibre GL JS 5.20.1
- Turf.js 7.1.0
- **No** MapboxDraw (containers use custom drag/rotate interaction, not the draw control)
- **No** SignalR (placement saves are fire-and-forget; real-time multi-user editing is out of scope)
- Server-rendered `<script>` config block with `YEAR`, `IS_MAP_ADMIN`, `USER_CAMP_SEASON_ID`, `MAP_BOUNDS`, `ESRI_TILES_URL`

---

## 5. JS architecture

New directory: `wwwroot/js/container-map/`

| File | Purpose |
|------|---------|
| `main.js` | Entry point: init map, load data, wire up sidebar and interaction |
| `config.js` | Server-rendered constants (same pattern as city-planning/config.js) |
| `geometry.js` | `buildContainerPolygon(centerLng, centerLat, rotationDegrees)` — returns GeoJSON Feature; `containerFeatureToGeoJson(feature)` — serialises for API |
| `layers.js` | `addContainerLayers(map)`, `addBackgroundLayers(map, stateData)` — read-only camp polygons + official zones reusing styling from the main map's layers.js |
| `sidebar.js` | Sidebar DOM: render unplaced/placed lists, handle click-to-activate |
| `interaction.js` | Custom drag-to-move and drag-handle-to-rotate using MapLibre mouse events |
| `api.js` | `savePlacement(id, geoJson)`, `clearPlacement(id)`, `loadContainers(year)` |

Shared files from `city-planning/` that are **imported directly** (not copied):
- `city-planning/config.js` is **not** shared — each map has its own config block
- Background layer styling constants (camp polygon colours, official zone styles) are extracted into a shared `wwwroot/js/shared/map-styles.js` module referenced by both maps

---

## 6. Container rendering

### Layers (MapLibre sources + layers)

**Source:** `containers` — GeoJSON FeatureCollection, populated from the API response on load.

**Layers (bottom to top):**

1. `containers-readonly-fill` — all containers where `canEdit = false`. Fill: `#e8a020`, opacity 0.6.
2. `containers-readonly-outline` — outline for read-only containers. Colour: `#c07010`, width 1.5.
3. `containers-editable-fill` — containers where `canEdit = true` and not active. Fill: `#e8a020`, opacity 0.85.
4. `containers-editable-outline` — outline for editable containers. Colour: `#ffffff`, width 2.
5. `containers-active-fill` — the currently active container (being placed/moved). Fill: `#4fc3f7`, opacity 0.9.
6. `containers-active-outline` — outline for active container. Colour: `#ffffff`, width 2.5.
7. `containers-labels` — symbol layer, `text-field: ['get', 'name']`, small white text, only shown above zoom 17.

**Active container** is a separate GeoJSON source (`container-active`) updated in place during drag to avoid re-rendering the full collection.

### Unplaced containers

Unplaced containers are **not** rendered on the map until the user activates one from the sidebar. Once activated, the container polygon appears at the barrio center (or site center for admins) in the active style.

### Rotation handle

When a container is active or selected, a single blue circle (radius 8 px, CSS `position: absolute` overlaid on the map via `map.project()`) is rendered at the triangle tip vertex (always vertex index 3 of the active polygon's coordinate ring). After every drag update the handle is repositioned by re-projecting vertex 3 to screen coordinates. This is a DOM element, not a MapLibre layer, for reliable mouse event handling.

---

## 7. Interaction model

### Click to activate (unplaced)

1. User clicks an unplaced container card in the sidebar
2. **Barrio center** is computed as `turf.centroid` of the camp's polygon, if one exists. Fallback (no polygon yet, or org container): geographic center of `CONFIG.MAP_BOUNDS`.
3. JS calls `buildContainerPolygon(barrioCenter.lng, barrioCenter.lat, 0)` (default rotation = 0°, pointing east)
4. Polygon added to `container-active` source; map pans to center it in view
5. Sidebar card highlighted; container moves to a "placing…" state

### Drag to move

- `mousedown` on the active container fill layer → begin drag
- `mousemove` → recompute polygon with new center, update `container-active` source and reposition rotation handle DOM element
- `mouseup` → call `savePlacement(id, geoJson)`, on success move card to "Placed" section in sidebar with ✓; on error show a toast and keep the container active

### Drag handle to rotate

- `mousedown` on rotation handle DOM element → begin rotation
- `mousemove` → compute bearing from container center to mouse position (via `turf.bearing`), call `buildContainerPolygon(center, bearing)`, update `container-active` source
- `mouseup` → call `savePlacement(id, geoJson)`

### Select placed container

Clicking a placed editable container on the map selects it: it moves to the `container-active` source (active style), the rotation handle appears, and the sidebar scrolls to its card in the "Placed" section. Drag to reposition → same per-drop save flow.

### Clear placement

A "Clear placement" button on each placed container's sidebar card. Clicking it shows a browser `confirm()` dialog — "Remove placement for «Container Name»? This cannot be undone." — before proceeding. On confirmation, calls `DELETE /api/city-planning/containers/{id}/placement`, removes the polygon from the map, and moves the card back to the "Unplaced" section.

---

## 8. Access control summary

| User | Phase open | Phase closed |
|------|-----------|--------------|
| Map admin (CampAdmin / city-planning team) | Full access | Full access |
| Barrio lead | Can place own containers | Page returns 403 |

The API enforces these rules independently of the UI — a barrio lead hitting `PUT /api/city-planning/containers/{id}/placement` when the phase is closed gets 403.

---

## 9. Out of scope

- Real-time multi-user updates (SignalR) — single-user workflow is sufficient for this scale
- Limit zone display on the placement map
- Container resizing (all containers are fixed 20 ft)
- Collision/overlap detection between containers
- Undo/redo
- Keyboard rotation ([ ] keys) — drag handle is the sole rotation mechanism

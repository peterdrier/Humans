# City Planning — GeoJSON Polygon Import

**Date:** 2026-04-25
**Section:** CityPlanning / Admin

---

## Context

The CityPlanning Admin page already has an Export card that downloads all camp polygons for the current year as a GeoJSON FeatureCollection. This feature adds a symmetrical Import card that lets a map admin upload a GeoJSON file to bulk-update polygon geometries.

---

## Goals

- Upload a GeoJSON file containing polygon features
- Match each feature to a camp in the current settings year by `campName` or `campSlug` property (case-insensitive)
- Compute area client-side using Turf.js before saving
- Preview matched/unrecognized features — including old vs. new area — before committing
- Save via the existing per-polygon save API (no new write logic)
- Record each update in polygon history with note `"Imported YYYY-MM-DD HH:mm"` attributed to the importing user

---

## Out of Scope

- Importing to a year other than the current settings year
- Deleting polygons not present in the import file
- Creating new camps from the import file
- Server-side area computation (stays client-side with Turf.js)

---

## Architecture

### Backend changes

**1. `SaveCampPolygonRequest` — add optional `Note` field**

```csharp
public sealed record SaveCampPolygonRequest(string GeoJson, double AreaSqm, string? Note = null);
```

The service/repository already accepts a `note` parameter in `SavePolygonAndAppendHistoryAsync`. The controller passes `request.Note ?? "Saved"` through to the service.

### No other backend changes

No new endpoints needed. The import JS fetches `GET /api/city-planning/state` (the same call the map makes on load) at import time to get a fresh camp list. The response already contains:

- `campPolygons[]` — camps with existing polygons: `{campSeasonId, campName, campSlug, areaSqm, ...}`
- `campSeasonsWithoutPolygon[]` — camps with no polygon yet: `{campSeasonId, campName, campSlug}`

Fetching at import time (rather than page load) ensures the list is always fresh.

All polygon writes go through the existing `PUT /api/city-planning/camp-polygons/{campSeasonId}` endpoint with `note: "Imported YYYY-MM-DD"` set in the request body.

---

## Frontend

### Admin page changes (`Admin.cshtml`)

Add an **Import** card after the existing Export card. The card contains:

- A file `<input type="file" accept=".geojson,application/geo+json">`
- A disabled **Preview import** button, enabled once a valid file is selected
- A result banner area (hidden until import completes)

### JS flow (`wwwroot/js/city-planning/import.js`)

**Phase 1 — File read & matching (on file select or "Preview" click):**

1. Read file via `FileReader`
2. Parse JSON; validate it is a GeoJSON FeatureCollection
3. Fetch `GET /api/city-planning/state` to get a fresh camp list; build a lookup from both `campPolygons` and `campSeasonsWithoutPolygon`
4. For each feature, attempt match against camp list:
   - Compare `feature.properties.campName` (case-insensitive) to `campName`
   - Fall back to `feature.properties.campSlug` (case-insensitive) to `campSlug`
5. For matched features, compute area with `turf.area(feature)` (result in m²)
6. Build two lists: **matched** (campSeasonId, campName, previousAreaSqm, newAreaSqm) and **unrecognized** (feature name or index)

**Phase 2 — Preview dialog:**

Show a Bootstrap modal with two sections:

- **Will be updated** — table with columns: Camp name | Previous area | New area
  - Previous area shows `—` if the camp has no existing polygon
  - Areas formatted as `1,234 m²`
- **Unrecognized features (will be skipped)** — shown only if non-empty; yellow/warning styling; lists the unrecognized feature names

**Confirm** button submits. **Cancel** closes the modal.

**Phase 3 — Import execution (on Confirm):**

1. Disable Confirm button, show spinner + "Updating N polygons..."
2. Iterate matched camps sequentially:
   - `PUT /api/city-planning/camp-polygons/{campSeasonId}` with `{ geoJson, areaSqm, note: "Imported YYYY-MM-DD HH:mm" }` (UTC, formatted by JS at import time)
3. On completion, close modal, show result banner on the Admin page:
   - Success: "N polygons updated."
   - Partial failure: "N updated, M failed." with a collapsible error list

---

## History Entries

Each updated polygon receives a `CampPolygonHistory` entry:

| Field | Value |
|---|---|
| `Note` | `"Imported 2026-04-25 14:30"` (UTC, format `Imported YYYY-MM-DD HH:mm`) |
| `ModifiedByUserId` | Authenticated admin user |
| `ModifiedAt` | Server timestamp at save time |
| `GeoJson` | New polygon geometry |
| `AreaSqm` | Newly computed area |

The `"Imported YYYY-MM-DD HH:mm"` note is visually distinct from `"Saved"` (map edit) and `"Restored from ..."` (restore) in the history panel.

---

## Error Handling

| Scenario | Behaviour |
|---|---|
| File is not valid JSON | Show inline error: "Invalid file — not valid JSON" |
| File is valid JSON but not a FeatureCollection | Show inline error: "File must be a GeoJSON FeatureCollection" |
| No features match any camp | Show modal with empty "Will be updated" list and all features in "Unrecognized"; Confirm disabled |
| Individual save API call fails | Mark that camp as failed; continue with remaining; show partial-failure banner |
| `GET /api/city-planning/state` fetch fails | Show inline error: "Could not load camp list. Please try again." |

---

## Authorization

- Import card is visible only to map admins (same condition as the Export card)
- `GET /api/city-planning/state` is already authorized (requires login); admin-only data is filtered by the service
- Individual polygon save calls are already guarded by the existing save endpoint's authorization

---

## Related

- Export: `GET /api/city-planning/export.geojson`
- Polygon save (reused as-is): `PUT /api/city-planning/camp-polygons/{campSeasonId}`
- History: `CampPolygonHistory`, `SavePolygonAndAppendHistoryAsync`
- Section invariants: `docs/sections/CityPlanning.md`

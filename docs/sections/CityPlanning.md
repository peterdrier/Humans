<!-- freshness:triggers
  src/Humans.Application/Services/CityPlanning/**
  src/Humans.Domain/Entities/CityPlanningSettings.cs
  src/Humans.Domain/Entities/CampPolygon.cs
  src/Humans.Domain/Entities/CampPolygonHistory.cs
  src/Humans.Infrastructure/Data/Configurations/CityPlanning/**
  src/Humans.Infrastructure/Repositories/CityPlanningRepository.cs
  src/Humans.Web/Controllers/CityPlanningController.cs
  src/Humans.Web/Controllers/CityPlanningApiController.cs
-->
<!-- freshness:flag-on-change
  Polygon edit authorization (lead vs city-planning team vs CampAdmin), placement-open gating, and append-only history rules — review when CityPlanning service/entities/controllers change.
-->

# City Planning — Section Invariants

Interactive map for camp barrio placement: polygon editing, placement phase control, append-only history.

## Concepts

- **City Planning** is an interactive map for camp barrio placement. Camp leads draw polygons to claim their barrio's physical footprint on the site.
- **CityPlanningSettings** is a per-year singleton controlling the placement phase (open/closed), site boundary (limit zone), and informational overlays (official zones).
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
| OpenedAt | Instant? | When placement was last opened |
| ClosedAt | Instant? | When placement was last closed |
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

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View the map and all placed barrios |
| Camp lead (own camp, placement open) | Draw or edit their own camp's polygon |
| City-planning team member (team slug: `city-planning`) | Full admin access always (any polygon, settings, exports) |
| CampAdmin role | Full admin access always |

## Invariants

- Only one CampPolygon per CampSeason (unique constraint on `CampSeasonId`).
- CampPolygonHistory is append-only — edits and restores always create a new history entry (design-rules §12).
- Camp leads can only edit their own camp's polygon when placement is open. City-planning team members and CampAdmin are exempt from the placement-open requirement.
- CityPlanningSettings row is auto-created per year from `CampSettings.PublicYear`.
- SignalR broadcasts polygon updates to all connected clients in real time.
- Limit zone and official zones are stored as GeoJSON on CityPlanningSettings; out-of-bounds and overlap detection is client-side.
- Enforced by `CityPlanningArchitectureTests` (no-decorator shape, append-only repository surface).

## Negative Access Rules

- Regular humans **cannot** edit polygons for camps they do not lead.
- Camp leads **cannot** edit their polygon when placement is closed.
- Non-admin humans **cannot** access the admin panel (placement toggle, zone uploads, export).

## Triggers

- Saving a polygon creates a CampPolygonHistory entry with note `"Saved"`.
- Restoring a historical version saves the current polygon state to history first (note: `"Restored from {timestamp}"`), then overwrites the polygon with the restored version.
- SignalR broadcasts `CampPolygonUpdated` to all connected clients after every save.

## Cross-Section Dependencies

- **Camps:** `ICampService` — CampSeason is the anchor entity; CampLead determines who can edit which polygon.
- **Teams:** `ITeamService` — membership in the city-planning team (slug: `city-planning`) grants admin access.
- **Profiles:** `IProfileService` — display data for polygon edit attribution.
- **Users/Identity:** `IUserService.GetByIdsAsync` — `LastModifiedByUser` / `ModifiedByUser` display names (replaces prior cross-domain `.Include`).

## Architecture

**Owning services:** `CityPlanningService` (`Humans.Application.Services.CityPlanning`)
**Owned tables:** `city_planning_settings`, `camp_polygons`, `camp_polygon_histories`
**Status:** (A) Migrated (peterdrier/Humans PR #543, 2026-04-22).

- `CityPlanningService` lives in `Humans.Application.Services.CityPlanning` and never imports `Microsoft.EntityFrameworkCore` — enforced structurally by `Humans.Application.csproj`'s reference graph.
- `ICityPlanningRepository` / `CityPlanningRepository` (`Humans.Infrastructure.Repositories`) is the only code path that touches this section's tables via `DbContext`.
- **Decorator decision — no caching decorator.** Admin-facing, low-traffic (same rationale as Governance / User / Feedback).
- **Cross-section reads** route through `ICampService`, `ITeamService`, `IProfileService`, and `IUserService`. The previous cross-domain `.Include(h => h.ModifiedByUser)` on `CampPolygonHistories` is replaced by a batched `IUserService.GetByIdsAsync` lookup at the service layer.
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/CityPlanningArchitectureTests.cs` pins the non-decorator shape and the append-only repository surface.

### Repository surface

`ICityPlanningRepository` exposes:

- Polygon reads by camp season ids (`GetPolygonsByCampSeasonIdsAsync`, `GetCampSeasonIdsWithPolygonAsync`).
- Polygon-history reads for a camp season (`GetHistoryForCampSeasonAsync`, `GetHistoryEntryAsync`).
- Atomic "save polygon + append history" write (`SavePolygonAndAppendHistoryAsync`). Polygon upsert and history insert happen in one unit of work.
- Settings read/upsert (`GetSettingsByYearAsync`, `GetOrCreateSettingsAsync`, `MutateSettingsAsync`). All field-level mutations (placement open/close, limit zone, official zones, placement dates, registration info) flow through `MutateSettingsAsync` at the service layer.

Per §12, `camp_polygon_histories` is append-only — the repository intentionally exposes no `UpdateHistoryAsync` / `RemoveHistoryAsync`.

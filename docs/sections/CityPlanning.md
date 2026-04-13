# City Planning — Section Invariants

## Concepts

- **City Planning** is an interactive map for camp barrio placement. Camp leads draw polygons to claim their barrio's physical footprint on the site.
- **CityPlanningSettings** is a per-year singleton controlling the placement phase (open/closed), site boundary (limit zone), and informational overlays (official zones).
- **CampPolygon** is a single polygon per CampSeason representing the camp's placed area.
- **CampPolygonHistory** is an append-only audit trail of polygon edits and restores.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Any authenticated human | View the map and all placed barrios |
| Camp lead (own camp, placement open) | Draw or edit their own camp's polygon |
| City-planning team member (team slug: city-planning) | Full admin access always (any polygon, settings, exports) |
| CampAdmin role | Full admin access always |

## Invariants

- Only one CampPolygon per CampSeason (unique constraint on CampSeasonId).
- CampPolygonHistory is append-only — edits and restores always create a new history entry.
- Camp leads can only edit their own camp's polygon when placement is open. City-planning team members and CampAdmin are exempt from the placement-open requirement.
- CityPlanningSettings row is auto-created per year from CampSettings.PublicYear.
- SignalR broadcasts polygon updates to all connected clients in real time.
- Limit zone and official zones are stored as GeoJSON on CityPlanningSettings; out-of-bounds and overlap detection is client-side.

## Negative Access Rules

- Regular humans **cannot** edit polygons for camps they do not lead.
- Camp leads **cannot** edit their polygon when placement is closed.
- Non-admin humans **cannot** access the admin panel (placement toggle, zone uploads, export).

## Triggers

- Saving a polygon creates a CampPolygonHistory entry with note "Saved".
- Restoring a historical version saves the current polygon state to history first (note: "Restored from {timestamp}"), then overwrites the polygon with the restored version.
- SignalR broadcasts `CampPolygonUpdated` to all connected clients after every save.

## Cross-Section Dependencies

- **Camps**: CampSeason is the anchor entity; CampLead determines who can edit which polygon.
- **Admin**: CampAdmin role grants full city-planning access.
- **Teams**: Membership in the city-planning team (slug: `city-planning`) grants admin access.

## Architecture — Current vs Target

See `.claude/DESIGN_RULES.md` for the full rules.

**Owning services:** `CityPlanningService`
**Owned tables:** `city_planning_settings`, `camp_polygons`, `camp_polygon_histories`

### Current Violations

**CityPlanningService — queries non-owned tables (Rule 2c):**
- Queries `CampSeasons` table (owned by Camps) in GetCampSeasonSoundZoneAsync(), GetCampSeasonNameAsync(), and GetCampSeasonsWithoutCampPolygonAsync()

**Controllers:** Compliant — no DbContext injection.

### Target State

- CityPlanningService calls `ICampService` for camp season data instead of querying `CampSeasons` directly
- CampService exposes methods like `GetCampSeasonSoundZoneAsync()`, `GetCampSeasonNameAsync()`, `GetUnplacedCampSeasonsAsync()`

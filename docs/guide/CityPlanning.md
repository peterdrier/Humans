# City Planning

## What this section is for

City Planning is an interactive aerial map where [camps](Glossary.md#barrio) stake out their physical footprint before the event. Camp leads draw a polygon for their own [barrio](Glossary.md#barrio) during the placement phase; everyone else sees the evolving layout live, color-coded by [sound zone](Glossary.md#sound-zone). Map admins manage the placement lifecycle, upload overlays (site boundary, official zones), and export placements as GeoJSON.

Three entities back this section: `CityPlanningSettings` (per-year singleton, controls whether placement is open and holds overlay GeoJSON), `CampPolygon` (one polygon per camp season), and `CampPolygonHistory` (append-only audit trail of every save and restore).

![TODO: screenshot — barrio map full-screen view]

## Key pages at a glance

- **Barrio map** (`/CityPlanning`) — authenticated humans view the live full-screen map of placed camps.
- **Admin panel** (`/CityPlanning/Admin`) — map admins (Camp Admin or City Planning team members) toggle placement, upload overlays, and export GeoJSON.

The map page is a single full-screen view. Editing, polygon history, the placement-phase card, and admin actions are all surfaced through panels inside it. The separate Admin panel is where overlay zones are uploaded and placement is toggled.

An API under `/api/city-planning/` and a SignalR hub at `/hubs/city-planning` power live polygon updates and cursor broadcast.

## As a Volunteer

Anyone signed in can open the map and watch it evolve:

- **View the map** at [/CityPlanning](/CityPlanning). Every placed barrio shows its name label and sound-zone color. Polygons outside the limit zone get a red crosshatch; overlaps with another camp get orange dashed stripes; both prepend a warning indicator to the label.
- **Find your camp.** If you lead a camp that has been placed, your polygon draws with a heavier outline and more opaque fill so it stands out.
- **See who else is on the map.** Other humans' cursors appear live as they move. When anyone saves a polygon the map updates for everyone — no refresh needed.
- **Check the placement phase.** A card shows whether placement is open or closed, and a help modal lists the scheduled open and close dates (informational, Spain time).

If you are a **Camp Lead** and placement is open, you also get tools to place and adjust your own barrio:

- **Place your barrio.** Enter edit mode for your camp, draw the polygon, and save. Area and edge lengths update live while you draw.
- **Adjust an existing polygon.** Move vertices, reshape, or reposition. Saving writes a history entry with the note "Saved".
- **View history.** The offcanvas lists every prior version with timestamp and the human who made the change.

You can only edit your own camp's polygon, and only while placement is open. To change it after placement closes, ask a map admin.

## As a Board member / Admin (Camp Admin)

Map admin access is held by **Camp Admin**, **[Admin](Glossary.md#admin)**, and members of the **City Planning team** (slug `city-planning`). Map admins act on any polygon at any time — the placement-open restriction doesn't apply to you.

- **Edit any camp's polygon.** Draw, reshape, or move any polygon regardless of who leads the camp and regardless of placement phase.
- **Place on behalf of a camp.** The admin dropdown lists camp seasons without a polygon; pick one to start drawing.
- **Restore a prior version.** From a polygon's history, choose a past version and restore. The current state writes to history first with the note "Restored from {timestamp}", then the polygon is overwritten. History is append-only — nothing is ever lost.
- **Toggle placement.** From [/CityPlanning/Admin](/CityPlanning/Admin), open or close placement. Timestamps are recorded. Closing blocks camp leads from editing but not you.
- **Set informational placement dates.** Scheduled open and close datetimes show in the help modal. They do not auto-open or auto-close the phase.
- **Upload a limit zone.** A GeoJSON FeatureCollection defining the site boundary. Renders as a dashed white outline; polygons drawn outside it are flagged. Download and delete are supported.
- **Upload official zones.** A GeoJSON FeatureCollection of named read-only overlay zones (dark gray, labeled). Each Feature needs a `name` property. Download and delete supported.
- **Export all placements.** Download every polygon for a year as a single GeoJSON FeatureCollection for logistics, signage, and public materials.

## Related sections

- [Camps](Camps.md) — `CampSeason` is the anchor entity; placement requires an approved camp season for the current year, and Primary/Co-Leads are the ones allowed to edit that camp's polygon.
- [Teams](Teams.md) — membership in the City Planning team (slug `city-planning`) grants map admin access without needing a global Camp Admin or Admin role.
- [Glossary](Glossary.md) — definitions for "barrio", "sound zone", "limit zone", and related terms.

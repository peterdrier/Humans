<!-- freshness:triggers
  src/Sections/Humans.CityPlanning/Controllers/**
  src/Sections/Humans.CityPlanning/Services/**
  src/Sections/Humans.CityPlanning/Domain/**
  src/Sections/Humans.CityPlanning/Data/Configurations/**
  src/Sections/Humans.CityPlanning/Views/**
  src/Sections/Humans.CityPlanning/wwwroot/js/city-planning/**
-->
<!-- freshness:flag-on-change
  Map view, polygon edit/save/restore flow, placement-phase toggle, overlay uploads, GeoJSON export, and City Planning team admin access. Review when map views, controllers, or entities change.
-->

# City Planning

## What this section is for

City Planning is an interactive aerial map where [camps](Glossary.md#barrio) stake out their physical footprint before the event. Camp leads draw their own [barrio](Glossary.md#barrio)'s placement during the placement phase; everyone else sees the evolving layout live, color-coded by [sound zone](Glossary.md#sound-zone). Map admins manage the placement lifecycle, upload overlays (site boundary, official zones), and export placements as GeoJSON.

Three entities back this section: `CityPlanningSettings` (per-year singleton, controls whether barrio placement and container placement are open and holds overlay GeoJSON), `CampPolygon` (one polygon per camp season), and `CampPolygonHistory` (append-only audit trail of every save and restore).

![TODO: screenshot — barrio map full-screen view]

## Key pages at a glance

- **Overview map** (`/CityPlanning`) — authenticated humans view the live full-screen map, with layer toggles for containers and barrio zones and a measuring tool. Read-only.
- **Barrio map** (`/CityPlanning/BarrioMap`) — where placements are actually drawn and edited. Linked from the overview map for camp leads while placement is open, and for map admins at any time.
- **Container map** (`/CityPlanning/ContainerMap/{year}`) — where containers are placed. Containers have their **own** placement phase, separate from barrio placement: unless you are a map admin, you can only open this page while container placement is open and you lead a camp.
- **Admin panel** (`/CityPlanning/BarrioMap/Admin`) — map admins (Camp Admin or City Planning team members) toggle barrio placement, set informational placement dates, upload overlays, export and import GeoJSON. Container admin lives under `/CityPlanning/BarrioMap/Admin/Containers/{year}`, and that is also where container placement is opened and closed.

Each map is its own full-screen view. On the barrio map, editing, polygon history and the placement-phase card are surfaced through panels inside it. The separate Admin panel is where overlay zones are uploaded and placement is toggled.

An API under `/api/city-planning/` and a SignalR hub at `/hubs/city-planning` power live polygon updates and cursor broadcast.

## As a Volunteer

Anyone signed in can open the map and watch it evolve:

- **View the map** at [/CityPlanning](/CityPlanning). Every placed barrio shows its name label and sound-zone color. Placements outside the limit zone get a red crosshatch; overlaps with another camp get orange dashed stripes; both prepend a warning indicator to the label.
- **Find your camp.** If you lead a camp that has been placed, your placement draws with a heavier outline and more opaque fill so it stands out.
- **See who else is on the map.** Other humans' cursors appear live as they move. When anyone saves a placement the map updates for everyone — no refresh needed.
- **Check the placement phase.** A card shows whether placement is open or closed, and a help modal lists the scheduled open and close dates (informational, Spain time).

If you are a **Camp Lead** and barrio placement is open, the overview map links you through to the barrio map (`/CityPlanning/BarrioMap`), where you get tools to place and adjust your own barrio. The container map is linked on the same panel, but on its own phase — you see that link while *container* placement is open.

- **Place your barrio.** Enter edit mode for your camp, draw your placement on the map, and save. Area and edge lengths update live while you draw.
- **Adjust an existing placement.** Move corners, reshape, or reposition. Saving writes a history entry with the note "Saved".
- **View history.** The offcanvas lists every prior version with timestamp and the human who made the change.

You can only edit your own camp's placement, and only while placement is open. To change it after placement closes, ask a map admin.

## As a Board member / Admin (Camp Admin)

Map admin access is held by **Camp Admin**, **[Admin](Glossary.md#admin)**, and members of the **City Planning team** (slug `city-planning`). Map admins act on any placement at any time — the placement-open restriction doesn't apply to you.

- **Edit any camp's placement.** Draw, reshape, or move any placement regardless of who leads the camp and regardless of placement phase.
- **Place on behalf of a camp.** The admin dropdown lists camp seasons without a placement; pick one to start drawing.
- **Restore a prior version.** From a placement's history, choose a past version and restore. The current state writes to history first with the note "Restored from {timestamp}", then the placement is overwritten. History is append-only — nothing is ever lost.
- **Toggle barrio placement.** From [/CityPlanning/BarrioMap/Admin](/CityPlanning/BarrioMap/Admin), open or close placement. Timestamps are recorded. Closing blocks camp leads from editing but not you.
- **Toggle container placement.** A separate phase with its own open/close buttons, on the container admin page (`/CityPlanning/BarrioMap/Admin/Containers/{year}`). While it is closed, camp leads can neither reach the container map nor place their containers; you can, either way.
- **Set informational placement dates.** Scheduled open and close datetimes show in the help modal. They do not auto-open or auto-close the phase.
- **Upload a limit zone.** A GeoJSON FeatureCollection defining the site boundary. Renders as a dashed outline coloured by each feature's `SoundZone` property (white dashes when the property is absent); placements drawn outside it are flagged. Download and delete are supported.
- **Upload official zones.** A GeoJSON FeatureCollection of named read-only overlay zones (dark gray, labeled). Each Feature needs a `name` property. Download and delete supported.
- **Export all placements.** Download every placement for a year as a single GeoJSON FeatureCollection for logistics, signage, and public materials.
- **Import placements.** Upload a GeoJSON FeatureCollection to bulk-update placements. The admin panel previews matched and unrecognized features before you confirm; matched camps are updated through the same save path as a manual edit (so each import row writes a history entry).

## Related sections

- [Camps](Camps.md) — `CampSeason` is the anchor entity; placement requires an approved camp season for the current year, and the camp's leads are the ones allowed to edit that camp's placement.
- [Teams](Teams.md) — membership in the City Planning team (slug `city-planning`) grants map admin access without needing a global Camp Admin or Admin role.
- [Glossary](Glossary.md) — definitions for "barrio", "sound zone", "limit zone", and related terms.

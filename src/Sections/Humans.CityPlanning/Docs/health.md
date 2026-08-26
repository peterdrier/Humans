# City Planning — Target Shape

Derived fresh each doctor run, before any scan. History rows at the bottom.

## 1. What the section does

Shows everyone where things sit on the festival site, and lets the people responsible for a
piece of it move that piece.

- Anyone signed in opens a map of the site and sees every barrio's claimed footprint over the
  official zone overlays, and can turn on the placed containers and the site boundary — both
  are layers, off by default. They can measure distances on it.
- A barrio lead, while the organisers have opened barrio placement, draws or reshapes their
  own barrio's outline. Every version they save is kept, so an organiser can look back at
  what the outline was on any earlier date and put it back.
- A barrio lead, while the organisers have opened container placement, drops their camp's
  containers onto the site, rotates them, adds a note or a sketch of how it should look, and
  clears a placement that was wrong.
- The organisers (the city-planning team, or a camp admin) do all of the above for anyone,
  at any time, open phase or not. They also decide when each phase is open,
  upload the site boundary and the official zones, publish the text barrio leads read on the
  registration page, keep the org-wide container list, and download everything placed as a
  file they can open in a GIS tool.

## 2. The shapes

Everything the section exposes, grouped by the question it answers. Load-bearing: every
structural item below follows from this table.

| Shape | Question it answers | Surface today |
|---|---|---|
| **See the site** | What is placed where, this year? | `GET /CityPlanning/`, `GET /api/city-planning/state`, `GET /api/city-planning/containers/{year}` |
| **Claim a barrio** | Where is my camp, and what was it before? | `GET /CityPlanning/BarrioMap`, `PUT /api/city-planning/camp-polygons/{id}`, `GET …/history`, `POST …/restore/{historyId}` |
| **Place a container** | Where does this box go, and what should it look like? | `GET /CityPlanning/ContainerMap/{year}`, `PUT/DELETE /api/city-planning/containers/{id}/placement/{year}`, `PUT …/notes` |
| **Run the phases** | Is placement open, and who may act now? | `GET /CityPlanning/BarrioMap/Admin`, the `Open*/Close*Placement` posts, `UpdatePlacementDates`, `ICityPlanningServiceRead.GetSettingsAsync` |
| **Publish the ground truth** | What is the boundary and what are the named zones? | `Upload*/Download*/Delete*` for limit zone and official zones |
| **Bulk-load barrios** | Here is the surveyor's file — match it to camps and apply it. | `barrio-map/admin-import.js` on the admin page: reads `GET …/state`, matches features to camps by name or slug, then `PUT`s each match |
| **Curate containers** | Which containers exist for which barrio? | `GET /CityPlanning/BarrioMap/Admin/Containers/{year}` plus its Create/Edit/Delete posts (entity owned by Containers) |
| **Hand the data out** | Give me this as a file. | `GET /api/city-planning/export.geojson`, `GET /api/city-planning/containers/{year}/export.geojson` |
| **Tell other sections** | Is placement open? Is this user an organiser? What's the registration blurb? | `ICityPlanningServiceRead` + `ICityPlanningService` |
| **Watch each other work** | Who else is on this map right now? | `CityPlanningHub` at `/hubs/city-planning` (cursors + polygon broadcast) |

*Curate containers* and half of *place a container* are City Planning URLs over an entity
Containers owns; that is deliberate (placement is a planning concern) and is the section's one
standing width cost.

## 3. Structure

The layout those shapes imply, written fresh:

- **One page controller** (`CityPlanningController`) for the map screens plus the
  admin screens, because they share the map-admin gate and the settings row.
- **One API controller** (`CityPlanningApiController`) for everything the map JavaScript
  calls, because the maps are single-page surfaces that fetch their own state.
- **One service** (`CityPlanningService`) holding both business rules: *who may edit what,
  when* and *what a save does to history*. It is the only repository caller.
- **One repository** over the section's tables, exposing polygon reads, an atomic
  save-polygon-and-append-history write, a season-scoped delete, and a get-or-create /
  mutate pair for the settings singleton.
- **One contracts leaf** carrying the narrowest cross-section surface — reads for anyone,
  writes only for the callers that need them.
- **A JavaScript bundle per map** — `main.js` (overview), `barrio-map/`, `container-map/` — and a
  `shared/` set holding exactly what more than one of them uses (map constants, the measure
  tool, the official-zones layer, the sound-zone colour expressions). A per-map file that only
  re-exports a shared one is indirection, not structure.
- **One resx set** for City Planning's own vocabulary; container vocabulary is bound from
  `ContainersResource` at the call site, shared strings from `SharedResource`.

This section does *not* have, and should not grow, a caching decorator (admin
traffic, one row and one small list per read) and an internal service interface (the section's
own controllers take the concrete class; the seam that matters is the contracts leaf).

## 4. Invariants

Stated so a violation is recognisable:

1. One `CampPolygon` per `CampSeasonId` — enforced by a unique index, not by code.
2. `camp_polygon_histories` is append-only. A save appends; a restore appends. The repository
   exposes no update and no delete for a single history row (the season-scoped cascade delete
   is the one exception, and it deletes the polygon with it).
3. A restore writes the restored geometry as a *new* current polygon with the note
   `Restored from {timestamp} UTC`, composed server-side; it never rewinds history. The note
   on an ordinary save is open-ended — the `PUT` persists whatever the caller sends and
   falls back to `Saved`. The one caller-supplied note the codebase itself sends is
   `Imported {date}`, from `admin-import.js`.
4. A camp lead may edit their own polygon only while `IsPlacementOpen`. City-planning team
   members and `CampAdmin` are exempt from the phase and from the ownership check.
5. A camp lead may place/clear their own camp's containers only while
   `IsContainerPlacementOpen`. Same exemptions.
6. Restore and the polygon export are map-admin only — a lead cannot restore even their own
   camp's polygon.
7. The settings row for a year is created on demand, closed (`IsPlacementOpen = false`), keyed
   to `CampSettings.PublicYear` — **except** `RegistrationInfo`, which is keyed to the highest
   open season year and falls back to `PublicYear`.
8. Every polygon save and restore broadcasts `CampPolygonUpdated` to every connected client; a
   broadcast failure is logged and never fails the save.
9. Stored GeoJSON is validated as *parseable JSON* only, except container placements, which
   must additionally be a `Feature` with `Polygon` geometry and `center_lng` / `center_lat` /
   `rotation_degrees` properties.
10. Uploaded zone files are rejected above 10 MB and when unparseable.
11. Every settings write that takes a `userId` — both placement phases, the zone uploads and
    the zone deletes — appends an audit entry naming that actor, after the save, and a request
    aborted mid-write does not drop it: the row id is resolved before the save, and the save
    itself runs on `CancellationToken.None`, so nothing cancellable sits between the committing
    write and the token-less `LogAsync`. The settings row records *when* a
    value changed; the audit log is the only record of *who*. A rejected upload never reaches the
    row and writes no entry.
12. The city-planning team slug is normalized on both sides before comparing, so the configured
    value and the stored slug match regardless of case. A blank configured slug matches nothing.

## 5. Seams

Specified but not built. Not ranked, not to be built by a doctor run — noted because items
touching these callers are shaped by them.

- **Container placement history, kept in season.** Barrio polygons keep every version;
  container placements keep none. Peter's call (2026-08-26): keeping placement history
  within a season is reasonable, and the history is expected to be archived at the rollover
  into the next season. Neither half is built.

## 6. Deliberately not done

- **No caching decorator.** Admin-facing and low-traffic; a decorator would add a cache-key
  surface for a single settings row.
- **No internal `ICityPlanningService`.** The section's own controllers take the concrete
  service (design §15 step 5); the only seam worth an interface is the cross-section one.
- **No SignalR and no MapboxDraw on the container map.** Placement is a single-user workflow
  at this scale, and containers use a custom drag-to-move / drag-handle-to-rotate interaction
  rather than a polygon editor.
- **No `jsonb`.** GeoJSON columns are `text`: the app never queries inside the structure, it
  round-trips whole FeatureCollections to the browser.
- **No generic `MapFeature` / toggleable-layer entity.** Proposed in
  nobodies-collective/Humans#521, declined in favour of purpose-built screens.
- **No test that the section lacks a decorator or a history-update method.** Absence
  assertions are forbidden (`memory/architecture/no-tests-for-absences.md`); the shape is
  documented here instead.

## Load-bearing weirdness

- **`Humans.CityPlanning.Contracts` is a project, not a folder.** The compile-time cycle it
  breaks is with Containers: `Humans.CityPlanning` references `Humans.Containers` outright, so
  Containers can reach `ICityPlanningServiceRead` only through the leaf. `Humans.Camps` is not
  the reason — it already references `Humans.CityPlanning` itself.
- **`Lazy<ICityPlanningService>` is registered by `Humans.Camps`, not by this section.** That
  is a separate, *runtime* cycle — `CampService → ICityPlanningService → ICampServiceRead`, hit
  on the camp-delete path — and the lazy belongs with the consumer, not the producer.
- **`RegistrationInfo` is keyed differently from every other settings field** (highest open
  season, not `PublicYear`) because the Register page it feeds targets the open season.
- **Out-of-bounds and overlap detection is client-side only.** The server stores whatever
  parses. This is a deliberate scale call, not an oversight.
- **Bulk import writes through the same PUT the map uses.** `admin-import.js` matches
  features to camps by lower-cased name *or* slug and issues one `PUT` per match, so an
  import is N ordinary saves and gets N history rows for free. There is no server-side
  import endpoint and should not be one.
- **The API route prefix `api/city-planning` is pinned by an architecture test** because the
  map JavaScript hard-codes it; renaming it fails silently at runtime otherwise.
- **The section hosts placement endpoints for `Container`, an entity Containers owns.**
  Placement is a planning concern; the entity is not. Both directions of that split are
  intentional.

## History

| Date | Run | Reforge score | Notes |
|---|---|---|---|
| 2026-08-26 | [2026-08-26-CityPlanning](../../../../docs/health/runs/2026-08-26-CityPlanning.md) | 210 → 218 (loc 1947 → 1964, cogP95 4, cogMax 6) | First doctor run; this target derived from scratch. The score rose only after Peter approved the audit change: the whole +8 is `crossSectionFullService` for injecting `IAuditLogService`, which has no read-only half — it is the one interface every writer to the crosscut takes, so the cost is not narrowable and is the price of the audit trail. Structure was sound — the value was in what the section claimed about itself (a documented ordering guarantee the query does not make, a non-existent EF relationship, both authorization rows naming the wrong guard) and in untested paths, including the cross-section delete Camps calls. Behaviour bugs found and recorded in `Docs/debt.yml` rather than fixed. PR: peterdrier/Humans#1525 |

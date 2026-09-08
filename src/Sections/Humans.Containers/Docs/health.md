# Containers — Health

Target shape, regenerated every section-doctor run and diffed against the previous one.
Scores live on the run's PR (surface report), never here.

## 1. What the section does

A camp owns physical shipping containers that persist year over year. A camp lead keeps the
camp's list current — name, description, up to five photos — from the camp's own page, any time.
Each year, while City Planning has the container placement phase open, the lead drags each
container onto the city map, and can attach notes and a sketch to that year's placement. Map
Admins (CampAdmin role or the city-planning team) do all of this for every camp, in or out of
phase, from City Planning's admin pages. Every write is audited.

## 2. The shapes

| Shape | Question it answers | Surface |
|---|---|---|
| Container list | which containers does this camp / everyone have; this one? | `GetByCampAsync`, `GetAllAsync`, `GetByIdAsync` |
| Container write | add / change / remove a container and its gallery | `CreateAsync`, `UpdateAsync`, `DeleteAsync` over `ContainerData` |
| Placement for a year | where is it this year; put it there; take it off; annotate it | `GetPlacementsByYearAsync`, `SavePlacementAsync`, `ClearPlacementAsync`, `UpdatePlacementNotesAsync` |
| Admin overview | every camp with a season this year, with its containers and placements | `GetAdminOverviewAsync` |
| May this user? | Manage (CRUD, year-round) vs Place (phase-gated for leads) on a camp | `ContainerOperationRequirement` × `ContainerAuthorizationTarget(CampId)` → handler |
| Card rendering | one row + its modals + its form, hosted by two pages | `_ContainerCardRow`, `_ContainerCardModals`, `_ContainerFormFields` over `ContainerCardModel` |

Routes owned: `/Camp/{slug}/Containers` (GET), `…/Create`, `…/{id}/Edit`, `…/{id}/Delete`
(POST). No jobs, no events, no config keys. City Planning hosts the map page, the admin CRUD
page and the placement JSON API over this section's service.

## 3. Structure

The shapes imply: one contract folder (service interface, the DTOs the shapes return, the
`ContainerData` write input, the two authorization types, the form model, and the card model the
two hosts fill in), one controller for the camp page, one service, one repository over three
tables, one handler, three partials plus the page view, one resource set.

**View models exist only where they hold something the DTO does not.** The page model (host
composition), the card model (host routing), and the form model (binding) each do. A container,
a placement, and a container-with-placement do not: the DTO *is* the display shape, the two
computed booleans (`IsPlaced`, `HasPlacementInfo`) live on the records that carry the data, and
the two host controllers hand DTOs straight to the views. Nothing copies a DTO field-for-field
into a second type.

A DTO field carrying a URL is named as a URL (`PlacementImageUrl`).

## 4. Invariants

- A container has a real `CampId`; its camp's current-year leads and Map Admins may manage it.
  Anyone else gets 403 on every write and on the camp's container page.
- `Manage` is never phase-gated; `Place` is, for the lead branch only. Map Admins bypass both
  gates. The year and the phase both come from `CityPlanningSettings`, not the camp.
- At most five images per container, counting the legacy column-backed one; removals in the same
  request free room first. Content type, extension and 10 MB size are all checked, on every image.
- The gallery is one uniform list; `Guid.Empty` addresses the legacy image; nothing outside
  `Service.ToDto` and `Service.UpdateAsync` knows the legacy columns exist.
- Every container write POST carries the 55 MB request-size limit (four actions in two sections).
- A placement row survives `Clear` iff it holds notes or an image; `Save` upserts geometry and
  never touches notes or image; annotating requires an existing row.
- Deleting a container removes its gallery files, its legacy file, its `container_images` rows and
  every year's placement row. Placement-image files may be orphaned (accepted).
- Every write is audited under the literal entity types `Container` / `ContainerPlacement`,
  related to `Camp` / `Container`.
- Names never contain `<`, `>` or `$`.

## 5. Seams

- Retiring the legacy `containers.Image*` columns is specified (admin migration screen, then a
  column-drop PR) and unbuilt. The `Guid.Empty` convention and the two legacy branches in
  `Service` are shaped by it; leave them until it lands.

## 6. Deliberately not done

- **No caching decorator** — small dataset, lead-facing, rare writes.
- **No `IContainerServiceRead` split** — both external consumers (City Planning's two
  controllers) also write; the split would carry nothing.
- **Placement API and admin pages stay in City Planning** — their URLs are City Planning's
  (`/api/city-planning/containers/*`, `/CityPlanning/BarrioMap/Admin/Containers/*`), and moving
  them is a URL change, out of scope.
- **Lead CRUD is not phase-gated** — Peter, 2026-08-16: intended; re-evaluate December 2026.
- **No virtual org camp** — org-level containers belong to an ordinary camp with no leads.
- **No `Contracts` project** — the only consumer references this project directly.

## Load-bearing weirdness

- `container_images.ContainerId` has a real cascading FK; `container_placements.ContainerId` has
  none and is deleted by the repository. Adding the FK is a schema change nobody has asked for.
- The `Contracts/` folder is wide (form model, card model, page models) because City Planning
  drives container CRUD from its own pages and renders this section's partials.
- The three partials live in this section, not Base, so they can localize from
  `ContainersResource`; the `ContainerMap_*` keys live here too because the map page is City
  Planning's URL over Containers' vocabulary.
- Audit entity types are persisted literals, not `nameof` — `Camp` is not even nameable here.
- `Service.ValidateName` and `ContainerFormModel`'s regex enforce the same character ban on
  purpose: the form gives the user a message, the service protects every caller.
- The handler reads `CityPlanningSettings.Year` to decide who is a lead, so the gate follows the
  planning year, not the camp's public year.
- `ClearPlacementAsync` keeps a row with notes; `SavePlacementAsync` upserts geometry only — the
  "unplaced but annotated" state is real and the UI shows it.

## History

| Date | Headline | PR |
|---|---|---|
| 2026-08-16 | Doc-code alignment (phase gate, `HumansDbContext`), dead `GetPlacementAsync` removed, 5 tests added, clear-placement flow localized, 3 dead resx keys deleted | peterdrier/Humans#1341 |
| 2026-09-08 | View-model layer collapsed into the DTOs (URL field renamed, dead surface deleted), camp container page fully localized, placement/audit/request-size invariants pinned, section docs and comments realigned; health.md moved to the target-shape format | pending |

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

**Expected refusals are values, not exceptions.** Every write a user can get wrong returns
`ContainerMutationResult<T>` — the DTO, or a `Containers_*` resource key plus its arguments — and
the host controller localizes it. Missing rows and dependency faults still throw.

**View models exist only where they hold something the DTO does not.** The page model (host
composition), the card model (host routing), and the form model (binding) each do. A container,
a placement, and a container-with-placement do not: the DTO *is* the display shape, the two
computed booleans (`IsPlaced`, `HasPlacementInfo`) live on the records that carry the data, and
the two host controllers hand DTOs straight to the views. Nothing copies a DTO field-for-field
into a second type.

A DTO field carrying a URL is named as a URL (`PlacementImageUrl`).

**One way to drop a file after a commit.** Every file the service deletes once its database write
is in — superseded gallery, legacy and placement images, and staged uploads of a failed batch —
goes through one best-effort helper that tries each path without request cancellation and logs a
failure instead of throwing.

## 4. Invariants

- A container write needs Manage or Place on its camp; anyone who is neither a Map Admin nor a
  lead of that camp in the target year gets 403 on every write and on the camp's container page —
  `Authorization/ContainerAuthorizationHandler.cs:49`, `Controllers/ContainerController.cs:34`,
  `Controllers/ContainerController.cs:145`.
- `Manage` is never phase-gated; `Place` is, for the lead branch only, and Map Admins return
  before either gate — `Authorization/ContainerAuthorizationHandler.cs:54`,
  `Authorization/ContainerAuthorizationHandler.cs:25`, `Authorization/ContainerAuthorizationHandler.cs:37`.
- At most five images per container, counting the legacy column-backed one; removals in the same
  request free room first — `Services/Service.cs:104`, `Services/Service.cs:343`.
- Every image is checked for content type, 10 MB size, a basename of at most 256 characters and
  an allowed extension before any file or row is written — `Services/Service.cs:349`.
- The gallery is one uniform list; `Guid.Empty` addresses the legacy image; nothing outside
  `Service.ToDto` and `Service.UpdateAsync` knows the legacy columns exist — `Services/Service.cs:438`,
  `Services/Service.cs:102`.
- Every container create/edit POST carries the 55 MB request-size limit, so a legitimate
  multi-image upload reaches the per-image check instead of Kestrel's default 413 —
  `Controllers/ContainerController.cs:77`, `Controllers/ContainerController.cs:103`, and City
  Planning's two container actions.
- A placement's GeoJSON is a closed Polygon Feature with finite in-range coordinates and the
  `center_lng` / `center_lat` / `rotation_degrees` properties; anything else is refused before any
  write or audit — `Services/Service.cs:182`.
- A placement row survives `Clear` iff it holds notes or an image — `Services/Service.cs:241`.
  `Save` upserts geometry and never touches notes or image — `Data/Repository.cs:171`. Annotating
  requires an existing row — `Services/Service.cs:274`.
- Deleting a container removes its `container_images` rows and every year's placement row in one
  save — `Data/Repository.cs:70` — then its gallery and legacy files — `Services/Service.cs:139`.
  Placement-image files are not deleted (accepted).
- Every write is audited under the literal entity types `Container` / `ContainerPlacement`,
  related to `Camp` / `Container` — `Services/AuditEntityTypes.cs:22`.
- Names never contain `<`, `>` or `$` — `Services/Service.cs:340`.

## 5. Seams

- Retiring the legacy image columns (`ImageStoragePath`, `ImageContentType`, `ImageFileName` on
  `containers`) is specified (admin migration screen, then a
  column-drop PR) and unbuilt. The `Guid.Empty` convention and the two legacy branches in
  `Service` are shaped by it; leave them until it lands.

## 6. Deliberately not done

- **No caching decorator** — small dataset, lead-facing, rare writes.
- **No separate read contract** — both external consumers (City Planning's two
  controllers) also write, so there is one public face, not a read/write pair.
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
  `ContainersResource`; the `Containers_Map*` keys live here too because the map page is City
  Planning's URL over Containers' vocabulary.
- Audit entity types are persisted literals, not `nameof` — `Camp` is not even nameable here.
- `Service.ValidateName` and `ContainerFormModel`'s regex enforce the same character ban on
  purpose: the form gives the user a message, the service protects every caller.
- The handler decides who is a lead from the target's year; a target with no year (the camp page)
  uses the year of City Planning's current settings, which is the active event's year. With no
  active event that target is denied, and the camp's container page answers 404.
- `ClearPlacementAsync` keeps a row with notes; `SavePlacementAsync` upserts geometry only — the
  "unplaced but annotated" state is real and the UI shows it.

## History

| Date | Headline | PR |
|---|---|---|
| 2026-08-16 | Doc-code alignment (phase gate, `HumansDbContext`), dead `GetPlacementAsync` removed, 5 tests added, clear-placement flow localized, 3 dead resx keys deleted | peterdrier/Humans#1341 |
| 2026-09-08 | View-model layer collapsed into the DTOs (URL field renamed, dead surface deleted), camp container page fully localized, placement/audit/request-size invariants pinned, section docs and comments realigned; health.md moved to the target-shape format | peterdrier/Humans#1620 |
| 2026-10-15 | Doc claims realigned with the code (GeoJSON validation owner, placement-image cleanup, full API list), one best-effort file-delete helper, dead `ForCamp` factory removed, content-type and Map Admin Place tests added | peterdrier/Humans#1983 |

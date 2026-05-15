# Container Placement Phase — Design Spec

**Date:** 2026-04-26
**Status:** Implemented in PR peterdrier/Humans#389 — see also `docs/sections/Containers.md` and `docs/sections/CityPlanning.md` for the section invariants.

## Overview

Introduce a gated "container placement phase" that controls whether barrio leads can add, edit, and delete their own containers. City planning admins toggle the phase open or closed from the containers admin page (`Admin/Containers/{year}`). Camp admins and city planning team members are never gated and can always manage containers.

## Data Model

Add three fields to the existing `CityPlanningSettings` entity, mirroring the barrio placement pattern:

| Field | Type | Default | Purpose |
|---|---|---|---|
| `IsContainerPlacementOpen` | `bool` | `false` | Whether the phase is currently open |
| `ContainerPlacementOpenedAt` | `Instant?` | `null` | When it was last opened (audit) |
| `ContainerPlacementClosedAt` | `Instant?` | `null` | When it was last closed (audit) |

One EF Core migration (`AddContainerPlacementPhase`) adds these columns to `city_planning_settings`.

## Service Layer

Two new methods on `ICityPlanningService` (and `CityPlanningService`):

```
OpenContainerPlacementAsync(Guid userId, CancellationToken ct)
CloseContainerPlacementAsync(Guid userId, CancellationToken ct)
```

Each loads the current-year settings row, flips `IsContainerPlacementOpen`, sets the relevant timestamp (`ContainerPlacementOpenedAt` or `ContainerPlacementClosedAt`), and saves. `GetSettingsAsync` already returns the full `CityPlanningSettings` object, so no new query method is needed.

## Authorization & Enforcement

The phase check is enforced in `ContainerController` (services are auth-free per design rules). A user is **privileged** if they are a camp admin (`RoleChecks.IsCampAdmin`) or a city planning team member (`ICityPlanningService.IsCityPlanningTeamMemberAsync`). Privileged users are never gated.

**Write actions** (Create, Edit, Delete, UploadImage, DeleteImage):
After the existing `CanManageAsync` / `AuthorizeAsync` check passes, if the user is not privileged and `IsContainerPlacementOpen == false`, redirect back to the Index with an error: *"Container placement is currently closed."*

**Index action:**
Computes `isPrivileged` and fetches `settings.IsContainerPlacementOpen`. The existing `CanManageAsync` check is unchanged. A phase gate is applied on top: `CanManage` passed to the view is `canManageResult && (isPrivileged || settings.IsContainerPlacementOpen)`. A new `IsPlacementOpen` bool on `ContainerIndexViewModel` lets the view show a contextual notice to leads when the phase is closed and they would otherwise have manage rights.

## View Model Changes

`ContainerIndexViewModel` gains one field:

```csharp
public bool IsPlacementOpen { get; set; }
```

Used only to show/hide the "placement closed" notice. The existing `CanManage` flag continues to gate all write UI.

## Controller Changes

**`CityPlanningController`** — two new POST actions mirroring the existing placement toggle:

```
POST CityPlanning/BarrioMap/Admin/OpenContainerPlacement
POST CityPlanning/BarrioMap/Admin/CloseContainerPlacement
```

Both require `IsMapAdminAsync`. On success, redirect to `Containers` (the containers admin sub-page at `/CityPlanning/BarrioMap/Admin/Containers/{year}`) with a success message.

**`ContainerController`** — changes to `Index` and all write actions as described above. `ICityPlanningService` is already injected.

## UI Changes

### `/CityPlanning/BarrioMap/Admin/Containers/{year}` (Containers admin view)

Status + toggle row at the top of the page (below the `<h1>`):

- Phase open: green badge "Container placement is open" + [Close] button (POST form)
- Phase closed: secondary badge "Container placement is closed" + [Open] button (POST form)

### `Camp/{slug}/Season/{year}/Containers` (Container/Index view)

When `CanManage == true` AND `!IsPlacementOpen` (i.e., the user is a lead and the phase is closed):

- Show an info alert: *"Container placement is currently closed. You can manage containers you own, but not yet place them on the map."*

The notice is shown only when the user would normally have manage rights (is a camp lead for this season) but the phase is closed. Non-leads are forbidden from the page entirely and never see the notice.

## Actors & Access

| Actor | Phase open | Phase closed |
|---|---|---|
| Camp admin | Full CRUD | Full CRUD |
| City planning team member | Full CRUD | Full CRUD |
| Barrio lead | Full CRUD for own camp | Read-only (blocked with message) |
| Regular human | No access | No access |

## Out of Scope

- Map/zone assignment for containers (separate feature, follows after this one)
- Scheduled auto-open/auto-close (informational dates only, same as barrio placement)
- Audit log entries beyond the timestamps on `CityPlanningSettings`

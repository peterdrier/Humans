# Containers Feature Design

**Date:** 2026-04-26
**Status:** Implemented in PR peterdrier/Humans#389 — see also `docs/sections/Containers.md` and `docs/sections/CityPlanning.md` for the section invariants.

## Overview

Add a proper `Container` entity so barrios can manage their camp containers (add, edit, delete, upload a photo), and so the city planning team can manage org-level containers not tied to any specific camp. This replaces the existing `ContainerCount`/`ContainerNotes` scalar fields on `CampSeason`.

A second phase (out of scope here) will add map placement — a GeoJSON polygon on the city planning map.

## Business Context

Today `CampSeason` stores only a count of containers and a free-text notes field. Camp leads have no way to name, describe, or photograph individual containers. The city planning team also needs to track org-level infrastructure containers (not tied to any barrio) grouped by year.

## Section Ownership

Containers is a **new section** (`IContainerService`, `IContainerRepository`). It does not belong to the Camps section or the City Planning section, though both sections will link into it from their UIs. The Containers section owns the `containers` table exclusively.

## Data Model

### Entity: `Container`

Table: `containers`

| Field | Type | Constraints |
|---|---|---|
| `Id` | `Guid` (init) | PK, `Guid.NewGuid()` default |
| `CampSeasonId` | `Guid?` (init) | nullable FK → `camp_seasons`; null = org-level |
| `CampSeason` | navigation | declared but not read by Containers code (design-rules §15i) |
| `Year` | `int` (init) | always set; copied from `CampSeason.Year` for barrio containers |
| `Name` | `string` | max 256 |
| `Description` | `string?` | max 2000 |
| `ImageStoragePath` | `string?` | max 512; relative path from wwwroot |
| `ImageContentType` | `string?` | max 64 |
| `ImageFileName` | `string?` | max 256 |
| `LocationGeoJson` | `string?` | GeoJSON Feature; null = unplaced (added via `AddContainerPlacement` migration) |
| `CreatedAt` | `Instant` (init) | |
| `UpdatedAt` | `Instant` | |

~~`SortOrder` (int) was added in the initial migration and dropped in `20260426140854_RemoveContainerSortOrder`. See "Decisions during implementation" below.~~

Indexes:
- `CampSeasonId` (non-unique, for barrio container lookups)
- `Year` (for org-level year lookups)

FK behaviour: `CampSeasonId` → `camp_seasons` with `DeleteBehavior.Cascade` (containers are deleted when a season is deleted).

### Removed from `CampSeason`

`ContainerCount` (int) and `ContainerNotes` (string?) are dropped from the `camp_seasons` table via migration.

### Image Storage

Path pattern: `wwwroot/uploads/containers/{containerId}/{guid}.{ext}`

Supported types: `image/jpeg` → `.jpg`, `image/png` → `.png`, `image/webp` → `.webp`

One image per container stored directly on the entity (no separate image table).

## Migration Strategy

The EF migration that creates the `containers` table also performs a data migration:

1. For every `CampSeason` where `ContainerCount > 0`, insert N `Container` rows:
   - Names: "Container #1", "Container #2", … "Container #N"
   - `CampSeasonId`: the season's id
   - `Year`: the season's year
   - `Description`: `ContainerNotes` value on Container #1 only; null on the rest
   - `CreatedAt` / `UpdatedAt`: migration timestamp
   (Note: `SortOrder` was in the initial migration but dropped by `20260426140854_RemoveContainerSortOrder`.)
2. Drop `ContainerCount` and `ContainerNotes` columns from `camp_seasons`.

## Application Layer

### DTOs

```csharp
public record ContainerDto(
    Guid Id,
    Guid? CampSeasonId,
    int Year,
    string Name,
    string? Description,
    string? ImageStoragePath,
    string? ImageContentType,
    string? ImageFileName,
    string? LocationGeoJson,  // added via placement phase
    Instant CreatedAt,
    Instant UpdatedAt
);

public record ContainerData(
    Guid? CampSeasonId,
    int Year,
    string Name,
    string? Description
);
// Note: SortOrder was removed — see "Decisions during implementation".
```

### IContainerService

```csharp
public interface IContainerService
{
    Task<IReadOnlyList<ContainerDto>> GetBySeasonAsync(Guid campSeasonId, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerDto>> GetOrgByYearAsync(int year, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerDto>> GetAllByYearAsync(int year, CancellationToken ct = default); // added
    Task<ContainerDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ContainerDto> CreateAsync(ContainerData data, CancellationToken ct = default);
    Task<ContainerDto> UpdateAsync(Guid id, ContainerData data, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task UploadImageAsync(Guid id, Stream stream, string fileName, string contentType, long length, CancellationToken ct = default);
    Task DeleteImageAsync(Guid id, CancellationToken ct = default);
    Task<ContainerDto> SavePlacementAsync(Guid id, string geoJson, CancellationToken ct = default); // added
    Task ClearPlacementAsync(Guid id, CancellationToken ct = default); // added
}
```

### IContainerRepository

```csharp
public interface IContainerRepository
{
    Task<IReadOnlyList<Container>> GetBySeasonAsync(Guid campSeasonId, CancellationToken ct = default);
    Task<IReadOnlyList<Container>> GetOrgByYearAsync(int year, CancellationToken ct = default);
    Task<Container?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Container> AddAsync(Container container, CancellationToken ct = default);
    Task<Container> UpdateAsync(Container container, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
```

### IContainerImageStorage

```csharp
public interface IContainerImageStorage
{
    Task<string> SaveImageAsync(Guid containerId, Stream stream, string contentType, CancellationToken ct = default);
    void DeleteImage(string storagePath);
}
```

## Authorization

Auth is enforced in controllers via `IAuthorizationService.AuthorizeAsync`. Services are auth-free.

| Operation | Barrio containers | Org-level containers |
|---|---|---|
| Read | Camp lead of owning camp, CampAdmin, City Planning team | City Planning team |
| Create / Edit / Delete | Camp lead of owning camp, CampAdmin, City Planning team | City Planning team |
| Image upload/delete | Same as above | City Planning team |

"Camp lead of owning camp" = a user who is a camp lead for the specific camp whose season owns the container (not all camp leads).

"City Planning team member" = member of the city planning team (checked via `ICityPlanningService.IsCityPlanningTeamMemberAsync`).

## UI & Routes

### Barrio Containers — `ContainerController`

Route prefix: `/Camp/{slug}/Season/{year}/Containers`

| Action | Method | Route | Description |
|---|---|---|---|
| `Index` | GET | `/Camp/{slug}/Season/{year}/Containers` | List all containers for the season |
| `Create` | POST | `/Camp/{slug}/Season/{year}/Containers/Create` | Add a new container |
| `Edit` | POST | `/Camp/{slug}/Season/{year}/Containers/{id}/Edit` | Update name/description/sort order |
| `Delete` | POST | `/Camp/{slug}/Season/{year}/Containers/{id}/Delete` | Delete container (and image) |
| `UploadImage` | POST | `/Camp/{slug}/Season/{year}/Containers/{id}/Image/Upload` | Upload or replace image |
| `DeleteImage` | POST | `/Camp/{slug}/Season/{year}/Containers/{id}/Image/Delete` | Remove image |

Nav link: added to the camp management page (the page where camp leads manage their season details).

### Org-Level Containers — added to `CityPlanningController`

Route prefix: `/CityPlanning/BarrioMap/Admin/Containers` (changed from spec — see "Decisions during implementation")

| Action | Method | Route | Description |
|---|---|---|---|
| `Containers` | GET | `/CityPlanning/BarrioMap/Admin/Containers/{year}` | List org + all-barrio containers for a year |
| `CreateOrgContainer` | POST | `/CityPlanning/BarrioMap/Admin/Containers/{year}/Create` | Add org container |
| `CreateBarrioContainer` | POST | `/CityPlanning/BarrioMap/Admin/Containers/{year}/Barrios/{seasonId}/Create` | Add barrio container (admin) |
| `EditOrgContainer` | POST | `/CityPlanning/BarrioMap/Admin/Containers/{id}/Edit` | Update org container |
| `DeleteOrgContainer` | POST | `/CityPlanning/BarrioMap/Admin/Containers/{id}/Delete` | Delete org container |
| `UploadOrgContainerImage` | POST | `/CityPlanning/BarrioMap/Admin/Containers/{id}/Image/Upload` | Upload image |
| `DeleteOrgContainerImage` | POST | `/CityPlanning/BarrioMap/Admin/Containers/{id}/Image/Delete` | Remove image |

Nav link: added to the City Planning BarrioMap admin page.

## Decisions during implementation

- **`SortOrder` removed.** The initial migration added `SortOrder int` to `containers` and used it for display ordering. Migration `20260426140854_RemoveContainerSortOrder` immediately dropped it — containers are sorted by `Name` in all queries and views instead. `ContainerData` and `ContainerDto` do not include `SortOrder`.
- **`GetAllByYearAsync` added to `IContainerService`.** Not in this spec; added to support the City Planning container map and admin page loading all containers for a year regardless of barrio.
- **Org-level admin route changed.** The spec listed `/CityPlanning/Admin/Containers/{year}` as the org-level admin route. The actual route implemented is `/CityPlanning/BarrioMap/Admin/Containers/{year}` — nested under the BarrioMap admin panel. All related POST actions (`Create`, `Edit`, `Delete`, `Image/Upload`, `Image/Delete`) also live under `/CityPlanning/BarrioMap/Admin/Containers/…`.

## Out of Scope (Phase 2)

- Map placement: GeoJSON polygon/coordinates on the city planning map (implemented in PR peterdrier/Humans#389 via the container placement phase spec)
- Public visibility of containers on the map
- Container type/category classification

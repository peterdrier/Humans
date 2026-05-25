# Container Placement Notes — Design

**Date:** 2026-05-08
**Section:** Containers
**Status:** Approved for implementation

## Goal

Add a "Placement notes" facet to each Container so barrio leads (and Map Admins) can record placement constraints in free-form text and attach an optional sketch image. Consolidate image management into the Add/Edit modal and remove the per-row image-upload button.

## Background

Today a `Container` has `Name`, optional `Description`, and one optional image (managed via dedicated row buttons that toggle between "upload image" and "remove image"). Image upload is not part of the Add/Edit modal.

Operational feedback: placement decisions need both a longer-form constraint description and an attached sketch (separate from the container's main photo). Image management is also confusing as a row-level action — users expect it inside the edit form.

## Domain Changes

`Humans.Domain.Entities.Container` gains four new properties:

| Property | Type | Notes |
|----------|------|-------|
| `PlacementNotes` | `string?` | `text` column type; no length cap |
| `PlacementImageStoragePath` | `string?` | max 512 |
| `PlacementImageContentType` | `string?` | max 64 |
| `PlacementImageFileName` | `string?` | max 256 (original upload filename) |

EF configuration in `ContainerConfiguration` mirrors the existing main-image properties for the three placement-image columns; `PlacementNotes` uses `HasColumnType("text")` with no `HasMaxLength`.

## Migration

A single EF migration named `AddContainerPlacementNotes` adds the four columns. All nullable, no backfill needed.

## Storage Layer

Introduce an enum:

```csharp
public enum ContainerImageKind { Main, Placement }
```

Extend `IContainerImageStorage` so `SaveAsync` / `DeleteAsync` take a `ContainerImageKind` argument. Files continue to live under `wwwroot/uploads/containers/{containerId}/`. The filename prefix distinguishes the kind:

- Main: `main-{guid}.{ext}`
- Placement: `placement-{guid}.{ext}`

Replacing an image deletes the prior file of the same kind only. Deleting a container removes all files in that container's directory (existing behavior; no change beyond removing both kinds).

## Application Service

`IContainerService` updates:

- `CreateAsync` / `UpdateAsync` (or whatever the existing methods are named) accept additional parameters:
  - `string? placementNotes`
  - `IFormFile? mainImage`
  - `IFormFile? placementImage`
  - `bool removeMainImage`
  - `bool removePlacementImage`
- Image add/replace/remove for both kinds is handled inline in the same call as notes/name/description updates, in a single transaction.
- The standalone `UploadImage` / `DeleteImage` service methods are removed (callers go through Update).

`IFormFile` is a Web-layer type; the service should accept a small DTO (e.g. `ContainerImageUpload { Stream Content; string ContentType; string FileName; }`) so the Application layer stays free of `Microsoft.AspNetCore.Http`. The Web controller adapts `IFormFile` to that DTO.

## Web Layer

### ViewModels (`ContainerViewModels.cs`)

`ContainerFormModel` gains:
- `string? PlacementNotes`
- `IFormFile? MainImage`
- `IFormFile? PlacementImage`
- `bool RemoveMainImage`
- `bool RemovePlacementImage`

(Drop the `[StringLength(2000)]` cap from `Description`? **No** — leave existing fields unchanged.)

`ContainerViewModel` gains:
- `string? PlacementNotes`
- `string? PlacementImageUrl`
- `string? PlacementImageFileName`
- (computed) `bool HasPlacementInfo => !string.IsNullOrEmpty(PlacementNotes) || PlacementImageUrl is not null;`

### Controller (`ContainerController`)

- `Create` and `Edit` POST actions accept the expanded `ContainerFormModel` and, on successful auth + validation, call the updated service method.
- `UploadImage` and `DeleteImage` actions are **removed** along with their routes.
- Auth checks unchanged: barrio-lead / Map-Admin check, placement-phase gate for non-admins.

### Views (`Views/Container/Index.cshtml`)

**Container row** — simplified action group:
- Thumbnail (main image, click → lightbox), as today.
- Name + "Placed" badge.
- Description.
- **New:** `[(i) Placement notes]` button — small `btn btn-outline-info btn-sm` with info icon — rendered **only when** `c.HasPlacementInfo == true`. Opens `#placementInfoModal-{id}`.
- Edit button → `#editModal-{id}`.
- Delete button (form post).
- **Removed:** the "upload image" / "remove image" toggle button.

**Add/Edit modal** — restructured into two clearly labeled sections:

1. **General**
   - Name (required, max 256)
   - Description (textarea, max 2000)
   - Main image: file input (`accept="image/jpeg,image/png,image/webp"`); on edit, if a main image exists, show thumbnail + "Remove image" checkbox bound to `RemoveMainImage`.

2. **Placement notes**
   - Notes (textarea, no `maxlength`, larger `rows="5"`)
   - Placement image: same UX as main image (file input + existing thumbnail + remove checkbox in edit mode).

Form uses `enctype="multipart/form-data"`.

**Placement-info modal** (read-only, one per container that has placement info):
- Header: `@Localizer["Container_PlacementNotes_Title"]` + container name.
- Body:
  - If `PlacementNotes` is set: rendered as preserved-whitespace text (`<div style="white-space: pre-wrap">@c.PlacementNotes</div>`).
  - If `PlacementImageUrl` is set: image rendered full-width inside the modal (no nested lightbox — the modal is already a sized container; clicking it can close).
- Footer: Close button.

### Localization

New resource keys (en + es):
- `Container_FieldPlacementNotes`
- `Container_PlacementNotesPlaceholder`
- `Container_FieldMainImage`
- `Container_FieldPlacementImage`
- `Container_RemoveMainImage_Label`
- `Container_RemovePlacementImage_Label`
- `Container_PlacementNotes_ButtonLabel`
- `Container_PlacementNotes_Title`
- `Container_PlacementNotes_Empty` (used if button shown only because of image with no notes — rare; can be omitted if we render nothing for empty notes)

Existing keys reused: `Container_FieldName`, `Container_FieldDescription`, etc.

The deprecated keys (`Container_UploadImageTitle`, `Container_UploadImageModal_Title`, `Container_RemoveImageConfirm`, `Container_RemoveImageTitle`, `Container_ImageFileLabel`) are removed since the row image button and standalone upload modal are gone.

## Section Doc Updates

`docs/sections/Containers.md`:
- Data Model table: add the four new columns.
- Triggers: clarify that container deletion removes both image kinds; uploading a Main image only deletes the prior Main file (and same for Placement).
- Invariants: storage path pattern updated to `uploads/containers/{containerId}/{kind}-{guid}.{ext}`.
- Note that image management is now part of Create/Edit, not a separate action.

## Out of Scope

- Bulk import of placement notes.
- Versioning / history of placement notes.
- Rich-text formatting (notes are plain text with preserved whitespace).
- A dedicated "Placement Notes" admin index (notes are still per-container only).

## Testing

- Architecture test (existing pattern): no `Microsoft.AspNetCore.Http` reference in `Humans.Application`.
- Service tests: create with both images + notes, edit replacing one image, edit removing both, edit clearing notes.
- Storage tests (if existing pattern): both kinds saved to expected path; replacing a kind removes the prior file of that kind only.
- Manual smoke: row button hidden when no placement info; modal renders text + image correctly; edit modal preserves existing images when no upload selected.

## Build Sequence

1. Domain: add four properties to `Container`.
2. Infrastructure: update `ContainerConfiguration`; add migration.
3. Storage: enum + `IContainerImageStorage` extension + `ContainerImageStorage` impl update.
4. Application: service interface + impl updates; new DTO for image uploads.
5. Web: ViewModels updated; controller actions updated; views restructured; localization keys added.
6. Tests updated.
7. Section doc refreshed.

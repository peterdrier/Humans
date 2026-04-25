# Camps — Section Invariants

Themed community camps (Barrios) with per-year season registrations, leads, images, and renaming history.

## Concepts

- A **Camp** (also called "Barrio") is a themed community camp. Each camp has a unique URL slug, one or more leads, and optional images.
- A **Camp Season** is a per-year registration for a camp, containing the year-specific name, description, community info, and placement details.
- A **Camp Lead** is a human responsible for managing a camp. Leads have a role: Primary or CoLead.
- **Camp Settings** is a singleton controlling which year is public (shown in the directory) and which seasons accept new registrations.

## Data Model

### Camp

Core entity: contact info, slug, flags.

**Table:** `camps`

Cross-domain nav `Camp.CreatedByUser` is declared on the entity but never read by Camps code. Pre-existing; tracked for cross-cutting cleanup with the User nav strip in design-rules §15i.

### CampSeason

Per-year season data (name, blurbs, community info, placement).

**Table:** `camp_seasons`

Cross-domain nav `CampSeason.ReviewedByUser` is declared on the entity but never read by Camps code. Pre-existing; tracked for cross-cutting cleanup.

### CampLead

Lead assignments with Primary or CoLead roles.

**Table:** `camp_leads`

Cross-domain nav `CampLead.User` is **stripped** (PR for issue nobodies-collective/Humans#542). Lead display names resolve via `IUserService.GetByIdsAsync`.

### CampImage

Image metadata; files are stored on disk via `ICampImageStorage` (Application interface). Display order is tracked per camp.

**Table:** `camp_images`

### CampHistoricalName

Name history for tracking renames.

**Table:** `camp_historical_names`

### CampSettings

Singleton settings: public year, open seasons, name-lock dates.

**Table:** `camp_settings`

### Camp enums

| Enum | Values |
|------|--------|
| CampSeasonStatus | Pending, Active, Full, Rejected, Withdrawn |
| CampLeadRole | Primary, CoLead |
| CampVibe | Adult, ChillOut, ElectronicMusic, Games, Queer, Sober, Lecture, LiveMusic, Wellness, Workshop |
| CampNameSource | Manual, NameChange |
| YesNoMaybe | Yes, No, Maybe |
| KidsVisitingPolicy | Yes, DaytimeOnly, No |
| PerformanceSpaceStatus | Yes, No, WorkingOnIt |
| AdultPlayspacePolicy | Yes, No, NightOnly |
| SpaceSize | Sqm150, Sqm300, Sqm450, Sqm600, Sqm750, Sqm900, Sqm1200, Sqm1500, Sqm2000, Sqm2400, Sqm2800 |
| SoundZone | Blue, Green, Yellow, Orange, Red, Surprise |
| ElectricalGrid | Yellow, Red, Norg, OwnSupply, Unknown |

All stored as strings via `HasConversion<string>()`. `Vibes` stored as jsonb array.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone (including anonymous) | Browse the camps directory, view camp details and season details |
| Any authenticated human | Register a new camp (which creates a new season in Pending status) |
| Camp lead | Edit their camp's details, manage season registrations, manage co-leads, upload/manage images, manage historical names |
| CampAdmin, Admin | All camp lead capabilities on all camps. Approve/reject season registrations. Manage camp settings (public year, open seasons, name lock dates). View withdrawn and rejected seasons. Export camp data |
| Admin | Delete camps |

## Invariants

- Each camp has a unique slug used for URL routing.
- Camp season status follows: Pending then Active, Full, Rejected, or Withdrawn. Only CampAdmin can approve or reject a season.
- Only camp leads or CampAdmin can edit a camp.
- Camp images are stored on disk via `ICampImageStorage`; metadata and display order are tracked per camp.
- Historical names are recorded when a camp is renamed.
- Camp settings control which year is shown publicly and which seasons accept registrations.
- Resource-based authorization per design-rules §11: `CampAuthorizationHandler` + `CampOperationRequirement` gate all admin writes.

## Negative Access Rules

- Regular humans **cannot** edit camps they do not lead.
- Camp leads **cannot** approve or reject season registrations — that requires CampAdmin or Admin.
- CampAdmin **cannot** delete camps. Only Admin can delete a camp.
- Anonymous visitors **cannot** register camps or edit any camp data.

## Triggers

- When a camp is registered, its initial season is created with Pending status.
- Season approval or rejection is performed by CampAdmin.

## Cross-Section Dependencies

- **Users/Identity:** `IUserService.GetByIdsAsync` — lead display names (stitched in memory after CampLead.User strip).
- **Admin:** Camp settings management is restricted to CampAdmin and Admin (resource-based auth handler).
- **City Planning:** CampSeason is the anchor for `camp_polygons`; City Planning reads camp data via `ICampService` but writes its own tables only.

## Architecture

**Owning services:** `CampService`, `CampContactService`
**Owned tables:** `camps`, `camp_seasons`, `camp_leads`, `camp_images`, `camp_historical_names`, `camp_settings`
**Status:** (A) Migrated (peterdrier/Humans PR for issue nobodies-collective/Humans#542, 2026-04-22).

- `CampService` lives in `Humans.Application.Services.Camps.CampService` and goes through `ICampRepository` (`Humans.Application.Interfaces.Repositories`) for all data access. It never imports `Microsoft.EntityFrameworkCore` — enforced at compile time by `Humans.Application.csproj`'s reference graph.
- `CampRepository` lives in `Humans.Infrastructure.Repositories`, uses `IDbContextFactory<HumansDbContext>`, and is registered as Singleton.
- **Decorator decision — no caching decorator.** The ~100-row camp list uses short-TTL `IMemoryCache` inside the service for `camps-for-year` and `camp-settings` (~5 min) per design-rules §15f. These are request-acceleration caches, not canonical domain data caches.
- Filesystem I/O for camp images is abstracted behind `ICampImageStorage` (Application interface + `CampImageStorage` implementation in `Humans.Infrastructure`); the service never touches `System.IO`.
- **Cross-domain navs stripped:** `CampLead.User` (issue nobodies-collective/Humans#542) — consumers route through `IUserService.GetByIdsAsync(...)`.
- `CampContactService` has no owned DB tables and does not inject `HumansDbContext`; it retains its `IMemoryCache` rate-limit usage since that's a request-acceleration cache, not canonical domain data.
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/CampsArchitectureTests.cs`.

### Touch-and-clean guidance

- `Camp.CreatedByUser` and `CampSeason.ReviewedByUser` are declared but never read. They are safe targets for the cross-cutting User nav strip when the wider effort lands.

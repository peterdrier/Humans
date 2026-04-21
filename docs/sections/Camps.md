# Camps — Section Invariants

## Concepts

- A **Camp** (also called "Barrio") is a themed community camp. Each camp has a unique URL slug, one or more leads, and optional images.
- A **Camp Season** is a per-year registration for a camp, containing the year-specific name, description, community info, and placement details.
- A **Camp Lead** is a human responsible for managing a camp. Leads have a role: Primary or CoLead.
- **Camp Settings** is a singleton controlling which year is public (shown in the directory) and which seasons accept new registrations.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Anyone (including anonymous) | Browse the camps directory, view camp details and season details |
| Any authenticated human | Register a new camp (which creates a new season in Pending status) |
| Camp lead | Edit their camp's details, manage season registrations, manage co-leads, upload/manage images, manage historical names |
| CampAdmin, Admin | All camp lead capabilities on all camps. Approve/reject season registrations. Manage camp settings (public year, open seasons, name lock dates). View withdrawn and rejected seasons. Export camp data |
| Admin | Delete camps |

## Invariants

- Each camp has a unique slug used for URL routing.
- Camp season status follows: Pending then Active, Full, Rejected, or Withdrawn. Only CampAdmin can approve or reject a season.
- Only camp leads or CampAdmin can edit a camp.
- Camp images are stored on disk; metadata and display order are tracked per camp.
- Historical names are recorded when a camp is renamed.
- Camp settings control which year is shown publicly and which seasons accept registrations.

## Negative Access Rules

- Regular humans **cannot** edit camps they do not lead.
- Camp leads **cannot** approve or reject season registrations — that requires CampAdmin or Admin.
- CampAdmin **cannot** delete camps. Only Admin can delete a camp.
- Anonymous visitors **cannot** register camps or edit any camp data.

## Triggers

- When a camp is registered, its initial season is created with Pending status.
- Season approval or rejection is performed by CampAdmin.

## Cross-Section Dependencies

- **Profiles**: Camp leads are linked to humans. Lead assignment requires a valid human account.
- **Admin**: Camp settings management is restricted to CampAdmin and Admin.

## Architecture — Migrated (issue #542)

See `docs/architecture/design-rules.md` §15 for the full rules.

**Owning services:** `CampService` (Humans.Application.Services.Camps), `CampContactService`
**Owned tables:** `camps`, `camp_seasons`, `camp_leads`, `camp_images`, `camp_historical_names`, `camp_settings`

**Status:** Migrated to the §15 Application-layer repository pattern (issue #542, 2026-04-22).

- `CampService` lives in `Humans.Application.Services.Camps.CampService` and goes through `ICampRepository` (`Humans.Application.Interfaces.Repositories`) for all data access. It never imports `Microsoft.EntityFrameworkCore` — enforced at compile time by `Humans.Application.csproj`'s reference graph.
- `CampRepository` lives in `Humans.Infrastructure.Repositories`, uses `IDbContextFactory<HumansDbContext>`, and is registered as Singleton.
- No caching decorator: camps list is ~100 rows; short-TTL `IMemoryCache` inside the service (for `camps-for-year` and `camp-settings`, ~5 min) is sufficient per design-rules §15f.
- Filesystem I/O for camp images is abstracted behind `ICampImageStorage` (Application interface + `CampImageStorage` implementation under Infrastructure); the service never touches `System.IO`.
- Cross-domain nav `CampLead.User` stripped — consumers route through `IUserService.GetByIdsAsync(...)`. `Camp.CreatedByUser` and `CampSeason.ReviewedByUser` remain declared on the entities but are never read (pre-existing; tracked for cross-cutting cleanup).
- `CampContactService` has no owned DB tables and does not inject `HumansDbContext`; it retains its `IMemoryCache` rate-limit usage since that's a request-acceleration cache, not canonical domain data.

Architecture invariants are enforced by tests in `tests/Humans.Application.Tests/Architecture/CampsArchitectureTests.cs`.

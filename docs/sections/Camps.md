<!-- freshness:triggers
  src/Humans.Application/Services/Camps/**
  src/Humans.Domain/Entities/Camp.cs
  src/Humans.Domain/Entities/CampSeason.cs
  src/Humans.Domain/Entities/CampLead.cs
  src/Humans.Domain/Entities/CampMember.cs
  src/Humans.Domain/Entities/CampImage.cs
  src/Humans.Domain/Entities/CampHistoricalName.cs
  src/Humans.Domain/Entities/CampSettings.cs
  src/Humans.Infrastructure/Data/Configurations/Camps/**
  src/Humans.Infrastructure/Repositories/CampRepository.cs
  src/Humans.Web/Controllers/CampController.cs
  src/Humans.Web/Controllers/CampAdminController.cs
  src/Humans.Web/Controllers/CampApiController.cs
  src/Humans.Web/Authorization/Requirements/CampAuthorizationHandler.cs
  src/Humans.Web/Authorization/Requirements/CampOperationRequirement.cs
-->
<!-- freshness:flag-on-change
  Camp/Season lifecycle, lead/membership authorization, public-year settings, and notification triggers — review when Camp services/entities/controllers/auth handlers change.
-->

# Camps — Section Invariants

Themed community camps (Barrios) with per-year season registrations, leads, images, and renaming history.

## Concepts

- A **Camp** (also called "Barrio") is a themed community camp. Each camp has a unique URL slug, one or more leads, and optional images.
- A **Camp Season** is a per-year registration for a camp, containing the year-specific name, description, community info, and placement details.
- A **Camp Lead** is a human responsible for managing a camp. Leads have a role: Primary or CoLead.
- A **Camp Member** is a human's post-hoc, per-season affiliation with a camp. The app does **not** admit humans to a camp — each camp runs its own process. A CampMember row exists so the app knows who belongs to which camp for per-camp roles (e.g. LNT lead), Early Entry allocations, and notifications. Status: Pending → Active → Removed. `Removed` is a soft-delete tombstone so re-requesting creates a new row.
- A **Camp Role Definition** is a CampAdmin-managed catalogue row describing a per-camp role (Consent Lead, LNT, Shit Ninja, Power, Build Lead, etc.) with a slot count, compliance threshold (`MinimumRequired`), sort order, and `IsRequired` flag. Soft-deleted via `DeactivatedAt` so historical assignments survive removal from the active catalogue.
- A **Camp Role Assignment** is a per-season binding of a `CampMember` to a `CampRoleDefinition`. The "Camp Lead" concept is **not** a Camp Role Definition in this PR — lead authz still flows through the `CampLead` entity until a follow-up issue retires it.
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

### CampMember

Per-season, post-hoc human/camp affiliation. Status: Pending → Active → Removed (soft-delete tombstone). Partial unique index on `(CampSeasonId, UserId) WHERE Status <> 'Removed'` so removed rows retain audit history and allow re-requesting.

**Table:** `camp_members`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| CampSeasonId | Guid | FK → CampSeason |
| UserId | Guid | FK → User (scalar; no nav per §6) |
| Status | CampMemberStatus | Pending, Active, Removed |
| RequestedAt | Instant | When the request was created |
| ConfirmedAt | Instant? | Set on approve |
| ConfirmedByUserId | Guid? | Lead who approved (scalar) |
| RemovedAt | Instant? | Set on remove/withdraw/leave/reject |
| RemovedByUserId | Guid? | Actor who closed the row (scalar) |

### CampRoleDefinition

CampAdmin-managed catalogue of per-camp roles. Soft-deleted via `DeactivatedAt`; historical assignments survive deactivation. Owned by `CampRoleService` (separate from `CampService`).

**Table:** `camp_role_definitions`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Name | string | Unique (case-insensitive) |
| Description | string? | Markdown |
| SlotCount | int | Default 1; soft cap enforced in service, not in DB |
| MinimumRequired | int | Default 1; cross-field validation enforces `0 ≤ MinimumRequired ≤ SlotCount` |
| SortOrder | int | Display order on Camp Edit roles panel |
| IsRequired | bool | True if compliance report tracks this role |
| DeactivatedAt | Instant? | Null = active; non-null hides from new-assignment UI |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

Aggregate-local nav: `CampRoleDefinition.Assignments` (back-ref).

### CampRoleAssignment

Per-season binding of a `CampMember` to a `CampRoleDefinition`.

**Table:** `camp_role_assignments`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| CampSeasonId | Guid | FK → CampSeason (`OnDelete(Cascade)`) |
| CampRoleDefinitionId | Guid | FK → CampRoleDefinition (`OnDelete(Restrict)` — deactivate, don't delete) |
| CampMemberId | Guid | FK → CampMember (`OnDelete(Cascade)` — hard-delete cascades; soft-delete cleared in service) |
| AssignedAt | Instant | |
| AssignedByUserId | Guid | Scalar; no nav per design-rules §6 |

**Unique index:** `(CampSeasonId, CampRoleDefinitionId, CampMemberId)` — a human cannot hold the same role twice in the same season.

Aggregate-local navs: `CampRoleAssignment.CampSeason`, `CampRoleAssignment.Definition`, `CampRoleAssignment.CampMember` (all within the Camps section).

### Camp enums

| Enum | Values |
|------|--------|
| CampSeasonStatus | Pending, Active, Full, Rejected, Withdrawn |
| CampLeadRole | Primary, CoLead |
| CampMemberStatus | Pending, Active, Removed |
| CampVibe | Adult, ChillOut, ElectronicMusic, Games, Queer, Sober, Lecture, LiveMusic, Wellness, Workshop |
| CampNameSource | Manual, NameChange |
| YesNoMaybe | Yes, No, Maybe |
| KidsVisitingPolicy | Yes, DaytimeOnly, No |
| PerformanceSpaceStatus | Yes, No, WorkingOnIt |
| AdultPlayspacePolicy | Yes, No, NightOnly |
| SpaceSize | Sqm150, Sqm300, Sqm450, Sqm600, Sqm800, Sqm1000, Sqm1200, Sqm1500, Sqm1800, Sqm2200, Sqm2800 |
| SoundZone | Blue, Green, Yellow, Orange, Red, Surprise |
| ElectricalGrid | Yellow, Red, Norg, OwnSupply, Unknown |

All stored as strings via `HasConversion<string>()`. `Vibes` stored as jsonb array.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone (including anonymous) | Browse the camps directory, view camp details and season details |
| Any authenticated human | Register a new camp (which creates a new season in Pending status). Request to join a camp for its open season; withdraw their own pending request; leave their own active membership. |
| Camp lead | Edit their camp's details, manage season registrations, manage co-leads, upload/manage images, manage historical names. Approve / reject pending membership requests for their camp. Remove active members. Add an active member directly to their camp (lead-driven shortcut). Assign / unassign per-camp role assignments for their camp. |
| CampAdmin, Admin | All camp lead capabilities on all camps. Approve/reject season registrations. Reactivate a Full or Withdrawn season. Manage camp settings (public year, open seasons, name lock dates). Update registration info copy. View withdrawn seasons on the admin dashboard. Export camp data as CSV. Manage the role-definition catalogue (create, edit, deactivate, reactivate). View the required-role compliance report. |
| Admin | Delete camps |

## Invariants

- Each camp has a unique slug used for URL routing.
- Camp season status follows: Pending then Active, Full, Rejected, or Withdrawn. Only CampAdmin can approve or reject a season.
- Only camp leads or CampAdmin can edit a camp.
- Camp images are stored on disk via `ICampImageStorage`; metadata and display order are tracked per camp.
- Historical names are recorded when a camp is renamed.
- Camp settings control which year is shown publicly and which seasons accept registrations.
- Resource-based authorization per design-rules §11: `CampAuthorizationHandler` + `CampOperationRequirement` gate all admin writes.
- Membership is **per-season**. One live (`Pending`/`Active`) row per `(CampSeasonId, UserId)` enforced by a partial unique index. `Removed` rows are kept for audit and do not block re-requests.
- Membership mutations (approve, reject, remove) are **scoped to the authorizing camp**. A lead or CampAdmin operating on camp A cannot mutate a member row whose season belongs to camp B even if they know the row id.
- Membership state is **never rendered on anonymous or public views**. It is only shown to the human themselves and to leads/CampAdmin of the camp.
- A `CampRoleAssignment` requires the linked `CampMember` to have `Status = Active` for the same `CampSeasonId`. Service rejects with `MemberNotActive` or `MemberSeasonMismatch` otherwise.
- A human cannot hold the same role twice in the same season — enforced by unique index on `(CampSeasonId, CampRoleDefinitionId, CampMemberId)`.
- All role-assignment data is private (no anonymous render). The public Camp Details page does not expose role assignments.
- Leave/Withdraw/Remove cascades clear role assignments via `ICampRoleService.RemoveAllForMemberAsync` before the soft-delete. Hard-delete of a `CampMember` row cascades through the FK directly.
- Camp Lead authz remains on the `CampLead` entity until the follow-up issue retires it. The "Camp Lead" role is **not** a `CampRoleDefinition` row in this PR.

## Negative Access Rules

- Regular humans **cannot** edit camps they do not lead.
- Camp leads **cannot** approve or reject season registrations — that requires CampAdmin or Admin.
- CampAdmin **cannot** delete camps. Only Admin can delete a camp.
- Anonymous visitors **cannot** register camps or edit any camp data.
- Anonymous visitors **cannot** see role assignments — the public Camp Details page does not render the roles section.
- Camp leads **cannot** manage the role-definition catalogue (create, edit, deactivate). Only CampAdmin or Admin can.
- A camp lead **cannot** assign or unassign roles on a camp other than their own (controller verifies `assignment.CampSeason.CampId == camp.Id` before delegating to the service).
- Anyone **cannot** assign a role to a human who is not an Active CampMember of the same season — service rejects with `MemberNotActive` / `MemberSeasonMismatch`.

## Triggers

- When a camp is registered, its initial season is created with Pending status.
- Season approval or rejection is performed by CampAdmin.
- Approving a membership request sends a `CampMembershipApproved` notification to the requester.
- Rejecting a membership request sends a `CampMembershipRejected` notification to the requester.
- When a season is rejected or withdrawn, pending requesters receive a `CampMembershipSeasonClosed` notification. Their membership rows are **not** auto-mutated — the notification is the only side effect, so if the season is later reactivated the request is still live.
- Camp leads do **not** receive a per-request stored notification when humans request to join. Instead a `NotificationMeter` ("N humans want to join your camp") shows the live pending count; it updates immediately on approve/reject/withdraw and drops to zero when the season is closed.
- Active leads appear in the camp's active-members list automatically, tagged with an `IsLead` flag. They do not need a CampMember row to be shown as part of the camp. **This `IsLead` flag is temporary** — the upcoming camp-roles PR will subsume the `CampLead` concept into role assignments on `CampMember` (Team-style). When that lands, the synthesis logic in `CampService.GetCampMembersAsync` and the `IsLead` flag on `CampMemberRow` / `CampMemberRowViewModel` must be removed, and the `camp_leads` table dropped after migrating existing leads to role assignments.
- When a CampMember is removed (Leave / Withdraw / Remove paths set `RemovedAt`), `ICampService` calls `ICampRoleService.RemoveAllForMemberAsync` before the soft-delete to clear any role assignments held by that member.
- When a lead uses the "add active member" shortcut at `/Camps/{slug}/Members/Add`, `ICampService.AddCampMemberAsLeadAsync` creates `CampMember(Status=Active)` directly and writes a `CampMemberAddedByLead` audit entry.
- Assigning a per-camp role writes a `CampRoleAssigned` audit entry and sends a best-effort `CampRoleAssigned` notification to the assignee. Unassign writes `CampRoleUnassigned` and does **not** notify.
- Definition CRUD (`CampRoleDefinitionCreated` / `Updated` / `Deactivated` / `Reactivated`) writes audit entries; ordering is `repo.Add` then `SaveChangesAsync` then `auditLog.LogAsync`.

## Cross-Section Dependencies

- **Users/Identity:** `IUserService.GetByIdsAsync` — lead and assignee display names (stitched in memory after `CampLead.User` strip; `CampRoleAssignment.AssignedByUserId` is scalar-only).
- **Admin:** Camp settings management is restricted to CampAdmin and Admin (resource-based auth handler).
- **City Planning:** CampSeason is the anchor for `camp_polygons`; City Planning reads camp data via `ICampService` but writes its own tables only.
- **Camps internal — `CampRoleService` ↔ `CampService`:** `CampRoleService` calls `ICampService` for camp/season lookup and active-membership verification, and is called back by `ICampService` from the Leave/Withdraw/Remove paths via `ICampRoleService.RemoveAllForMemberAsync`. Both services live within the Camps section.
- **Audit Log:** `IAuditLogService` — definition CRUD, role assign/unassign, and `CampMemberAddedByLead` actions.
- **Notifications:** `INotificationService` — `CampRoleAssigned` notification on assign (best-effort, try/catch in controller).

## Architecture

**Owning services:** `CampService`, `CampContactService`, `CampRoleService`
**Owned tables:**
- `CampService` — `camps`, `camp_seasons`, `camp_leads`, `camp_members`, `camp_images`, `camp_historical_names`, `camp_settings`
- `CampRoleService` — `camp_role_definitions`, `camp_role_assignments`

**Status:** (A) Migrated (peterdrier/Humans PR for issue nobodies-collective/Humans#542, 2026-04-22). `CampRoleService` introduced in (A) shape from day one per issue nobodies-collective#489 (this PR).

- `CampService` lives in `Humans.Application.Services.Camps.CampService` and goes through `ICampRepository` (`Humans.Application.Interfaces.Repositories`) for all data access. It never imports `Microsoft.EntityFrameworkCore` — enforced at compile time by `Humans.Application.csproj`'s reference graph.
- `CampRepository` lives in `Humans.Infrastructure.Repositories`, uses `IDbContextFactory<HumansDbContext>`, and is registered as Singleton.
- **Decorator decision — no caching decorator.** The ~100-row camp list uses short-TTL `IMemoryCache` inside the service for `camps-for-year` and `camp-settings` (~5 min) per design-rules §15f. These are request-acceleration caches, not canonical domain data caches.
- Filesystem I/O for camp images is abstracted behind `ICampImageStorage` (Application interface + `CampImageStorage` implementation in `Humans.Infrastructure`); the service never touches `System.IO`.
- **Cross-domain navs stripped:** `CampLead.User` (issue nobodies-collective/Humans#542) — consumers route through `IUserService.GetByIdsAsync(...)`.
- `CampContactService` has no owned DB tables and does not inject `HumansDbContext`; it retains its `IMemoryCache` rate-limit usage since that's a request-acceleration cache, not canonical domain data.
- `CampRoleService` lives in `Humans.Application.Services.Camps.CampRoleService` and goes through `ICampRoleRepository` (`Humans.Application.Interfaces.Repositories`) for all data access. It owns `camp_role_definitions` and `camp_role_assignments` and never imports `Microsoft.EntityFrameworkCore`. Display-name stitching for `AssignedByUserId` routes through `IUserService.GetByIdsAsync`. Plain pass-through (no caching decorator); add `IMemoryCache` later if list-of-definitions reads dominate.
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/CampsArchitectureTests.cs`.

### Touch-and-clean guidance

- `Camp.CreatedByUser` and `CampSeason.ReviewedByUser` are declared but never read. They are safe targets for the cross-cutting User nav strip when the wider effort lands.

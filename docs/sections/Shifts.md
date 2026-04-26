<!-- freshness:triggers
  src/Humans.Application/Services/Shifts/**
  src/Humans.Domain/Entities/Rota.cs
  src/Humans.Domain/Entities/Shift.cs
  src/Humans.Domain/Entities/ShiftSignup.cs
  src/Humans.Domain/Entities/ShiftTag.cs
  src/Humans.Domain/Entities/EventSettings.cs
  src/Humans.Domain/Entities/EventParticipation.cs
  src/Humans.Domain/Entities/GeneralAvailability.cs
  src/Humans.Domain/Entities/VolunteerEventProfile.cs
  src/Humans.Domain/Entities/VolunteerTagPreference.cs
  src/Humans.Infrastructure/Data/Configurations/Shifts/**
  src/Humans.Web/Controllers/ShiftsController.cs
  src/Humans.Web/Controllers/ShiftAdminController.cs
  src/Humans.Web/Controllers/ShiftDashboardController.cs
  src/Humans.Web/Controllers/VolController.cs
-->
<!-- freshness:flag-on-change
  Shift signup state machine, capacity ceilings, range-block atomicity, voluntelling rules, and coordinator/manager scope — review when Shifts services/entities/controllers change.
-->

# Shifts — Section Invariants

Event shifts, rotas, signups, range blocks, event settings, general availability, per-event volunteer profiles.

## Concepts

- A **Rota** is a named container for shifts, belonging to a department or sub-team and an event. Each rota has a period (Build, Event, or Strike) that determines whether its shifts are all-day or time-slotted.
- A **Shift** is a single work slot with a day offset, optional start time, duration, and maximum volunteer count.
- A **Shift Signup** links a human to a shift. Signups progress through states: Pending, Confirmed, Refused, Bailed, Cancelled, or NoShow.
- **Range Signups** link multiple shifts via a block ID. Operations on a range (bail, approve, refuse) apply to the entire block atomically.
- **Event Settings** is a singleton per event controlling dates, timezone, early-entry capacity, global volunteer cap, and whether shift browsing is open to regular volunteers.
- **General Availability** tracks per-human per-event day availability.
- **Volunteer Event Profile** stores per-event volunteer data including skills, dietary preferences, and medical information.
- **Rota Tags** are labels on rotas used for filtering and volunteer preference matching.
- **Voluntelling** is when an admin or coordinator signs up a human for a shift on their behalf.

## Data Model

### EventSettings

Singleton per event — dates, timezone, early-entry capacity, global volunteer cap, shift browsing toggle.

**Table:** `event_settings`

Aggregate-local navs: `EventSettings.Rotas`.

### Rota

Shift container, belongs to department + event. Has `Period` (Build/Event/Strike), optional `PracticalInfo`, `IsVisibleToVolunteers` (default true).

**Table:** `rotas`

Aggregate-local navs: `Rota.Shifts`, `Rota.EventSettings`, `Rota.Tags`. Cross-domain nav `Rota.Team → Rota.TeamId` target (Teams).

### Shift

Single work slot — `DayOffset + StartTime + Duration + IsAllDay`.

**Table:** `shifts`

Aggregate-local navs: `Shift.Rota`, `Shift.ShiftSignups`.

### ShiftSignup

Links User to Shift with state machine (Pending/Confirmed/Refused/Bailed/Cancelled/NoShow), optional `SignupBlockId` for range signups.

**Table:** `shift_signups`

Aggregate-local navs: `ShiftSignup.Shift`. Cross-domain navs `ShiftSignup.User`, `ShiftSignup.EnrolledByUser`, `ShiftSignup.ReviewedByUser` — `User` and `ReviewedByUser` are **deliberately preserved** (see Migration status); `EnrolledByUser` is a safe nav-strip target.

### GeneralAvailability

Per-user per-event day availability. `AvailableDayOffsets` stored as jsonb.

**Table:** `general_availabilities`

Cross-domain nav `GeneralAvailability.User` was **stripped** in peterdrier/Humans PR for sub-task nobodies-collective/Humans#541c (FK kept via `HasOne<User>().WithMany().HasForeignKey(...)`).

### VolunteerEventProfile

Per-event volunteer profile with skills, dietary, medical data.

**Table:** `volunteer_event_profiles`

Cross-domain nav `VolunteerEventProfile.User → UserId` target.

### Shift tag tables (§8 gap)

- `shift_tags` — read/written by `ShiftManagementService`. Not yet listed in design-rules §8 under any section. Likely Shifts-owned.
- `volunteer_tag_preferences` — read/written by `ShiftManagementService`. Also not listed. Likely Shifts-owned.

§8 needs an explicit ownership decision for both before full migration; assumed Shifts-owned for the repository split.

### RotaPeriod

Explicit period set on a Rota. Drives creation UX (all-day vs time-slotted) and signup UX (date-range vs individual). Distinct from computed `ShiftPeriod`.

| Value | Int | Description |
|-------|-----|-------------|
| Build | 0 | Build period — all-day shifts, date-range signup |
| Event | 1 | Event period — time-slotted shifts, individual signup |
| Strike | 2 | Strike period — all-day shifts, date-range signup |

Stored as string via `HasConversion<string>()`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any active human | Browse available shifts (when browsing is open or they have existing signups). Sign up for shifts. View own signups and schedule. Bail from own signups. Set general availability. Fill out volunteer event profile |
| Department coordinator | Manage rotas and shifts for their department and all sub-teams. Approve, refuse, and bail signups. Voluntell humans. Manage rota tags. View volunteer event profiles (except medical data) |
| Sub-team manager | Manage rotas and shifts for their sub-team only. Approve, refuse, and bail signups on their sub-team. Voluntell humans on their sub-team. Cannot manage sibling sub-teams or the parent department |
| VolunteerCoordinator | All coordinator capabilities across all departments. Move rotas between departments. Access the cross-department shift dashboard |
| NoInfoAdmin, Admin | Approve, refuse, and bail signups across all departments. View volunteer medical data. Access the cross-department shift dashboard |
| Admin | Manage event settings (dates, timezone, early-entry capacity, global volunteer cap, shift browsing toggle) |

## Invariants

- Shift signup status follows: Pending then Confirmed, Refused, Bailed, Cancelled, or NoShow. Only valid forward transitions are allowed.
- MaxVolunteers is a hard capacity ceiling. Signups, approvals, and voluntelling are blocked when the confirmed count reaches MaxVolunteers. Range operations skip full shifts.
- Rota visibility is controlled by an "is visible to volunteers" toggle (default: visible). Hidden rotas are only shown to coordinators and privileged roles.
- Voluntelling (admin-initiated signup) records who enrolled the human.
- Range signups create or cancel all shifts in the date range atomically.
- Event settings is a singleton per event.
- Rota period (Build, Event, Strike) determines the shift creation UX (all-day vs time-slotted) and signup UX (date-range vs individual).
- Medical data in volunteer event profiles is restricted to Admin and NoInfoAdmin.
- When shift browsing is closed, regular volunteers can only see shifts if they already have signups. Coordinators and privileged roles can always browse.
- All dashboard methods on `IShiftManagementService` (`GetDashboardOverviewAsync`, `GetCoordinatorActivityAsync`, `GetDashboardTrendsAsync`) require the `ShiftDashboardAccess` policy at the controller. The service itself is auth-free per design rules.
- `DevelopmentDashboardSeeder` and its `POST /dev/seed/dashboard` endpoint are gated to `IWebHostEnvironment.IsDevelopment()` only. QA, preview, and production environments cannot invoke it regardless of role.

## Negative Access Rules

- Regular humans **cannot** manage rotas or shifts. They can only browse and sign up.
- Regular humans **cannot** approve, refuse, or bail other humans' signups.
- Regular humans **cannot** voluntell other humans.
- Department coordinators **cannot** manage rotas or approve signups outside their own department.
- Sub-team managers **cannot** manage rotas or approve signups outside their own sub-team (not siblings, not parent department).
- Department coordinators **cannot** view volunteer medical data.
- NoInfoAdmin **cannot** create or edit rotas or shifts. They can only manage signups (approve, refuse, bail) and view medical data.
- VolunteerCoordinator **cannot** view volunteer medical data.

## Triggers

- When a signup is approved or refused, an email notification is queued to the volunteer.
- When a human is voluntelled, an email notification is queued to them.
- Range signup or bail operations create or cancel all shifts in the block atomically.

## Cross-Section Dependencies

- **Teams:** `ITeamService` — rotas belong to a department or sub-team. Coordinator/manager status determines shift management access.
- **Profiles:** `IProfileService` / `IUserService` — volunteer event profile stores per-event volunteer data. NoShow history is shown on a human's profile to coordinators and privileged roles.
- **Auth:** `IRoleAssignmentService` — role checks for dashboard access, volunteer coordinator scope.
- **Email:** `IEmailOutboxService` — signup status change notifications queued through the email outbox.
- **Admin:** Event settings management is Admin-only.

## Architecture

**Owning services:** `ShiftManagementService`, `ShiftSignupService`, `GeneralAvailabilityService`
**Owned tables:** `rotas`, `shifts`, `shift_signups`, `event_settings`, `general_availabilities`, `volunteer_event_profiles` (plus `shift_tags` and `volunteer_tag_preferences` — pending §8 confirmation)
**Status:** (A) Fully migrated. `ShiftManagementService`, `ShiftSignupService`, and `GeneralAvailabilityService` all live in `Humans.Application.Services.Shifts` and route through `IShiftManagementRepository` / `IShiftSignupRepository` / `IGeneralAvailabilityRepository`. Cross-domain navs on Shifts-owned entities (`Rota.Team`, `ShiftSignup.User` / `EnrolledByUser` / `ReviewedByUser`, `VolunteerEventProfile.User`, `VolunteerTagPreference.User`) deleted 2026-04-25 in nobodies-collective/Humans#541 final pass; FKs stay wired in EF via the typed-FK form.

### Target repositories

- **`IShiftManagementRepository`** — owns `rotas`, `shifts`, `event_settings` (and the shift-tag tables once §8 ownership is confirmed).
  - Aggregate-local navs kept: `Rota.Shifts`, `Rota.EventSettings`, `Rota.Tags`, `Shift.Rota`, `Shift.ShiftSignups` (read-side), `EventSettings.Rotas`
  - Cross-domain navs stripped: `Rota.Team` (Teams); any `.Select(r => new { r.Team.Id, r.Team.Name })` projections
- **`IShiftSignupRepository`** — owns `shift_signups` — **LANDED 2026-04-22 (#541b), nav-strip COMPLETED 2026-04-25 (#541 final pass)**
  - Aggregate-local navs kept: `ShiftSignup.Shift`, `Shift.Rota`, `Rota.EventSettings` (read-only projection chain), `Shift.ShiftSignups` (capacity counts)
  - Cross-domain navs **stripped**: `.Include(d => d.User)` and `.Include(s => s.ReviewedByUser)` removed from `GetByShiftAsync` and `GetNoShowHistoryAsync`. The ShiftAdmin view reads display fields from a `Dictionary<Guid, User>` populated by the controller via `IUserService.GetByIdsAsync`; `ProfileController` NoShow history resolves `ReviewedByUser` and team-name lookups via service interfaces. `Rota.Team` `.Include` chain stripped from every repo method; team names resolve via `ITeamService.GetTeamNamesByIdsAsync`.
  - **Within-section cross-service reads also live here temporarily:** `rotas` / `shifts` (owned by `ShiftManagementService`, pending #541a), `volunteer_event_profiles` / `general_availabilities` / `volunteer_tag_preferences` (GDPR contributor reads, pending #541a and #541c surface expansion). These move out when those migrations land.
- **`IVolunteerEventProfileRepository`** — owns `volunteer_event_profiles`.
  - Aggregate-local navs kept: none beyond the row itself.
  - Cross-domain navs stripped: `VolunteerEventProfile.User`.
- **`IGeneralAvailabilityRepository`** — owns `general_availabilities` — **LANDED 2026-04-22** (sub-task nobodies-collective/Humans#541c)
  - Aggregate-local navs kept: `GeneralAvailability.EventSettings` (read-side, for cross-repo join on shared aggregate root)
  - Cross-domain navs stripped: `GeneralAvailability.User` (removed from entity; FK kept via `HasOne<User>().WithMany().HasForeignKey(...)` — schema unchanged)

**Note on service/table mapping:** §8 groups all tables under three owning services but does not split 1:1. Actual observed distribution:

- `ShiftManagementService` writes `rotas`, `shifts`, `event_settings`; also reads/writes `shift_tags` and `volunteer_tag_preferences` (neither listed in §8 — flagged above).
- `ShiftSignupService` writes `shift_signups` (migrated #541b — now goes through `IShiftSignupRepository`); reads `rotas`, `shifts`, `event_settings` (within-section, cross-service — temporarily inside `IShiftSignupRepository`; move to `IShiftManagementRepository` when #541a lands).
- `GeneralAvailabilityService` writes `general_availabilities` (migrated #541c).
- `volunteer_event_profiles` is not currently touched by any of the three services on disk. Ownership needs explicit resolution before the split lands; pulled into its own repo above as the neutral default.

### Current violations

- **Cross-domain `.Include()` calls:** all stripped 2026-04-25 (#541 final pass).
  - ~~`ShiftManagementService.cs:216, 280, 488, 538, 546` — `.Include(r => r.Team)` / `.ThenInclude(r => r.Team)` (Teams)~~ — resolved.
  - ~~`ShiftSignupService.cs:57, 687, 802, 849, 862, 888, 918`~~ — service migrated in #541b.
  - ~~`ShiftSignupRepository.GetByShiftAsync` — `.Include(d => d.User)`~~ — stripped 2026-04-25.
  - ~~`ShiftSignupRepository.GetNoShowHistoryAsync` — `.Include(s => s.ReviewedByUser)`~~ — stripped 2026-04-25.
  - ~~`GeneralAvailabilityService.cs:60` — `.Include(g => g.User)`~~ — resolved 2026-04-22 in #541c (nav stripped from entity; FK preserved).
- **Cross-section direct DbContext reads:** None found. Role checks go through `_roleAssignmentService` (Auth), team lookups through `ITeamService` (Teams). The `.Select(r => new { r.Team.Id, r.Team.Name })` projection at `ShiftManagementService.cs:864` is the only direct Teams-table touch and is covered under cross-domain `.Include`/nav-walk above.
- **Within-section cross-service direct DbContext reads:**
  - `ShiftSignupService` reads `_dbContext.Rotas` (`:326, 510`), `_dbContext.Shifts` (`:55, 257`), and `_dbContext.EventSettings` (via `.Include` chains) — all owned by `ShiftManagementService` per §8. Acceptable once both are behind repos and the dependency is expressed as `IShiftManagementRepository` → `IShiftSignupRepository`.
- **Inline `IMemoryCache` usage in service methods:**
  - `ShiftManagementService.cs:97` — `_cache.GetOrCreateAsync(CacheKeys.ShiftAuthorization(userId), ...)` wrapping `TeamService.GetUserCoordinatedTeamIdsAsync(userId)`. Per §4/§5 this cache belongs in the Teams caching decorator, not here. Drop the `shift-auth` cache and let Teams own the result.
  - No `_cache.` references in `ShiftSignupService` or `GeneralAvailabilityService`.
- **Cross-domain nav properties on this section's entities:** all deleted 2026-04-25 (#541 final pass). FKs stay wired in EF via the typed-FK form (`HasOne<Team>().WithMany().HasForeignKey(...)`).
  - ~~`Rota.Team`~~ — deleted 2026-04-25.
  - `Shift` has no cross-domain nav (clean).
  - ~~`ShiftSignup.User`, `ShiftSignup.EnrolledByUser`, `ShiftSignup.ReviewedByUser`~~ — deleted 2026-04-25.
  - ~~`VolunteerEventProfile.User`~~ — deleted 2026-04-25.
  - ~~`GeneralAvailability.User`~~ — stripped 2026-04-22 in #541c; `GeneralAvailability.EventSettings` is section-local and still present.
  - ~~`VolunteerTagPreference.User`~~ — deleted 2026-04-25.
- **§8 gaps (tables touched by this section but not listed under Shifts):**
  - `shift_tags` — read/written at `ShiftManagementService.cs:878, 886, 896, 907, 924`. Likely Shifts.
  - `volunteer_tag_preferences` — read/written at `:939, 949, 953, 957`. Likely Shifts. Flag: §8 needs an explicit decision; assumed Shifts-owned for the repository split.

### Touch-and-clean guidance

- Do not add new `.Include(r => r.Team)` / `.ThenInclude(r => r.Team)` calls — the existing occurrences in `ShiftManagementService.cs` (pending #541a) and those pulled into `IShiftSignupRepository` (pending the Teams-nav strip) are the full set to remove; any new view/DTO needing a team name should pull it from `ITeamService` by id.
- Do not add new `.Include(... => ... .User)` chains on `ShiftSignup` or `GeneralAvailability` — project to `UserId` and resolve display data via `IProfileService` / `IUserService`. The two existing User includes preserved in `IShiftSignupRepository` (`GetByShiftAsync`, `GetNoShowHistoryAsync`) are the only exceptions; do not add more. (`GeneralAvailability.User` no longer exists — access `UserId` directly.)
- Do not add new `_cache.` calls in `ShiftManagementService`; route authorization caching through `IRoleAssignmentService` / `ITeamService`. The existing `ShiftAuthorization` cache at `:97` should be deleted in the same PR that moves Teams to a cached store.
- If you add a new table to this section, add it to §8 of `design-rules.md` **in the same commit** — do not repeat the `shift_tags` / `volunteer_tag_preferences` omission.

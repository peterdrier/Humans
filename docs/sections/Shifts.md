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

- A **Rota** is a named container for shifts, belonging to a department or sub-team and an event. Each rota has a period (Build, Event, Strike, or All) that determines whether its shifts are all-day or time-slotted and the allowed day-offset range for new shifts.
- A **Shift** is a single work slot with a day offset, optional start time, duration, and maximum volunteer count.
- A **Shift Signup** links a human to a shift. Signups progress through states: Pending, Confirmed, Refused, Bailed, Cancelled, or NoShow.
- **Range Signups** link multiple shifts via a block ID (`SignupBlockId`). Operations on a range (sign-up, voluntell, bail, approve, refuse) apply to the entire block atomically.
- **Event Settings** is a singleton per event controlling dates, timezone, early-entry capacity, barrios EE allocation, early-entry close instant, global volunteer cap, reminder lead time, and whether shift browsing is open to regular volunteers.
- **General Availability** tracks per-human per-event day availability (one row per user per event; `AvailableDayOffsets` is a jsonb list of day offsets).
- **Volunteer Event Profile** stores per-user volunteer profile data: skills, quirks (working-style toggles like Sober Shift, Work In Shade, plus a single time preference), languages, dietary preference, allergies, intolerances, and medical conditions. One-to-one with `User`.
- **Rota Tags** (`shift_tags`) are labels applied to rotas (e.g., "Heavy lifting"). Volunteers save preferred tags via `VolunteerTagPreference`; matching rotas are starred on the browse page.
- **Voluntelling** is when an Admin, NoInfoAdmin, VolunteerCoordinator, or department coordinator signs up a human for a shift on their behalf. Voluntold signups are auto-confirmed and recorded with `Enrolled = true` and `EnrolledByUserId`.
- **Event Participation** (`event_participations`) is a per-user, per-year record tracking declared event participation status. Used cross-section (e.g., to gate "who hasn't bought a ticket" lists). Owned by Shifts but consumed by Tickets and other sections.

## Data Model

### EventSettings

Singleton per event — dates (gate-opening date, build/event/strike offsets), timezone, early-entry capacity (step function), barrios EE allocation, early-entry close instant, global volunteer cap, reminder lead time hours, shift browsing toggle, IsActive flag, and event name/year.

**Table:** `event_settings`

Aggregate-local navs: `EventSettings.Rotas`.

### Rota

Shift container, belongs to department + event. Has `Period` (Build/Event/Strike/All), `Priority` (Normal/Important/Essential — feeds urgency scoring with weights 1×/3×/6×), `Policy` (Public/RequireApproval), optional `Description`, optional `PracticalInfo` (max 2000 chars, markdown), and `IsVisibleToVolunteers` (default true).

**Table:** `rotas`

Aggregate-local navs: `Rota.Shifts`, `Rota.EventSettings`, `Rota.Tags`. Cross-domain nav to `Team` is stripped from the entity; FK preserved via typed-FK form. Team display data resolves through `ITeamService`.

### Shift

Single work slot — `DayOffset + StartTime + Duration + IsAllDay`. Also: `MinVolunteers` (understaffed threshold for urgency scoring), `MaxVolunteers` (hard capacity ceiling), `AdminOnly` (hides shift from regular volunteers), `Description`. There is no `IsCancelled` column — shift cancellation flows through cascade on rota deletion or `ShiftSignup.Cancel`.

**Table:** `shifts`

Aggregate-local navs: `Shift.Rota`, `Shift.ShiftSignups`.

### ShiftSignup

Links User to Shift with state machine (Pending/Confirmed/Refused/Bailed/Cancelled/NoShow), optional `SignupBlockId` for range signups, `Enrolled` flag (true for voluntell), `EnrolledByUserId`, `ReviewedByUserId`, `ReviewedAt`, and `StatusReason`. Allowed transitions enforced by entity methods (`Confirm`, `Refuse`, `Bail`, `MarkNoShow`, `Cancel`, `Remove`):

| From → To  | Confirmed | Refused | Bailed | NoShow | Cancelled |
|---|:-:|:-:|:-:|:-:|:-:|
| Pending    | Confirm | Refuse | Bail | — | Cancel (system) |
| Confirmed  | — | — | Bail | MarkNoShow | Remove (coordinator) / Cancel (system) |

Other transitions throw `InvalidOperationException`. `Cancel` is system-only (rota/shift deletion, account deletion) and skips reviewer attribution.

**Table:** `shift_signups`

Aggregate-local navs: `ShiftSignup.Shift`. Cross-domain navs to `User` (volunteer / enroller / reviewer) are **stripped from the entity** — FKs preserved via typed-FK form (`HasOne<User>().WithMany().HasForeignKey(...)`). Display data resolves through `IUserService.GetByIdsAsync`.

### GeneralAvailability

Per-user per-event day availability. `AvailableDayOffsets` stored as jsonb. Unique on `(UserId, EventSettingsId)`.

**Table:** `general_availability`

Cross-domain nav `GeneralAvailability.User` was **stripped** in peterdrier/Humans PR for sub-task nobodies-collective/Humans#541c (FK kept via `HasOne<User>().WithMany().HasForeignKey(...)`).

### VolunteerEventProfile

Per-user volunteer profile (1:1 with User) capturing `Skills`, `Quirks`, `Languages`, `DietaryPreference`, `Allergies`, `Intolerances`, `AllergyOtherText`, `IntoleranceOtherText`, and `MedicalConditions`. List columns are jsonb. Unique on `UserId`.

**Table:** `volunteer_event_profiles`

Cross-domain nav to `User` is stripped from the entity; FK preserved via typed-FK form with `OnDelete(Cascade)`.

### EventParticipation

Per-user, per-year record tracking declared event participation status (`ParticipationStatus`, `ParticipationSource`, `DeclaredAt`). Absence of a row = unknown/no response (default state, not stored). Used by other sections (e.g., Tickets) to gate "who hasn't bought a ticket" exclusions for self-declared non-attendees.

**Table:** `event_participations`

Cross-domain nav `EventParticipation.User` is currently kept on the entity.

### Shift tag tables (§8 gap)

- `shift_tags` — read/written by `ShiftManagementService`. Many-to-many with rotas via the `rota_shift_tags` join table. Not yet listed in design-rules §8 under any section. Likely Shifts-owned.
- `volunteer_tag_preferences` — read/written by `ShiftManagementService`. Also not listed. Likely Shifts-owned.

§8 needs an explicit ownership decision for both before full migration; assumed Shifts-owned for the repository split.

### RotaPeriod

Explicit period set on a Rota. Drives creation UX (all-day vs time-slotted) and signup UX (date-range vs individual). Distinct from computed `ShiftPeriod`.

| Value | Int | Description |
|-------|-----|-------------|
| Build | 0 | Build period — all-day shifts, date-range signup |
| Event | 1 | Event period — time-slotted shifts, individual signup |
| Strike | 2 | Strike period — all-day shifts, date-range signup |
| All | 3 | Spans all periods — used by rotas whose shifts straddle build/event/strike boundaries |

Stored as string via `HasConversion<string>()`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any active human | Browse available shifts (when browsing is open or they have existing signups). Sign up for shifts (single or date-range for build/strike rotas). View own signups and schedule. Bail from own signups (single or whole range). Set general availability. Fill out volunteer event profile. Save preferred rota tags |
| Department coordinator | Manage rotas and shifts for their department and all sub-teams. Approve, refuse, and bail signups. Voluntell humans (single or range). Mark no-show. Remove confirmed signups. Manage rota tags. View volunteer event profiles (except medical data) |
| Sub-team manager | Manage rotas and shifts for their sub-team only. Approve, refuse, and bail signups on their sub-team. Voluntell humans on their sub-team. Cannot manage sibling sub-teams or the parent department |
| VolunteerCoordinator | All coordinator capabilities across all departments (rotas, shifts, signups, voluntell, no-show, remove). Move rotas between departments. Access the cross-department shift dashboard. Cannot view medical data |
| NoInfoAdmin | Approve, refuse, and bail signups across all departments. Voluntell humans. Mark no-show. Remove confirmed signups. View volunteer medical data. Access the cross-department shift dashboard. **Cannot create or edit rotas or shifts** (management is gated to Admin/VolunteerCoordinator + dept coordinators) |
| Admin | All NoInfoAdmin capabilities plus full rota/shift management system-wide. Manage event settings (dates, timezone, early-entry capacity, barrios EE allocation, early-entry close, global volunteer cap, reminder lead time, shift browsing toggle). View medical data |

## Invariants

- Shift signup state machine (enforced by entity methods on `ShiftSignup`):
  - Pending → Confirm / Refuse / Bail / Cancel
  - Confirmed → Bail / MarkNoShow / Remove (Cancelled) / Cancel
  - All other transitions throw `InvalidOperationException`. NoShow is post-shift only (`now >= shift.GetAbsoluteEnd(es)`). Cancel is system-only (rota/shift deletion, account deletion).
- MaxVolunteers is a hard capacity ceiling. SignUp, Approve, Voluntell, and ApproveRange are blocked when the confirmed count reaches MaxVolunteers. Range signups skip full shifts; ApproveRange auto-refuses pending signups for shifts that have filled since the request was placed.
- Rota visibility is controlled by `IsVisibleToVolunteers` (default: visible). Hidden rotas are only shown to privileged roles (Admin/NoInfoAdmin/VolunteerCoordinator/dept coordinator). Browse and Mine queries pass `includeHidden = isPrivileged`.
- Voluntelling (admin/coordinator-initiated signup) creates a Confirmed `ShiftSignup` with `Enrolled = true` and records `EnrolledByUserId` / `ReviewedByUserId`. Range voluntell uses a shared `SignupBlockId` and skips shifts that are full or already booked.
- Range signups (build/strike rotas) create signups for every all-day shift in the date range under one `SignupBlockId`; conflicts and capacity are reported as warnings, not failures (provided at least one slot is available). The whole block is bailed/approved/refused atomically by `BailRangeAsync` / `ApproveRangeAsync` / `RefuseRangeAsync`.
- Event settings is a singleton per event — `CreateAsync` / `UpdateAsync` reject a second IsActive=true row.
- Rota period (Build, Event, Strike, All) determines the shift creation UX (all-day vs time-slotted) and signup UX (date-range vs individual). Day offsets entered in the create/edit shift form must fall within the rota's period range.
- Medical data on volunteer event profiles is restricted to Admin and NoInfoAdmin (`ShiftRoleChecks.CanViewMedical`). `IShiftManagementService.GetShiftProfileAsync(uid, includeMedical)` strips the field when `includeMedical = false`.
- When shift browsing is closed (`IsShiftBrowsingOpen = false`), regular volunteers can only see shifts if they already have signups (`hasSignups = true`). Coordinators and privileged roles can always browse. Sign-up and range sign-up are also gated by this flag.
- Early-entry freeze: after `EventSettings.EarlyEntryClose`, non-privileged humans cannot sign up for, range-sign-up to, bail from, or have approval issued on Build-period shifts. Admin/NoInfoAdmin/VolunteerCoordinator/dept coordinators bypass the freeze.
- Voluntelling and signup overlap detection rejects a target shift whose absolute time range intersects any of the user's existing Confirmed signups. The check uses event-timezone-resolved absolute instants.
- All dashboard endpoints on `ShiftDashboardController` (and its analytics methods on `IShiftManagementService`: `GetDashboardOverviewAsync`, `GetCoordinatorActivityAsync`, `GetDashboardTrendsAsync`, `GetCoverageHeatmapAsync`, `GetDailyDepartmentStaffingAsync`, `GetShiftDurationBreakdownAsync`) require the `ShiftDashboardAccess` policy at the controller (Admin/NoInfoAdmin/VolunteerCoordinator). The services themselves are auth-free per design rules.
- `DevelopmentDashboardSeeder` and its `POST /dev/seed/dashboard` endpoint are gated to `IWebHostEnvironment.IsDevelopment()` AND the `DevAuth:Enabled` setting. QA, preview, and production environments cannot invoke it regardless of role. The endpoint also requires `ShiftDashboardAccess`.

## Negative Access Rules

- Regular humans **cannot** manage rotas or shifts. They can only browse and sign up.
- Regular humans **cannot** approve, refuse, or bail other humans' signups.
- Regular humans **cannot** voluntell other humans.
- Department coordinators **cannot** manage rotas or approve signups outside their own department.
- Sub-team managers **cannot** manage rotas or approve signups outside their own sub-team (not siblings, not parent department).
- Department coordinators **cannot** view volunteer medical data.
- NoInfoAdmin **cannot** create or edit rotas or shifts (management gates to Admin/VolunteerCoordinator + dept coordinators). They can manage signups (approve, refuse, bail, voluntell, mark no-show, remove) and view medical data.
- VolunteerCoordinator **cannot** view volunteer medical data.

## Triggers

- Every signup state change (auto-confirm, approve, refuse, bail, voluntell, remove) writes an audit log entry (`AuditAction.ShiftSignupConfirmed/Refused/Bailed/Voluntold/Cancelled/NoShow`) and dispatches a `ShiftSignupChange` notification to the department's coordinators via `INotificationService`.
- Voluntelling additionally fires a `ShiftAssigned` informational notification to the assigned volunteer (best-effort; failures logged but do not roll back the signup).
- When a Bail or Remove drops the confirmed count below `MinVolunteers`, a `ShiftCoverageGap` actionable notification (priority High) is sent to the department's coordinators.
- Range signup, range voluntell, range bail, range approve, and range refuse all use a shared `SignupBlockId` and operate on the entire block atomically (with per-shift filtering for capacity/conflicts on creation paths).
- Moving a rota to a different team writes an `AuditAction.RotaMovedToTeam` log entry and updates `Rota.TeamId` via a targeted update (only `TeamId` + `UpdatedAt` are marked modified).
- Deleting a rota or shift is rejected if any signup is in Confirmed state. Pending signups on a deleted rota/shift are auto-Cancelled via the entity's `Cancel` method.

## Cross-Section Dependencies

- **Teams:** `ITeamService` — rotas belong to a department or sub-team. Used for `GetByIdsWithParentsAsync`, `GetTeamNamesByIdsAsync`, `GetCoordinatorUserIdsAsync`, `GetUserCoordinatedTeamIdsAsync`. Coordinator status determines shift management access.
- **Users:** `IUserService` — `GetByIdsAsync` resolves display data (name, profile picture) for signup rows now that `ShiftSignup.User` nav is stripped. Also used by the dashboard activity computation and the volunteer search builder.
- **Auth:** `IRoleAssignmentService` (lazy-resolved) — role checks for `Admin`, `NoInfoAdmin`, `VolunteerCoordinator` from `HasActiveRoleAsync`.
- **Tickets:** `ITicketQueryService` — used by the coordinator dashboard to compute ticket-buyer cross-references; `EventParticipation` is consumed by Tickets to gate "who hasn't bought" lists.
- **Audit Log:** `IAuditLogService` — every signup state change and rota move emits an audit entry.
- **Notifications:** `INotificationService` — coordinator notifications for signup changes, voluntell assignments, and coverage gaps. No direct email-outbox dependency from this section.
- **GDPR:** `ShiftSignupService` implements `IUserDataContributor` (export of signups, volunteer event profile, general availability, tag preferences) and `CancelActiveSignupsForUserAsync` (deletion).

## Architecture

**Owning services:** `ShiftManagementService`, `ShiftSignupService`, `GeneralAvailabilityService`
**Owned tables:** `rotas`, `shifts`, `shift_signups`, `event_settings`, `general_availability`, `volunteer_event_profiles`, `event_participations` (plus `shift_tags`, `volunteer_tag_preferences`, and the `rota_shift_tags` join table — pending §8 confirmation)
**Status:** (A) Fully migrated. `ShiftManagementService`, `ShiftSignupService`, and `GeneralAvailabilityService` all live in `Humans.Application.Services.Shifts` and route through `IShiftManagementRepository` / `IShiftSignupRepository` / `IGeneralAvailabilityRepository`. Cross-domain navs on Shifts-owned entities (`Rota.Team`, `ShiftSignup.User` / `EnrolledByUser` / `ReviewedByUser`, `VolunteerEventProfile.User`, `VolunteerTagPreference.User`) deleted 2026-04-25 in nobodies-collective/Humans#541 final pass; FKs stay wired in EF via the typed-FK form.

### Target repositories

- **`IShiftManagementRepository`** — owns `rotas`, `shifts`, `event_settings` (and the shift-tag tables once §8 ownership is confirmed).
  - Aggregate-local navs kept: `Rota.Shifts`, `Rota.EventSettings`, `Rota.Tags`, `Shift.Rota`, `Shift.ShiftSignups` (read-side), `EventSettings.Rotas`
  - Cross-domain navs stripped: `Rota.Team` (Teams); any `.Select(r => new { r.Team.Id, r.Team.Name })` projections
- **`IShiftSignupRepository`** — owns `shift_signups` — **LANDED 2026-04-22 (#541b), nav-strip COMPLETED 2026-04-25 (#541 final pass)**
  - Aggregate-local navs kept: `ShiftSignup.Shift`, `Shift.Rota`, `Rota.EventSettings` (read-only projection chain), `Shift.ShiftSignups` (capacity counts)
  - Cross-domain navs **stripped**: `.Include(d => d.User)` and `.Include(s => s.ReviewedByUser)` removed from `GetByShiftAsync` and `GetNoShowHistoryAsync`. The ShiftAdmin view reads display fields from a `Dictionary<Guid, User>` populated by the controller via `IUserService.GetByIdsAsync`; `ProfileController` NoShow history resolves `ReviewedByUser` and team-name lookups via service interfaces. `Rota.Team` `.Include` chain stripped from every repo method; team names resolve via `ITeamService.GetTeamNamesByIdsAsync`.
  - **Within-section cross-service reads also live here temporarily:** `rotas` / `shifts` (owned by `ShiftManagementService`, pending #541a), `volunteer_event_profiles` / `general_availability` / `volunteer_tag_preferences` (GDPR contributor reads, pending #541a and #541c surface expansion). These move out when those migrations land.
- **`IVolunteerEventProfileRepository`** — owns `volunteer_event_profiles`.
  - Aggregate-local navs kept: none beyond the row itself.
  - Cross-domain navs stripped: `VolunteerEventProfile.User`.
- **`IGeneralAvailabilityRepository`** — owns `general_availability` — **LANDED 2026-04-22** (sub-task nobodies-collective/Humans#541c)
  - Aggregate-local navs kept: `GeneralAvailability.EventSettings` (read-side, for cross-repo join on shared aggregate root)
  - Cross-domain navs stripped: `GeneralAvailability.User` (removed from entity; FK kept via `HasOne<User>().WithMany().HasForeignKey(...)` — schema unchanged)

**Note on service/table mapping:** §8 groups all tables under three owning services but does not split 1:1. Actual observed distribution:

- `ShiftManagementService` writes `rotas`, `shifts`, `event_settings`; also reads/writes `shift_tags` and `volunteer_tag_preferences` (neither listed in §8 — flagged above).
- `ShiftSignupService` writes `shift_signups` (migrated #541b — now goes through `IShiftSignupRepository`); reads `rotas`, `shifts`, `event_settings` (within-section, cross-service — temporarily inside `IShiftSignupRepository`; move to `IShiftManagementRepository` when #541a lands).
- `GeneralAvailabilityService` writes `general_availability` (migrated #541c).
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

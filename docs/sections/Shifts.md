<!-- freshness:triggers
  src/Humans.Application/Services/Shifts/**
  src/Humans.Domain/Entities/Rota.cs
  src/Humans.Domain/Entities/Shift.cs
  src/Humans.Domain/Entities/ShiftSignup.cs
  src/Humans.Domain/Entities/ShiftTag.cs
  src/Humans.Domain/Entities/EventSettings.cs
  src/Humans.Domain/Entities/GeneralAvailability.cs
  src/Humans.Domain/Entities/VolunteerEventProfile.cs
  src/Humans.Domain/Entities/VolunteerTagPreference.cs
  src/Humans.Infrastructure/Data/Configurations/Shifts/**
  src/Humans.Web/Controllers/ShiftsController.cs
  src/Humans.Web/Controllers/ShiftAdminController.cs
  src/Humans.Web/Controllers/ShiftDashboardController.cs
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
- **Event Participation** is a per-user, per-year record tracking declared event participation status, used cross-section (e.g., to gate "who hasn't bought a ticket" lists). Owned by Users (see [`Users.md`](Users.md)); Shifts may surface it as a derived view but does not write to it.

## Data Model

### EventSettings

Singleton per event — dates (gate-opening date, build/event/strike offsets, build sub-period offsets), timezone, early-entry capacity (step function), barrios EE allocation, early-entry close instant, global volunteer cap, reminder lead time hours, shift browsing toggle, IsActive flag, and event name/year.

The build period is split into four named sub-phases via four day-offset fields on EventSettings: `FirstCrewStartOffset` (default -25), `SetupWeekStartOffset` (-16), `PreEventWeekStartOffset` (-9), `FinishingWeekendStartOffset` (-4). Offsets are inclusive starts; the next sub-period's start is the exclusive end. All four must be negative and ascending: `BuildStartOffset ≤ FirstCrew ≤ Set-up week ≤ Pre-event week ≤ Finishing weekend < 0`. Coordinators reconfigure per event so the absolute calendar dates auto-shift with `GateOpeningDate`.

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

### VolunteerBuildStatus

Per-user per-event build-period coordination state. Drives the Volunteer Tracking sub-page (gap detection, "went to camp set-up" marker, blocked-day list). Tracks two orthogonal facts that the schedule itself cannot infer:

- `BarrioSetupStartDate` (nullable `LocalDate`) — the day a volunteer left scheduled rotas to join camp set-up. From this day onwards their row renders blue and gap detection stops flagging missing days.
- `BarrioSetupSetByUserId` (nullable `Guid`) and `BarrioSetupSetAt` (nullable `Instant`) — audit fields recording who set the camp-set-up marker and when. Cleared when the marker is cleared.
- `BlockedDayOffsets` (jsonb `IList<int>`) — day offsets (relative to `EventSettings.GateOpeningDate`, all negative for build days) the volunteer or coordinator marked as unavailable. Blocked days render yellow on the heatmap and are excluded from gap counts.

**Table:** `volunteer_build_statuses`

**Indices:** PK on `Id`; unique on `(UserId, EventSettingsId)` (one row per volunteer per event).

**FK rules:** Bare `Guid UserId` (no nav property — cross-section, per `memory/architecture/no-cross-section-ef-joins.md`). `EventSettingsId` is a same-section FK with cascade delete; deleting an event removes its build statuses.

**Write paths:**

- `VolunteerTrackingController` (Admin / VolunteerCoordinator, gated by the `VolunteerTrackingWrite` policy) writes `BarrioSetupStartDate`, `BarrioSetupSetByUserId`, `BarrioSetupSetAt`, and individual `BlockedDayOffsets` entries via `IVolunteerTrackingService.SetCampSetupAsync` / `ClearCampSetupAsync` / `SetDayOffAsync` / `ClearDayOffAsync`.

All mutations route through `IVolunteerTrackingRepository` and emit `AuditAction.VolunteerCampSetupSet` / `VolunteerCampSetupCleared` / `VolunteerDayOffMarked` / `VolunteerDayOffCleared` audit entries with `EntityType = nameof(VolunteerBuildStatus)`.

### VolunteerEventProfile

Per-user volunteer profile (1:1 with User) capturing `Skills`, `Quirks`, `Languages`, `DietaryPreference`, `Allergies`, `Intolerances`, `AllergyOtherText`, `IntoleranceOtherText`, and `MedicalConditions`. List columns are jsonb. Unique on `UserId`.

**Table:** `volunteer_event_profiles`

Cross-domain nav to `User` is stripped from the entity; FK preserved via typed-FK form with `OnDelete(Cascade)`.

### EventParticipation (owned by Users)

The `event_participations` entity is owned by Users — the natural key is User + Year. See [`Users.md`](Users.md) for field-level detail. Shifts consumes it as a read-only cross-section reference and does not write to the table directly.

### Shift tag tables

- `shift_tags` — read/written by `ShiftManagementService` via `IShiftManagementRepository`. Many-to-many with rotas via the `rota_shift_tags` join table. Seeded with 8 initial values in `ShiftTagConfiguration`. Name column is unique (`IX_shift_tags_name_unique`).
- `volunteer_tag_preferences` — read/written by `ShiftManagementService` via `IShiftManagementRepository`. Unique on `(UserId, ShiftTagId)`. Cross-domain FK to `User` via typed-FK form; section-local FK to `ShiftTag`.

Both tables are listed under Shifts in `design-rules.md §8`.

### RotaPeriod

Explicit period set on a Rota. Drives creation UX (all-day vs time-slotted) and signup UX (date-range vs individual). Distinct from computed `ShiftPeriod`.

| Value | Int | Description |
|-------|-----|-------------|
| Build | 0 | Build period — all-day shifts, date-range signup |
| Event | 1 | Event period — time-slotted shifts, individual signup |
| Strike | 2 | Strike period — all-day shifts, date-range signup |
| All | 3 | Spans all periods — used by rotas whose shifts straddle build/event/strike boundaries |

Stored as string via `HasConversion<string>()`.

### BuildSubPeriod

Computed sub-classification of a Build-period shift, narrowed by the four day-offset boundaries on `EventSettings`. **NOT stored in DB** — derived per shift on read via `BuildSubPeriodClassifier.Classify(dayOffset, eventSettings)` (lives in `Humans.Domain.Helpers`, pure mapping, no framework deps). Returns `null` for offsets outside the build window (≥ 0).

| Value | Int | Range |
|-------|-----|-------|
| FirstCrew | 0 | `FirstCrewStartOffset ≤ DayOffset < SetupWeekStartOffset` |
| SetupWeek | 1 | `SetupWeekStartOffset ≤ DayOffset < PreEventWeekStartOffset` |
| PreEventWeek | 2 | `PreEventWeekStartOffset ≤ DayOffset < FinishingWeekendStartOffset` |
| FinishingWeekend | 3 | `FinishingWeekendStartOffset ≤ DayOffset < 0` |

Used by the shift dashboard's set-up sub-filter to narrow per-day staffing data, urgency lists, coverage heatmap, etc. when the user drills from the Build period into a specific phase.

## Routing

Four controllers serve this section, each with distinct URL scope and authorization:

| Controller | Base route | Auth |
|---|---|---|
| `ShiftsController` | `/Shifts` | `[Authorize]` (per-action for admin/settings) |
| `ShiftAdminController` | `/Teams/{slug}/Shifts` | `[Authorize]` + `CanManageDepartment` / `CanApproveDepartment` |
| `ShiftDashboardController` | `/Shifts/Dashboard` | `[Authorize(Policy = PolicyNames.ShiftDepartmentManager)]` |
| `VolunteerTrackingController` | `/Shifts/Dashboard/VolunteerTracking` | `[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]` (per-action `VolunteerTrackingWrite` for mutating POSTs — Admin / VolunteerCoordinator) |

Selected routes:

| Route | Purpose |
|---|---|
| `GET /Shifts` | Browse shifts (department/date/period/tag filters) |
| `GET /Shifts/Mine` | Volunteer's own signups |
| `POST /Shifts/SignUp` | Single shift signup |
| `POST /Shifts/SignUpRange` | Date-range signup (build/strike) |
| `POST /Shifts/Bail` | Single bail |
| `POST /Shifts/BailRange` | Range bail (by SignupBlockId) |
| `POST /Shifts/Mine/Availability` | Save general availability |
| `POST /Shifts/Mine/RegenerateIcal` | Regenerate iCal subscription |
| `POST /Shifts/Preferences/Tags` | Save volunteer tag preferences |
| `GET /Shifts/Settings` | Admin: view event settings |
| `POST /Shifts/Settings` | Admin: update event settings |
| `GET /Shifts/OrphanSignups` | Admin: signups without audit log entries (AdminOnly) |
| `GET /Teams/{slug}/Shifts` | Coordinator: rota/shift admin |
| `POST /Teams/{slug}/Shifts/Rotas` | Create rota |
| `POST /Teams/{slug}/Shifts/Rotas/{rotaId}` | Edit rota |
| `POST /Teams/{slug}/Shifts/Rotas/{rotaId}/ConfigureStaffing` | Bulk-configure all-day shift staffing |
| `POST /Teams/{slug}/Shifts/Rotas/{rotaId}/GenerateShifts` | Bulk-generate event shifts |
| `POST /Teams/{slug}/Shifts/Rotas/{rotaId}/ToggleVisibility` | Toggle `IsVisibleToVolunteers` |
| `POST /Teams/{slug}/Shifts/Rotas/{rotaId}/Move` | Move rota to different team |
| `POST /Teams/{slug}/Shifts/Rotas/{rotaId}/Delete` | Delete rota |
| `POST /Teams/{slug}/Shifts/Shifts` | Create shift |
| `POST /Teams/{slug}/Shifts/Shifts/{shiftId}` | Edit shift |
| `POST /Teams/{slug}/Shifts/Shifts/{shiftId}/Delete` | Delete shift |
| `POST /Teams/{slug}/Shifts/BailRange` | Admin bail range |
| `POST /Teams/{slug}/Shifts/ApproveRange` | Approve range |
| `POST /Teams/{slug}/Shifts/RefuseRange` | Refuse range |
| `POST /Teams/{slug}/Shifts/Signups/{signupId}/Approve` | Approve signup |
| `POST /Teams/{slug}/Shifts/Signups/{signupId}/Refuse` | Refuse signup |
| `POST /Teams/{slug}/Shifts/Signups/{signupId}/NoShow` | Mark no-show |
| `POST /Teams/{slug}/Shifts/Signups/{signupId}/Remove` | Remove (coordinator unassign) |
| `GET /Teams/{slug}/Shifts/SearchVolunteers` | Volunteer search for voluntell |
| `POST /Teams/{slug}/Shifts/Voluntell` | Voluntell single shift |
| `POST /Teams/{slug}/Shifts/VoluntellRange` | Voluntell range |
| `GET /Teams/{slug}/Shifts/Tags/Search` | Tag autocomplete |
| `POST /Teams/{slug}/Shifts/Tags/Create` | Create new tag |
| `GET /Shifts/Dashboard` | Cross-department coordinator dashboard |
| `GET /Shifts/Dashboard/SearchVolunteers` | Dashboard volunteer search |
| `POST /Shifts/Dashboard/Voluntell` | Dashboard voluntell |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any active human | Browse available shifts (when browsing is open or they have existing signups). Sign up for shifts (single or date-range for build/strike rotas). View own signups and schedule. Bail from own signups (single or whole range). Set general availability. Fill out volunteer event profile. Save preferred rota tags. **Currently** can also see who has signed up for any shift on `/Shifts` (temporary public-signup-list policy — see [feature 26](../features/shifts/shift-signup-visibility.md)) |
| Department coordinator | Manage rotas and shifts for their department and all sub-teams. Approve, refuse, and bail signups. Voluntell humans (single or range) on their own department's shifts. Mark no-show. Remove confirmed signups. Manage rota tags. View volunteer event profiles (except medical data). View the cross-department shift dashboard, but the coordinator-activity panel and the per-shift voluntell action on it remain gated to VolunteerCoordinator/Admin/NoInfoAdmin |
| Sub-team manager | Manage rotas and shifts for their sub-team only. Approve, refuse, and bail signups on their sub-team. Voluntell humans on their own sub-team's shifts. Cannot manage sibling sub-teams or the parent department. View the cross-department shift dashboard with the same privileged-panel restrictions as a department coordinator |
| VolunteerCoordinator | All coordinator capabilities across all departments (rotas, shifts, signups, voluntell, no-show, remove). Move rotas between departments. Access the cross-department shift dashboard including the coordinator-activity panel and per-shift voluntell action. Cannot view medical data |
| NoInfoAdmin | Approve, refuse, and bail signups across all departments. Voluntell humans. Mark no-show. Remove confirmed signups. View volunteer medical data. Access the cross-department shift dashboard including the privileged sub-panels. **Cannot create or edit rotas or shifts** (management is gated to Admin/VolunteerCoordinator + dept coordinators) |
| Admin | All NoInfoAdmin capabilities plus full rota/shift management system-wide. Manage event settings (dates, timezone, early-entry capacity, barrios EE allocation, early-entry close, global volunteer cap, reminder lead time, shift browsing toggle). View medical data |

## Invariants

- Shift signup state machine (enforced by entity methods on `ShiftSignup`):
  - Pending → Confirm / Refuse / Bail / Cancel
  - Confirmed → Bail / MarkNoShow / Remove (Cancelled) / Cancel
  - All other transitions throw `InvalidOperationException`. NoShow is post-shift only (`now >= shift.GetAbsoluteEnd(es)`). Cancel is system-only (rota/shift deletion, account deletion).
- MaxVolunteers is a hard capacity ceiling. SignUp, Approve, Voluntell, and ApproveRange are blocked when the confirmed count reaches MaxVolunteers. Range signups skip full shifts; ApproveRange auto-refuses pending signups for shifts that have filled since the request was placed.
- Rota visibility is controlled by `IsVisibleToVolunteers` (default: visible). Hidden rotas are only shown to privileged roles (Admin/NoInfoAdmin/VolunteerCoordinator/dept coordinator). Browse and Mine queries pass `includeHidden = isPrivileged`. The Hidden pill rendered on hidden rotas is therefore admin-only by virtue of the server-side filter (no separate role check).
- Signup-list visibility on `/Shifts` is currently public to all authenticated viewers (temporary policy — see [feature 26](../features/shifts/shift-signup-visibility.md)). The browse partials (`_EventRotaTable`, `_BuildStrikeRotaTable`) render avatar chips for everyone; pending signups appear faded with a dashed border and the localized "Pending" label in the hover popover. `includeSignups` is unconditionally true so the column has data; the `isPrivileged` computation is preserved so reverting visibility is a one-line flip in `ShiftsController`. Admin-side signup lists (`/Teams/{slug}/Shifts`) remain coordinator-gated via `CanApproveAsync`.
- Voluntelling (admin/coordinator-initiated signup) creates a Confirmed `ShiftSignup` with `Enrolled = true` and records `EnrolledByUserId` / `ReviewedByUserId`. Range voluntell uses a shared `SignupBlockId` and skips shifts that are full or already booked.
- Range signups (build/strike rotas) create signups for every all-day shift in the date range under one `SignupBlockId`; conflicts and capacity are reported as warnings, not failures (provided at least one slot is available). The whole block is bailed/approved/refused atomically by `BailRangeAsync` / `ApproveRangeAsync` / `RefuseRangeAsync`.
- Event settings is a singleton per event — `CreateAsync` / `UpdateAsync` reject a second IsActive=true row.
- Rota period (Build, Event, Strike, All) determines the shift creation UX (all-day vs time-slotted) and signup UX (date-range vs individual). Day offsets entered in the create/edit shift form must fall within the rota's period range.
- Medical data on volunteer event profiles is restricted to Admin and NoInfoAdmin (`ShiftRoleChecks.CanViewMedical`). `IShiftManagementService.GetShiftProfileAsync(uid, includeMedical)` strips the field when `includeMedical = false`.
- When shift browsing is closed (`IsShiftBrowsingOpen = false`), regular volunteers can only see shifts if they already have signups (`hasSignups = true`). Coordinators and privileged roles can always browse. Sign-up and range sign-up are also gated by this flag.
- Early-entry freeze: after `EventSettings.EarlyEntryClose`, non-privileged humans cannot sign up for, range-sign-up to, bail from, or have approval issued on Build-period shifts. Admin/NoInfoAdmin/VolunteerCoordinator/dept coordinators bypass the freeze.
- Voluntelling and signup overlap detection rejects a target shift whose absolute time range intersects any of the user's existing Confirmed signups. The check uses event-timezone-resolved absolute instants.
- All-day shifts cover the standard work block **08:00–18:00** local time (`Shift.AllDayWindowStart` / `Shift.AllDayWindowEnd`). Patterns outside this window must be modeled as regular time-slotted shifts, not as `IsAllDay = true`. The window is computed at read time by `GetAbsoluteStart` / `GetAbsoluteEnd`; the `StartTime` and `Duration` columns on `IsAllDay` rows are don't-care and must never be used directly for overlap math or staffing calculations.
- All dashboard endpoints on `ShiftDashboardController` (and its analytics methods on `IShiftManagementService`: `GetDashboardOverviewAsync`, `GetCoordinatorActivityAsync`, `GetDashboardTrendsAsync`, `GetCoverageHeatmapAsync`, `GetDailyDepartmentStaffingAsync`, `GetShiftDurationBreakdownAsync`) require the `ShiftDashboardAccess` policy at the controller (Admin/NoInfoAdmin/VolunteerCoordinator). The services themselves are auth-free per design rules.
- The dashboard filter has two mutually exclusive modes selected via the same UI: **period mode** (Set-up / Event / Strike with optional sub-period for Build) and **date-range mode** (start + end inputs). Picking a period auto-populates the date inputs as a visual cue but the server still uses period+sub-period as the filter. Manually editing a date clears the period+sub-period selection so the date range becomes the filter. The server defends the same mutex: when both period and dates arrive on a single request, period wins for filtering (dates round-trip back to the inputs but are not applied as bounds). End-date input enforces `min = startDate` so the user cannot pick an end date before the start.
- All 9 dashboard analytics methods on `IShiftManagementService` accept an optional `BuildSubPeriod? subPeriod = null` parameter. When set, it narrows the filter to that sub-window using `BuildSubPeriodClassifier.BoundsFor`. Sub-period is meaningful only when `period == ShiftPeriod.Build` — calls with sub-period set against any other period are treated as if sub-period is null. Sub-period bypasses the dashboard cache (4× key fan-out is not worth it for a side filter).
- `DevelopmentDashboardSeeder` and its `POST /dev/seed/dashboard` endpoint are gated to `IWebHostEnvironment.IsDevelopment()` AND the `DevAuth:Enabled` setting. QA, preview, and production environments cannot invoke it regardless of role. The endpoint also requires `ShiftDashboardAccess`.
- Signup status at creation is determined solely by the rota's `Policy`: Public rotas auto-confirm immediately, RequireApproval rotas park signups as Pending for coordinator review. A volunteer's admission/consent status does **not** affect this — a not-yet-admitted user (e.g. mid-`/OnboardingWidget`, consents unsigned) who signs up on a Public rota gets a Confirmed slot before finishing consents. Tracking down committed-but-unconsented volunteers is a business/coordinator concern handled out-of-band, not a signup-time gate.
- Department-filter rollup mirrors the pie bucketing across browse (`GetBrowseShiftsAsync`), urgency (`GetUrgentShiftsAsync`), and dashboard staffing (`GetStaffingDataAsync` / `GetStaffingHoursAsync`): filtering by a department returns rotas owned by the department itself **plus** rotas on its non-promoted sub-teams. Promoted sub-teams (`IsPromotedToDirectory = true`) have their own pie and filter separately. Resolution lives in `ShiftManagementService.ResolveDepartmentTeamIdsAsync`; the repo takes a flat `IReadOnlyCollection<Guid>` so the service is the only place that knows about the hierarchy.
- **Department coverage pies** (rendered above `/Shifts`, see [feature](../features/shifts/department-coverage-pies.md)):
  - Pie eligibility = `Team.IsInDirectory` (top-level department OR promoted sub-team). Non-promoted sub-team rotas roll up into the parent's pie; if no eligible ancestor exists, the rota is dropped from pies.
  - `AdminOnly` shifts and rotas with `IsVisibleToVolunteers = false` contribute zero hours regardless of viewer privilege.
  - Confirmed signups are capped at `MaxVolunteers` per shift before they roll into `FilledHours`, so a pie never exceeds 100 %.
  - All-day shifts contribute the standard 08:00–18:00 window's duration per slot, never `Shift.Duration` directly.
  - Service (`IShiftManagementService.GetDepartmentCoveragePiesAsync`) returns rows in natural `TeamName` order; the "promoted sub-team next to its parent" display ordering is applied in `ShiftBrowsePageBuilder.OrderPiesGroupedByParent` (display ordering belongs in view-model assembly).

## Negative Access Rules

- Regular humans **cannot** manage rotas or shifts. They can only browse and sign up.
- Regular humans **cannot** approve, refuse, or bail other humans' signups.
- Regular humans **cannot** voluntell other humans.
- Regular humans (no team coordinator / management role anywhere) **cannot** see the cross-department shift dashboard.
- Department coordinators / sub-team managers **cannot** see the dashboard's coordinator-activity panel or trigger the per-shift voluntell action — those stay on the narrower `ShiftDashboardAccess` policy (Admin / NoInfoAdmin / VolunteerCoordinator). The page entry is on the wider `ShiftDepartmentManager` policy.
- Department coordinators **cannot** manage rotas or approve signups outside their own department.
- Sub-team managers **cannot** manage rotas or approve signups outside their own sub-team (not siblings, not parent department).
- Department coordinators **cannot** view volunteer medical data.
- NoInfoAdmin **cannot** create or edit rotas or shifts (management gates to Admin/VolunteerCoordinator + dept coordinators). They can manage signups (approve, refuse, bail, voluntell, mark no-show, remove) and view medical data.
- VolunteerCoordinator **cannot** view volunteer medical data.

## Triggers

- Every signup state change writes an audit log entry and dispatches a `ShiftSignupChange` notification to the department's coordinators via `INotificationService`. Action set: `AuditAction.ShiftSignup{Created,Confirmed,Refused,Voluntold,Bailed,Cancelled,NoShow,Reassigned}`. `ShiftSignupCreated` fires on every self-signup (Pending or Confirmed) so the creation moment is always traceable; `ShiftSignupConfirmed` fires only on the later Pending → Confirmed transition by an approver. `ShiftSignupReassigned` fires once per account-merge fold (re-FK of signups from source to target).
- Voluntelling additionally fires a `ShiftAssigned` informational notification to the assigned volunteer (best-effort; failures logged but do not roll back the signup).
- When a Bail or Remove drops the confirmed count below `MinVolunteers`, a `ShiftCoverageGap` actionable notification (priority High) is sent to the department's coordinators.
- Range signup, range voluntell, range bail, range approve, and range refuse all use a shared `SignupBlockId` and operate on the entire block atomically (with per-shift filtering for capacity/conflicts on creation paths).
- Moving a rota to a different team writes an `AuditAction.RotaMovedToTeam` log entry and updates `Rota.TeamId` via a targeted update (only `TeamId` + `UpdatedAt` are marked modified).
- Deleting a rota or shift is rejected if any signup is in Confirmed state. Pending signups on a deleted rota/shift are auto-Cancelled via the entity's `Cancel` method.
- When an account merge accepts, `IShiftSignupService.ReassignToUserAsync` re-FKs `ShiftSignup` rows (volunteer / enrolled-by / reviewed-by user references) from source to target; `IShiftManagementService.ReassignProfilesAndTagPrefsToUserAsync` re-FKs `VolunteerEventProfile` + `VolunteerTagPreference` (with conflict resolution since both are `(UserId)`-unique); `IGeneralAvailabilityService.ReassignToUserAsync` re-FKs `GeneralAvailability`. Called only by `IAccountMergeService.AcceptAsync` (Profiles section).

## Cross-Section Dependencies

- **Teams:** `ITeamService` — rotas belong to a department or sub-team. Used for `GetByIdsWithParentsAsync`, `GetTeamNamesByIdsAsync`, `GetCoordinatorUserIdsAsync`, `GetUserCoordinatedTeamIdsAsync`. Coordinator status determines shift management access.
- **Users:** `IUserService` — `GetByIdsAsync` resolves display data (name, profile picture) for signup rows now that `ShiftSignup.User` nav is stripped. Also used by the dashboard activity computation and the volunteer search builder.
- **Auth:** `IRoleAssignmentService` (lazy-resolved) — role checks for `Admin`, `NoInfoAdmin`, `VolunteerCoordinator` from `HasActiveRoleAsync`.
- **Tickets:** `ITicketQueryService` — used by the coordinator dashboard to compute ticket-buyer cross-references; `EventParticipation` is consumed by Tickets to gate "who hasn't bought" lists.
- **Audit Log:** `IAuditLogService` — every signup state change and rota move emits an audit entry.
- **Notifications:** `INotificationService` — coordinator notifications for signup changes, voluntell assignments, and coverage gaps. No direct email-outbox dependency from this section.
- **GDPR:** `ShiftSignupService` implements `IUserDataContributor` (export of signups, volunteer event profile, general availability, tag preferences) and `CancelActiveSignupsForUserAsync` (deletion).
- **Profiles:** Called by `IAccountMergeService` (Profiles section) — `IShiftSignupService.ReassignToUserAsync`, `IShiftManagementService.ReassignProfilesAndTagPrefsToUserAsync`, and `IGeneralAvailabilityService.ReassignToUserAsync` re-FK Shifts-owned user-scoped rows from source to target during account merge fold.

## Architecture

**Owning services:** `ShiftManagementService`, `ShiftSignupService`, `GeneralAvailabilityService`, `VolunteerTrackingService`, `BurnSettingsService` (cross-section read DTO supplier — returns `BurnSettingsInfo` over `event_settings`, issue nobodies-collective/Humans#719), `WorkloadService` (read-only aggregations, no DbSet writes)
**Owned tables:** `rotas`, `shifts`, `shift_signups`, `event_settings`, `general_availability`, `volunteer_event_profiles`, `volunteer_build_statuses`, `shift_tags`, `volunteer_tag_preferences`, `rota_shift_tags` (join table). `event_participations` is owned by Users (see [`Users.md`](Users.md)); Shifts only reads it via `IUserService`.
**Status:** (A) Fully migrated. All four services live in `Humans.Application.Services.Shifts` and route through `IShiftManagementRepository` / `IShiftSignupRepository` / `IGeneralAvailabilityRepository` / `IVolunteerTrackingRepository`. Cross-domain navs on Shifts-owned entities deleted 2026-04-25 in nobodies-collective/Humans#541 final pass; FKs stay wired in EF via the typed-FK form.

- Services live in `Humans.Application.Services.Shifts/` and never import `Microsoft.EntityFrameworkCore`.
- `IShiftManagementRepository`, `IShiftSignupRepository`, `IGeneralAvailabilityRepository`, `IVolunteerTrackingRepository` (impls in `Humans.Infrastructure/Repositories/Shifts/`) are the only code paths touching this section's tables via `DbContext`.
- **Caching:**
  - `ShiftManagementService` takes `IMemoryCache` directly (no decorator). Auth cache (`shift-auth:{userId}`, 60 s absolute) wraps `ITeamService.GetUserCoordinatedTeamIdsAsync` on a hot per-request path. Dashboard queries (overview / coordinator-activity / trends) use a 5-minute sliding cache. External sections (Teams, Profiles) invalidate the auth cache via `IShiftAuthorizationInvalidator.Invalidate(userId)` rather than poking `IMemoryCache` directly. `ShiftSignupService` and `GeneralAvailabilityService` use no cache (§15 Option A).
  - **View cache (`IShiftView`)** — issue #720. Singleton `CachingShiftViewService` (in `Humans.Infrastructure.Services.Shifts`) wraps a keyed Scoped `ShiftViewService` (in `Humans.Application.Services.Shifts`). Two `ConcurrentDictionary` caches keyed by user id (`ShiftUserView`) and rota id (`ShiftRotaView`). Synchronous read surface — `IShiftView.GetUser(uid)` returns dict-hit data without a `DbContext` round-trip. Lazy-on-miss cold build; no startup warmup (open Q 2). Implements `IShiftViewInvalidator` (`InvalidateUser` / `InvalidateRota` / `InvalidateShift` / `InvalidateAll`). Every Shifts mutation in `ShiftSignupService` / `ShiftManagementService` / `GeneralAvailabilityService` / `VolunteerTrackingService` calls the appropriate `Invalidate*` after `SaveChanges`; cross-section fan-in from `AccountDeletionService` alongside the existing `IShiftAuthorizationInvalidator` hooks. Mirrors `CachingProfileService` / `CachingTeamService`. **T-09 + T-10 (issue #720)** drained the per-user signup-bypass callers onto `IShiftView`. T-09 covered the hot path: `ShiftVolunteerSearchBuilder` (bulk `GetUsersAsync`, replacing an N+1 voluntell-search loop), `AgentUserSnapshotProvider` and `AgentToolDispatcher` (`GetUserAsync` replacing `IShiftSignupService.GetByUserAsync`), and `ProfileController` + `DashboardService` (read `TagPreferences` from `ShiftUserView` instead of `IShiftManagementService.GetVolunteerTagPreferencesAsync`). T-10 covered the legacy controller/VC surface: `ShiftsController.Index` / `Mine`, `ShiftSignupsViewComponent`, and `ShiftBrowsePageBuilder` — all per-user signup reads (and `Index`'s tag-preference + availability reads) now route through `IShiftView.GetUserAsync`. Remaining legacy read methods on `IShiftSignupService` / `IShiftManagementService` migrate in follow-up batches.
- **Cross-domain navs stripped** 2026-04-25 (#541 final pass): `Rota.Team`, `ShiftSignup.User` / `EnrolledByUser` / `ReviewedByUser`, `VolunteerEventProfile.User`, `VolunteerTagPreference.User`. Display stitching routes through `IUserService.GetByIdsAsync` and `ITeamService.GetTeamNamesByIdsAsync`.
- **Cross-section calls:** `ITeamService`, `IUserService`, `IRoleAssignmentService` (lazy), `ITicketQueryService` (lazy, dashboard only), `IAuditLogService`, `INotificationService`.
- **Architecture tests:** `tests/Humans.Application.Tests/Architecture/ShiftManagementArchitectureTests.cs`, `ShiftSignupArchitectureTests.cs`, `GeneralAvailabilityArchitectureTests.cs`, `VolunteerTrackingArchitectureTests.cs`, `ShiftViewArchitectureTests.cs`.

### Repository surface

- **`IShiftManagementRepository`** — owns `rotas`, `shifts`, `event_settings`, `shift_tags`, `volunteer_tag_preferences`, `rota_shift_tags`, `volunteer_event_profiles`.
  - Aggregate-local navs kept: `Rota.Shifts`, `Rota.EventSettings`, `Rota.Tags`, `Shift.Rota`, `Shift.ShiftSignups` (read-side), `EventSettings.Rotas`.
  - Cross-domain navs stripped: `Rota.Team`; team display data resolves via `ITeamService.GetByIdsWithParentsAsync` / `GetTeamNamesByIdsAsync`.
- **`IShiftSignupRepository`** — owns `shift_signups`. Also surfaces a read for `volunteer_event_profiles` used by the GDPR contributor (`GetVolunteerEventProfilesForUserAsync`).
  - Aggregate-local navs kept: `ShiftSignup.Shift`, `Shift.Rota`, `Rota.EventSettings` (read-only projection chain), `Shift.ShiftSignups` (capacity counts).
  - Cross-domain navs stripped: `ShiftSignup.User`, `ShiftSignup.ReviewedByUser`. Display data resolves via `IUserService.GetByIdsAsync`.
- **`IGeneralAvailabilityRepository`** — owns `general_availability`.
  - Aggregate-local navs kept: `GeneralAvailability.EventSettings`.
  - Cross-domain navs stripped: `GeneralAvailability.User` (removed 2026-04-22 in #541c; FK kept via typed-FK form — schema unchanged).

### Touch-and-clean guidance

- Do not add new `.Include(r => r.Team)` or `.Include(... => ... .User)` chains on Shifts-owned entities — cross-domain navs are stripped; resolve via `ITeamService` / `IUserService` by id.
- Do not add new `_cache.` calls in `ShiftManagementService` beyond the existing auth + dashboard caches — auth invalidation routes through `IShiftAuthorizationInvalidator`.
- If you add a new table to this section, add it to §8 of `design-rules.md` **in the same commit**.

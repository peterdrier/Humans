# Shifts — Section Invariants

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

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
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
- Rota period (Build, Event, Strike) determines the shift creation UX (all-day vs. time-slotted) and signup UX (date-range vs. individual).
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

- **Teams**: Rotas belong to a department or sub-team. Coordinator/manager status on a team determines shift management access. Sub-team managers have scoped access; department coordinators have access across all sub-teams.
- **Profiles**: Volunteer event profile stores per-event volunteer data (skills, dietary, medical). NoShow history is shown on a human's profile to coordinators and privileged roles.
- **Admin**: Event settings management is Admin-only.
- **Email**: Signup status change notifications are queued through the email outbox.

## Architecture — Current vs Target

See `docs/architecture/design-rules.md` for the full rules.

**Owning services:** `ShiftManagementService`, `ShiftSignupService`, `GeneralAvailabilityService`
**Owned tables:** `rotas`, `shifts`, `shift_signups`, `event_settings`, `general_availabilities`, `volunteer_event_profiles`

## Target Architecture Direction

> **Status:** This section currently follows the "services in Infrastructure, direct DbContext" model. It will be migrated to the repository/store/decorator pattern per [`../architecture/design-rules.md`](../architecture/design-rules.md). **Delete this block once the migration lands and this section's services live in `Humans.Application` with `*Repository.cs` impls in `Humans.Infrastructure/Repositories/`.**

### Target repositories

- **`IShiftManagementRepository`** — owns `rotas`, `shifts`, `event_settings` (and the shift-tag tables, see note below)
  - Aggregate-local navs kept: `Rota.Shifts`, `Rota.EventSettings`, `Rota.Tags`, `Shift.Rota`, `Shift.ShiftSignups` (read-side delete cascade only), `EventSettings.Rotas`
  - Cross-domain navs stripped: `Rota.Team` (Teams), any `.Select(r => new { r.Team.Id, r.Team.Name })` projections
- **`IShiftSignupRepository`** — owns `shift_signups`
  - Aggregate-local navs kept: `ShiftSignup.Shift`, `Shift.Rota` and `Rota.EventSettings` (read-only projection chain), `Shift.ShiftSignups` (for capacity counts)
  - Cross-domain navs stripped: `ShiftSignup.User`, `ShiftSignup.ReviewedByUser`, `ShiftSignup.EnrolledByUser`, `Rota.Team`
- **`IVolunteerEventProfileRepository`** — owns `volunteer_event_profiles`
  - Aggregate-local navs kept: none beyond the row itself
  - Cross-domain navs stripped: `VolunteerEventProfile.User`
- **`IGeneralAvailabilityRepository`** — owns `general_availabilities`
  - Aggregate-local navs kept: `GeneralAvailability.EventSettings` (read-side, for cross-repo join on shared aggregate root)
  - Cross-domain navs stripped: `GeneralAvailability.User`

**Note on service/table mapping:** §8 groups all six tables under the three owning services but does not split them 1:1. Actual read/write distribution observed in code:

- `ShiftManagementService` writes `rotas`, `shifts`, `event_settings`; also reads/writes `shift_tags` and `volunteer_tag_preferences` (neither listed in §8 — flagged below).
- `ShiftSignupService` writes `shift_signups`; reads `rotas`, `shifts`, `event_settings` (within-section, cross-service — acceptable under §3 once those become repo calls instead of direct `DbContext`).
- `GeneralAvailabilityService` writes `general_availabilities`.
- `volunteer_event_profiles` is not currently touched by any of the three services on disk (search returned zero hits in these files) — its ownership needs explicit resolution before the split lands. Pulled into its own repo above as the neutral default.

### Current violations

Observed in this section's service code as of 2026-04-15:

- **Cross-domain `.Include()` calls:**
  - `ShiftManagementService.cs:216, 280, 488, 538, 546` — `.Include(r => r.Team)` / `.ThenInclude(r => r.Team)` (Teams section)
  - `ShiftSignupService.cs:57, 687, 802, 849, 862, 888, 918` — `.ThenInclude(r => r.Team)` (Teams section)
  - `ShiftSignupService.cs:541` — `.Include(s => s.ShiftSignups).ThenInclude(ss => ss.User)` (Users/Identity)
  - `ShiftSignupService.cs:876` — `.Include(d => d.User)` on `ShiftSignup` (Users/Identity)
  - `ShiftSignupService.cs:890` — `.Include(s => s.ReviewedByUser)` (Users/Identity)
  - `GeneralAvailabilityService.cs:60` — `.Include(g => g.User)` (Users/Identity)
- **Cross-section direct DbContext reads:** None found. Role checks go through `_roleAssignmentService` (Auth), team lookups through `ITeamService` (Teams). The `.Select(r => new { r.Team.Id, r.Team.Name })` projection at `ShiftManagementService.cs:864` is the only direct Teams-table touch and is covered under cross-domain `.Include`/nav-walk above.
- **Within-section cross-service direct DbContext reads:**
  - `ShiftSignupService` reads `_dbContext.Rotas` (`ShiftSignupService.cs:326, 510`), `_dbContext.Shifts` (`:55, 257`), and `_dbContext.EventSettings` (via `.Include` chains) — all owned by `ShiftManagementService` per §8. Acceptable once both are behind repos and the dependency is expressed as `IShiftManagementRepository` → `IShiftSignupRepository`.
- **Inline `IMemoryCache` usage in service methods:**
  - `ShiftManagementService.cs:97` — `_cache.GetOrCreateAsync(CacheKeys.ShiftAuthorization(userId), ...)` wrapping `TeamService.GetUserCoordinatedTeamIdsAsync(userId)`. Per §4/§5 this cache belongs in the Teams caching decorator, not here. Drop the `shift-auth` cache and let Teams own the result.
  - No `_cache.` references in `ShiftSignupService` or `GeneralAvailabilityService`.
- **Cross-domain nav properties on this section's entities:**
  - `Rota.Team` (→ Teams)
  - `Shift` has no cross-domain nav (clean)
  - `ShiftSignup.User`, `ShiftSignup.EnrolledByUser`, `ShiftSignup.ReviewedByUser` (→ Users/Identity)
  - `VolunteerEventProfile.User` (→ Users/Identity)
  - `GeneralAvailability.User` (→ Users/Identity); `GeneralAvailability.EventSettings` is section-local
  - `VolunteerTagPreference.User` (→ Users/Identity) — entity not listed in §8
- **§8 gaps (tables touched by this section but not listed under Shifts in the ownership map):**
  - `shift_tags` — read/written by `ShiftManagementService` (`:878, 886, 896, 907, 924`). Not listed in §8 under any section. Likely Shifts.
  - `volunteer_tag_preferences` — read/written by `ShiftManagementService` (`:939, 949, 953, 957`). Not listed in §8 under any section. Likely Shifts. **Flag: §8 needs an explicit decision; assumed Shifts-owned for the repository split.**

### Touch-and-clean guidance

Until this section is migrated end-to-end, when touching its code:

- Do not add new `.Include(r => r.Team)` / `.ThenInclude(r => r.Team)` calls — the seven existing occurrences in `ShiftManagementService.cs` and `ShiftSignupService.cs` are the full set to remove; any new view/DTO needing a team name should pull it from `ITeamService` by id.
- Do not add new `.Include(... => ... .User)` chains on `ShiftSignup` or `GeneralAvailability` — project to `UserId` and resolve display data via `IProfileService` / `IUserService`.
- Do not add new `_cache.` calls in `ShiftManagementService`; route authorization caching through `IRoleAssignmentService` / `ITeamService`. The existing `ShiftAuthorization` cache at `ShiftManagementService.cs:97` should be deleted in the same PR that moves Teams to a cached store.
- If you add a new table to this section, add it to §8 of `design-rules.md` **in the same commit** — do not repeat the `shift_tags` / `volunteer_tag_preferences` omission.

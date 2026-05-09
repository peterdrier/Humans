<!-- freshness:triggers
  src/Humans.Domain/Entities/VolunteerBuildStatus.cs
  src/Humans.Application/Services/Shifts/VolunteerTrackingService.cs
  src/Humans.Application/Interfaces/Shifts/IVolunteerTrackingService.cs
  src/Humans.Application/Interfaces/Repositories/IVolunteerTrackingRepository.cs
  src/Humans.Web/Controllers/VolunteerTrackingController.cs
  src/Humans.Web/Views/VolunteerTracking/Index.cshtml
  src/Humans.Web/Views/VolunteerTracking/_VolunteerHeatmap.cshtml
  src/Humans.Web/Views/VolunteerTracking/_VolunteerUnbookedHeatmap.cshtml
-->
<!-- freshness:flag-on-change
  Volunteer Tracking heatmap algorithm, the two cohorts (signups-with-gaps vs declared-but-unbooked), and the camp set-up date semantics — review when these change.
-->

# Volunteer Tracking

Adds a `/ShiftDashboard/VolunteerTracking` sub-page that surfaces volunteers whose build-period schedule has gaps, plus volunteers who declared participation and filled in availability but haven't signed up for any shifts.

## Business Context

Building the camp before gates open is a multi-week effort with rolling crew turnover. Volunteers commit to date ranges months ahead, then real life happens — they arrive late, leave early, or join camp set-up halfway through. The Volunteer Coordinator (VC) needs a single view that answers:

- **Who said they'd be on build but isn't covered today?** (signup gaps)
- **Who declared participation and gave availability but never signed up for anything?** (the unbooked cohort)
- **Who left scheduled shifts to help with camp set-up?** (so coverage isn't flagged as a real gap)

Before this feature the VC was reconstructing this state by hand from the rota grid and the participation list — error-prone and didn't scale.

This feature adds:

1. A heatmap view (one row per volunteer, one column per day in the build period) showing signup state, gaps, and camp-set-up days at a glance.
2. A second heatmap below it for the declared-but-unbooked cohort, pulled from `EventParticipation` + `GeneralAvailability`.
3. Coordinator write actions: mark "went to camp set-up" from a given day.

> **Note (May 2026):** an earlier iteration also shipped a "day off / blocked day" surface (coordinator single-day toggle + volunteer self-service multi-select on `/Shifts/Mine`). That feature was removed pending redesign; the underlying coordination need ("volunteer is unavailable for specific days but otherwise still on") is still open.

## User Stories

### US-47.1: VC identifies a volunteer with a schedule gap
**As a** Volunteer Coordinator
**I want to** see at a glance which volunteers committed to build days and have no signup on a given day
**So that** I can chase them up or backfill the slot before it becomes a real coverage problem

**Acceptance Criteria:**
- `/ShiftDashboard/VolunteerTracking` lists every volunteer with at least one build-period signup, sorted by gap count (descending), then last name.
- Each row shows one cell per day from the volunteer's first build signup through gate-open day. Cell colours: green = confirmed signup, light green = pending signup, red = gap, blue = on camp set-up, grey = outside their active window.
- A red badge on the row shows the gap count (`{0} gaps`).
- A header card shows aggregate counts: total tracked, total with gaps, total on camp set-up, total declared-but-unbooked.
- Filter toggles let the VC hide rows with no gaps, hide rows already on camp set-up, and hide the unbooked section.
- An empty state ("Everyone is on track.") renders when no row has gaps after filtering.

### US-47.2: VC marks a volunteer as gone to camp set-up
**As a** Volunteer Coordinator
**I want to** record that a volunteer left scheduled rotas to join camp set-up from a specific day
**So that** the gap detection stops flagging their (now-irrelevant) build-period coverage as missing

**Acceptance Criteria:**
- Clicking any cell in the volunteer's heatmap row opens a popover with action buttons.
- "Mark went to camp set-up from this day" submits a form with the cell's date; on success the row turns blue from that day forward and the gap count drops.
- A "Clear set-up date" action on the popover reverts the row.
- The set-up date must be inside the build period (before gate-open) and on or after the volunteer's first build signup — server-side validated; invalid submissions show a TempData error.
- An audit row is written (`VolunteerCampSetupSet` / `VolunteerCampSetupCleared`) recording the actor.

## Data Model

One new entity, owned by the Shifts section: `VolunteerBuildStatus`. Full field-level invariants in [`docs/sections/Shifts.md` § VolunteerBuildStatus](../sections/Shifts.md#volunteerbuildstatus).

Summary:

| Field | Type | Purpose |
|-------|------|---------|
| `Id` | `Guid` | PK |
| `UserId` | `Guid` | Bare cross-section FK to `users.id` (no nav property) |
| `EventSettingsId` | `Guid` | Same-section FK to `event_settings.id` (cascade delete) |
| `BarrioSetupStartDate` | `LocalDate?` | Day the volunteer joined camp set-up; nullable |
| `SetByUserId` | `Guid?` | Actor who set the camp-set-up marker |
| `SetAt` | `Instant?` | When the marker was set |
| `Notes` | `string?` | Free-text from the coordinator who set/cleared the date |

**Table:** `volunteer_build_statuses`. **Unique:** `(UserId, EventSettingsId)`.

`AuditAction` values: `VolunteerCampSetupSet`, `VolunteerCampSetupCleared`. `EntityType = nameof(VolunteerBuildStatus)`. (Three additional values — `VolunteerDayBlocked`, `VolunteerDayUnblocked`, `VolunteerOwnBlockedDaysSaved` — exist as positional reservations from the removed day-off feature.)

## Workflows

### Heatmap algorithm (high level)

For the active event the service builds a `VolunteerTrackingViewModel` containing two cohorts. Sort order is the controller's job (per `memory/architecture/display-sort-in-controllers.md`).

**Cohort A — main (signups-with-gaps):**

1. Collect every user with at least one Confirmed or Pending signup on a Build-period rota for the active event.
2. For each user, the heatmap window starts at `min(firstBuildSignupDate, BarrioSetupStartDate ?? +∞)` and ends at `GateOpeningDate - 1`.
3. For each day in the window, compute the cell state in this priority order:
   - `BarrioSetupStartDate` set and day ≥ that date → `CampSetup` (blue).
   - Day before the user's first build signup → `Outside` (grey).
   - User has at least one Confirmed signup that covers the day → `Confirmed` (green).
   - User has at least one Pending signup that covers the day → `Pending` (light green).
   - Otherwise → `Gap` (red). Gaps are not capped by "today" — any unfilled day in the window counts so coordinators can plan ahead for future events, not just react during build.
4. `GapCount` = number of `Gap` cells. Rows with `GapCount = 0` may still show (the filter toggle hides them).

**Cohort B — declared-but-unbooked:**

1. Collect every user where `EventParticipation.Status = Attending` for the active event AND `GeneralAvailability.AvailableDayOffsets` is non-empty AND they have **zero** Confirmed/Pending signups on any Build rota.
2. Each cell renders `AvailableUnbooked` (yellow), `AvailableExpected` (light yellow), `CampSetup` (blue), or `NotAvailable` (grey) per `AvailableDayOffsets ∩ build window`.
3. As soon as a user from this cohort confirms a signup, they migrate to Cohort A on the next page load.

### Write paths

- `VolunteerTrackingController.SetCampSetup` / `ClearCampSetup` — gated by the `VolunteerTrackingWrite` policy (Admin or VolunteerCoordinator).

All write paths route through `IVolunteerTrackingService` → `IVolunteerTrackingRepository` and emit audit rows. Validation lives in the service: set-up date must be inside the build window and on/after the user's first build signup.

## Routes

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/ShiftDashboard/VolunteerTracking` | GET | `VolunteerTrackingWrite` (read on the same gate) | Heatmap page |
| `/ShiftDashboard/VolunteerTracking/SetCampSetup` | POST | `VolunteerTrackingWrite` | Set `BarrioSetupStartDate` for a volunteer |
| `/ShiftDashboard/VolunteerTracking/ClearCampSetup` | POST | `VolunteerTrackingWrite` | Null `BarrioSetupStartDate` |

## Related

- [`docs/sections/Shifts.md`](../sections/Shifts.md) — section invariant doc; the `VolunteerBuildStatus` sub-section under § Data Model is the canonical entity reference.
- [`docs/features/25-shift-management.md`](25-shift-management.md) — base rotas / shifts / signups model; the gap algorithm reads `ShiftSignup` rows it produces.
- [`docs/features/26-shift-signup-visibility.md`](26-shift-signup-visibility.md) — site-wide signup-visibility policy; the tracking page does not reuse that policy (its access is the new `VolunteerTrackingWrite` gate, not the public-signup-list gate).
- [`docs/features/event-participation.md`](event-participation.md) — `EventParticipation.Status = Attending` is the pre-filter for the declared-but-unbooked cohort.
- [`memory/architecture/display-sort-in-controllers.md`](../../memory/architecture/display-sort-in-controllers.md) — why the row sort lives in `VolunteerTrackingController`, not the service or repo.
- [`memory/architecture/no-cross-section-ef-joins.md`](../../memory/architecture/no-cross-section-ef-joins.md) — why `VolunteerBuildStatus.UserId` is a bare `Guid` with no nav.

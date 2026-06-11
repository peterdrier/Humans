<!-- freshness:triggers
  src/Humans.Application/Services/Shifts/ShiftManagementService.cs
  src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs
  src/Humans.Application/DTOs/PostEventStats.cs
  src/Humans.Infrastructure/Repositories/Shifts/ShiftManagementRepository.cs
  src/Humans.Web/Controllers/ShiftDashboardController.cs
  src/Humans.Web/Views/ShiftDashboard/PostEventStats.cshtml
-->
<!-- freshness:flag-on-change
  Aggregation logic (period buckets, no-show rate formula, AdminOnly/hidden exclusions),
  authorization gate, or department-row shape may have changed.
-->

# Post-Event Stats Dashboard

## Business Context

After an event, coordinators and admins need a quick debrief view: which departments had the most no-shows, and which periods (Set-Up / Event / Strike) drove the gaps? The existing coordinator dashboard answers real-time operational questions; this view answers the post-event accountability question — "how did we actually do?"

Asked for in nobodies-collective/Humans#161 (partial scope — CSV exports are deferred pending a GDPR decision; this spec covers only the stats dashboard).

## User Stories

### US-PES.1: Admin Sees Event-Level Completion and No-Show Rates

**As an** Admin / NoInfoAdmin / VolunteerCoordinator
**I want to** see overall Confirmed, No-Show, and completion-rate counters for the active event
**So that** I can assess at a glance how the event went

**Acceptance Criteria:**
- Four summary cards: Total Shifts, Total Confirmed, No-Shows, and Completion Rate
- Completion Rate = `100 - NoShowPct` where `NoShowPct = NoShow / (Confirmed + NoShow) × 100` (rounded to nearest integer)
- When there are no recorded outcomes (Confirmed + NoShow = 0), Completion Rate shows "—" with "no outcome data recorded"
- AdminOnly shifts and rotas with `IsVisibleToVolunteers = false` are excluded from all counts

### US-PES.2: Admin Sees Per-Department Breakdown

**As an** Admin / NoInfoAdmin / VolunteerCoordinator
**I want to** see a table with one row per top-level department showing confirmed, no-shows, completion rate, and per-period badges
**So that** I can identify which departments drove no-show problems and in which periods

**Acceptance Criteria:**
- One row per parent team (department) that had at least one shift in the active event
- Columns: Department name (linked to ShiftAdmin if the team has a slug), Confirmed, No-Shows, Completion badge, Set-Up badge, Event badge, Strike badge
- Completion badge uses: green ≥ 90%, amber ≥ 75%, red < 75% (consistent across department-level column and all period columns)
- Period badge shows completion percentage; dash ("—") when the period had no Confirmed or NoShow signups
- Departments are sorted alphabetically (case-insensitive ordinal) — applied at the controller, not the service
- Same AdminOnly / hidden rota exclusions as US-PES.1

### US-PES.3: Dashboard Access Is Gated

**As a** regular volunteer
**I cannot** access the post-event stats dashboard

**Acceptance Criteria:**
- Route `GET /Shifts/Dashboard/PostEventStats` requires the `ShiftDashboardAccess` policy (Admin, NoInfoAdmin, VolunteerCoordinator)
- Unauthenticated or unauthorized users are redirected to the login page / access-denied page per the standard middleware

## Data Model

No new tables or columns. Derived from existing `event_settings`, `rotas`, `shifts`, `shift_signups`, and `teams`.

### Period assignment

A shift's period is determined by its rota's `Period` property (`ShiftPeriod` enum: `Build`, `Event`, `Strike`, `All`). Shifts on an `All`-period rota are bucketed by day offset relative to `EventSettings.EventStartDate`:
- Day offset < 0 → Build
- Day offset 0..N (within event dates) → Event
- Day offset > event end → Strike

### Signup inclusion

Only `ShiftSignupStatus.Confirmed` and `ShiftSignupStatus.NoShow` are counted. All other statuses (Pending, Refused, Bailed, Cancelled) are excluded from rates. A shift with zero Confirmed + NoShow signups is counted in TotalShifts but not in ShiftsWithData or rate calculations.

### Department roll-up

Each shift is attributed to the parent team of the rota's owning team (or the owning team itself if it has no parent). Only top-level departments appear as rows; sub-teams' signups roll up into their parent's bucket.

## Routes

| Method | URL | Controller action |
|--------|-----|-------------------|
| GET | `/Shifts/Dashboard/PostEventStats` | `ShiftDashboardController.PostEventStats` |

Nav entry appears under Shifts in the admin sidebar, next to the existing shift dashboard entries.

## Authorization

`ShiftDashboardAccess` policy: Admin, NoInfoAdmin, VolunteerCoordinator.

## DTOs

- `PostEventStats` — top-level record: shift counts, overall totals, `NoShowPct`/`CompletionPct` computed properties, and the sorted department list.
- `PostEventDepartmentRow` — per-department record: totals, `NoShowPct`/`CompletionPct` computed properties, and three `PostEventPeriodRow` sub-records.
- `PostEventPeriodRow` — per-period record: `TotalConfirmed`, `TotalNoShow`, `NoShowPct`/`CompletionPct` computed properties.

All three share the same rate formula: `NoShowPct = round(100 × NoShow / (Confirmed + NoShow))`, 0 when denominator is 0; `CompletionPct = clamp(100 − NoShowPct, 0, 100)`.

## Related Features

- [Shift Management](shift-management.md) — repository and service methods used by this feature.
- [Department Coverage Pies](department-coverage-pies.md) — similar read-only aggregation over the same signup data.
- Section invariants: [`docs/sections/Shifts.md`](../../sections/Shifts.md).
- Upstream issue: nobodies-collective/Humans#161.

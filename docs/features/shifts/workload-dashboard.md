<!-- freshness:triggers
  src/Humans.Application/Services/Shifts/Workload/WorkloadService.cs
  src/Humans.Application/Interfaces/Shifts/Workload/IWorkloadService.cs
  src/Humans.Application/DTOs/Shifts/Workload/WorkloadReport.cs
  src/Humans.Web/Controllers/ShiftWorkloadAdminController.cs
  src/Humans.Web/Views/ShiftWorkloadAdmin/Index.cshtml
-->
<!-- freshness:flag-on-change
  Workload math (Confirmed-only hours, MaxVolunteers cap, all-day window),
  pending-vs-confirmed split, role-hours mapping (RolePeriod → column;
  per-holder annual estimate; dept planned = estimate × slots), or scope of
  admin-only/hidden inclusion may have changed.
-->

# Workload Dashboard

## Business Context

Coordinators and admins balancing the event need an at-a-glance view of "who is doing how much" so they can spot burnout candidates (too many confirmed hours), idle volunteers (no signups), and under-staffed departments (low coverage). The existing `/Shifts/Dashboard` answers operational questions per-department; this view answers the cross-event distribution question — sliced three ways on one page.

Asked by Peter for the 2026 cycle. Combines **shift-based** workload (Confirmed shift hours) with **role-based** workload — each `TeamRoleDefinition.EstimatedHours` (whole hours per year) a person holds, mapped to the column matching the role's `RolePeriod`. Year-round roles get their own column; Build/Event/Strike roles fold into the matching shift-period column.

## User Stories

### US-WL.1: Admin Sees Per-Person Workload

**As an** Admin / NoInfoAdmin / VolunteerCoordinator
**I want to** see every volunteer with shift signups or role assignments, with their hours split by period and Pending count
**So that** I can identify volunteers nearing burnout and volunteers who have queued work but no approved work

**Acceptance Criteria:**
- Row per user with `≥ 1` Pending/Confirmed signup **or** a role assignment carrying estimated hours, for the active event
- Columns: Display name, Confirmed signup count, Pending signup count, Year-round / Build / Event / Strike hours, Total
- Confirmed shift hours land in their shift period's column; a held role's `EstimatedHours` (full annual estimate, per holder) lands in the column matching the role's `RolePeriod`
- Pending signups do **not** contribute to hours (no burnout inflation from queued work)
- Display order: descending by Total hours, then ascending by display name (sort is applied in the controller, not the service)
- Click a column header to re-sort asc/desc client-side

### US-WL.2: Admin Sees Per-Shift Coverage

**As an** Admin / NoInfoAdmin / VolunteerCoordinator
**I want to** see every shift in the active event with planned slots, Confirmed/Pending/Open counts
**So that** I can find shifts that still need fills

**Acceptance Criteria:**
- Row per shift in the active event
- Includes shifts on `AdminOnly = true` and rotas with `IsVisibleToVolunteers = false` (admin view; coordinators need full visibility for balancing)
- Columns: Date, start time (or "all-day"), Hours, Department, Rota, Slots (MaxVolunteers), Confirmed, Pending, Open (`max(0, MaxVolunteers - Confirmed)`)
- All-day shifts contribute the standard **08:00–18:00** window's duration (10 h), not `Shift.Duration`
- Default order: Day offset asc, start time asc, team name asc (sort is applied in the controller)
- Client-side column sort

### US-WL.3: Admin Sees Per-Department Roll-Up

**As an** Admin / NoInfoAdmin / VolunteerCoordinator
**I want to** see every department with planned vs filled slots and hours, plus coverage %
**So that** I can find departments that are under-staffed at the planning level

**Acceptance Criteria:**
- Row per department: any team owning at least one rota with shifts in the event, **or** owning a role with estimated hours
- Columns: Department, Rota count, Shift count, Planned slots, Filled slots, Coverage %, Planned hours, Filled hours
- `FilledSlots` and `FilledHours` cap at `MaxVolunteers` per shift (over-enrolled shifts cannot drive coverage above 100 %)
- Role estimates fold into `Planned hours` (estimate × slots) and `Filled hours` (estimate × assigned slots); slot counts and Coverage % stay shift-only
- Default order: team name ascending (sort is applied in the controller)
- Client-side column sort

## Data Model

No new tables. Derived from existing `event_settings`, `rotas`, `shifts`, `shift_signups`, `teams`, and team role definitions/assignments (`TeamRoleDefinition.EstimatedHours` + `RolePeriod`, read via the cached `TeamInfo` projection).

### Hour math

```
# Shift hours
hours       = shift.IsAllDay ? (AllDayWindowEnd − AllDayWindowStart) : shift.Duration.TotalHours
planned    += hours × shift.MaxVolunteers              # per-department
filled     += hours × min(confirmed, MaxVolunteers)    # per-department
confirmed  += hours                                    # per-user, only for Confirmed signups, by shift period

# Role hours (estimate = TeamRoleDefinition.EstimatedHours, whole hours/year)
per-user   += estimate                                 # each holder, into the role's RolePeriod column
planned    += estimate × SlotCount                     # per-department
filled     += estimate × assignedSlotCount             # per-department
```

Pending signups contribute to the per-user `PendingSignupCount` only, never to hours. Roles with a null `EstimatedHours` contribute nothing. Roles on **deactivated** teams are excluded (deactivation doesn't clear role assignments, so stale holders would otherwise leak in). Role assignments are current holders (no per-event-year history); role hours show only when the active event has at least one shift.

### Inclusion rule

The admin workload view includes every shift on every rota in the active event — **including** `AdminOnly` shifts and rotas with `IsVisibleToVolunteers = false`. Diverges from the public `/Shifts` view (which hides both); justified because coordinators need full visibility for balancing.

## Routes

| Route | Purpose | Auth |
|---|---|---|
| `GET /Shifts/Admin/Workload` | Workload dashboard (three tabs) | `ShiftDashboardAccess` (Admin / NoInfoAdmin / VolunteerCoordinator) |

Lives under `/Shifts/Admin/*` per `memory/architecture/no-admin-url-section.md`. Surfaced in the admin sidebar under the "Shifts" group.

## Authorization

Gated to `PolicyNames.ShiftDashboardAccess` at the controller — same narrow policy that controls the privileged sub-panels on the existing `/Shifts/Dashboard`. Department coordinators do **not** see this view (they have per-department visibility on `/Shifts/Dashboard` already).

## Architecture

`WorkloadService` lives in `Humans.Application.Services.Shifts.Workload` — read-only, no DbSet writes. Reads per-rota shift + signup rows through `IShiftView.GetRotasAsync`; uses `IShiftManagementRepository` only for the active-event lookup and an inlined distinct over `GetShiftsForEventAsync` to derive the set of rota ids to walk (no new interface method — see `memory/architecture/interface-method-additions-are-debt.md`). Cross-section name stitching via `ITeamService.GetByIdsWithParentsAsync` and `IUserService.GetUserInfosAsync`. Role hours come from `ITeamServiceRead.GetTeamsAsync` — the cached `TeamInfo` projection already carries each team's role definitions (with `EstimatedHours`, `RolePeriod`, `SlotCount`) and assignment holder ids, so no new cross-section method was added.

**Cache:** No service-level cache. Source data lives in the Shifts-section per-rota cache owned by `CachingShiftViewService` (§15 Option B at the section level). Signup / shift / rota mutations evict the affected rota cache entries via `IShiftViewInvalidator`, so workload totals stay consistent without a parallel cache key. The aggregation itself is microsecond-scale CPU work over a few hundred rotas at our ~500-user scale.

**Display sort:** The service returns unsorted lists; `ShiftWorkloadAdminController.SortForDisplay` applies the default ordering before passing the report to the view. Per `memory/architecture/display-sort-in-controllers.md` — sorting in the service would leak presentation into the data layer.

## Deferred — Year filter

- **Year filter** — there is no multi-year surface on `IShiftManagementService` today. The view uses the active event. Easy follow-up once a year-based lookup lands.

Role-based hours (the original #734 follow-up) are now implemented — see the per-person/per-department hour math above. Role hours only surface when the active event has at least one shift (the report short-circuits to empty for a shift-less event); revisit if year-round role workload needs to show before any shifts exist.

## Related Features

- [Shift Management](shift-management.md) — workload reads source data through the same `IShiftView` cache.
- [Department Coverage Pies](department-coverage-pies.md) — same shape (planned/filled hours) but volunteer-facing and on `/Shifts`.
- Section invariants: [`docs/sections/Shifts.md`](../../sections/Shifts.md).

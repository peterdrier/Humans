<!-- freshness:triggers
  src/Humans.Application/Services/Shifts/Workload/WorkloadService.cs
  src/Humans.Application/Interfaces/Shifts/Workload/IWorkloadService.cs
  src/Humans.Application/DTOs/Shifts/Workload/WorkloadReport.cs
  src/Humans.Web/Controllers/ShiftWorkloadAdminController.cs
  src/Humans.Web/Views/ShiftWorkloadAdmin/Index.cshtml
-->
<!-- freshness:flag-on-change
  Workload math (Confirmed-only hours, MaxVolunteers cap, all-day window),
  pending-vs-confirmed split, cache TTL, role-hours follow-up, or scope of
  admin-only/hidden inclusion may have changed.
-->

# Workload Dashboard

## Business Context

Coordinators and admins balancing the event need an at-a-glance view of "who is doing how much" so they can spot burnout candidates (too many confirmed hours), idle volunteers (no signups), and under-staffed departments (low coverage). The existing `/Shifts/Dashboard` answers operational questions per-department; this view answers the cross-event distribution question — sliced three ways on one page.

Asked by Peter for the 2026 cycle. Scoped to **shift-based** workload only — role-based hours (the time a `TeamRoleDefinition` is estimated to require) are deferred until `TeamRoleDefinition.EstimatedHours` lands as a separate field. Once that ships, a follow-up extends `WorkloadByPersonRow` with `RoleHours` and unifies the burnout signal.

## User Stories

### US-WL.1: Admin Sees Per-Person Workload

**As an** Admin / NoInfoAdmin / VolunteerCoordinator
**I want to** see every volunteer with at least one shift signup, with their Confirmed hours and Pending count
**So that** I can identify volunteers nearing burnout and volunteers who have queued work but no approved work

**Acceptance Criteria:**
- Row per user with `≥ 1` Pending or Confirmed signup for the active event
- Columns: Display name, Confirmed hours, Confirmed signup count, Pending signup count
- Pending signups do **not** contribute to Confirmed hours (no burnout inflation from queued work)
- Display order: descending by Confirmed hours, then ascending by display name (sort is applied in the controller, not the service)
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
- Row per department (any team owning at least one rota with shifts in the event)
- Columns: Department, Rota count, Shift count, Planned slots, Filled slots, Coverage %, Planned hours, Filled hours
- `FilledSlots` and `FilledHours` cap at `MaxVolunteers` per shift (over-enrolled shifts cannot drive coverage above 100 %)
- Default order: team name ascending (sort is applied in the controller)
- Client-side column sort

## Data Model

No new tables. Derived entirely from existing `event_settings`, `rotas`, `shifts`, `shift_signups`, and `teams`.

### Hour math

```
hours       = shift.IsAllDay ? (AllDayWindowEnd − AllDayWindowStart) : shift.Duration.TotalHours
planned    += hours × shift.MaxVolunteers              # per-department
filled     += hours × min(confirmed, MaxVolunteers)    # per-department
confirmed  += hours                                    # per-user, only for Confirmed signups
```

Pending signups contribute to the per-user `PendingSignupCount` only, never to hours.

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

`WorkloadService` lives in `Humans.Application.Services.Shifts.Workload` — read-only, no DbSet writes. Reads through `IShiftManagementRepository.GetShiftsWithSignupsForEventAsync` (no new repository surface). Cross-section name stitching via `ITeamService.GetByIdsWithParentsAsync` and `IUserService.GetUserInfosAsync`.

**Cache:** Service-level `IMemoryCache` (§15 Option B), 5-minute sliding expiration. Same TTL as the existing shift-dashboard analytics. Invalidation is intentionally TTL-only; mutations don't ping the cache.

**Display sort:** The service returns unsorted lists; `ShiftWorkloadAdminController.SortForDisplay` applies the default ordering before passing the report to the view. Per `memory/architecture/display-sort-in-controllers.md` — sorting in the service would leak presentation into the data layer and bake a single sort order into the cached object.

**Architecture-test allowlist:** `WorkloadService` is in the `ApplicationServicesTakeNoMemoryCacheRule` allowlist; the §15 caching choice is documented inline at the service.

## Deferred — Role-based hours

Acceptance criterion *"Year filter is supported"* and the role-hours dimension (a `TeamRoleDefinition.EstimatedHours` contribution to per-person totals) are **deferred**:

- **Year filter** — there is no multi-year surface on `IShiftManagementService` today. The view uses the active event. Easy follow-up once a year-based lookup lands.
- **Role hours** — `TeamRoleDefinition.EstimatedHours` does not exist yet. Filed as a separate issue. Once it ships, extend `WorkloadByPersonRow` with `RoleHours` and unify the burnout signal across shift hours + role hours.

Upstream issue stays open via `Refs nobodies-collective/Humans#734` until both follow-ups land.

## Related Features

- [Shift Management](shift-management.md) — workload reads through the same `IShiftManagementRepository.GetShiftsWithSignupsForEventAsync`.
- [Department Coverage Pies](department-coverage-pies.md) — same shape (planned/filled hours) but volunteer-facing and on `/Shifts`.
- Section invariants: [`docs/sections/Shifts.md`](../../sections/Shifts.md).

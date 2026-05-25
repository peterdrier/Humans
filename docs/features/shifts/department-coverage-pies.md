<!-- freshness:triggers
  src/Humans.Application/Services/Shifts/ShiftManagementService.cs
  src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs
  src/Humans.Web/Models/Shifts/ShiftBrowsePageBuilder.cs
  src/Humans.Web/Views/Shifts/Index.cshtml
  src/Humans.Web/Views/Shifts/_DepartmentCoveragePieRow.cshtml
  src/Humans.Web/wwwroot/css/site.css
-->
<!-- freshness:flag-on-change
  Pie eligibility rule (IsInDirectory), sub-team roll-up math, capacity cap, AllDay window, or display ordering may have changed.
-->

# Department Coverage Pies

## Business Context

Volunteers browsing `/Shifts` see a long flat list of shifts grouped by department. Coordinators wanted an at-a-glance way to see which departments are under-staffed and a one-click filter to focus on a specific department. The coverage-pie row above the page shows percentage-filled per department and acts as a clickable filter — analogous to the existing department dropdown but visual and persistent.

## User Stories

### US-Pie.1: Volunteer Sees Department Coverage at a Glance
**As a** volunteer
**I want to** see a row of conic-gradient discs above the shift list, one per department, with a percentage-filled fill
**So that** I can pick an under-staffed department to help with without scanning the whole page

**Acceptance Criteria:**
- One pie per pie-eligible department (top-level + promoted sub-team)
- Disc fill = `FilledHours / RequestedHours` clamped to `[0, 100]%`
- Hover tooltip shows `Name — N h / M h (P%)`
- ARIA label reads `Name: P% covered, N of M hours` for screen readers
- A tip pill above the row reads `Tip — click any chart below to filter shifts to that department.`

### US-Pie.2: Click a Pie to Filter; Click Again to Clear
**As a** volunteer
**I want to** click a pie to filter the shift list to that department, and click the same pie again to clear the filter
**So that** filtering by department is one click instead of opening the dropdown

**Acceptance Criteria:**
- Click on a pie POSTs the existing `?departmentId=…` query to `/Shifts`
- Selected pie renders with a gold outline + `vellum` background
- Clicking the selected pie submits with empty `departmentId`, clearing the filter
- Period (`?period=…`) and date-range (`?fromDate=…&toDate=…`) filters are preserved across pie clicks

### US-Pie.3: Promoted Sub-Team Pies Sit Next to Their Parent
**As a** volunteer
**I want to** see a promoted sub-team's pie immediately after its parent's pie
**So that** the relationship between sub-team and parent is visually clear

**Acceptance Criteria:**
- A team with `IsPromotedToDirectory = true` and `ParentTeamId != null` renders its own pie
- The promoted sub-team's pie appears immediately after its parent's pie in the row (not alphabetically by its own name)
- Multiple promoted sub-teams under the same parent sort alphabetically among themselves after the parent
- A non-promoted sub-team does **not** render a pie; its shift hours roll up into the parent's pie

## Data Model

No new tables. Derived from existing `event_settings`, `rotas`, `shifts`, `shift_signups`, and `teams`.

### Pie eligibility

A team is **pie-eligible** when `Team.IsInDirectory` is true:

```
IsInDirectory = (ParentTeamId == null) OR IsPromotedToDirectory
```

— top-level departments always; sub-teams only when promoted.

### Bucket assignment

Each visible rota's shifts contribute hours to a single pie:

- If the rota's owning team is pie-eligible → its own pie.
- Otherwise (a non-promoted sub-team) → the parent team's pie (if the parent is pie-eligible).
- Otherwise → dropped (no bucket).

### Hour math

For each non-`AdminOnly` shift on a visible (`IsVisibleToVolunteers = true`) rota:

```
hours       = shift.IsAllDay ? (AllDayWindowEnd − AllDayWindowStart) : shift.Duration.TotalHours
requested  += hours × shift.MaxVolunteers
filled     += hours × min(confirmed signups on shift, shift.MaxVolunteers)
```

- `AdminOnly` shifts and rotas with `IsVisibleToVolunteers = false` are excluded entirely.
- Confirmed-only — signups in `Pending`, `Refused`, `Bailed`, `Cancelled`, `NoShow` do not count.
- The cap (`min(confirmed, MaxVolunteers)`) prevents > 100 % when over-enrolled.

### Date range filter

When the page has a `fromDate` / `toDate` filter applied, the same window is applied per-shift via `EventSettings.GateOpeningDate + DayOffset`. The percentage matches what the user is filtering to.

### Display ordering

Service returns rows in natural `TeamName` order. The view-model assembly layer (`ShiftBrowsePageBuilder.OrderPiesGroupedByParent`) applies the sub-team-next-to-parent grouping for display — keeping display ordering out of the service per `memory/architecture/display-sort-in-controllers`.

## Routes

No new routes. Pies submit GET to the existing `/Shifts` action with the same `departmentId` parameter the dropdown uses.

## Authorization

No new gates. Pies inherit `/Shifts`'s existing `[Authorize]` + `CanBrowseShifts` check. A pie's filtered view shows the same shifts a non-pie filter (the dropdown) would.

## Related Features

- [Shift Management](shift-management.md) — pies derive from rotas/shifts/signups owned by Shift Management.
- [Teams](../teams/teams.md) — `IsPromotedToDirectory` is set in Team admin.
- Section invariants: [`docs/sections/Shifts.md`](../../sections/Shifts.md#department-coverage-pies).

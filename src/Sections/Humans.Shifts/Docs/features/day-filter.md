<!-- freshness:triggers
  src/Sections/Humans.Shifts/Controllers/ShiftsController.cs
  src/Sections/Humans.Shifts/Models/ShiftBrowsePageBuilder.cs
  src/Sections/Humans.Shifts/Models/ShiftViewModels.cs
  src/Sections/Humans.Shifts/Views/Shifts/Index.cshtml
-->
<!-- freshness:flag-on-change
  Day-filter query param name/format, the openings-first ranking rule, or how the dropdown labels each day may have changed.
-->

# Day Filter (issue #889)

## Business Context

Volunteers browsing `/Shifts` had no way to jump straight to a specific calendar day — they had to scroll the full department/rota list to find out whether the day they're free is already fully booked. A community report (nobodies-collective/Humans#889) asked for a day dropdown that filters the shift list and surfaces open shifts first.

## User Stories

### US-Day.1: Filter the Browse Page to One Day
**As a** volunteer
**I want to** pick a single day from a dropdown
**So that** I only see shifts on the day I'm actually free

**Acceptance Criteria:**
- Dropdown defaults to "All Days" — unfiltered, same shift list as before this feature
- Dropdown lists every calendar day that has at least one browsable shift, labeled `<phase> <weekday day month>` (e.g. "Pre-event Fri Jun 26", "Event Tuesday Jul 8") — phase is the build sub-period name (First crew / Set-up week / Pre-event week / Finishing weekend) or "Event" / "Strike"
- Selecting a day auto-submits the filter form and round-trips via the `?day=yyyy-MM-dd` query parameter, so the selection survives a page refresh or bookmark
- A day filter composes with the existing department and tag filters, but overrides the phase-card and date-range filters (mutually exclusive controls)

### US-Day.2: Openings-First Ordering When a Day Is Selected
**As a** volunteer looking at one day
**I want to** see the rotas with the most open slots first
**So that** I don't waste time opening rotas that are already full

**Acceptance Criteria:**
- With a day selected, the flat ranked list (normally urgency-sorted) is instead ordered by total remaining slots descending
- Fully-booked rotas (zero remaining slots) sink to the bottom of the list rather than being hidden
- The urgency/department sort toggle is hidden while a day filter is active — the day filter owns the ordering

## Data Model

No new tables or fields. Derived entirely from the existing `IShiftManagementService.GetBrowseShiftsAsync` browse query (`UrgentShiftInfo.Date` / `.Period`, `Shift.DayOffset`) and `BuildSubPeriodClassifier` (already used by the shift dashboard's sub-period filter).

### Day option list

`ShiftBrowsePageBuilder.BuildDayOptionsAsync` runs one extra unfiltered `GetBrowseShiftsAsync` call (same visibility flags as the main query, no department/date/day filter) and groups the results by calendar date, so the dropdown's option list stays stable regardless of which other filters are currently applied.

### Ranking

`ShiftBrowsePageBuilder.RankRotas` orders the flat rota list by `Shifts.Sum(s => s.RemainingSlots)` descending when a day filter is active, replacing the normal `MaxUrgencyScore` ordering (`memory/architecture/display-sort-in-controllers` — display ordering lives in the view-model assembly layer, not the service).

## Routes

No new routes. `GET /Shifts` gains one optional query parameter, `day` (ISO date, same format as the existing `fromDate`/`toDate` parameters).

## Authorization

No new gates. The day filter inherits `/Shifts`'s existing `[Authorize]` check and the page's existing admin-only/hidden-rota visibility rules (`GetBrowseShiftsAsync`'s `IncludeAdminOnly`/`IncludeHidden` flags, unchanged).

## Related Features

- [Shift Management](shift-management.md) — the day filter reads from the same browse query.
- [Department Coverage Pies](department-coverage-pies.md) — sibling filter on the same page; the day filter's date window also narrows the pies.
- Section invariants: [`src/Sections/Humans.Shifts/Docs/Shifts.md`](../Shifts.md).

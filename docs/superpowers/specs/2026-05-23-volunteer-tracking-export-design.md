# Volunteer Tracking Export

**Date:** 2026-05-23
**Branch:** TBD
**Section:** Shifts → Volunteer Tracking

## Overview

Add an "Export volunteer grid" XLSX download to `/Shifts/Dashboard/VolunteerTracking`. The export reproduces the visual story the team has historically maintained in a hand-edited spreadsheet (see reference PDF, `vols numbers 2025 vs 2026 - First Crew (1).pdf`): a grid of humans × days, cells colored by the team each human worked that day, white cells marking arrival days, and a totals row at the bottom that gives the on-site headcount for meal planning.

The filter surface mirrors the existing `/Shifts/Dashboard` (department, explicit date range, preset period) so coordinators reach for the same controls in both places.

## Filters

Identical to `ShiftDashboardController`:

| Param | Type | Notes |
|---|---|---|
| `departmentId` | `Guid?` | Optional. When set, all rules apply only to that team's confirmed shifts (see §Department-scoped behavior). |
| `startDate` | `string?` (ISO date) | Explicit range start. |
| `endDate` | `string?` (ISO date) | Explicit range end. |
| `period` | `ShiftPeriod?` | Preset (Build, Event, etc.). |

`period` and explicit dates are mutually exclusive — resolved via the same `ResolveActiveDateRange` mutex the dashboard uses (see Shifts.md L237). The export action calls the same resolver.

## Roster Selection

A human appears in the export iff they have **≥1 confirmed shift signup** with a start date intersecting the active range. No minimum shift duration applies — any confirmed signup counts.

Pending, cancelled, declined signups are ignored. The dashboard's "Unbooked cohort" section is not exported.

## Row Grouping

When `departmentId` is unset:
- Each human's **primary team** = the team where they have the most confirmed shift hours in the range. Tie-break: team name ascending, then team Id ordering.
- Department groups are ordered by total confirmed hours descending (biggest crew first); tie-break: team name ascending.
- Each group begins with a **banner row**: a single merged cell across all day columns, background = team palette color, text = `"{TEAM NAME} ({n} humans)"`, white bold text.
- Within a group, humans are ordered alphabetically by playa name (case-insensitive).

When `departmentId` is set:
- Only the filtered department's group renders. No banner needed (single team), but the methodology block still names the team.
- Humans alphabetical by playa name.

## Cell Rules

Build map `(userId, date) → List<(teamId, hoursThatDay)>` from confirmed shifts. Hours = clipped overlap with the day in **event-local time** (NodaTime; the event's time zone lives on `EventSettings`).

For each `(human row, date column)`:

1. If `date < humanArrivalDay` → empty (no fill, no text).
2. If `date == humanArrivalDay` → white fill, text = playa name.
3. Otherwise, if the map has an entry → fill = palette color of the team with the most hours that day (max-hours wins; tie-break: team name asc). Text = playa name.
4. Otherwise → empty.

**Arrival day** = day before the human's first confirmed shift in scope (the filtered department's shifts, when filtered; otherwise all departments). If arrival day falls outside `[start, end]`, no white cell is shown for that human; their first in-range cell colors normally.

**Multi-team day:** single color per cell (max-hours team). No stripes, no compounds.

## Totals Row

After the last group, a totals row:

| Cell | Content |
|---|---|
| Label cell (col A or merged label area) | `"Total humans on-site"`, bold, no fill. |
| Each day column | Count of distinct humans whose arrival day ≤ that date AND who have ≥1 confirmed shift on that day in scope. Bold, no fill. |

This is the feeding count. Pending signups never contribute.

## Department-scoped Behavior (when `departmentId` is set)

When the user picks a single department, all rules apply **relative to that department**:

- Roster = humans with ≥1 confirmed shift in that department.
- "First confirmed shift" (for arrival day) = first one in that department.
- Cell colors fill only for days they worked that department; other days appear empty.
- Totals row counts presence in that department only.

This gives a clean single-team grid useful when a department lead wants their own crew sheet.

## Names

Always `UserInfo.BurnerName` (which falls back to `User.DisplayName` when `Profile.BurnerName` is empty). No real names, no email addresses. See `memory/architecture/burnername-is-the-display-name.md`.

## Team Palette

Deterministic, in-exporter only. No `Team.HexColor` field, no migration.

```
paletteIndex = stableHash(team.Id.ToString()) % palette.Length
```

`palette` is a fixed list of ~20 distinct fills with sufficient contrast for white bold text. Hash is stable across runs (`SHA256` of the Id string, take first 4 bytes). Same team → same color across exports.

If two teams collide on the same color in a given export, it's accepted — the row grouping by team name keeps them visually distinct.

## File Output

- Library: `ClosedXML` (BSD-3-Clause, MIT-compatible, no Excel install required).
- Filename: `volunteer-tracking-{startISO}-to-{endISO}.xlsx` (e.g. `volunteer-tracking-2026-06-12-to-2026-06-20.xlsx`). If filtered to one department, prefix the team slug: `volunteer-tracking-cantina-2026-06-12-to-2026-06-20.xlsx`.
- Content type: `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`.
- Single sheet named `Volunteers`.

### Sheet layout

| Row | Content |
|---|---|
| 1 | `"Volunteer tracking export — generated {YYYY-MM-DD HH:mm UTC} by {actor playa name}"` |
| 2 | Filter summary: `"Department: {name or 'All'} · Range: {start} → {end} ({period or 'custom'})"` |
| 3 | Methodology paragraph (see §Methodology blurb), wrapped across merged cells, light italic. |
| 4 | (blank spacer) |
| 5 | Day-of-week header (Mon, Tue, …) |
| 6 | Date header (`dd/MM/yyyy`) |
| 7+ | First department banner row, then its humans, then next banner + humans, …, then totals row. |

Day columns start at column B (column A reserved for row labels / blank). Freeze panes at row 7, column B.

### Methodology blurb (same wording on page and in XLSX row 3)

```
Rows = humans with ≥1 confirmed shift in range. Cell color = the team
they worked most hours that day. White cell = day before their first
confirmed shift (arrival day). Totals row = humans on-site that day
(used for meal counts). Names shown are playa names.
```

## UI

In `Views/VolunteerTracking/Index.cshtml`, above the existing heatmap, add an Export card:

```
Department: [select]   Period: [select]
From: [date]   To: [date]                [ Download XLSX ]
Methodology: <same blurb as above>
```

- Form is a `<form method="get" action="@Url.Action("ExportXlsx")">` so filter values land in the query string.
- Field names match the controller param names exactly (`departmentId`, `startDate`, `endDate`, `period`).
- `period` picker mirrors `ShiftDashboardController`'s preset list; selecting a preset clears the date fields client-side (mutex matches server-side `ResolveActiveDateRange`).
- Department list source: `IShiftManagementService.GetDepartmentsWithRotasAsync(eventSettings.Id)`.
- Default load: `period = Event`, no department.

The card is collapsible (`<details>` / `<summary>`) so it doesn't dominate the page for read-only viewers.

## Authorization

`[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]` on the action — same policy as the dashboard read. No mutations, no new policy required.

## Error Handling

- No auth → standard 403.
- Empty roster (no confirmed humans in scope) → still return a valid XLSX with the metadata block (rows 1–3), date headers, and a single row `"No confirmed humans in this range."` Bytes back to the user — cheaper than a flash-message round-trip.
- `endDate < startDate` → form-side validation (HTML5 `min`/`max`); controller also model-state-errors and re-renders the page if hit directly.
- Empty range when `period` resolves to nothing meaningful → same as empty roster.

## Architecture

Clean Architecture layers touched:

**Domain:** none.

**Application:**

- New interface `IVolunteerTrackingExportService` (in `Humans.Application/Interfaces/Shifts/`):
  ```csharp
  Task<VolunteerExportModel> BuildAsync(VolunteerExportRequest request, CancellationToken ct);
  ```
- New DTOs (`Humans.Application/DTOs/VolunteerTrackingExport/`):
  - `VolunteerExportRequest` — filter params + resolved date range + event settings id.
  - `VolunteerExportModel` — { Methodology, FilterSummary, GeneratedAtUtc, GeneratedByName, Days: List<DateOnly>, Groups: List<DepartmentGroup>, Totals: List<int> }.
  - `DepartmentGroup` — { TeamId, TeamName, TeamPaletteColor, Humans: List<HumanRow> }.
  - `HumanRow` — { UserId, PlayaName, Cells: List<CellState> }.
  - `CellState` (record) — { Kind: enum(Empty, Arrival, Worked), TeamId?, TeamPaletteColor? }.
- New impl `VolunteerTrackingExportService` in `Humans.Application/Services/Shifts/`. Constructs `VolunteerExportModel` from confirmed shift signups, user lookups, and the deterministic palette. Encapsulates all the rules from §Cell Rules and §Department-scoped Behavior.

**Infrastructure:**

- New repository method on `IVolunteerTrackingRepository` (or a sibling read-only repo):
  ```csharp
  Task<IReadOnlyList<ConfirmedShiftRow>> GetConfirmedShiftsInRangeAsync(
      Guid eventSettingsId,
      DateOnly start,
      DateOnly end,
      Guid? departmentId,
      CancellationToken ct);
  ```
  `ConfirmedShiftRow` = { UserId, TeamId, StartsAtUtc, EndsAtUtc }. Single EF query, projected to a read DTO — no entity tracking.

**Web:**

- New action on existing `VolunteerTrackingController`:
  ```csharp
  [HttpGet("ExportXlsx")]
  [Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
  public async Task<IActionResult> ExportXlsx(
      Guid? departmentId, string? startDate, string? endDate, ShiftPeriod? period, CancellationToken ct);
  ```
- New builder `Humans.Web/Models/VolunteerTracking/VolunteerTrackingXlsxBuilder.cs` (mirrors `CampCsvExportBuilder` pattern). Pure: takes `VolunteerExportModel`, returns `(byte[] Content, string ContentType, string FileName)`.
- `Views/VolunteerTracking/Index.cshtml`: add the Export card per §UI. Existing heatmap untouched.

**DI:** register `IVolunteerTrackingExportService` and the builder in `Humans.Web/Program.cs` alongside other shifts services.

## Testing

TDD where it pays off (the service has multiple regression-prone rules).

### Service tests (`tests/Humans.Application.UnitTests/Shifts/VolunteerTrackingExportServiceTests.cs`)

| Scenario | Asserts |
|---|---|
| Single human, one team, three consecutive confirmed shifts | Row exists under that team. Arrival cell (day before first shift) is white. Three following cells colored to that team. Totals row counts 1 across those days. |
| Human with shifts spanning two teams across the range | Grouped under team with most total hours. Per-day cells colored by that day's max-hours team. |
| Human with two confirmed shifts on the same day (Team A 3h, Team B 5h) | That day's cell = Team B color. |
| Arrival day falls outside `[start, end]` | No white cell shown. First in-range cell colors normally. Human still appears in their group. |
| Pending-only signups | Human excluded. |
| Cancelled signup on a day that also has a confirmed signup | Confirmed wins; cancelled ignored. |
| `departmentId` filter set | Roster = humans with confirmed shift in that dept. Cells only fill for that dept's days (other-dept work appears empty). Arrival day = day before first shift IN THAT DEPT. |
| Empty range / no confirmed humans | Model has zero groups, zero rows, totals row of zeros, methodology block populated. |
| Multi-day shift (starts day N, ends day N+1) | Hours clipped to each day in event-local time; both days receive the team's hours contribution. |
| Two humans, same primary team, same hour totals | Stable alphabetical order by playa name within the group. |
| Department ordering | When unfiltered, groups sorted by total team hours desc; tie-break team name asc. |

### Builder tests (`tests/Humans.Web.UnitTests/Models/VolunteerTracking/VolunteerTrackingXlsxBuilderTests.cs`)

- Round-trip: build XLSX from a fixture `VolunteerExportModel`, re-open with `ClosedXML`, assert: rows 1–3 contain expected metadata; banner rows in the correct positions with the expected fills; human rows have the expected cell texts and fills; totals row at the expected position with bold styling.
- Filename derivation: department-filtered → contains team slug; otherwise → date-only.

### Controller test (`tests/Humans.Web.UnitTests/Controllers/VolunteerTrackingControllerTests.cs`)

- `ExportXlsx` unauthenticated → 403.
- Authenticated with `period=Event`, no department → `FileContentResult`, content type matches, filename pattern matches.
- Both `period` and `startDate` set → mutex applies (resolver picks period).

No infrastructure-layer unit tests; the repository method is a thin EF query exercised end-to-end by the service tests via the existing in-memory shifts test fixture.

## Out of Scope

- Editing team palette colors. If the user wants per-team configurable colors later, add `Team.HexColor` then; the exporter swaps its palette source.
- PDF or print rendering. XLSX is the only output for v1.
- Including "unbooked" or "pending" humans.
- Including coordinator / role metadata beyond the playa name.
- Localization of the methodology blurb. English only for v1 (the dashboard itself is English).
- Background generation / email delivery. Synchronous response is fine at this dataset size (~500 humans × ~30 days = trivial).

## Open Questions

None blocking.

## Change Enforcement

- **If you add a new ShiftPeriod preset value** → make sure it's accepted by `ExportXlsx`'s `period` param (no new switch — relies on `ResolveActiveDateRange`).
- **If the Team palette is changed** → bump nothing; deterministic hash means the same team still maps to the same palette index unless the palette length changes. Document the chosen palette in the exporter source comments.
- **If `BurnerName` resolution changes** → exporter inherits it via `UserInfo.BurnerName`; no further action.

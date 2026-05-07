# Volunteer Tracking — Design

**Status:** Draft, pending implementation
**Date:** 2026-05-07
**Branch:** `feat/volunteer-tracking`

## Problem

Volunteer Coordinators (VC) have no view that surfaces volunteers who started showing up in the build period and then quietly stopped — the "arrived June 20, did 5 days, dropped off" pattern. They also have no way to mark a volunteer as having migrated to their barrio for camp set-up, after which the volunteer is no longer expected on noorg shifts.

## Goal

A new sub-page of the Shift Dashboard that:

1. Identifies volunteers with **gaps** in their build-period schedule (empty days inside their active volunteering window).
2. Lets a Volunteer Coordinator or Admin mark a volunteer as **"went to camp set-up"** with an effective date, after which empty days for that volunteer are not flagged as gaps.
3. Reads cleanly, supports day-by-day inspection, and stays out of the existing dashboard's already-dense surface.

## Non-Goals

- Auto-cancelling, auto-bailing, or otherwise mutating existing `ShiftSignup` rows when a volunteer is marked for camp set-up. The flag is informational; signup state changes still go through the normal Shifts workflows.
- Tracking gaps in the Event or Strike periods. This iteration is build-period-only per the original problem framing.
- Surfacing volunteers who declared participation but haven't signed up for any shift yet.
- Detecting partial-day patterns (e.g. signed up for morning, missed afternoon). The unit of analysis is a day.

## User-facing scope

- **Read access:** existing `ShiftDashboardAccess` policy — admits Admin, NoInfoAdmin, VolunteerCoordinator. No new read policy; the page is just another `ShiftDashboardAccess` page (matches `/ShiftDashboard` itself).
- **Write access** (mark/clear camp set-up date): new `VolunteerTrackingWrite` policy in `AuthorizationPolicyExtensions`, defined as `policy.RequireRole(RoleNames.Admin, RoleNames.VolunteerCoordinator)` — a pure role-list policy, no custom requirement/handler. **Excluded explicitly:** NoInfoAdmin (can view but not coordinate), department coordinators and sub-team managers (this page is a cross-event volunteer-coordination tool, not a per-team management surface; the existing `ShiftDepartmentManager` policy is intentionally not used).

## Definitions

- **Day offset → calendar date conversion** (matches `EventSettings.GateOpeningDate` semantics — gate-open day is day-offset 0):
  - `DateFor(offset) = es.GateOpeningDate.PlusDays(offset)`
  - `OffsetFor(date) = Period.Between(es.GateOpeningDate, date, PeriodUnits.Days).Days`
- **Build period day-offset range:** `[BuildStartOffset, 0)` (exclusive of 0, since offset 0 is the start of the Event period). Matches the `< 0` upper bound used by `BuildSubPeriodClassifier` ranges in `Shifts.md`.
- **`todayOffset`:** `OffsetFor(today)` where `today = clock.GetCurrentInstant().InZone(es.TimeZoneId).Date`.
- **Eligible signup state:** Confirmed or Pending. Refused, Bailed, Cancelled, NoShow are excluded from the per-day "has signup" check. (Rationale: a Bail or NoShow leaves the day visually empty in the heatmap because no actual coverage occurred — that's the gap the coordinator is looking at.)
- **Active window** for a volunteer:
  - `firstSignupDay` = the earliest Build-period day-offset on which they have *any* eligible signup.
  - `lastExpectedDay` (exclusive upper bound) `= min(setupOffset (if set), 0, todayOffset + 1)` where `setupOffset = OffsetFor(BarrioSetupStartDate)` if set, else +∞.
  - Active window = `[firstSignupDay, lastExpectedDay)`. The window includes today (so a confirmed/pending today renders correctly) but `Gap` flagging requires the day to be **strictly before** today (see cell-state below).
- **Volunteer eligibility for the page:** A user is listed iff they have ≥1 eligible Build-period signup *and* their `EventParticipation` for the year is not `NotAttending`. Volunteers whose only Build-period signups are in non-eligible states (all Bailed/Refused/Cancelled/NoShow) are **excluded** — the page is keyed off Confirmed/Pending intent. (Surfacing volunteers who bailed every signup is a follow-up; out of scope here.)
- **Cell states** (for a day inside the active window):
  - has Confirmed signup → `Confirmed`
  - else has Pending signup → `Pending`
  - else day < `todayOffset` → `Gap`
  - else → `Expected` (not a gap; rendered grey)
  Days outside the active window are `Outside` (rendered grey, never a gap). Days at offset ≥ `setupOffset` (if set) override to `CampSetup` (blue).
- **Gap count** = number of `Gap` cells in the active window.

## Architecture & Data Model

### New entity: `VolunteerBuildStatus`

Owned by Shifts.

**Source location:** `src/Humans.Domain/Entities/VolunteerBuildStatus.cs`.
**EF configuration:** `src/Humans.Infrastructure/Data/Configurations/Shifts/VolunteerBuildStatusConfiguration.cs`.
**DbSet:** `HumansDbContext.VolunteerBuildStatuses`.

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `UserId` | `Guid` | FK to `users` (typed-FK form, no nav, per project rules) |
| `EventSettingsId` | `Guid` | FK to `event_settings` |
| `BarrioSetupStartDate` | `LocalDate?` | Calendar date the volunteer left for barrio set-up. Null = not yet on set-up. |
| `Notes` | `string?` (≤500, server-side `[StringLength(500)]`) | Optional free-text from the coordinator who set the status. |
| `SetByUserId` | `Guid?` | Coordinator who last modified the row. |
| `SetAt` | `Instant?` | When the row was last modified. |

- **Table:** `volunteer_build_statuses`.
- **Indices:** PK on `Id`. Unique on `(UserId, EventSettingsId)`.
- **Nav properties:** none (FK only). Display data resolves via `IUserService.GetByIdsAsync`.
- **No concurrency token** (per `memory/architecture/no-concurrency-tokens.md`).
- A row with `BarrioSetupStartDate = null` is treated identically to no row by the gap detector; the row exists only to retain a `Notes` value.

### Service & repository

- `IVolunteerTrackingService` (in `Humans.Application.Services.Shifts`) owns gap-detection and camp-set-up mutations.
- `IVolunteerTrackingRepository` (in `Humans.Application.Interfaces.Repositories`) owns I/O on `volunteer_build_statuses`.
- The service depends on `IShiftSignupRepository` and `IShiftManagementRepository` for signup, shift, rota, and event-settings reads — never on `HumansDbContext` directly.
- The service is auth-free per design rules; authorization lives on the controller.

### `design-rules.md` §8

Add `volunteer_build_statuses` to the Shifts table list in the same commit as the migration. Do not repeat the `shift_tags` / `volunteer_tag_preferences` omission.

### Untouched

`EventParticipation`, `ShiftSignup`, `Rota`, `Shift`, `EventSettings`, `GeneralAvailability`, `VolunteerEventProfile` are all read-only from this feature's perspective. No schema changes to any of them.

## Components & UI

### Controller

`VolunteerTrackingController` (`Humans.Web.Controllers`).

- Class-level: `[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]`.
- `GET /ShiftDashboard/VolunteerTracking` → `Index(hideNoGaps: bool = false, hideCampSetup: bool = false)` — renders the page.
- `POST /ShiftDashboard/VolunteerTracking/SetCampSetup` — sets or updates the row. `[Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]`.
- `POST /ShiftDashboard/VolunteerTracking/ClearCampSetup(userId)` — clears `BarrioSetupStartDate`. `[Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]`.

**`SetCampSetup` form binding (NodaTime `LocalDate` cannot bind directly from form input — the project does not register an MVC `LocalDate` model binder; `Program.cs` only configures NodaTime for JSON serialization and Npgsql):**

```csharp
public sealed class SetCampSetupForm
{
    [Required]
    public Guid UserId { get; set; }

    /// Wire format: "yyyy-MM-dd" (ISO 8601 calendar date).
    [Required]
    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    public string Date { get; set; } = "";

    [StringLength(500)]
    public string? Notes { get; set; }
}
```

The action parses `Date` with `LocalDatePattern.Iso.Parse`, returns a `BadRequest` view-model error on parse failure, and forwards the parsed `LocalDate` to the service. View renders the input as `<input type="date">` whose value is `yyyy-MM-dd`.

**Audit & redirect:** All mutations write an `AuditLogService` entry (new `AuditAction` enum values: `VolunteerCampSetupSet`, `VolunteerCampSetupCleared` — appended to the enum; per the enum's docstring "new values can be appended without migration"). The action returns `RedirectToAction(nameof(Index))` with TempData success/error.

### View & partial

- `Views/VolunteerTracking/Index.cshtml`
  - Breadcrumb: `Shifts › Shift Dashboard › Volunteer Tracking`.
  - Header card: counts of volunteers tracked, with at least one gap, on camp set-up. Filter toggles `Hide volunteers with no gaps` (default off) and `Hide volunteers already on camp set-up` (default off).
  - Main panel renders the `_VolunteerHeatmap` partial.
  - Footer: legend explaining cell colors.
- **`Views/VolunteerTracking/_VolunteerHeatmap.cshtml`** — a Razor partial view (matches the existing dashboard convention: `_CoverageHeatmap.cshtml`, `_DepartmentsTable.cshtml`, etc. are partials, not view components). Pure rendering off `VolunteerHeatmapViewModel` (rows, day columns, cell states).
  - Sticky left column: avatar, name, gap-count badge, set-up date if any.
  - Cells, color-coded:
    - Green (`bg-success`) — Confirmed signup
    - Light green (`bg-success-subtle`) — Pending signup
    - Red (`bg-danger`) — `Gap`
    - Grey (`bg-secondary-subtle`) — `Outside` or `Expected`
    - Blue (`bg-primary`) — `CampSetup`
  - Cell click → popover with date, signup rota name(s), and (write-policy gated) `<input type="date">` defaulted to that day plus a "Mark went to camp set-up from this day" submit button and optional Notes field.
  - **Default sort:** gap count desc, tiebreak by `lastEligibleSignupOffset` asc, then by display name asc. `lastEligibleSignupOffset` = MAX day-offset across the volunteer's Confirmed + Pending Build signups. (Defining the tiebreak deterministically; the page should never re-order on refresh.)
- All strings localized via `Localizer["VolTrack_*"]`. Audit-log display strings for the new `AuditAction` values added to the audit-log resource keys used in `Views/AuditLog/`.
- Add an entry-point card or link on `Views/ShiftDashboard/Index.cshtml` (immediately below the existing top-of-page header block, above the period filter row) gated on `ShiftDashboardAccess` — visible to all viewers of the dashboard.

## Gap-Detection Algorithm

In `VolunteerTrackingService.GetTrackingDataAsync(ct)`:

1. Load the singleton active `EventSettings` via `IShiftManagementRepository.GetActiveEventSettingsAsync()`. If none, return an empty `VolunteerHeatmapViewModel`.
2. Build-period day-offset range = `[es.BuildStartOffset, 0)` (exclusive of 0). Compute `todayOffset = OffsetFor(today)` where `today` is in the event timezone via `clock.GetCurrentInstant().InZone(es.TimeZoneId).Date`.
3. Load eligible Build-period signups via the repository: rows where `Shift.DayOffset ∈ [BuildStartOffset, 0)`, the rota's period ∈ `{Build, All}`, and `ShiftSignup.Status ∈ {Confirmed, Pending}`. Group in-memory by `UserId` → `Dictionary<int, ShiftSignupStatus>` (per-user, day-offset → status). Skip Refused, Bailed, Cancelled, NoShow.
4. Load `VolunteerBuildStatus` rows for the event into a dictionary keyed by `UserId`.
5. **Exclude `NotAttending`:** the plan adds a new method `Task<IReadOnlySet<Guid>> GetNonAttendingUserIdsAsync(int year, CancellationToken ct)` on `IUserService` (returns user ids with `EventParticipation.Status = NotAttending` for the year), implemented in `UserService` against the existing `event_participations` repository read it already does for `TicketQueryService`. Filter out those user ids.
6. For each remaining user with ≥1 eligible Build signup, build a row:
   - `firstSignupDay = MIN(eligibleDayOffsets)`.
   - `lastEligibleSignupOffset = MAX(eligibleDayOffsets)`.
   - `setupOffset = bs.BarrioSetupStartDate.HasValue ? OffsetFor(bs.BarrioSetupStartDate.Value) : (int?) null`.
   - `lastExpectedDay = min(setupOffset ?? int.MaxValue, 0, todayOffset + 1)` (exclusive upper bound on the active window — see Definitions for why `+1`).
   - Active window = `[firstSignupDay, lastExpectedDay)`.
   - For each day-offset `d` in `[BuildStartOffset, 0)`:
     - if `setupOffset.HasValue && d >= setupOffset.Value` → cell `CampSetup`
     - else if `d < firstSignupDay || d >= lastExpectedDay` → cell `Outside`
     - else if has `Confirmed` for `d` → cell `Confirmed`
     - else if has `Pending` for `d` → cell `Pending`
     - else if `d < todayOffset` → cell `Gap`
     - else → cell `Expected`
   - `gapCount = count of Gap cells`.
7. Resolve user display data (name, avatar, slug) via `IUserService.GetByIdsAsync`.
8. Sort rows: gap count desc, then `lastEligibleSignupOffset` asc, then display name asc.

The repository layer scopes the query to the active event's Build-period rotas to keep the result set tight; in-memory grouping is fine at the project's `~500 users` scale (per `CLAUDE.md` "Prefer in-memory caching over query optimization").

## Edge Cases & Error Handling

- **No active event** → page renders an empty state: "No active event."
- **Volunteer declared `NotAttending`** → excluded from list. (They are not a gap because they explicitly opted out.)
- **Volunteer has no eligible Build-period signups** → excluded. Volunteers whose only Build signups are Refused/Bailed/Cancelled/NoShow are also excluded (out of scope; future iteration).
- **`BarrioSetupStartDate` < volunteer's `firstSignupDay`** → reject at controller-level validation: "Set-up date must be on or after the volunteer's first build signup."
- **`BarrioSetupStartDate` at offset ≥ 0** (i.e. on or after gate-open day) → reject: "Set-up date must be inside the build period (before gate-open day)."
- **`BarrioSetupStartDate` parses but is not a valid `LocalDate`** → return BadRequest with a localized error.
- **Concurrent set/clear** → repository upsert by `(UserId, EventSettingsId)` (insert or update single row in one transaction). No concurrency token (per project rule).
- **Clearing the set-up date** → nulls `BarrioSetupStartDate` and `Notes`, leaves the row in place to preserve audit lineage via `SetByUserId` / `SetAt`. The next algorithm run treats the user as never marked.
- **Volunteer signups all fall on/after `setupOffset`** → `firstSignupDay >= setupOffset`, so `lastExpectedDay <= firstSignupDay` and the active window is empty. The row renders with all `CampSetup` cells from set-up onward and no gaps. Intentional.
- **Set-up date moved earlier** → days that were previously `CampSetup` may become `Gap` or `Confirmed` etc.; recompute handles it naturally on the next page load.
- **Audit log:** every `SetCampSetup` and `ClearCampSetup` writes one entry. Failures in the audit write log a warning but do not roll back the status change (audit is best-effort, matching the existing `ShiftAssigned` pattern in `Shifts.md`).

## Localization

All user-facing strings under `VolTrack_*` keys in the existing resource files. Color legend, cell tooltips, action labels, validation errors, audit-log labels, page title, breadcrumb segments. Use the same languages as the existing `ShiftDash_*` keys.

## Testing

### Unit tests — `tests/Humans.Application.Tests/Shifts/VolunteerTrackingServiceTests.cs`

Cover:

- Empty event → empty result.
- Single volunteer, fully covered → zero gaps, all green.
- Single volunteer, mid-window gap → red cell flagged on the empty day.
- Trailing drop-off → multiple consecutive gaps after their last signup.
- Volunteer marked camp set-up partway through → blue cells from the set-up date onward, no gaps after that date.
- `NotAttending` excluded.
- Volunteer with no signups excluded.
- Future empty days inside active window not counted as gaps.
- Pending signup → light-green cell, not a gap.
- Set-up date validation rejections (before first signup, on/after event start).

### Repository tests — `tests/Humans.Infrastructure.Tests/Repositories/VolunteerTrackingRepositoryTests.cs`

- Upsert by `(UserId, EventSettingsId)` on first set.
- Upsert updates `BarrioSetupStartDate` + `SetByUserId` + `SetAt` + `Notes` on second set.
- `ClearCampSetup` nulls `BarrioSetupStartDate` but leaves the row (preserving `Notes` audit).

### Controller tests — `tests/Humans.Web.Tests/Controllers/VolunteerTrackingControllerTests.cs`

- Anonymous → 401/redirect.
- Regular user → 403.
- NoInfoAdmin can `GET Index` (200) but `POST SetCampSetup` returns 403.
- VolunteerCoordinator can `POST SetCampSetup` and `POST ClearCampSetup` (302 to Index with success TempData).
- Admin can do all three.
- Audit log entry written on each successful mutation.

### E2E test — `tests/Humans.E2E.Tests/`

- Sign in as VolunteerCoordinator.
- Navigate Shifts → Shift Dashboard → Volunteer Tracking.
- Identify a seeded volunteer with a known gap (visual: red cell present).
- Click the cell on a date ≥ their first signup day; submit "Mark went to camp set-up".
- Confirm row turns blue from that day onward and the gap badge updates.
- Click "Clear set-up date"; confirm the row reverts.

## Out-of-Scope Follow-ups

- Per-team or per-department filter on the heatmap.
- Notifying coordinators automatically when a volunteer's gap-streak exceeds a threshold.
- Tracking Event-period or Strike-period gaps with the same UI.
- Inferring set-up date automatically from `Team.IsBarrio` membership patterns.

## Acceptance

- [ ] New `volunteer_build_statuses` table created via EF migration.
- [ ] `design-rules.md` §8 lists the new table under Shifts (same commit as the migration).
- [ ] `Shifts.md` section invariant doc mentions the new entity and its read-only relationship to existing Shifts data.
- [ ] `PolicyNames.VolunteerTrackingWrite` constant added; policy registered in `AuthorizationPolicyExtensions` as a pure role-list policy (`Admin`, `VolunteerCoordinator`).
- [ ] `AuditAction.VolunteerCampSetupSet` and `AuditAction.VolunteerCampSetupCleared` appended to the enum, and audit-log display strings added to the audit-log resource keys.
- [ ] `IUserService.GetNonAttendingUserIdsAsync(int year, ct)` added (signature in §Algorithm step 5).
- [ ] All unit, repository, controller, and E2E tests pass.
- [ ] `dotnet build Humans.slnx -v quiet` clean. `dotnet test Humans.slnx -v quiet` clean.
- [ ] Localization resources updated for all `VolTrack_*` keys.

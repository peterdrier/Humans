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
3. Lets volunteers (and VCs/Admins on their behalf) **block out individual days** they're unavailable (doctor appointment, rest day, etc.). Blocked days never count as gaps.
4. Surfaces a separate cohort: volunteers who **declared participation** for the event year and have **filled in availability** but have **no signups** yet — distinct from the "fell off" pattern, this is "expected to volunteer but not booked".
5. Reads cleanly, supports day-by-day inspection, and stays out of the existing dashboard's already-dense surface.

## Non-Goals

- Auto-cancelling, auto-bailing, or otherwise mutating existing `ShiftSignup` rows when a volunteer is marked for camp set-up or blocks a day. Both flags are informational; signup state changes still go through the normal Shifts workflows.
- Tracking gaps in the Event or Strike periods. This iteration is build-period-only per the original problem framing.
- Detecting partial-day patterns (e.g. signed up for morning, missed afternoon). The unit of analysis is a day.
- Surfacing volunteers who declared participation but have **neither** signups **nor** availability filled in. Without either signal we have nothing to compare against.

## User-facing scope

- **Read access (tracking page):** existing `ShiftDashboardAccess` policy — admits Admin, NoInfoAdmin, VolunteerCoordinator. No new read policy; the page is just another `ShiftDashboardAccess` page (matches `/ShiftDashboard` itself).
- **Write access — camp set-up date** (mark/clear another user's `BarrioSetupStartDate`): new `VolunteerTrackingWrite` policy in `AuthorizationPolicyExtensions`, defined as `policy.RequireRole(RoleNames.Admin, RoleNames.VolunteerCoordinator)` — a pure role-list policy, no custom requirement/handler.
- **Write access — block another user's day** (from the tracking page): same `VolunteerTrackingWrite` policy. NoInfoAdmin can view but not block; department/sub-team coordinators are intentionally excluded (this is a cross-event coordination tool, not a per-team management surface).
- **Write access — block own days** (from `/Shifts/Mine`): any active human, gated by the existing `ShiftsController` authorization. A volunteer can only modify their own `VolunteerBuildStatus.BlockedDayOffsets` — the controller hard-codes the `UserId` from `ClaimsPrincipal`, no `userId` parameter accepted.

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
- **Main heatmap eligibility:** A user appears in the **main heatmap** iff they have ≥1 eligible Build-period signup *and* their `EventParticipation` for the year is not `NotAttending`. Volunteers whose only Build signups are in non-eligible states (all Bailed/Refused/Cancelled/NoShow) are **excluded** — the page is keyed off Confirmed/Pending intent. (Surfacing volunteers who bailed every signup is a follow-up; out of scope here.)
- **Declared-but-unbooked cohort** (separate section beneath the main heatmap): A user appears here iff
  - their `EventParticipation.Status` for the year is `Ticketed` or `Attended` (i.e. declared participating), **and**
  - they have a `GeneralAvailability` row for the active event with `AvailableDayOffsets` overlapping `[BuildStartOffset, 0)`, **and**
  - they have **zero** eligible Build-period signups.

  These two cohorts are disjoint by construction (the main heatmap requires ≥1 signup; this cohort requires zero).
- **Blocked day:** any day-offset present in `VolunteerBuildStatus.BlockedDayOffsets` for the user/event. Blocked days are never a gap (in either cohort).
- **Cell states** (main heatmap, for a day inside the active window):
  - day-offset ≥ `setupOffset` (if set) → `CampSetup` (blue)
  - else day-offset is in `BlockedDayOffsets` → `Blocked` (yellow)
  - else has Confirmed signup → `Confirmed` (green)
  - else has Pending signup → `Pending` (light green)
  - else day < `todayOffset` → `Gap` (red)
  - else → `Expected` (grey, not a gap)

  Days outside the active window are `Outside` (grey, never a gap). The branch order is significant: `CampSetup` and `Blocked` dominate, so even an empty active window renders correctly.
- **Cell states** (declared-but-unbooked cohort heatmap, for a day inside `[BuildStartOffset, 0)`):
  - day-offset ≥ `setupOffset` (if set) → `CampSetup`
  - else day-offset is in `BlockedDayOffsets` → `Blocked`
  - else day-offset is in `AvailableDayOffsets` and day < `todayOffset` → `AvailableUnbooked` (orange) — soft gap
  - else day-offset is in `AvailableDayOffsets` and day ≥ `todayOffset` → `AvailableExpected` (light orange/cyan) — informational, not a gap
  - else → `NotAvailable` (grey)
- **Gap count (main heatmap)** = number of `Gap` cells in the active window.
- **Unbooked count (declared-but-unbooked cohort)** = number of `AvailableUnbooked` cells.

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
| `BlockedDayOffsets` | `List<int>` (jsonb, default `[]`) | Day offsets the volunteer is unavailable (doctor, rest day, etc.). Always inside `[BuildStartOffset, 0)`. Stored sorted, deduped. Pattern matches `GeneralAvailability.AvailableDayOffsets`. |
| `Notes` | `string?` (≤500, server-side `[StringLength(500)]`) | Optional free-text from the coordinator who set/cleared the camp set-up date. |
| `SetByUserId` | `Guid?` | Coordinator who last modified `BarrioSetupStartDate` (only — block edits do not touch this field; see audit trail below). |
| `SetAt` | `Instant?` | When `BarrioSetupStartDate` was last modified. |

- **Table:** `volunteer_build_statuses`.
- **Indices:** PK on `Id`. Unique on `(UserId, EventSettingsId)`.
- **Nav properties:** none (FK only). Display data resolves via `IUserService.GetByIdsAsync`.
- **No concurrency token** (per `memory/architecture/no-concurrency-tokens.md`).
- A row with `BarrioSetupStartDate = null` and empty `BlockedDayOffsets` is functionally equivalent to no row. The repository may still leave the row in place to preserve audit lineage.
- **`BlockedDayOffsets` audit:** per-edit `AuditLogService` entries are written by the controllers; the entity itself does not store who-blocked-what (would bloat the row). Audit log is the canonical history.

### Service & repository

- `IVolunteerTrackingService` (in `Humans.Application.Services.Shifts`) owns gap-detection and camp-set-up mutations.
- `IVolunteerTrackingRepository` (in `Humans.Application.Interfaces.Repositories`) owns I/O on `volunteer_build_statuses`.
- The service depends on `IShiftSignupRepository` and `IShiftManagementRepository` for signup, shift, rota, and event-settings reads — never on `HumansDbContext` directly.
- The service is auth-free per design rules; authorization lives on the controller.

### `design-rules.md` §8

Add `volunteer_build_statuses` to the Shifts table list in the same commit as the migration. Do not repeat the `shift_tags` / `volunteer_tag_preferences` omission.

### Untouched

`EventParticipation`, `ShiftSignup`, `Rota`, `Shift`, `EventSettings`, `GeneralAvailability`, `VolunteerEventProfile` are all read-only from this feature's perspective. No schema changes to any of them.

**Note on the relationship to `GeneralAvailability`:** the existing `general_availability` table tells us what days a volunteer **is** available; the new `BlockedDayOffsets` field tells us specific days inside their active volunteering window they are explicitly unavailable. The two are not redundant: `GeneralAvailability` is a positive-set declaration filled out once, while blocks happen ad-hoc ("doctor visit Tuesday"). The algorithm reads both, with `BlockedDayOffsets` taking precedence in the cell-state branch order.

## Components & UI

### Tracking-page controller (VC/Admin)

`VolunteerTrackingController` (`Humans.Web.Controllers`).

- Class-level: `[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]`.
- `GET /ShiftDashboard/VolunteerTracking` → `Index(hideNoGaps: bool = false, hideCampSetup: bool = false, hideUnbookedSection: bool = false)` — renders both the main heatmap and the declared-but-unbooked section.
- `POST /ShiftDashboard/VolunteerTracking/SetCampSetup` — sets or updates `BarrioSetupStartDate`. `[Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]`.
- `POST /ShiftDashboard/VolunteerTracking/ClearCampSetup(userId)` — clears `BarrioSetupStartDate`. `[Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]`.
- `POST /ShiftDashboard/VolunteerTracking/SetBlock` — adds or removes a single day offset on another user's `BlockedDayOffsets`. Form: `{ UserId, DayOffset, Block: bool }`. `[Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]`.

### Volunteer self-service controller (any active human)

Extend the existing `ShiftsController` (already has `GET /Shifts/Mine`, `POST /Shifts/Mine/Availability`).

- `POST /Shifts/Mine/BlockedDays` → `SaveBlockedDays(List<int> dayOffsets)`. Accepts the full desired list of blocked offsets for the *current* user (UserId pulled from `ClaimsPrincipal`, never from the form). Replaces the volunteer's `BlockedDayOffsets` for the active event. Validation: every offset ∈ `[BuildStartOffset, 0)`, deduped, sorted. Returns `RedirectToAction(nameof(Mine))` with TempData success/error.

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

**Audit & redirect:** All mutations on the tracking controller write an `AuditLogService` entry. New `AuditAction` enum values appended (per the enum's docstring "new values can be appended without migration"):

- `VolunteerCampSetupSet` — coordinator set/changed `BarrioSetupStartDate`
- `VolunteerCampSetupCleared` — coordinator cleared `BarrioSetupStartDate`
- `VolunteerDayBlocked` — `BlockedDayOffsets` gained an offset (entry includes the offset and target UserId)
- `VolunteerDayUnblocked` — `BlockedDayOffsets` lost an offset
- `VolunteerOwnBlockedDaysSaved` — volunteer self-saved their blocked-days list (entry includes the resulting list)

The actions return `RedirectToAction(nameof(Index))` (tracking controller) or `RedirectToAction(nameof(Mine))` (`ShiftsController`) with TempData success/error.

### Tracking-page view & partials

- `Views/VolunteerTracking/Index.cshtml`
  - Breadcrumb: `Shifts › Shift Dashboard › Volunteer Tracking`.
  - Header card: counts of (a) volunteers in main heatmap, (b) main-heatmap volunteers with ≥1 gap, (c) volunteers on camp set-up, (d) declared-but-unbooked volunteers. Filter toggles: `Hide volunteers with no gaps`, `Hide volunteers already on camp set-up`, `Hide declared-but-unbooked section` (all default off).
  - Top panel renders the `_VolunteerHeatmap` partial (the main "fell off" cohort).
  - Bottom panel, beneath a divider with heading "Declared participating, not booked yet", renders the `_VolunteerUnbookedHeatmap` partial.
  - Footer: legend explaining all cell colors across both panels.
- **`Views/VolunteerTracking/_VolunteerHeatmap.cshtml`** — Razor partial (matches existing dashboard convention: `_CoverageHeatmap.cshtml`, `_DepartmentsTable.cshtml`). Renders the main cohort.
  - Sticky left column: avatar, name, gap-count badge, set-up date if any.
  - Cell color mapping:
    - Green (`bg-success`) — `Confirmed`
    - Light green (`bg-success-subtle`) — `Pending`
    - Red (`bg-danger`) — `Gap`
    - Yellow (`bg-warning-subtle`) — `Blocked`
    - Blue (`bg-primary`) — `CampSetup`
    - Grey (`bg-secondary-subtle`) — `Outside` or `Expected`
  - Cell click → popover with: date, signup rota name(s), and write-policy-gated controls: (a) `<input type="date">` + "Mark went to camp set-up from this day" submit, (b) "Block this day" / "Unblock this day" toggle (single click via `SetBlock` action).
  - **Default sort:** gap count desc, tiebreak by `lastEligibleSignupOffset` asc, then display name asc. `lastEligibleSignupOffset` = MAX day-offset across the volunteer's Confirmed + Pending Build signups.
- **`Views/VolunteerTracking/_VolunteerUnbookedHeatmap.cshtml`** — Razor partial. Renders the declared-but-unbooked cohort.
  - Same column layout as the main heatmap (build-period day offsets).
  - Sticky left column: avatar, name, "available days" count, "unbooked" count badge.
  - Cell color mapping:
    - Orange (`bg-warning`) — `AvailableUnbooked` (soft gap)
    - Light orange / `bg-warning-subtle` — `AvailableExpected`
    - Yellow with diagonal stripe / `bg-warning-subtle` + class — `Blocked` (visually distinct from `AvailableUnbooked`)
    - Blue (`bg-primary`) — `CampSetup`
    - Grey (`bg-secondary-subtle`) — `NotAvailable`
  - Cell click → popover with date, "available" badge if applicable, and write-policy-gated `SetBlock` + camp-set-up controls (same as main).
  - **Default sort:** unbooked count desc, then `firstAvailableDayOffset` asc, then display name asc.
- All strings localized via `Localizer["VolTrack_*"]`. Audit-log display strings for the new `AuditAction` values added to the audit-log resource keys.
- **Entry point** on `Views/ShiftDashboard/Index.cshtml`: card or link immediately below the top-of-page header block, above the period filter row, gated on `ShiftDashboardAccess`.

### Volunteer-facing self-service view

Extend `Views/Shifts/Mine.cshtml`:

- New panel "Days you can't volunteer" beneath the existing availability calendar.
  - Visual: same calendar grid pattern as availability — one cell per day-offset across the build period, click to toggle.
  - Form posts to `POST /Shifts/Mine/BlockedDays` with the full list of selected offsets.
  - Inline help text: "Mark days you can't volunteer (doctor, rest day, etc.). Blocked days won't be flagged as missing on the coordinator dashboard."
  - Visible only when there's an active event with a non-empty build period (matches the availability panel's visibility rule).
- All strings localized via `Localizer["VolTrack_*"]`.

## Gap-Detection Algorithm

### Shared loading

In `VolunteerTrackingService.GetTrackingDataAsync(ct)` (returns both cohorts in one `VolunteerTrackingViewModel`):

1. Load the singleton active `EventSettings` via `IShiftManagementRepository.GetActiveEventSettingsAsync()`. If none, return an empty `VolunteerTrackingViewModel`.
2. Build-period day-offset range = `[es.BuildStartOffset, 0)`. Compute `todayOffset = OffsetFor(today)` where `today` is in the event timezone via `clock.GetCurrentInstant().InZone(es.TimeZoneId).Date`.
3. Load eligible Build-period signups via the repository: rows where `Shift.DayOffset ∈ [BuildStartOffset, 0)`, the rota's period ∈ `{Build, All}`, and `ShiftSignup.Status ∈ {Confirmed, Pending}`. Group in-memory by `UserId` → `Dictionary<int, ShiftSignupStatus>`. Skip Refused, Bailed, Cancelled, NoShow.
4. Load all `VolunteerBuildStatus` rows for the event into a `Dictionary<Guid, VolunteerBuildStatus>` keyed by `UserId`.
5. Load all `GeneralAvailability` rows for the event via the existing `IGeneralAvailabilityRepository.GetByEventAsync(eventSettingsId, ct)` into a `Dictionary<Guid, IReadOnlySet<int>>` of `UserId → AvailableDayOffsets`.
6. Load participations via the existing `IUserService.GetAllParticipationsForYearAsync(es.Year, ct)` (already used by `TicketQueryService` etc.). Project into an in-memory `IReadOnlyDictionary<Guid, ParticipationStatus>`. Use this map both to exclude `NotAttending` and to qualify the declared-but-unbooked cohort by `Ticketed`/`Attended`. **No new `IUserService` method is added.**

### Main heatmap (signups cohort)

7. Filter signed-up users by removing those whose `ParticipationStatus = NotAttending`. For each remaining user with ≥1 eligible signup, build a row:
   - `firstSignupDay = MIN(eligibleDayOffsets)`.
   - `lastEligibleSignupOffset = MAX(eligibleDayOffsets)`.
   - `setupOffset = bs.BarrioSetupStartDate.HasValue ? OffsetFor(bs.BarrioSetupStartDate.Value) : (int?) null`.
   - `blockedSet = bs?.BlockedDayOffsets.ToHashSet() ?? new()`.
   - `lastExpectedDay = min(setupOffset ?? int.MaxValue, 0, todayOffset + 1)`.
   - Active window = `[firstSignupDay, lastExpectedDay)`.
   - For each day-offset `d` in `[BuildStartOffset, 0)`:
     - if `setupOffset.HasValue && d >= setupOffset.Value` → cell `CampSetup`
     - else if `d < firstSignupDay || d >= lastExpectedDay` → cell `Outside`
     - else if `blockedSet.Contains(d)` → cell `Blocked`
     - else if has `Confirmed` for `d` → cell `Confirmed`
     - else if has `Pending` for `d` → cell `Pending`
     - else if `d < todayOffset` → cell `Gap`
     - else → cell `Expected`
   - `gapCount = count of Gap cells`.
8. Sort: `gapCount` desc, then `lastEligibleSignupOffset` asc, then display name asc.

### Declared-but-unbooked cohort

9. From all `EventParticipation` rows with `Status ∈ {Ticketed, Attended}`, take user ids that have **zero** eligible Build signups (i.e. not in the signups dictionary built in step 3).
10. For each such user, require a non-empty `availabilitySet = availability[userId] ∩ [BuildStartOffset, 0)`. Skip users with no in-build availability.
11. Build a row:
    - `firstAvailableDay = MIN(availabilitySet)`.
    - `setupOffset` and `blockedSet` resolved as in step 7.
    - For each day-offset `d` in `[BuildStartOffset, 0)`:
      - if `setupOffset.HasValue && d >= setupOffset.Value` → cell `CampSetup`
      - else if `blockedSet.Contains(d)` → cell `Blocked`
      - else if `availabilitySet.Contains(d) && d < todayOffset` → cell `AvailableUnbooked`
      - else if `availabilitySet.Contains(d) && d >= todayOffset` → cell `AvailableExpected`
      - else → cell `NotAvailable`
    - `unbookedCount = count of AvailableUnbooked cells`.
12. Sort: `unbookedCount` desc, then `firstAvailableDay` asc, then display name asc.

### Display data

13. Resolve user display data (name, avatar, slug) via `IUserService.GetByIdsAsync` for the union of both cohorts' user ids in a single call.

The repository layer scopes the query to the active event's Build-period rotas to keep the result set tight; in-memory grouping is fine at the project's `~500 users` scale (per `CLAUDE.md` "Prefer in-memory caching over query optimization").

## Edge Cases & Error Handling

- **No active event** → both cohorts render empty: "No active event."
- **Volunteer declared `NotAttending`** → excluded from both cohorts.
- **Volunteer has no eligible Build-period signups and no availability** → excluded from both cohorts.
- **Volunteer has signups but no availability** → main cohort only.
- **Volunteer has availability with at least one day inside `[BuildStartOffset, 0)`, no signups, declared participating** → declared-but-unbooked cohort only. Volunteers whose availability lies entirely outside the build window are excluded (no in-build days to render).
- **Volunteer has availability but no signups, but `EventParticipation` not `Ticketed`/`Attended`** → excluded from both cohorts. Without a positive participation signal we don't pull them into either view.

### Camp set-up validation

- **`BarrioSetupStartDate` < volunteer's `firstSignupDay`** → reject: "Set-up date must be on or after the volunteer's first build signup."
- **`BarrioSetupStartDate` at offset ≥ 0** → reject: "Set-up date must be inside the build period (before gate-open day)."
- **`BarrioSetupStartDate` parses but is not a valid `LocalDate`** → return BadRequest with a localized error.

### Block-day validation

- **`DayOffset` outside `[BuildStartOffset, 0)`** → reject: "Blocked days must be inside the build period."
- **Self-block on a day where the volunteer has a Confirmed signup** → allowed; the block does not auto-bail the signup, but the cell renders as `Blocked` (yellow) with the signup details still in the popover. A coordinator-visible inline note explains: "Volunteer blocked this day but has a confirmed signup — coordinator may need to bail manually." (No automatic mutation, per Non-Goals.)
- **Coordinator blocks a day where the volunteer has a Confirmed signup** → same: allowed, no mutation, advisory note rendered.
- **Self-save replaces the entire list** → idempotent. Removing a previously-blocked day is achieved by submitting a list without that offset. The controller diffs old vs new to emit per-offset audit entries (`VolunteerDayBlocked` / `VolunteerDayUnblocked`) plus one summarizing `VolunteerOwnBlockedDaysSaved`.

### Storage & concurrency

- **Concurrent set/clear/block** → repository upsert by `(UserId, EventSettingsId)` (insert or update single row in one transaction). No concurrency token (per project rule). Last-write-wins on the JSONB list; the small chance of two coordinators simultaneously blocking different days for the same volunteer is acceptable at this scale.
- **Clearing the set-up date** → nulls `BarrioSetupStartDate`, `SetByUserId`, `SetAt`, `Notes`. Leaves `BlockedDayOffsets` untouched.
- **Audit log** is best-effort: failures log a warning but do not roll back the status change (matches the existing `ShiftAssigned` pattern in `Shifts.md`).
- **Set-up date moved earlier** → days that were previously `CampSetup` may become `Gap`, `Confirmed`, `Blocked`, etc.; recompute handles it naturally.

## Localization

All user-facing strings under `VolTrack_*` keys in the existing resource files. Color legend, cell tooltips, action labels, validation errors, audit-log labels, page title, breadcrumb segments. Use the same languages as the existing `ShiftDash_*` keys.

## Testing

### Unit tests — `tests/Humans.Application.Tests/Shifts/VolunteerTrackingServiceTests.cs`

Main heatmap:

- Empty event → both cohorts empty.
- Single volunteer, fully covered → zero gaps, all green.
- Single volunteer, mid-window gap → red cell on empty day.
- Trailing drop-off → multiple consecutive gaps after last signup.
- Volunteer marked camp set-up partway through → blue cells from set-up onward, no gaps after.
- `NotAttending` excluded.
- Volunteer with no signups → not in main cohort.
- Future empty days inside active window not counted as gaps.
- Pending signup → light-green cell, not a gap.
- Set-up date validation rejections (before first signup, on/after gate-open).
- **Block on empty day → yellow cell, not a gap.**
- **Block on a day with Confirmed signup → yellow cell, advisory in popover, signup unchanged.**
- **Block + camp set-up overlap → CampSetup wins (branch order).**

Declared-but-unbooked cohort:

- Volunteer with `Ticketed` + availability + zero signups → appears, with `AvailableUnbooked` cells on past available days.
- Same volunteer + a single Confirmed signup → moves to main cohort, disappears from unbooked cohort.
- Volunteer with availability but `NotAttending` → excluded.
- Volunteer with availability but `Status = null` (no row) → excluded (no positive participation signal).
- Block on an available day → `Blocked` cell, not `AvailableUnbooked`.
- Camp set-up date set → cells from set-up date onward render `CampSetup`, no `AvailableUnbooked` after.

### Repository tests — `tests/Humans.Infrastructure.Tests/Repositories/VolunteerTrackingRepositoryTests.cs`

- Upsert by `(UserId, EventSettingsId)` on first set.
- Upsert updates `BarrioSetupStartDate` + `SetByUserId` + `SetAt` + `Notes` on second set.
- `ClearCampSetup` nulls `BarrioSetupStartDate`/`Notes` but preserves the row (and any `BlockedDayOffsets`).
- `SaveBlockedDays` replaces the list (sorted, deduped) without touching `BarrioSetupStartDate`.
- `SetBlock(add)` adds the offset; idempotent if already present.
- `SetBlock(remove)` removes the offset; idempotent if absent.
- Row exists with `BarrioSetupStartDate = null` and empty `BlockedDayOffsets` → algorithm treats user as never marked (regression: empty-row trap).

### Controller tests — `tests/Humans.Web.Tests/Controllers/VolunteerTrackingControllerTests.cs`

- Anonymous → 401/redirect.
- Regular user → 403 on the page.
- NoInfoAdmin: `GET Index` 200; `POST SetCampSetup` / `ClearCampSetup` / `SetBlock` all 403.
- VolunteerCoordinator: all three POSTs succeed (302 to Index with success TempData).
- Admin: all three POSTs succeed.
- Audit log entry written on each successful mutation (including the per-offset `VolunteerDayBlocked` / `VolunteerDayUnblocked`).
- `SetBlock` rejects offset outside `[BuildStartOffset, 0)` with BadRequest.

### Controller tests — `tests/Humans.Web.Tests/Controllers/ShiftsControllerTests.cs` (additions for `SaveBlockedDays`)

- Anonymous → redirect to login.
- Regular volunteer can `POST /Shifts/Mine/BlockedDays` (302 to Mine with success TempData).
- Submitted offsets are validated, deduped, and sorted before persistence.
- Form parameter `userId` is **ignored if present** — the action always reads `UserId` from `ClaimsPrincipal`. (Regression test: passing another user's id must not modify their row.)
- Bulk save emits one `VolunteerOwnBlockedDaysSaved` audit entry plus per-diff `VolunteerDayBlocked` / `VolunteerDayUnblocked` entries.

### E2E tests — `tests/Humans.E2E.Tests/`

VC flow:

- Sign in as VolunteerCoordinator.
- Navigate Shifts → Shift Dashboard → Volunteer Tracking.
- Identify a seeded volunteer with a known gap (visual: red cell present).
- Click the cell; submit "Mark went to camp set-up". Confirm row turns blue from that day onward and gap badge updates.
- Click "Clear set-up date"; confirm row reverts.
- Click an empty-window cell; click "Block this day". Confirm cell turns yellow and gap count drops by 1.
- Click the same cell; click "Unblock this day". Confirm cell reverts to red.
- Scroll to the "Declared participating, not booked yet" section; confirm a seeded ticketed-with-availability volunteer is listed with orange `AvailableUnbooked` cells.

Volunteer self-service flow:

- Sign in as a regular volunteer with build-period signups.
- Navigate to `/Shifts/Mine`; locate the new "Days you can't volunteer" panel.
- Toggle one or two day offsets; submit. Confirm success TempData and that selections persist on reload.
- Sign back in as VolunteerCoordinator; navigate to tracking page; confirm those days render yellow on the volunteer's row.

## Out-of-Scope Follow-ups

- Per-team or per-department filter on the heatmap.
- Notifying coordinators automatically when a volunteer's gap-streak exceeds a threshold.
- Tracking Event-period or Strike-period gaps with the same UI.
- Inferring set-up date automatically from `Team.IsBarrio` membership patterns.

## Acceptance

- [ ] New `volunteer_build_statuses` table created via EF migration with `BarrioSetupStartDate`, `BlockedDayOffsets` (jsonb), `Notes`, `SetByUserId`, `SetAt`.
- [ ] `design-rules.md` §8 lists the new table under Shifts (same commit as the migration).
- [ ] `Shifts.md` section invariant doc adds a sub-section for the new entity (parallel to `GeneralAvailability`) describing its read-only relationship to existing Shifts data.
- [ ] `PolicyNames.VolunteerTrackingWrite` constant added; policy registered in `AuthorizationPolicyExtensions` as a pure role-list policy (`Admin`, `VolunteerCoordinator`).
- [ ] `AuditAction` enum gains `VolunteerCampSetupSet`, `VolunteerCampSetupCleared`, `VolunteerDayBlocked`, `VolunteerDayUnblocked`, `VolunteerOwnBlockedDaysSaved`. Audit-log display strings added.
- [ ] Service reuses existing `IUserService.GetAllParticipationsForYearAsync(int year, ct)` and `IGeneralAvailabilityRepository.GetByEventAsync(eventSettingsId, ct)` — no new `IUserService` or repository methods added.
- [ ] `ShiftsController.SaveBlockedDays(List<int> dayOffsets)` added at `POST /Shifts/Mine/BlockedDays`. UI panel added to `Views/Shifts/Mine.cshtml`.
- [ ] `VolunteerTrackingController` actions: `Index`, `SetCampSetup`, `ClearCampSetup`, `SetBlock`.
- [ ] All unit, repository, controller, and E2E tests pass.
- [ ] `dotnet build Humans.slnx -v quiet` clean. `dotnet test Humans.slnx -v quiet` clean.
- [ ] Localization resources updated for all `VolTrack_*` keys.

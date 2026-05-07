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

- **Read access:** Admin, NoInfoAdmin, VolunteerCoordinator (existing `ShiftDashboardAccess` policy).
- **Write access** (mark/clear camp set-up date): Admin, VolunteerCoordinator only — a new `VolunteerTrackingWrite` policy. NoInfoAdmin can view but not change set-up status; this is a coordination workflow, not a moderation one.

## Definitions

- **Active window** for a volunteer: `[firstSignupDay, lastExpectedDay)` where
  - `firstSignupDay` = the earliest day-offset on which they have a Confirmed or Pending Build-period signup.
  - `lastExpectedDay = min(BarrioSetupStartDate→offset (if set), EventStartOffset, today + 1)`.
- **Gap** for a volunteer: a day inside the active window, *strictly before today*, on which they have no signup of any kind. Future empty days inside the active window are *expected*, not gaps — the page is about people who already fell off, not people who haven't signed up yet for tomorrow.
- **Camp set-up day** for a volunteer: `BarrioSetupStartDate` and any later day. These days are blue and never count as gaps.

## Architecture & Data Model

### New entity: `VolunteerBuildStatus`

Owned by Shifts.

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `UserId` | `Guid` | FK to `users` (typed-FK form, no nav per project rules) |
| `EventSettingsId` | `Guid` | FK to `event_settings` |
| `BarrioSetupStartDate` | `LocalDate?` | Calendar date the volunteer left for barrio set-up. Null = not yet on set-up. |
| `Notes` | `string?` (≤500) | Optional free-text from the coordinator who set the status. |
| `SetByUserId` | `Guid?` | Coordinator who last modified the row. |
| `SetAt` | `Instant?` | When the row was last modified. |

- Table: `volunteer_build_statuses`.
- Unique constraint on `(UserId, EventSettingsId)` — at most one row per volunteer per event.
- No row = "no special status" (default). A row with `BarrioSetupStartDate = null` is treated identically to no row by the gap detector; the row exists only to retain a `Notes` value.
- No cross-domain navigation properties on the entity (FK only). Display data resolves through `IUserService.GetByIdsAsync`.
- No concurrency token (per `memory/architecture/no-concurrency-tokens.md`).

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

- Class-level: `[Authorize(Policy = ShiftDashboardAccess)]`.
- `GET /ShiftDashboard/VolunteerTracking` → `Index(includeNoGaps: bool = false, hideCampSetup: bool = false)` — renders the page.
- `POST /ShiftDashboard/VolunteerTracking/SetCampSetup(userId, date, notes?)` — sets or updates the row. `[Authorize(Policy = VolunteerTrackingWrite)]`.
- `POST /ShiftDashboard/VolunteerTracking/ClearCampSetup(userId)` — clears `BarrioSetupStartDate`. `[Authorize(Policy = VolunteerTrackingWrite)]`.
- All mutations write an `AuditLogService` entry (`AuditAction.VolunteerCampSetupSet` and `VolunteerCampSetupCleared` — new enum values) and return a redirect with TempData success/error.

### View & view component

- `Views/VolunteerTracking/Index.cshtml`
  - Breadcrumb: `Shifts › Shift Dashboard › Volunteer Tracking`.
  - Header card: counts of volunteers tracked, with at least one gap, on camp set-up. Filter toggles `Hide volunteers with no gaps` (default off) and `Hide volunteers already on camp set-up`.
  - Main panel renders `_VolunteerHeatmap.cshtml` view component.
  - Footer: legend explaining cell colors.
- `_VolunteerHeatmap.cshtml` view component:
  - Pure rendering off `VolunteerHeatmapViewModel` (rows, day columns, cell states).
  - Sticky left column: avatar, name, gap-count badge, set-up date if any.
  - Cells, color-coded:
    - Green (`bg-success`) — Confirmed signup
    - Light green (`bg-success-subtle`) — Pending signup
    - Red (`bg-danger`) — Gap (inside active window, before today, no signup)
    - Grey (`bg-secondary-subtle`) — Outside active window (before first signup, or future days not yet expected)
    - Blue (`bg-primary`) — On/after `BarrioSetupStartDate`
  - Cell click → popover with date, signup details (rota name(s) if any), and (write-policy gated) "Mark went to camp set-up from this day" button + optional notes.
  - Default sort: gap count desc, tiebreak by last-signup-day asc (earliest drop-offs surface first).
- All strings localized via `Localizer["VolTrack_*"]`.
- Add an entry-point card or link on `/ShiftDashboard` Index gated on `ShiftDashboardAccess`.

## Gap-Detection Algorithm

In `VolunteerTrackingService.GetTrackingDataAsync(eventSettingsId, ct)`:

1. Load active `EventSettings`. If none, return an empty `VolunteerHeatmapViewModel`.
2. Compute build-day-offset range `[BuildStartOffset, EventStartOffset)`. Resolve absolute dates using event timezone.
3. Load Confirmed and Pending `ShiftSignup` rows joined to `Shift.DayOffset` for shifts in any Build-period rota for the event. Group by `UserId` → set of `(dayOffset, status)`. Skip Refused, Bailed, Cancelled, NoShow.
4. Load `VolunteerBuildStatus` rows for the event into a dictionary keyed by `UserId`.
5. Exclude users with `EventParticipation.Status = NotAttending` for the event year (read via `IUserService` projection).
6. For each remaining user with ≥1 Build signup:
   - `firstSignupDay = MIN(dayOffset)`.
   - `setupOffset = BarrioSetupStartDate.HasValue ? OffsetFor(BarrioSetupStartDate) : null`.
   - `lastExpectedDay = MIN(setupOffset ?? +∞, EventStartOffset, todayOffset + 1)`.
   - Active window = `[firstSignupDay, lastExpectedDay)`.
   - For each day in active window:
     - has confirmed signup → cell `Confirmed`
     - else has pending signup → cell `Pending`
     - else day < today → cell `Gap`
     - else → cell `Expected` (rendered grey, not counted as a gap)
   - For each day outside active window: cell `Outside`. For each day ≥ `setupOffset` (if set): cell `CampSetup`.
   - `gapCount = active-window cells flagged Gap`.
7. Resolve user display data (name, avatar) via `IUserService.GetByIdsAsync`.
8. Sort: gap count desc, then last-signup-day asc.

The repository layer scopes the query to the active event's Build-period rotas to keep the result set tight; in-memory grouping is fine at the project's `~500 users` scale (per `CLAUDE.md` "Prefer in-memory caching over query optimization").

## Edge Cases & Error Handling

- **No active event** → page renders an empty state: "No active event."
- **Volunteer declared `NotAttending`** → excluded from list. (They are not a gap because they explicitly opted out.)
- **Volunteer has no Build-period signups** → excluded (per scoping decision).
- **`BarrioSetupStartDate` < volunteer's `firstSignupDay`** → reject at controller validation: "Set-up date must be on or after the volunteer's first build signup."
- **`BarrioSetupStartDate` ≥ event-start date** → reject: "Set-up date must be before the event starts."
- **Concurrent set/clear** → repository upsert by `(UserId, EventSettingsId)` (insert or update single row). No concurrency token required.
- **Clearing the set-up date** → does not retroactively re-flag past days as gaps unless they were genuinely empty inside the original active window. The algorithm recomputes from scratch on each load; it carries no historical state of prior set-up dates.
- **Volunteer has signups but range fully overlaps the set-up window** → all days from set-up onward are blue; the active window collapses if `firstSignupDay >= setupOffset`. The volunteer renders with zero gaps and a single blue stripe. This is intentional: they signed up after going to set-up, which is fine.
- **Set-up date is changed earlier** → some previously blue days may now be red gaps (the volunteer was *not* on set-up on those days). The recompute handles this naturally.

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
- [ ] `design-rules.md` §8 lists the new table.
- [ ] `Shifts.md` section invariant doc mentions the new entity and its read-only relationship to existing Shifts data.
- [ ] `VolunteerTrackingWrite` policy added in `AuthorizationPolicyExtensions`.
- [ ] All unit, repository, controller, and E2E tests pass.
- [ ] `dotnet build Humans.slnx -v quiet` clean. `dotnet test Humans.slnx -v quiet` clean.
- [ ] Localization resources updated for all `VolTrack_*` keys.

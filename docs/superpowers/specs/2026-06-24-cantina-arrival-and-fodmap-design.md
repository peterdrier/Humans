# Design: Cantina arrival-day feeding + remove FODMAP intolerance

Date: 2026-06-24
Status: Approved (brainstorm) — pending spec review
Branch: `feat/cantina-arrival-and-fodmap`

Two small, independent changes bundled in one branch (two commits, possibly two PRs at merge time).

---

## Feature 1 — Remove FODMAP from intolerances

### Problem
"FODMAP" is no longer wanted as a selectable intolerance.

### Design
Intolerance options have a single canonical source: `DietaryOptions.IntoleranceOptions`
(`src/Humans.Domain/Constants/DietaryOptions.cs:38`). Removing the string removes the checkbox,
the cantina rollup row, and validation acceptance everywhere, because all consumers read that list.

Changes:
1. `DietaryOptions.cs:38` — drop `"FODMAP"`:
   `["Lactose", "Gluten", "Histamine", "FODMAP", OtherOption]` → `["Lactose", "Gluten", "Histamine", OtherOption]`.
2. Delete the `Profile_DietaryMedical_Intolerance_FODMAP` entry from all six locale files:
   `SharedResource.resx`, `.es`, `.de`, `.ca`, `.fr`, `.it`.
3. `tests/Humans.Application.Tests/Services/Cantina/CantinaRosterServiceTests.cs:307` — remove the
   `intolerance["FODMAP"].Should().Be(0)` assertion (the key no longer exists in the rollup).

### Existing data — leave inert (decided)
Intolerances persist as a JSON array on `profiles.Intolerances` (not a bitmask), so stored
`"FODMAP"` values are harmless:
- `BuildRollup` only counts canonical labels → FODMAP never appears in cantina rollups.
- The edit view filters unknown values via `IsKnownIntolerance()` → it's dropped on the next profile save.

No migration. No flag. Data self-heals as humans re-save.

---

## Feature 2 — Cantina: feed people the day before their first shift

### Problem
The cantina cohort for a given day is exactly "humans with a shift that day"
(`ShiftRepository.Signups.cs:106`, via `IShiftManagementService.GetOnSiteUserIdsForDayAsync`).
There is no concept of a continuous on-site stay. So a human whose first shift is Wednesday is
**absent from the cantina on Tuesday** — the day they actually arrive and need feeding.

### Decisions (from brainstorm)
- **Confirmed-only.** The cantina currently counts `PendingOrConfirmed`. Switch to `ConfirmedOnly`.
  Consequence (intended): humans whose shifts are all still pending no longer appear in the cantina
  roster until a shift is confirmed.
- **Arrival day = (first confirmed shift day − 1).** Mirrors the already-blessed pattern in
  `ShiftEarlyEntryProjection.FirstShiftDayByUser` / `VolunteerTrackingExportService`
  (`arrivalDay = firstShiftDay − 1`). That code is confirmed-only too, which now aligns.
- **No clamp.** If the arrival day falls before the event (negative offset), show it anyway.
- **Scope:** weekly roster (drives both the on-screen roster and the CSV export — they share
  `WeeklyRosterDto`) **and** the per-day drill-down matrix, so the two views agree.
- **Inline, no new interface methods.** Compute first-confirmed-day inside `CantinaRosterService`
  using the existing `GetOnSiteUserIdsForDayAsync`. (Reuse-first; avoids new interface surface that
  would need Peter's approval.)

### Design

All logic lives in `src/Humans.Application/Services/Cantina/CantinaRosterService.cs`.

**(a) Flip the scope.** Both `GetOnSiteUserIdsForDayAsync(...)` calls (lines 200, 299) pass
`ShiftDayUserStatusScope.ConfirmedOnly` instead of the current `PendingOrConfirmed`.
(Confirm the current default is passed implicitly; make it explicit.)

**(b) Earliest-confirmed-day map (inline helper).**
`firstConfirmedDayOffsetByUser(eventSettings, ct)` scans the **full event day range**
`[eventSettings.BuildStartOffset … eventSettings.StrikeEndOffset]`, calling the existing
`GetOnSiteUserIdsForDayAsync(eventId, offset, ConfirmedOnly)` per offset, and records the minimum
offset seen per user id. At ~500 humans and a bounded day count this is in-memory-cheap
(CLAUDE.md scale notes: prefer in-memory over query tuning). Returns `Dictionary<Guid,int>`.

Scanning the whole event range — not just the visible week — is required so that "first shift" is
the human's *true* earliest confirmed shift, not merely their first appearance within the current
week window (which would wrongly grant an arrival day to multi-week attendees).

**(c) Weekly view (`GetWeeklyRosterAsync`).**
After building `daysOnSiteByUserId` from the week's confirmed cohorts, for each user compute
`arrivalOffset = firstConfirmedDayOffset[user] − 1`. If `arrivalOffset` maps to a date inside the
visible week window, add that date to the user's on-site day set — pulling the user into the cohort
even if they have no shift in this week (e.g. first shift is the Monday of next week → arrival is
the Sunday shown this week). Downstream this means:
- `ArrivesOn` (`daysList[0]`) becomes the true arrival day.
- The arrival day appears in `NoShift` (present, no shift) — existing treatment, no new field.
- Dietary breakdown, allergy/intolerance rollups, totals and unanswered counts include arrival-only
  humans automatically, because they join `uniqueUserIds`.

**(d) Daily drill-down (`GetDailyRosterAsync(dayOffset = N)`).**
Cohort = confirmed users on day N **∪** users whose `firstConfirmedDayOffset == N + 1`. The added
arrival-day humans flow through the same per-day people list and aggregates.

### Edge cases
- First confirmed shift on the event's first possible day → arrival offset is one earlier
  (possibly negative). Shown anyway (no clamp).
- A human with confirmed shifts on consecutive days starting day N: arrival = N−1; days N, N+1…
  already covered by their shifts; set semantics prevent double counting.
- A human with only pending shifts: excluded entirely (confirmed-only) — no arrival day either.
- No active event: unchanged (empty roster path).

### Out of scope (YAGNI)
- Filling *all* gaps between arrival and departure (only the single arrival day is added, matching
  the request and the existing EE pattern).
- Any visual "arriving / setup" badge — the existing `NoShift` rendering is sufficient.
- Departure-day / day-after-last-shift feeding.

---

## Testing

Feature 1:
- Adjust the existing cantina rollup test (drop FODMAP assertion).
- (Existing edit/validation tests already cover that unknown intolerances are filtered.)

Feature 2 (TDD — `CantinaRosterServiceTests` / `CantinaDailyRosterServiceTests`,
`Substitute`-mocked `IShiftManagementService`):
- Weekly: human with first confirmed shift on Wed → appears Tue (arrival), `ArrivesOn` = Tue,
  Tue ∈ `NoShift`.
- Weekly: pending-only human → absent (confirmed-only).
- Weekly: arrival day falling before the visible week is not shown this week; arrival day on the
  last day of the previous-relative window pulls a no-shift human into the week.
- Weekly: arrival-only human counts once in totals / dietary / rollups.
- Daily(N): includes humans whose first confirmed shift is N+1; excludes pending-only.
- No-clamp: first shift on day 0 → arrival day −1 still produced.
- Earliest-day map uses the *global* minimum (a human with a shift in a prior week is not granted a
  spurious arrival day in a later week).

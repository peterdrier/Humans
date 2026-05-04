# Shift range signup — confirmation modal — design

**Status:** Draft, brainstorm complete, awaiting plan.
**Date:** 2026-05-04
**Owner:** Frank (on Peter's repo)
**Branch context:** built on `shifts-dashboard-setup-subfilter` (head `148ce41b feat(shifts): build sub-period filter, date range, grouped urgency accordion`).

## Problem

The shifts dashboard's Build/Strike rota partial (`_BuildStrikeRotaTable.cshtml`) renders a single form with two `<select>`s (start day, end day) defaulting to the **earliest → latest** available shift, and one submit button labelled "Sign up for setup/strike". Clicking the button posts directly to `[HttpPost("SignUpRange")]` and creates signups for every day in the chosen range with no further confirmation.

Because the defaults cover the entire range, an unintentional click commits the user to a multi-day on-site commitment. Users frequently misclick and end up signed up for far more than they intended.

## Goal

Insert a confirmation modal between the click and the actual POST. The modal shows a plain-language summary of what the user is about to commit to (phase, date range, day count, on-site arrival expectation), surfaces conflicts with the user's existing signups across all rotas in the same Event, and gives the user a clear way to back out.

## Scope

**In scope (this spec):**
- Confirmation modal for the Build/Strike multi-day range signup form in `_BuildStrikeRotaTable.cshtml` only.
- Cross-rota conflict surfacing with time-window overlap (not just same-date overlap).
- Server-side enforcement of conflict skipping (verify or add).
- New localizer keys (English + Spanish).

**Out of scope (deferred):**
- Confirmation on per-shift single-day signup buttons — single-shift signups are already a one-click acknowledged action and are not the source of misclick reports.
- Confirmation on cancel/withdraw actions.
- Notifying or warning when the user's chosen range crosses days where the shift is full (`RemainingSlots <= 0`); those days are already filtered out of the dropdown options.
- Any change to how the dashboard chooses default start/end values.

## Background — what the partial currently does

`src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml` (lines 10–41):

```html
<form asp-controller="Shifts" asp-action="SignUpRange" method="post" class="row g-2 align-items-end mb-3">
    @Html.AntiForgeryToken()
    <input type="hidden" name="rotaId" value="@rotaGroup.Rota.Id" />
    @* hidden filter inputs: departmentId, fromDate, toDate, period, periods, tags, sort *@
    <select name="startDayOffset" ...> ... </select>
    <select name="endDayOffset" ...> ... </select>
    <button type="submit" class="btn btn-sm btn-success">Sign up for setup/strike</button>
</form>
```

`startDayOffset` and `endDayOffset` are **day-offsets relative to `EventSettings.GateOpeningDate`**. Option text uses `ToDisplayShiftDate()`. The `availableShifts` collection passed in already filters out (a) shifts the user is signed up to in this rota, and (b) shifts with no remaining slots — so the dropdowns only contain valid endpoints, but **the range between two valid endpoints can still span over days the user is signed up to or cross other rotas' shifts on the same calendar date.**

`ShiftsController.cs:291-315`:

```csharp
[HttpPost("SignUpRange")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SignUpRange(
    Guid rotaId, int startDayOffset, int endDayOffset,
    Guid? departmentId, string? fromDate, string? toDate, string? period,
    [FromForm(Name = "tags")] List<Guid>? tagIds,
    [FromForm(Name = "periods")] List<string>? periods = null,
    string? sort = null)
{
    ...
    var result = await _signupService.SignUpRangeAsync(user.Id, rotaId, startDayOffset, endDayOffset, isPrivileged: privileged);
    ...
}
```

The codebase has a generic browser-`confirm()`-based `data-confirm` attribute pattern in `wwwroot/js/site.js:21-43`, plus full Bootstrap modals used elsewhere (e.g. `_MarkdownHelp.cshtml`).

## UX flow

1. User picks start/end on the form, clicks "Sign up for [setup|strike]".
2. Button is `type="button"` — opens a Bootstrap modal scoped per-rota (id `confirmSignup-{rotaId}`).
3. Modal body shows:
   - **Phase line:** `You're signing up for the {setup|strike} phase.`
   - **Date line:** `From {startDate} to {endDate} ({N} days).`
   - **Conflicts section** (conditional, see below).
   - **Arrival callout** (`alert-warning`): `You'll be expected on site by {startDate − 1 day}.`
   - **Sanity prompt:** `Is this the period you intended to sign up for?`
4. Modal footer: `Cancel` (secondary, dismiss only) and `Confirm sign-up` (success, `type="submit"` inside the same form).
5. Confirming submits the form normally — all hidden filter inputs and the antiforgery token are preserved because the modal lives **inside** the form element.

## Conflict detection — cross-rota with time overlap

### Definition

A **conflict** is when, for a calendar date inside the chosen `[startDayOffset, endDayOffset]` range, the user is already signed up to an active shift whose **absolute time window overlaps** the Build/Strike day's absolute time window.

Scope: **all of the user's active signups across all events**, matching the existing server-side check in `ShiftSignupService.SignUpRangeAsync` (lines 562-583), which uses `IShiftSignupRepository.GetActiveSignupsForUserAsync(userId)` and compares per-shift `GetAbsoluteStart` / `GetAbsoluteEnd` `Instant`s. The modal previews exactly what the service will check, so cross-event signups are included. (In practice the user is generally only active in one Event at a time, but matching the service's scope avoids divergence.)

Overlap rule: `existing.AbsoluteStart < buildStrikeDay.AbsoluteEnd AND existing.AbsoluteEnd > buildStrikeDay.AbsoluteStart` (using NodaTime `Instant`s, not wall-clock `LocalTime`s — DST-safe and matches the service).

All-day Build/Strike shifts use the canonical `Shift.AllDayWindowStart` (08:00) and `Shift.AllDayWindowEnd` (18:00) constants on the day's calendar date in the Event's time zone, via `Shift.GetAbsoluteStart` / `GetAbsoluteEnd` (`src/Humans.Domain/Entities/Shift.cs:64,71,110-132`). A 12:00–14:00 lunch shift inside an 08:00–18:00 Setup day is a conflict; an 18:30–20:30 dinner shift on the same date is not.

### Distinguishing the two skip-kinds

There are two reasons a day inside the chosen range may be skipped, and they are surfaced separately:

1. **Already signed up in *this* rota.** Already filtered out of the dropdown options' `availableShifts` list (line 6 of the partial), so it can't be an endpoint — but the range can still span over such a day. These are **silently skipped** at the day level by the service today (the duplicate-check at `ShiftSignupService.cs:556-560` blocks a range from running at all if any day in range is already taken — this also needs revisiting in "Server-side enforcement" below).
2. **Time-overlap with a signup in another rota or event.** This is the cross-rota conflict the modal foregrounds.

Modal copy treats the two together for the user ("days you can't add to this range") but the server logic distinguishes them.

### Server-side data

`BuildStrikeRotaTableViewModel` grows two new properties:

```csharp
public IReadOnlyList<UserSignupConflictItem> UserActiveSignups { get; init; }
public ShiftWindow RotaAllDayWindow { get; init; }

public record UserSignupConflictItem(
    LocalDate Date,           // event-local calendar date of existing signup
    string ShiftName,
    string RotaName,
    Instant AbsoluteStart,    // matches service comparison semantics
    Instant AbsoluteEnd,
    string DisplayStart,      // pre-formatted wall-clock for UI ("12:00")
    string DisplayEnd);       // pre-formatted wall-clock for UI ("14:00")

public record ShiftWindow(Instant AbsoluteStart, Instant AbsoluteEnd);
// Per-rota; one window per (rota, dayOffset) — but since this rota is all-day-only,
// we store it as a per-day-offset map: Dictionary<int, ShiftWindow>.
public IReadOnlyDictionary<int, ShiftWindow> RotaWindowsByDayOffset { get; init; }
```

The window per-day-offset is needed because each day's absolute Instant differs (different calendar dates). The wall-clock window (08:00–18:00) is identical across days for an all-day rota, but the absolute Instants are not.

Built by the controller action that renders the dashboard partial (`ShiftsController.Index`). Loads the current user's active signups via the existing `IShiftSignupRepository.GetActiveSignupsForUserAsync(userId)` (already used by the service — same data, no new repo method). For each existing signup, computes the absolute window using the existing `Shift.GetAbsoluteStart` / `GetAbsoluteEnd` and the signup's own `Rota.EventSettings`.

### View emission

The partial emits two pieces of data inside the form. Instants are serialized as ISO-8601 UTC strings (`"2026-05-06T10:00:00Z"`) — `Date` and string comparison work for the overlap check since both sides are UTC strings of the same format.

```html
<script type="application/json" class="js-user-signups">
[{"date":"2026-05-06","shift":"Kitchen Lunch","rota":"Event",
  "absStart":"2026-05-06T10:00:00Z","absEnd":"2026-05-06T12:00:00Z",
  "displayStart":"12:00","displayEnd":"14:00"}]
</script>

<script type="application/json" class="js-rota-windows">
{"5":{"absStart":"2026-05-06T06:00:00Z","absEnd":"2026-05-06T16:00:00Z"},
 "6":{"absStart":"2026-05-07T06:00:00Z","absEnd":"2026-05-07T16:00:00Z"}}
</script>
```

The data is per-rota, so it lives inside the per-rota form scope and there's no cross-form bleed.

Each `<option>` in **both** selects gains `data-date="{dayOffset → calendar date, formatted with ToDisplayShiftDate()}"`. Start `<option>`s additionally carry `data-arrive-by="{startDate − 1 day, formatted}"` so JS doesn't do client-side date arithmetic for the arrival callout.

### Client-side computation (small inline `<script>` in the partial)

On `show.bs.modal`:
1. Read selected `startDayOffset` and `endDayOffset` integer values and the selected `<option>`s' `data-date`.
2. Compute `days = endDayOffset − startDayOffset + 1`.
3. Read `data-arrive-by` from the selected start option.
4. For each `offset` in `[start..end]`:
   a. Look up the Build/Strike day's `{absStart, absEnd}` from `js-rota-windows`.
   b. Look up the calendar date for that offset from the corresponding option's `data-date` (build a one-time map at modal-init from the start `<select>`).
   c. Filter `js-user-signups` to entries whose `absStart`/`absEnd` overlap the day's window using the rule above (string comparison on UTC ISO-8601 strings is correct here).
5. Render the conflicts section based on three states (below).
6. Inject `startDate`, `endDate`, `days`, `arriveBy` into modal template spans.

### Conflicts section — three states

- **No conflicts:** section hidden, confirm button enabled.
- **Some days conflict:** `alert-info`. Heading + per-day list. One row per conflict:
  > **May 6** — already signed up for *Kitchen Lunch* (Event rota, 12:00–14:00)

  Footer note: *Sign-up will only add days that don't conflict.* Confirm enabled.
- **Every day conflicts:** `alert-warning`. Confirm button **disabled**.

If the user changes the dropdowns and reopens the modal, recomputation happens on the next `show.bs.modal` — Bootstrap fires `show` every time, so close-and-reopen always refreshes. We don't try to live-sync while open.

## Server-side enforcement (concrete change required)

Today's behaviour, confirmed at `src/Humans.Application/Services/Shifts/ShiftSignupService.cs:556-590`:

- If any day in the range is already signed up by this user (any rota, any event), the entire range fails with `SignupResult.Fail("Already signed up for one or more shifts in this range.")`.
- If any day in the range time-overlaps with another active signup, the entire range fails with `SignupResult.Fail($"Time conflict on day(s): {dayList}.")`.

This is incompatible with the modal's "Sign-up will only add days that don't conflict" promise. The service must change.

### Required change

Add a `bool skipConflicts = false` parameter to `ShiftSignupService.SignUpRangeAsync`. When `true`:

1. The duplicate-signup check (lines 557-560) **filters** rather than rejects: drop already-signed-up shifts from `shiftsInRange` and append them to a "skipped: already signed up" list.
2. The overlap check (lines 562-590) **filters** rather than rejects: drop overlapping days from `shiftsInRange` and append them to a "skipped: time conflict" list.
3. If after filtering `shiftsInRange.Count == 0`, return `SignupResult.Fail` with the existing-signup / conflict summary — there's nothing to add.
4. Otherwise proceed with the existing capacity check and signup creation, and merge the skip summary into `SignupResult.Warning` so the controller's existing toast surfacing (`ShiftsController.cs:310-312`) shows it.

The default `skipConflicts: false` preserves the strict behaviour for any existing internal callers (none in current code, but the flag keeps the option open).

`ShiftsController.SignUpRange` passes `skipConflicts: true` so the user-initiated dashboard flow matches the modal's promise.

### Test coverage

Unit tests on `ShiftSignupService` (in the existing `Humans.Tests` project — verify project layout at implementation time):
- `skipConflicts: true`, no conflicts → unchanged behaviour, no warning.
- `skipConflicts: true`, partial overlap (some days conflict) → signups created for non-conflicting days, warning summarises skipped days.
- `skipConflicts: true`, every day conflicts → `Fail` with summary, no signups created.
- `skipConflicts: true`, mixed already-signed-up + time-conflict + free → free days get signed up; warning lists the two skip kinds distinctly.
- `skipConflicts: false` (existing internal callers) → unchanged behaviour, all original failure paths preserved.

## Markup changes

### `_BuildStrikeRotaTable.cshtml`

- Submit button: `type="submit"` → `type="button"` with `data-bs-toggle="modal"` `data-bs-target="#confirmSignup-{rotaId}"`.
- After the existing button, append a hidden `<div class="modal fade" id="confirmSignup-{rotaId}" ...>` containing:
  - Header with title key `Shifts_ConfirmSignup_Title`.
  - Body with phase line, date line, conflicts container (three template states), arrival callout, sanity prompt.
  - Footer with `Cancel` + `Confirm sign-up` (`type="submit"`).
- Two `<script type="application/json">` blocks emitting the data.
- One small `<script>` block (or extracted into `wwwroot/js/shifts-range-confirm.js` if it grows past ~50 lines) wiring up `show.bs.modal`.
- `<option>` tags in both selects gain `data-date="..."`; start `<option>`s gain `data-arrive-by="..."`.

### `BuildStrikeRotaTableViewModel`

Two new properties: `UserActiveSignups` (`IReadOnlyList<UserSignupConflictItem>`) and `RotaWindowsByDayOffset` (`IReadOnlyDictionary<int, ShiftWindow>`). Plus the two record types `UserSignupConflictItem` and `ShiftWindow` if not already present in the file.

### Controller — `ShiftsController.Index`

Populate the new view-model properties (`UserActiveSignups`, `RotaWindowsByDayOffset`) by calling `IShiftSignupRepository.GetActiveSignupsForUserAsync(userId)` and projecting. Pass `skipConflicts: true` from `ShiftsController.SignUpRange` (line 302) into the service call.

### `ShiftSignupService.SignUpRangeAsync`

Add `bool skipConflicts = false` parameter. See "Server-side enforcement" for behaviour.

### Resource files

`Shifts_*` keys live in `src/Humans.Web/Resources/SharedResource.resx` (confirmed: `Shifts_Start`, `Shifts_SignUpForDates`, `Common_Cancel` are all present). New keys go there plus the five locale siblings:

- `SharedResource.resx` (English — default)
- `SharedResource.de.resx`
- `SharedResource.es.resx`
- `SharedResource.ca.resx`
- `SharedResource.fr.resx`
- `SharedResource.it.resx`

All six locales are required (matches existing pattern). Spanish is the highest-quality non-English translation; others can be machine-translated stubs (matching how the rest of the resx is maintained).

## New localizer keys

| Key | English |
|---|---|
| `Shifts_ConfirmSignup_Title` | `Confirm your {0} sign-up` *(0 = setup/strike)* |
| `Shifts_ConfirmSignup_Phase` | `You're signing up for the {0} phase.` |
| `Shifts_ConfirmSignup_Range` | `From {0} to {1} ({2} days).` |
| `Shifts_ConfirmSignup_Range_Single` | `For {0} (1 day).` |
| `Shifts_ConfirmSignup_ArriveBy` | `You'll be expected on site by {0}.` |
| `Shifts_ConfirmSignup_Prompt` | `Is this the period you intended to sign up for?` |
| `Shifts_ConfirmSignup_Confirm` | `Confirm sign-up` |
| `Shifts_ConfirmSignup_Conflicts_Heading` | `Some days in this range conflict with shifts you're already signed up for:` |
| `Shifts_ConfirmSignup_Conflicts_Row` | `{0} — already signed up for {1} ({2}, {3}–{4})` *(date, shiftName, rotaName, start, end)* |
| `Shifts_ConfirmSignup_Conflicts_PartialNote` | `Sign-up will only add days that don't conflict.` |
| `Shifts_ConfirmSignup_Conflicts_AllBlocked` | `Every day in this range conflicts with existing signups — nothing to add.` |
| `Common_Cancel` | reuse if present, otherwise add. |

Spanish translations follow the same structure — done at implementation time, mirroring patterns in the existing `.es.resx`.

## Edge cases

- **`days = 1`** (start == end): use `Shifts_ConfirmSignup_Range_Single`.
- **All-day Build/Strike day** vs short existing shift: handled by the overlap rule (12:00–14:00 inside 08:00–18:00 = conflict).
- **Two existing shifts on the same conflict date:** list both rows.
- **Time zones / DST:** display strings are server-side-formatted; JS doesn't do TZ or DST math. Overlap math is on UTC ISO-8601 strings emitted from server-side `Instant`s, so all DST / TZ resolution happens once on the server via the existing `GetAbsoluteStart` / `GetAbsoluteEnd` paths.
- **Multiple rotas on the page** (e.g. Setup and Strike both shown): per-rota modal ids prevent collision.
- **No JavaScript:** site already requires JS for other features. Don't add a `<noscript>` fallback — accept the current site-wide assumption.
- **User with no other active signups:** `UserActiveSignups` is empty → conflicts section never shows. Cheap.
- **Rendering perf:** the user's active-signup list is small (one user, generally a handful of shifts). Inline JSON is fine; no new endpoint needed.

## Files touched

| File | Change |
|---|---|
| `src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml` | Button + modal + inline data blobs + show.bs.modal handler. ~70 lines added. |
| `src/Humans.Web/Models/ShiftViewModels.cs` (`BuildStrikeRotaTableViewModel` lives there at line 548 alongside the other shift VMs) | Two new properties (`UserActiveSignups`, `RotaWindowsByDayOffset`) plus the two record types `UserSignupConflictItem` and `ShiftWindow`. |
| `src/Humans.Web/Controllers/ShiftsController.cs` (`Index` action; line 302 for the `SignUpRange` POST) | Populate new VM properties via `IShiftSignupRepository.GetActiveSignupsForUserAsync`; pass `skipConflicts: true` from `SignUpRange`. |
| `src/Humans.Application/Services/Shifts/ShiftSignupService.cs` | Add `bool skipConflicts = false` to `SignUpRangeAsync`; rework duplicate + overlap checks per "Server-side enforcement". |
| `src/Humans.Web/Resources/SharedResource.{resx,de.resx,es.resx,ca.resx,fr.resx,it.resx}` | Add new localizer keys (all six locales). |
| (Optional) `src/Humans.Web/wwwroot/js/shifts-range-confirm.js` | If the inline script grows past ~50 lines, extract here. |

No DB changes. No migrations. No new routes. No new service interfaces.

## Testing

- **Unit tests** (xUnit, existing test project): cover any new conflict-skip logic in `SignUpRangeAsync`. Cases: no overlap, partial overlap (some days skipped), full overlap (no signups created), idempotency on re-submit.
- **Manual smoke test** (`dotnet run --project src/Humans.Web`):
  1. Log in as a user with no existing signups → modal shows, no conflicts section, confirm creates signups.
  2. Sign up for a non-overlapping shift in another rota, reopen the dashboard → still no conflicts section.
  3. Sign up for an overlapping shift, reopen → conflicts section lists that day; confirm creates signups for the rest of the range.
  4. Manually craft a range where every day conflicts → confirm button disabled.
  5. Cancel button closes modal with no POST (verify in network tab).
  6. Open modal, change dropdowns, reopen → modal reflects new range.

No browser-automated test in this spec — the existing test infrastructure for views is thin; manual smoke is proportionate.

## Implementation-time verification checklist

The following are genuine verify-against-code items (not design holes — design decisions all made above):

1. The `Humans.Tests` test project layout for `ShiftSignupService` — confirm fixture / setup conventions before adding new tests.
2. The de/ca/fr/it locale stub maintenance pattern (machine-translated, hand-edited, or marked `<!-- TODO -->`) — match what's already in those files.

## Decisions log (from brainstorm)

| Topic | Decision |
|---|---|
| Mechanism | Bootstrap modal, not browser `confirm()`. |
| Scope | Build/Strike multi-day range form only — no per-shift, no cancel/withdraw. |
| Conflicts | Cross-rota AND cross-event, with `Instant`-based time-window overlap (matches existing service semantics). |
| Conflict source of truth | `ShiftSignupService.SignUpRangeAsync(skipConflicts: true)`. Client previews, doesn't decide. |
| Service signature | New parameter `bool skipConflicts = false` on existing method (default preserves strict behaviour). |
| Modal lifetime | Per-rota (`#confirmSignup-{rotaId}`), modal lives inside the form so submit naturally posts the form. |
| Date / TZ math | Server-side via NodaTime `Instant`s. JS does string comparison on UTC ISO-8601 strings. |
| Performance | Inline JSON of the user's own active signups (small: one user, handful of shifts). No new endpoint. |
| Localization | All six locales (en/de/es/ca/fr/it). Spanish hand-translated; others stubbed per existing pattern. |

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

A **conflict** is when, for a calendar date inside the chosen `[startDayOffset, endDayOffset]` range, the user is already signed up to a shift (in *any* rota of the same Event) whose **time window overlaps** the Build/Strike day's time window.

Overlap rule: `existing.Start < buildStrikeDay.End AND existing.End > buildStrikeDay.Start`.

A 09:00–11:00 morning shift on the same calendar date as a 13:00–22:00 Strike day is **not** a conflict. A 12:00–14:00 lunch shift inside an all-day 08:00–22:00 Setup window **is** a conflict.

### Server-side data

`BuildStrikeRotaTableViewModel` grows two new properties:

```csharp
public IReadOnlyList<UserSignupConflictItem> UserSignupsInEvent { get; init; }
public IReadOnlyDictionary<int, ShiftWindow> RotaShiftWindowsByDayOffset { get; init; }

public record UserSignupConflictItem(
    LocalDate Date,
    string ShiftName,
    string RotaName,
    LocalTime Start,
    LocalTime End);

public record ShiftWindow(LocalTime Start, LocalTime End);
```

Built by the controller action that renders the dashboard partial. Loads the current user's signups across all rotas of the active Event (single query through whatever store/service exposes it — confirmed at implementation time, likely `IShiftSignupService.GetSignupsForUserInEventAsync` or equivalent), projected to the DTO. The Build/Strike rota's per-`DayOffset` time windows come from the rota's existing shift list.

### View emission

The partial emits two pieces of data inside the form:

```html
<script type="application/json" class="js-user-signups-in-event">
[{"date":"2026-05-06","shift":"Kitchen Lunch","rota":"Event","start":"12:00","end":"14:00"}]
</script>

<script type="application/json" class="js-rota-windows">
{"5":{"start":"08:00","end":"22:00"},"6":{"start":"08:00","end":"22:00"}}
</script>
```

The data is per-rota, so it lives inside the per-rota form scope and there's no cross-form bleed.

The start `<option>`s additionally carry `data-arrive-by="{startDate − 1 day, formatted}"` so JS doesn't do client-side date arithmetic for the arrival callout.

### Client-side computation (small inline `<script>` in the partial)

On `show.bs.modal`:
1. Read selected `startDayOffset` and `endDayOffset` integer values and option text (already-formatted dates).
2. Compute `days = endDayOffset − startDayOffset + 1`.
3. Read `data-arrive-by` from the selected start option.
4. For each `offset` in `[start..end]`:
   a. Look up the Build/Strike day window from `js-rota-windows`.
   b. Look up the calendar date for that offset (option text from a hidden lookup, or rebuild from the `js-rota-windows` map keyed by offset+date — TBD at implementation, simplest is to add `data-date` to each option).
   c. Filter `js-user-signups-in-event` to entries whose `date` matches AND whose time window overlaps.
5. Render the conflicts section based on three states (below).
6. Inject `startDate`, `endDate`, `days`, `arriveBy` into modal template spans.

### Conflicts section — three states

- **No conflicts:** section hidden, confirm button enabled.
- **Some days conflict:** `alert-info`. Heading + per-day list. One row per conflict:
  > **May 6** — already signed up for *Kitchen Lunch* (Event rota, 12:00–14:00)

  Footer note: *Sign-up will only add days that don't conflict.* Confirm enabled.
- **Every day conflicts:** `alert-warning`. Confirm button **disabled**.

If the user changes the dropdowns and reopens the modal, recomputation happens on the next `show.bs.modal` — Bootstrap fires `show` every time, so close-and-reopen always refreshes. We don't try to live-sync while open.

## Server-side enforcement

Client-side filtering is display only. `SignupService.SignUpRangeAsync` must be the source of truth and **must skip days where the user already has a time-overlapping signup** rather than creating duplicates or failing the entire range. At implementation time:

1. Read the current behaviour of `SignUpRangeAsync`.
2. If it already silently skips overlapping days → confirm with a unit/integration test, document, done.
3. If it errors on overlap → grow it with a `skipConflicts: true`-style code path that returns a summary of skipped days, and surface that summary in the post-redirect toast.
4. If it has neither behaviour → add overlap-skip logic with the same time-window rule the modal uses.

This is a **pre-implementation verification step** captured here so it isn't forgotten — the modal's "Sign-up will only add days that don't conflict" copy depends on the service guaranteeing that promise.

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

Two new properties: `UserSignupsInEvent`, `RotaShiftWindowsByDayOffset`.

### Controller (`ShiftsController.Dashboard` or whatever loads the partial)

Populate the two new view-model properties from the existing services/stores. No new endpoint.

### `SignupService.SignUpRangeAsync`

Verify and, if needed, extend to skip conflicts (see "Server-side enforcement").

### Resource files

- `src/Humans.Web/Resources/SharedResource.resx` (English)
- `src/Humans.Web/Resources/SharedResource.es.resx` (Spanish)

(Or wherever `Shifts_*` keys currently live — TBD at implementation by grep.)

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
- **All-day Build/Strike day** vs short existing shift: handled by the overlap rule (12:00–14:00 inside 08:00–22:00 = conflict).
- **Two existing shifts on the same conflict date:** list both rows.
- **Time zones:** display strings are server-side-formatted; JS doesn't do TZ math. Times in the JSON blob are wall-clock event-local times, same as elsewhere on the dashboard.
- **Multiple rotas on the page** (e.g. Setup and Strike both shown): per-rota modal ids prevent collision.
- **No JavaScript:** site already requires JS for other features. Don't add a `<noscript>` fallback — accept the current site-wide assumption.
- **User with no other signups in the event:** `UserSignupsInEvent` is empty → conflicts section never shows. Cheap.
- **Rendering perf:** the user's signups-in-event list is small (one user, one event). Inline JSON is fine; no new endpoint needed.

## Files touched

| File | Change |
|---|---|
| `src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml` | Button + modal + inline data + show.bs.modal handler. ~70 lines added. |
| `src/Humans.Web/Models/BuildStrikeRotaTableViewModel.cs` *(or wherever defined)* | Two new properties. |
| `src/Humans.Web/Controllers/ShiftsController.cs` *(or whichever action renders the partial)* | Populate new VM properties. |
| `src/Humans.Application/...SignupService.cs` *(name TBD)* | Verify / extend `SignUpRangeAsync` to skip overlapping days. |
| `src/Humans.Web/Resources/SharedResource.resx` + `.es.resx` *(path TBD)* | New localizer keys. |
| (Optional) `src/Humans.Web/wwwroot/js/shifts-range-confirm.js` | If the inline script grows past ~50 lines, extract here. |

No DB changes. No migrations. No new routes.

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

These items are deliberately not finalised here; they get answered during the implementation plan, not the spec:

1. `Shift` time-field shape: `LocalTime`, `TimeOnly`, derived? (Affects DTO + JSON shape.)
2. All-day shift effective window: `00:00–24:00` or event-defined?
3. Service for "this user's signups across all rotas in this Event": which interface, which method, does it already exist?
4. Resx file path for `Shifts_*` keys.
5. Existing `Common_Cancel` key — reuse or add.
6. `SignUpRangeAsync` current overlap behaviour: skip / error / unaware.

## Decisions log (from brainstorm)

| Topic | Decision |
|---|---|
| Mechanism | Bootstrap modal, not browser `confirm()`. |
| Scope | Build/Strike multi-day range form only — no per-shift, no cancel/withdraw. |
| Conflicts | Cross-rota, with time-window overlap (not date-only). |
| Conflict source of truth | Service-side; client only displays. |
| Modal lifetime | Per-rota (`#confirmSignup-{rotaId}`), modal lives inside the form so submit naturally posts the form. |
| Date math | Server-side. JS reads pre-formatted strings via `data-*` attributes and JSON blobs. |
| Performance | Inline JSON of the user's own signups-in-event. No new endpoint. |

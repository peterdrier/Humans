# Shifts — Per-Day Signup (instant, no reload)

**Status:** Design approved, pending implementation.
**Section:** Shifts.
**Date:** 2026-06-04.
**Component class:** Load-bearing (this is the primary volunteer signup surface).

## Problem

Signing up for shifts on the browse page (`/Shifts`) has two different flows, both of which reload the whole page on every change:

- **Build/Strike all-day rotas** use a *period* picker: a Start/End day dropdown pair plus a confirmation modal (conflict + "arrive by" callout) that POSTs `SignUpRange`. You can only pick a contiguous span, you can't cherry-pick non-adjacent days, and the per-day table below the picker is read-only.
- **Timed Event shifts** already have a per-shift "Sign up" button, but each one is a form POST that reloads the page.

The volunteer asked for the ability to **select per-day signups rather than a period**, with toggling that is quick and easy and **does not reload the page** on every click. Signing up for 20 days means 20 clicks — that's accepted, as long as each click is fast and in-place.

## Goal

Replace period signup with **instant, per-day click-to-toggle** on both signup surfaces:

- Click an available day → you're signed up for that day.
- Click a day you're on → you're removed from it (instant, no confirmation).
- The row updates in place (fill bar, count, your avatar, status). No full-page reload.
- Works for both Build/Strike all-day rows and timed Event-shift rows.

## Non-goals

- **No change to the overlap rule.** You still cannot be on two time-overlapping shifts (see *Conflict handling*).
- **No new signup capabilities** — no waitlists, no partial-day signups, no bulk "select span" helper. The range/period concept is removed, not reimplemented.
- **No aggregate live-update.** Rota-level and department-level fill bars (the headers) refresh on next page load, not per toggle.
- **No progressive enhancement for no-JS browsers.** No-reload toggling requires JavaScript (see *Documented assumptions*).
- **No DB/schema change.** Existing range signups (grouped by `SignupBlockId`) stay as they are; `VoluntellRangeAsync` is untouched.
- **OnboardingWidget shift step is out of scope.** `_BuildStrikeRotaTable` / `_EventRotaTable` are *also* rendered by `Views/OnboardingWidget/Shifts.cshtml` (pointed at `OnboardingWidgetController`'s own `SignUp` / `SignUpRange` actions). The onboarding wizard keeps its **existing form-POST range/per-shift behavior unchanged**. Per-day toggle is a possible fast-follow there, not part of this change.

## Decisions (from brainstorm)

| Question | Decision |
|---|---|
| Interaction model | Instant click-to-toggle per day (no modal). |
| Scope | Both Build/Strike all-day rows **and** timed Event-shift rows. |
| Fate of the range picker | Removed entirely (Start/End selects + confirm modal deleted). |
| Removal | Instant, **no confirm — everywhere**, including the My Shifts page (drop its `data-confirm` prompts). |
| Conflict (overlap) | **Block + warn.** A conflicting click is refused with a warning toast; the day stays unsigned. Overlap rule unchanged. |
| Verification | Build + tests **and** run the app locally and exercise the toggle (per project doctrine). |

## Conceptual model

A "day signup" is just an existing single-shift signup. The all-day Build/Strike shifts are already one `Shift` per `DayOffset`, so signing up for one day = `SignUpAsync(shiftId)` for that day's shift; removing it = `BailAsync(signupId)`. No new domain concept is introduced — we are changing the *interaction*, not the data.

The browse rows know a shift's id but not the user's `signupId` for it. Rather than leak `signupId` into the views, the toggle is keyed by `shiftId` and the server resolves sign-up-vs-bail. This keeps a single, intent-free client contract ("toggle this day for me").

## Server surface

### New action — `ShiftsController.ToggleDay`

```
POST /Shifts/ToggleDay
[ValidateAntiForgeryToken]
(Guid shiftId, …existing filter params for context)
→ JSON
```

Logic:

1. Resolve current user (reuse `ResolveCurrentUserOrChallengeAsync`).
2. **Name gate** — reuse `RedirectIfNameMissing`; if it trips, return `{ ok:false, redirect:<url> }`.
3. **Dietary gate** — reuse the same condition as `RedirectIfDietaryMissingAsync`; if it trips, return `{ ok:false, redirect:<url> }`. (In practice rare: when dietary is missing the buttons render disabled — this is the belt-and-suspenders server check for the race.) Since `RedirectIfDietaryMissingAsync` is a removal candidate (it returns a `RedirectToAction` that `ToggleDay` can't use) and the predicate would otherwise be copied a third time, **extract the predicate** (`shift.QualifiesForCantinaMeal()` + empty `DietaryPreference`) into a shared helper that both the redirect helper and `ToggleDay` call, rather than inlining it.
4. Look up the user's **active** signup for `shiftId`. Note `ShiftSignupHelper.ResolveActiveStatuses` returns only shift-ids/statuses — **not** the `signupId` that `BailAsync` needs — so filter the raw `GetByUserAsync` list for the entry whose `ShiftId == shiftId` and status is `Confirmed`/`Pending`, and read its `.Id`:
   - **Present** → `BailAsync(signup.Id, actorUserId: user.Id, reason: "<self-service>")` → `signedUp=false`. (`reason` is `string?`; a short audit string like `"self-service toggle"` keeps parity with the My Shifts bail audit trail.)
   - **Absent** → `SignUpAsync(user.Id, shiftId, flags: privileged ? Privileged : None)` → `signedUp=true`.
5. **Conflict / capacity:** `SignUpAsync` already returns `Fail` on time overlap and surfaces capacity warnings. On `Fail`, re-render the row in its **unchanged** state and attach a warning toast header (state did not change). On success with a `Warning` (e.g. EE cap), attach a warning toast header.
6. Re-render the row partial for this shift (see *Views*) and return it.

**Transport — partial body + headers (no render-to-string helper).** The project has no render-partial-to-string utility, and adding one is unnecessary durable surface. Instead `ToggleDay` returns the re-rendered row partial **as the response body** (`return PartialView("_…Row", rowVm)` — no layout) and carries metadata in response headers:

| Header | Value |
|---|---|
| `X-Signed-Up` | `true` / `false` — the user's new state for this shift |
| `X-Toast-Type` | `success` / `warning` / `error` (omitted when no toast) |
| `X-Toast-Msg` | URL-encoded message (omitted when no toast) |
| `X-My-Signup-Count` | int, for the My Shifts tab badge |

A tripped name/dietary gate returns **`204 No Content` + `X-Redirect: <url>`** (no row body); the client navigates there. All responses are `200`/`204` (not error codes) so `fetch` `.ok` stays true and the client branches on headers.

**Row re-render data source.** After the signup/bail, `SignUpAsync`/`BailAsync` already invalidate the cached shift view (`viewInvalidator.InvalidateUser` + `InvalidateShift`, `ShiftSignupService.cs:108-109` etc.), so a fresh read returns current counts/avatars. `ToggleDay` reuses `ShiftBrowsePageBuilder` to build the single row's `ShiftDisplayItem` (fresh `IShiftView` read) rather than duplicating item construction — see *Views*.

### Reused, not added

`SignUpAsync`, `BailAsync`, `GetByUserAsync` already exist on `IShiftSignupService`. **No new service/interface methods.** The only new public surface is the one controller action plus two extracted view partials.

**Rejected alternatives (reuse-first audit):**
- *Extend the existing `SignUp` action via content negotiation* — rejected: it is PRG-redirect-shaped and signup-only; it can't express bail, and a toggle keyed by `shiftId` is a cleaner client contract.
- *Add a `Bail`-by-`shiftId` overload / expose `signupId` per row* — rejected: keying the toggle by `shiftId` and resolving server-side avoids widening the read view models with `signupId`.
- *Pure-JSON response with client-side DOM patching* — rejected: it reimplements the `vc:human` avatar component and badge logic in JS. Returning re-rendered row HTML keeps rendering in one place.

## Views

### Interaction mode (preserves OnboardingWidget unchanged)

The two table partials are shared between the browse page and the onboarding wizard, so we cannot simply swap the forms for toggles. Add an interaction mode the host view selects:

```
enum ShiftSignupInteraction { FormPost, InstantToggle }
```

Add an `Interaction` property (default `FormPost`) to `BuildStrikeRotaTableViewModel` and `EventRotaTableViewModel`. `Views/Shifts/Index.cshtml` sets `Interaction = InstantToggle` on every table VM it builds; `Views/OnboardingWidget/Shifts.cshtml` leaves the default, so **onboarding renders exactly as today** (range form + confirm modal + per-rota conflict script + per-shift signup form, all retained, all VM fields retained).

- **`FormPost`** (onboarding): unchanged — the range `<form>`, `confirmSignup-*` modal, the per-rota inline conflict/arrival-by script, the read-only status rows, and the Event-row signup `<form>` all render as they do now.
- **`InstantToggle`** (browse): the table renders **no** range form and **no** modal; each day/shift row renders a toggle button (below) instead of the read-only status cell / signup form.

**Build/Strike layout in InstantToggle:** the current Build/Strike table collapses contiguous days into expandable range headers + detail rows. In `InstantToggle` mode that collapsing is dropped — the table renders a **flat per-day list** (one row per all-day shift, each independently toggleable), matching the approved mockup. The contiguous-range grouping is a `FormPost`-only presentation, kept for onboarding.

### Extract one row partial per surface

Extract each table's `<tr>` into a row partial so the same markup serves the full-page render *and* the `ToggleDay` response:

- `Views/Shared/_BuildStrikeRotaRow.cshtml` — one all-day day-row.
- `Views/Shared/_EventRotaRow.cshtml` — one timed-shift row.

Each row partial takes the `Interaction` mode and branches the action cell only (everything else — date, fill bar, avatars, badges — is identical across modes). The tables loop over their shifts and render these partials. `ToggleDay` renders the single matching partial (always in `InstantToggle` mode) for the toggled shift, choosing build/strike vs event via `shift.IsAllDay`.

### The action cell (InstantToggle mode) becomes a toggle button

In `InstantToggle` mode the row's action cell renders a single button:

```html
<button type="button"
        class="btn btn-sm js-day-toggle"
        data-shift-id="@shift.Id"
        data-signed-up="@isSignedUp.ToString().ToLowerInvariant()"
        …disabled when full or dietary-blocked…>
  …label…
</button>
```

Button states (label + class come from the row partial, server-rendered):

| State | Render |
|---|---|
| Available | green outline, "Sign up" (`fa-plus`) |
| Signed up | solid success, "You're on — click to remove" (`fa-check`) |
| Full (and not signed up) | disabled, `Full` badge (unchanged) |
| Dietary-blocked | disabled, existing tooltip `Shifts_SignupDisabledTooltip_MissingDietary` |
| Conflict heads-up | available button + `fa-triangle-exclamation` warning icon (a hint; the click will be refused server-side per the block rule) |

### What is *not* removed

Because `FormPost` mode (onboarding) still uses them, nothing in the existing table markup is deleted outright. The Start/End range `<form>`, the `confirmSignup-<rotaId>` modal, the per-rota inline conflict/arrival-by `<script>`, the Event-row signup `<form>`, and the VM fields that feed them (`UserActiveSignups`, `RotaWindowsByDayOffset`, `SignUpController`/`SignUpAction`/`SignUpRangeController`/`SignUpRangeAction`) all remain — gated behind `Interaction == FormPost`. They simply never render on the browse page.

### My Shifts page

Drop the `data-confirm` attributes on the `Bail` / `BailRange` buttons (`Views/Shifts/Mine.cshtml`) so removal is a single click there too ("instant everywhere"). My Shifts stays a normal form-POST page (reload on bail is acceptable there — the no-reload requirement is specific to the browse toggle). Converting My Shifts to the no-reload toggle is **out of scope** for this change.

## Client JavaScript

One delegated handler (new `wwwroot/js/shifts.js`, referenced from the Shifts views; or a scoped block) replaces today's per-rota inline modal scripts — a net reduction in inline JS.

On `click` of `.js-day-toggle`:
1. Ignore if `disabled`. Disable the button + show a spinner (guards double-click / double-submit).
2. `fetch('/Shifts/ToggleDay', { method:'POST', headers:{'X-Requested-With':'XMLHttpRequest'}, body: FormData with __RequestVerificationToken + shiftId + current filters })` — the existing house pattern (see `Views/Notifications/Index.cshtml`).
3. On response (branch on headers, not a JSON body):
   - `X-Redirect` header set (204) → `window.location = <that url>`.
   - otherwise → read the response body (the re-rendered `<tr>` HTML) and replace the row's `<tr>` with it; update the My Shifts tab badge from `X-My-Signup-Count`.
   - `X-Toast-Type`/`X-Toast-Msg` present → inject a toast (decode `X-Toast-Msg`) into the existing `.shifts-toast-container` (`Views/Shifts/Index.cshtml:70`) so it inherits the auto-dismiss JS already keyed to that container (`Index.cshtml:304`). A conflict/capacity rejection comes back as a `200` with the unchanged row + a warning toast — swapping the unchanged row naturally re-enables the button.
4. Network error → re-enable button, generic error toast.

The antiforgery token is read from the page's `__RequestVerificationToken` hidden input (already emitted by the existing forms; keep one on the page).

## Out of scope / documented assumptions

- **JS required.** No-reload toggling needs JavaScript. Acceptable for an internal ~500-user app on modern browsers. No-JS users get no toggle (they are not served a form fallback in this design).
- **Aggregate fill bars** (rota header, department header) refresh on next page load, not per toggle. Per-row fill updates live.
- **My Shifts** removal becomes instant (no confirm) but remains a page-reload form POST; it is not converted to the no-reload toggle here.
- **Existing range signups** in the DB (grouped by `SignupBlockId`) are untouched and still bail as a group on the My Shifts page.

## Dead-surface cleanup

**Stays — confirmed live callers (do NOT delete):**
- `IShiftSignupService.SignUpRangeAsync` + impl — called by `OnboardingWidgetController.SignUpRange` (`:128`) and the `ProfileController` dietary replay `case "signuprange"` (`:1683`). Still load-bearing.
- `OnboardingWidgetController.SignUp` / `SignUpRange` — onboarding wizard's own actions; untouched.
- The `ProfileController` dietary-replay `case "signup"` (`:1663`) / `case "signuprange"` (`:1683`) — both call the **service directly**, not the `ShiftsController` actions, so they are unaffected by this change.

**Removal *candidates* — only what the browse page was the sole caller of, each gated on a Reforge/grep caller check before deletion:**
- `ShiftsController.SignUp` (action) — the browse Event-row form was its only poster (onboarding posts to `OnboardingWidgetController.SignUp`; the dietary replay calls the service directly). After browse moves to toggle, expected to be callerless → delete if the check confirms.
- `ShiftsController.SignUpRange` (action) + `ShiftsController.RedirectIfDietaryMissingForRangeAsync` + `ShiftsController.RedirectIfDietaryMissingAsync` — same: browse was the sole caller. Delete iff the caller check confirms; note the `returnAction=signup`/`signuprange` strings are *replayed* by `ProfileController` against the service, so removing these `ShiftsController` helpers does not break the replay.

Anything with a surviving caller is kept and noted, not force-deleted. Per Peter's hard rule (fix it right / no dead surface), genuinely dead `ShiftsController` actions are removed in this PR rather than left behind.

## Testing

Controller/service-level (xUnit, the section's existing test project):
- Toggle idempotence: signup → bail → signup leaves a single active signup and correct counts.
- Overlap: toggling a day that overlaps an existing signup returns `ok:false` + warning toast and creates **no** signup.
- Full shift: toggling a full shift you're not on is refused (button is disabled in UI; server rejects the race).
- Dietary gate: toggle with missing `DietaryPreference` on a qualifying shift returns `redirect`.
- Privileged actor: toggle auto-confirms (Privileged flag) as today.
- `ToggleDay` returns the correct row partial for all-day vs timed shifts.

Architecture baseline tests: unaffected — no new cross-section calls, no new repository access, controller calls only `IShiftSignupService` / existing read services.

**Manual (required):** run locally (`dotnet run --project src/Humans.Web`), sign in, and on `/Shifts`: toggle an all-day day on/off, toggle a timed event shift on/off, attempt an overlapping day (expect warning + no signup), confirm no full-page reload and that the My Shifts badge updates.

## Change-enforcement notes

- New user-facing strings (button labels, toasts) → add resource keys for all supported locales (EU/GDPR locale set). Reuse existing `Shifts_*` keys where they already say the right thing.
- Per project doctrine, no feature flag: this is an internal UX change with no rollback/regulatory surface, replacing an existing flow rather than running beside it.

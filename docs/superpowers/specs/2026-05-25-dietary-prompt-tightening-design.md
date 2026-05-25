# Dietary Prompt Tightening — Design

**Status:** Draft
**Date:** 2026-05-25
**Section:** Profile / Shifts
**Issue:** TBD (open after spec approval; companion to upstream `nobodies-collective/Humans#279`)
**Related:** `docs/features/profiles/dietary-medical-nudge.md` (the existing nudge this design tightens)

## Baseline

This spec is written against **`upstream/main`** (= `fork/feature/issue-279-dietary-nudge-impl` at HEAD `f4a0c966`, where the dietary-medical nudge work landed). The local `main` in this checkout is 86 commits behind; implementing this design requires branching from `upstream/main` (per the project's two-remote workflow), where every primitive referenced in "What Already Exists" below is real and named exactly as cited. Reviewers reading this against an older base will see ghosts.

## Goal

Tighten the existing dietary-medical nudge so the cantina never gets blindsided by a human on a 6+ hour shift with no dietary info on file. Two changes:

1. **Earlier soft prompt** — surface the dashboard Things-To-Do card whenever `DietaryPreference` is empty, regardless of whether the human has signed up for any shift yet. Today the card only fires after a qualifying signup exists.
2. **Hard gate at shift signup** — block the signup attempt for any 6+ hour ("qualifying") shift until dietary is filled, mirroring the existing `RedirectIfNameMissing` redirect-and-replay pattern. Plus a persistent banner + disabled "Sign Up" buttons across `/Shifts` and `/Shifts/Mine` for humans who already have a qualifying signup from before this lands.

## Non-Goals

- **No new entity, column, or migration.** All needed fields (`DietaryPreference`, `Allergies`, `Intolerances`, `MedicalConditions`, `AllergyOtherText`, `IntoleranceOtherText`) exist on `VolunteerEventProfile`.
- **No modal / JS / HTMX.** Issue #279 explicitly considered and rejected a modal; this design stays full-page + server-side banner.
- **No notification email.** Banner on next dashboard/shifts load and redirect at next signup attempt are sufficient.
- **No retroactive backfill job.** Humans with a pre-existing qualifying signup and empty dietary just see the banner the next time they hit `/Shifts` or `/Shifts/Mine`.
- **No new form / no changes to `/Profile/Me/DietaryMedical`.** The existing form is reused as the destination for both the soft card and the hard redirect.
- **No new role / policy.** Authorization is unchanged.

## What Already Exists

| Primitive | Location | Role in this design |
|---|---|---|
| `VolunteerEventProfile.DietaryPreference` | `Humans.Domain.Entities.VolunteerEventProfile` | Sentinel: `!string.IsNullOrEmpty(...)` ⇒ "answered" |
| `Shift.QualifiesForCantinaMeal()` | `Humans.Domain.Entities.Shift` | Pure helper, returns true for all-day or `Duration >= 6h` shifts |
| `IShiftSignupRepository.GetActiveSignupsForUserAsync(userId, ct)` / `IShiftSignupService.GetActiveSignupsForUserAsync(userId, ct)` | `Humans.Application.Interfaces.Repositories` / `Humans.Application.Interfaces.Shifts` | Returns user's `Pending`/`Confirmed` signups eager-loading `Shift.Rota.EventSettings`. Used internally by `HasQualifyingCantinaSignupAsync`; available to view-components/page-builders if a non-aggregate read is needed. |
| `IShiftManagementService.HasQualifyingCantinaSignupAsync(userId, ct)` | `Humans.Application.Services.Shifts` | True iff user has any active qualifying signup — already used by `ThingsToDoViewComponent` |
| `ThingsToDoViewComponent` | `Humans.Web.ViewComponents` | Renders dashboard "Things to do" card; currently gates dietary item on `HasQualifyingCantinaSignup && DietaryEmpty` |
| `ProfileController.DietaryMedical` (GET/POST) | `Humans.Web.Controllers` | Full-page form at `/Profile/Me/DietaryMedical` |
| `_EventRotaTable.cshtml` | `Humans.Web.Views.Shared` | Renders the per-shift Sign-Up form/button row |
| GDPR plumbing for `VolunteerEventProfile` | `AccountDeletionService` step 5, `GdprExportSections.VolunteerEventProfiles` | Unchanged — no new data covered |

## Behavior Matrix

Sentinel everywhere: **dietary empty** ⇔ `string.IsNullOrEmpty(profile.DietaryPreference)`.

| Human's state | What they see |
|---|---|
| Dietary empty, **no qualifying signup** | **Soft:** Things-To-Do card on dashboard with copy *"Tell us about your food needs so we're ready when you sign up."* Action button links to `/Profile/Me/DietaryMedical`. |
| Dietary empty, **has ≥1 qualifying signup** (pre-existing) | **Hard:** Red Bootstrap-style banner at the top of `/Shifts` and `/Shifts/Mine` with title + CTA. All Sign-Up buttons in `_EventRotaTable` render `disabled` with tooltip *"Tell us your food needs first."* The Things-To-Do card still shows the existing-copy variant (*"You're signed up for a long shift — the cantina needs to know your food preferences."*). |
| Dietary empty, **clicks Sign-Up on a qualifying shift** | **Hard redirect:** Server-side redirect to `/Profile/Me/DietaryMedical?returnAction=signup&shiftId={id}` (or `returnAction=signuprange&rotaId={id}&startDayOffset={n}&endDayOffset={m}` for `SignUpRange`). On successful POST, the controller replays the signup call, then redirects to the standard post-signup destination with the standard success message. |
| Dietary empty, **clicks Sign-Up on a non-qualifying shift, no qualifying signups yet** | No gate. Signup proceeds. Soft card still shows on dashboard. |
| Dietary empty, **clicks Sign-Up on any shift while banner is showing** | Button is rendered `disabled`; the form POST is also defended server-side by re-running the banner gate before delegating to `_signupService`. |
| Dietary filled | Card hidden, banner hidden, buttons enabled, no redirect. |

### Voluntelling (on-behalf-of signup)

`ShiftsController.SignUp` and `SignUpRange` always sign up the **current** user — there is no target-user parameter. `IsPrivilegedSignupApprover` only relaxes signup-validation rules (overlap checks etc.); it does not switch the actor. So no privileged-actor bypass is needed in these endpoints: the actor and the human being signed up are always the same person, and the hard gate is correct for both.

Voluntell flows (a coordinator creating a signup for another human via `ShiftAdminController` or the manage panel) are out of scope for the redirect gate — the coordinator's own dietary info is irrelevant there, and the human being voluntolded picks up the banner + Things-To-Do card on their next login (already covered by US-35.4 in the underlying feature spec).

## Code Changes

### 1. `ThingsToDoViewComponent.cs`

- Move the dietary-medical item out of the `if (hasShiftSignups)` branch and gate it solely on `string.IsNullOrEmpty(profile?.DietaryPreference)`.
- Two copy variants driven by `HasQualifyingCantinaSignupAsync`:
  - has-qualifying ⇒ existing `Todo_DietaryMedical_Pending` key
  - no-qualifying ⇒ new `Todo_DietaryMedical_NoShift_Pending` key
- `IsShiftProfileEmpty` is no longer consulted for the dietary item (it's already narrowed in upstream main; this design keeps it untouched for the existing "Set shift preferences" item).

### 2. `ShiftsController` — new `RedirectIfDietaryMissingAsync(user, shift)` / `…ForRangeAsync(user, rotaId, startOffset, endOffset)`

Standalone helpers (no existing precedent in `ShiftsController` on this branch — `SignUp`/`SignUpRange` currently jump straight to `_signupService`). Shape:

- `RedirectIfDietaryMissingAsync(UserInfo user, Guid shiftId, CancellationToken ct)` — loads the shift via the same path the signup uses (already cached in the request), checks `QualifiesForCantinaMeal()`, loads `VolunteerEventProfile` via `_shiftMgmt.GetShiftProfileAsync(user.Id, includeMedical: false)`, checks `DietaryPreference`.
- Returns `null` when dietary already filled OR shift doesn't qualify.
- Otherwise sets info-flash via `SetInfo(_localizer["Shifts_DietaryRequiredBeforeSignup"].Value)` (new key) and returns `RedirectToAction("DietaryMedical", "Profile", new { returnAction = "signup", shiftId })`.
- Range variant signature: `RedirectIfDietaryMissingForRangeAsync(UserInfo user, Guid rotaId, int startDayOffset, int endDayOffset, CancellationToken ct)`. "Any shift in range qualifies" requires reading the rota's shifts in the offset window. The cleanest read path is to expose a new `IShiftSignupService.PeekRangeShiftsAsync(rotaId, startDayOffset, endDayOffset, ct)` (returns `IReadOnlyList<Shift>`) — `SignUpRangeAsync` already does this enumeration internally (`rota.Shifts.Where(s => s.IsAllDay && s.DayOffset between …)` around `ShiftSignupService.cs` line 563); lift it into a separate method that both `SignUpRangeAsync` and the new gate can call. Net Application-layer surface: one new public service method + delegating implementation.
- **Simplification:** the range candidate set is already all-day-only after the existing filter, and `QualifiesForCantinaMeal()` returns true for every all-day shift. So "any shift in range qualifies" reduces to "the filtered list is non-empty" — no extra per-shift predicate is needed in the gate. Tests in `ShiftSignupServiceTests` cover the new method (returns the same set `SignUpRangeAsync` would have iterated).
- Called from `SignUp` and `SignUpRange` immediately after `ResolveCurrentUserOrChallengeAsync`.

### 3. `ProfileController.DietaryMedical` (POST) — replay support

**New dependency:** inject `IShiftSignupService` into `ProfileController`. It's already a registered service used by `ShiftsController`; adding it as a constructor parameter is a one-line change. No abstraction extraction (per project doctrine: extract on the third copy, not the second — this is copy two).

**Hidden-field carryover:** the GET view renders `returnAction`, `shiftId`, `rotaId`, `startDayOffset`, `endDayOffset` as hidden fields on the dietary form. They round-trip on POST.

**Branches** on `returnAction` after a successful save:

| `returnAction` | Behavior |
|---|---|
| `signup` (with `shiftId`) | Call `_signupService.SignUpAsync(user.Id, shiftId, isPrivileged: ShiftRoleChecks.IsPrivilegedSignupApprover(User))`. Redirect to `/Shifts` with flash derived from the signup-result. The mapping is the same shape `ShiftsController.SignUp` inlines today (`!result.Success` ⇒ `SetError(result.Error ?? "Shift signup failed.")`; otherwise `SetSuccess(result.Warning is not null ? $"Signed up successfully. Note: {result.Warning}" : "Signed up successfully!")`) — about 8 lines. Duplicate it inline in `ProfileController` here; that puts the count at two call sites, which per project doctrine is not yet enough to extract. Leave a one-line `// extract on 3rd copy — see ShiftsController.SignUp`. |
| `signuprange` (with `rotaId`, `startDayOffset`, `endDayOffset`) | Call `_signupService.SignUpRangeAsync(user.Id, rotaId, startDayOffset, endDayOffset, isPrivileged: …, skipConflicts: true)`. Flash uses the range-variant strings from `ShiftsController.SignUpRange` ("Signed up for date range!" success / "Shift range signup failed." error), **not** the single-signup strings. |
| `shifts` | Redirect to `/Shifts` with the standard `Profile_DietaryMedical_Saved` success flash. No signup replay. (Used by the banner CTA so the user lands back at the page they came from.) |
| anything else (incl. missing) | Existing default: redirect to `Home/Index` with `Profile_DietaryMedical_Saved`. |

**Signup-replay failure** (shift full, conflict, validation error from `_signupService`) is treated like any other signup failure: the result's error flash wins, redirect is still `/Shifts`. The dietary save itself is not rolled back — the user got the info in, which is what we ultimately want.

**Validation failure on the dietary form itself** (before the save) re-renders the form with the carryover hidden fields preserved; no signup replay happens.

### 4. New `_DietaryMissingBanner` view component + partial

- `DietaryMissingBannerViewComponent` reads current user, returns `Content("")` unless `HasQualifyingCantinaSignupAsync && DietaryPreference is empty`.
- When it should render: Bootstrap `alert-danger` with localized title, body, and a primary CTA button linking to `/Profile/Me/DietaryMedical?returnAction=shifts`.
- Invoked from `/Shifts/Index.cshtml` and `/Shifts/Mine.cshtml` at the top of the page content area (above any rota tables).

### 5. Rota tables — Sign-Up button lockout

Two view models bind to the rota partials, both need the flag:

- `EventRotaTableViewModel` (`Humans.Web.Models.EventRotaTableViewModel`, defined in `src/Humans.Web/Models/ShiftViewModels.cs` line ~626) — used by `_EventRotaTable.cshtml`.
- `BuildStrikeRotaTableViewModel` (same file, line ~603) — used by `_BuildStrikeRotaTable.cshtml`.

Add `public bool SignupsBlockedByMissingDietary { get; set; }` to both. The page-builders for `/Shifts/Index` and `/Shifts/Mine` compute the boolean once per request (same value the banner uses, ideally via a `ShiftsPageGate.BlockedByMissingDietary(userId, ct)` helper to dedupe) and set it on every `EventRotaTableViewModel` / `BuildStrikeRotaTableViewModel` they construct.

When `true`, the partials replace `<button type="submit">…Sign Up…</button>` with `<button type="submit" disabled aria-disabled="true" title="…">…Sign Up…</button>`. Form element is still rendered so layout doesn't reflow.

Defense-in-depth: the controller-level `RedirectIfDietaryMissingAsync` catches any POST that slips past a disabled button (curl, stale tab, browser-extension shenanigans).

### 6. Resource keys (EN + ES)

New (or new variants of existing):

| Key | Used by | Example EN |
|---|---|---|
| `Todo_DietaryMedical_NoShift_Pending` | Things-To-Do card, no-shift variant | "Tell us about your food needs so we're ready when you sign up." |
| `DietaryMissingBanner_Title` | Banner | "We need your dietary info" |
| `DietaryMissingBanner_Body` | Banner | "You're signed up for a long shift. The cantina can't plan your meals until you tell us your food preferences." |
| `DietaryMissingBanner_Cta` | Banner button | "Tell us now" |
| `Shifts_SignupDisabledTooltip_MissingDietary` | Disabled Sign-Up button tooltip | "Tell us your food needs first." |
| `Shifts_DietaryRequiredBeforeSignup` | Flash before redirect from `SignUp` / `SignUpRange` | "Quick stop — we need your dietary info before signing up for this shift." |

Strings land in `src/Humans.Web/Resources/SharedResource.resx` and `.es.resx` (plus `.ca`, `.de`, `.fr`, `.it` to match the existing translation coverage in `src/Humans.Web/Resources/` on this branch).

## Tests

TDD per project doctrine (logic + regression-prone). Place under `tests/Humans.Web.Tests/`.

### `ThingsToDoViewComponentTests`

- New: dietary empty + no qualifying signup ⇒ item appears with `_NoShift_Pending` description, action URL set, `IsDone == false`.
- Regression: dietary empty + has qualifying signup ⇒ existing copy still used.
- Regression: dietary filled ⇒ item not added.

### `ShiftsControllerTests` (`SignUp` and `SignUpRange`)

- New: dietary empty + qualifying shift ⇒ redirect to `Profile.DietaryMedical?returnAction=signup&shiftId={id}`, `_signupService` **not** called.
- New: dietary empty + non-qualifying shift ⇒ no redirect, `_signupService.SignUpAsync` called.
- New: dietary empty + qualifying shift + privileged approver (self-signup, `isPrivileged: true`) ⇒ redirect still fires (the actor IS the user being signed up).
- Regression: dietary filled ⇒ no redirect, signup proceeds.
- Range variant: any shift in range qualifies + dietary empty ⇒ redirect with `returnAction=signuprange` + range params.
- Range variant: no shift in range qualifies + dietary empty ⇒ no redirect, range signup proceeds.

### `ProfileControllerTests` (`DietaryMedical` POST)

- New: valid save + `returnAction=signup` + `shiftId` ⇒ `_signupService.SignUpAsync` is called with the parsed shiftId; redirect to `/Shifts` with success flash.
- New: valid save + `returnAction=signuprange` + range params ⇒ `_signupService.SignUpRangeAsync` called; redirect to `/Shifts`.
- New: valid save + `returnAction=shifts` ⇒ no signup replay; redirect to `/Shifts` with `Profile_DietaryMedical_Saved` flash.
- New: valid save + signup replay returns failure result ⇒ still redirect to `/Shifts`, error flash from signup-result helper, dietary save persisted.
- Regression: valid save + no `returnAction` ⇒ existing default redirect to `Home/Index`.
- Validation: failed POST never triggers signup replay (no `IShiftSignupService` calls).

### `DietaryMissingBannerViewComponentTests`

- Renders when `HasQualifyingCantinaSignupAsync == true && DietaryPreference is empty`.
- Returns empty content otherwise (three negative cases: no signup, dietary filled, both).

### Manual / preview verification

- After PR opens, hit preview env (`{pr_id}.n.burn.camp`) and walk both flows end-to-end:
  1. Fresh user, no signups ⇒ dashboard card with `_NoShift` copy ⇒ click ⇒ fill ⇒ card gone.
  2. User with a pre-existing 6h+ signup and no dietary ⇒ `/Shifts` shows banner, signup buttons disabled ⇒ click banner CTA ⇒ fill ⇒ banner gone, buttons enabled.
  3. User with no dietary, clicks Sign Up on a 6h+ shift ⇒ redirect ⇒ fill ⇒ auto-returns to `/Shifts` with success flash for the signup.

## Authorization / GDPR

No changes. All authorization is delegated to the existing `/Profile/Me/DietaryMedical` page and `ShiftsController.SignUp` paths. No new data is collected, so GDPR posture (right-to-erasure, export, retention) is unchanged — `docs/features/profiles/dietary-medical-nudge.md` already covers it.

## Edit-page entry point (added during implementation)

Added at the user's request after smoke-testing, **not in the original design above**. Surfaces **meal preference + allergies** under the **General Information** section of `/Profile/Me/Edit` as a second entry point, keeping the dedicated `/Profile/Me/DietaryMedical` page.

- **Scope:** only `DietaryPreference` + `Allergies` (+ `AllergyOtherText`). `Intolerances` / `IntoleranceOtherText` / `MedicalConditions` stay owned by the DietaryMedical page — medical is GDPR Art. 9 health data and is deliberately kept off the general profile form.
- **Write path:** `ProfileController.Edit` POST loads the existing `VolunteerEventProfile` via `GetOrCreateShiftProfileAsync` and sets only the three fields, so it never clobbers the DietaryMedical-owned fields (regression-tested in `ProfileControllerEditTests`).
- **Read path:** Edit GET populates the three fields from `GetShiftProfileAsync(includeMedical: false)`.
- **UI:** mirrors `DietaryMedical.cshtml` (radio + allergy chips + "Other" reveal), reuses existing `Profile_DietaryMedical_*` resource keys and `DietaryOptions` sets.
- **Validation:** allergy "Other" requires text (mirrors the DietaryMedical POST). `DietaryPreference` enum validation is the same known gap noted below.

See US-35.7 in `docs/features/profiles/dietary-medical-nudge.md`.

## Out of Scope (YAGNI)

Recorded so the next engineer doesn't ask:

- **Email/push notification.** Soft + hard prompts are visible-on-next-page-load only.
- **Modal/HTMX nudge.** Rejected for issue #279 and re-rejected here. Full-page form is the pattern.
- **Backfill job** to enumerate humans needing the prompt and push it. Banner-on-next-load handles it lazily.
- **Annual re-confirmation** of dietary info. Sentinel is "field non-empty" forever; re-prompting after N months is a future concern with its own spec.
- **Dietary preference as a controlled enum** at the column level. Form already constrains to `Omnivore / Vegetarian / Vegan / Pescatarian` via radio group; the column stays `varchar(200)?` so legacy free-text values from before issue #279 don't need migration.
- **Generic "required-field redirect" abstraction.** With exactly two instances (`Name`, `Dietary`), copy-paste is cheaper than extracting. Re-evaluate at instance three.

## Risks

- **Cache invalidation around the banner.** `ThingsToDoViewComponent` already re-fetches per-request; the banner does the same. No new cache layer; the same staleness window (effectively zero) applies.
- **Privileged-approver test gap.** The privileged-bypass branch is easy to forget; the test plan above covers it explicitly.
- **Disabled-button accessibility.** `disabled` on `<button>` removes it from keyboard tab order; pair with `aria-disabled="true"` and the tooltip text in a `title` attribute (kept short for screen readers).

## Filenames Touched

- `src/Humans.Web/ViewComponents/ThingsToDoViewComponent.cs`
- `src/Humans.Web/ViewComponents/DietaryMissingBannerViewComponent.cs` *(new)*
- `src/Humans.Web/Views/Shared/Components/DietaryMissingBanner/Default.cshtml` *(new)*
- `src/Humans.Web/Controllers/ShiftsController.cs`
- `src/Humans.Web/Controllers/ProfileController.cs`
- `src/Humans.Web/Views/Shared/_EventRotaTable.cshtml`
- `src/Humans.Web/Views/Shifts/Index.cshtml`
- `src/Humans.Web/Views/Shifts/Mine.cshtml`
- `src/Humans.Web/Models/Shifts/*` *(view-model `SignupsBlockedByMissingDietary` flag + page-builder wiring)*
- `src/Humans.Web/Resources/SharedResource.resx` + `.es.resx`, `.ca.resx`, `.de.resx`, `.fr.resx`, `.it.resx`
- `tests/Humans.Web.Tests/ViewComponents/ThingsToDoViewComponentTests.cs`
- `tests/Humans.Web.Tests/Controllers/ShiftsControllerTests.cs`
- `tests/Humans.Web.Tests/Controllers/ProfileControllerTests.cs`
- `tests/Humans.Web.Tests/ViewComponents/DietaryMissingBannerViewComponentTests.cs` *(new)*
- `docs/features/profiles/dietary-medical-nudge.md` *(append US-35.5 + US-35.6 referencing this design)*

<!-- freshness:triggers
  src/Humans.Domain/Entities/VolunteerEventProfile.cs
  src/Humans.Infrastructure/Data/Configurations/Shifts/VolunteerEventProfileConfiguration.cs
  src/Humans.Application/Services/Shifts/ShiftSignupService.cs
  src/Humans.Application/Services/Shifts/ShiftManagementService.cs
  src/Humans.Web/ViewComponents/ThingsToDoViewComponent.cs
  src/Humans.Web/Controllers/ProfileController.cs
  src/Humans.Web/Views/Shared/_VolunteerProfileBadges.cshtml
-->
<!-- freshness:flag-on-change
  Qualifying-shift threshold, dietary/medical field shape, NoInfoAdmin visibility rule, or Things-to-do card composition.
-->

# 35 — Dietary & Medical Nudge

## Business Context

The cantina feeds humans working shifts of 6+ hours. Dietary preferences, allergies, intolerances, and medical conditions are only relevant once someone has actually signed up for a qualifying shift — collecting them at preference-setup time is premature and was removed from `/Profile/ShiftInfo` when Feature 33 (Shift Preference Wizard) shipped.

This feature replaces that loss by surfacing the questions exactly when the data becomes useful: when a human signs up for (or is voluntold into) a 6+ hour shift and the cantina therefore needs to plan their meals.

## Authorization

- **Filling out the form:** any authenticated user — for their own profile only.
- **Reading medical conditions:** owner, `NoInfoAdmin`, `Admin`. Same restriction the existing `_VolunteerProfileBadges` partial enforces via its `ShowMedical` flag.
- **Reading dietary preference, allergies, intolerances:** any user who can already see the volunteer's `VolunteerEventProfile` (coordinators, shift admins, the badges partial in non-medical mode).

No new role; no new policy. Reuse existing `ShowMedical` plumbing and the existing shift-profile read paths.

## GDPR (special-category data)

`MedicalConditions` is health data under GDPR Art. 9. No new handling is required — `VolunteerEventProfile` is already covered by the existing right-to-erasure and data-export flows:

- **Erasure:** `AccountDeletionService` step 5 deletes the user's `VolunteerEventProfile` rows.
- **Export:** the user's `VolunteerEventProfile` rows are emitted via `GdprExportSections.VolunteerEventProfiles` (contributed by `ShiftSignupService`).

No retention policy changes; the data lives only as long as the user account does.

## User Stories

### US-35.1: Nudge appears after qualifying shift signup
**As a** human who has just signed up for a long shift
**I want to** be reminded to record my dietary and medical info
**So that** the cantina can feed me appropriately and coordinators can plan around any conditions

**Acceptance Criteria:**
- A "Dietary & medical info" item appears in the dashboard `ThingsToDoViewComponent` card iff **all** of:
  1. The user has at least one `ShiftSignup` in `Pending` or `Confirmed` status …
  2. … on a `Shift` whose effective duration is ≥ 6 hours (see "Qualifying shift" below), and
  3. The user's `VolunteerEventProfile.DietaryPreference` is null/empty.
- Item title: "Tell us about your food needs" (resource-key `Todo_DietaryMedical_Title`).
- Item description (pending): "We need this to plan cantina meals" (resource-key `Todo_DietaryMedical_Pending`).
- Item description (done): "Thanks — we've got it" (resource-key `Todo_DietaryMedical_Done`).
- Action button: "Fill out" → `Url.Action("DietaryMedical", "Profile")`.
- Icon class: `fa-solid fa-utensils`.
- Item is marked done (and disappears with the rest of the card when no other items remain) once `DietaryPreference` is set, regardless of whether the user also filled out allergies/intolerances/medical.
- Item is **never shown** to users without a qualifying signup, even if their dietary fields are empty.

### US-35.2: Form collects dietary, allergy, intolerance, medical info
**As a** human filling out the nudge
**I want to** record my food preferences, allergies, intolerances, and any medical conditions
**So that** the cantina and coordinators have what they need

**Acceptance Criteria:**
- Route: `GET /Profile/Me/DietaryMedical` (form view), `POST /Profile/Me/DietaryMedical` (save).
- Renders as a standalone full page. URL is shareable / bookmarkable / works without JS. The Things-to-do card item navigates to this page; there is no modal overlay. (Modal-from-dashboard was considered but dropped — no HTMX or modal-coordination infrastructure exists yet in the app, and introducing it for a single nudge is not warranted.)
- Fields, in order:
  - **Dietary preference** — required radio group: `Omnivore`, `Vegetarian`, `Vegan`, `Pescatarian`.
  - **Allergies** — optional multi-select chips: `Peanut`, `Tree nut`, `Dairy`, `Egg`, `Shellfish`, `Wheat/Gluten`, `Soy`, `Sesame`, `Other`. Choosing `Other` reveals a single-line text input (`AllergyOtherText`, max 500 chars — matches existing DB length).
  - **Intolerances** — optional multi-select chips: `Lactose`, `Gluten`, `Histamine`, `FODMAP`, `Other`. Choosing `Other` reveals a single-line text input (`IntoleranceOtherText`, max 500 chars — matches existing DB length).
  - **Medical conditions** — optional free-text textarea (max 4000 chars, the existing DB length). Hint copy: "Only visible to you and the No-Info Admins. Anything coordinators should know — diabetes, epilepsy, severe injuries, etc."
- All values persist to `VolunteerEventProfile` columns of the same name. No new columns, no migration.
- POST validates: dietary preference must be one of the four enum values; "Other" text fields required iff `Other` is selected in their parent chip; medical conditions ≤ 4000 chars; allergy/intolerance items must be from the allowed set or `Other`.
- On success: redirect back to `/` (dashboard). The Things-to-do card re-renders on the dashboard with the dietary/medical item gone (per US-35.1).
- On validation failure: re-render with errors, preserve all entered values.

### US-35.3: Edit later from profile
**As a** human whose situation changes
**I want to** revisit and edit my dietary/medical info
**So that** updates reach the cantina before the next event

**Acceptance Criteria:**
- A "Dietary & medical info" link appears on `/Profile/Me` (in the same vertical nav-link list as the existing Shift Info / Communication Preferences links).
- Link is visible to any authenticated user — not gated by qualifying-shift status (a user can fill this out proactively).
- Clicking it navigates to `GET /Profile/Me/DietaryMedical` as the full-page view, pre-populated with the current values.
- The "Dietary & medical info" Things-to-do item still only appears under the qualifying-shift gate; the profile edit link is the unconditional path.

### US-35.4: Voluntold humans get the same nudge
**As a** coordinator who voluntells a human into a long shift
**I want** that human to see the nudge on their next dashboard load
**So that** the cantina doesn't get blindsided by unknown dietary needs

**Acceptance Criteria:**
- The nudge fires regardless of how the signup was created — self-signup (`Enrolled = false`) and coordinator-voluntolded (`Enrolled = true`) both qualify. The gate cares only about status (`Pending`/`Confirmed`) and shift duration; `Enrolled` is informational, not part of the gate.
- No email or push notification is sent — surfacing on next dashboard visit is sufficient.

### US-35.5: Nudge appears before any shift signup
**As a** newly registered human with no shifts yet
**I want to** be reminded to record my dietary and medical info
**So that** the cantina has it on file the moment I sign up for a long shift

**Acceptance Criteria:**
- The dashboard `ThingsToDoViewComponent` dietary item appears whenever `VolunteerEventProfile.DietaryPreference` is empty, regardless of signups.
- Description uses `Todo_DietaryMedical_NoShift_Pending` when there is no qualifying signup; the existing `_Pending` copy is used otherwise.
- See `docs/superpowers/specs/2026-05-25-dietary-prompt-tightening-design.md`.

### US-35.6: Hard gate on qualifying-shift signup
**As** the system
**I want to** block sign-up for a 6+ hour shift until the human's dietary preference is on file
**So that** the cantina never gets blindsided

**Acceptance Criteria:**
- `ShiftsController.SignUp` / `SignUpRange` redirect to `/Profile/Me/DietaryMedical?returnAction=signup|signuprange&...` when the target shift `QualifiesForCantinaMeal()` and `DietaryPreference` is empty.
- On successful save, the form replays the signup (`ProfileController.DietaryMedical` POST branches on `returnAction`).
- A banner on `/Shifts` and `/Shifts/Mine` (`DietaryMissingBannerViewComponent`) plus disabled Sign-Up buttons catch humans who already have a qualifying signup but no dietary on file.
- See `docs/superpowers/specs/2026-05-25-dietary-prompt-tightening-design.md`.

### US-35.7: Meal preference + allergies editable from the main profile
**As a** human filling in my profile
**I want to** set my meal preference and allergies under General Information on `/Profile/Me/Edit`
**So that** I don't have to discover the dedicated dietary page to record the basics

**Acceptance Criteria:**
- `/Profile/Me/Edit` → **General Information** shows a meal-preference radio group + allergy chips (with an "Other" free-text reveal), reusing the `Profile_DietaryMedical_*` resource keys and the `DietaryOptions` option sets.
- These write to the **same** `VolunteerEventProfile` fields as the dedicated `/Profile/Me/DietaryMedical` page; the Edit save updates **only** `DietaryPreference` + `Allergies` (+ `AllergyOtherText`) and leaves `Intolerances` / `IntoleranceOtherText` / `MedicalConditions` untouched (those remain owned by the DietaryMedical page — medical is GDPR Art. 9 health data kept off the general profile form).
- The dedicated `/Profile/Me/DietaryMedical` page is retained (it's the redirect target for the signup hard-gate and the banner CTA).
- See `docs/superpowers/specs/2026-05-25-dietary-prompt-tightening-design.md` (§ "Edit-page entry point").

## Qualifying Shift

A shift qualifies the user for the nudge when:

- `Shift.IsAllDay = true` (all-day shifts run 08:00–18:00 per `Shift.AllDayWindowStart/End` = 10 hours), **OR**
- `Shift.Duration ≥ Duration.FromHours(6)`.

Computed via a new pure helper `Shift.QualifiesForCantinaMeal()` on the entity (no service call, no DB hit beyond the existing signup → shift join). Adding it on the entity keeps the rule co-located with the data; bumping the threshold later is a one-line change.

The check operates over the user's **currently-active signups only**:

- `Pending` and `Confirmed` count.
- `Refused`, `Bailed`, `NoShow`, `Cancelled` do not.
- Past shifts (`Shift.GetAbsoluteEnd(eventSettings) < clock.Now()`) do not — the cantina need is forward-looking.

## Data Model

No schema changes. All fields already exist on `VolunteerEventProfile`:

| Column | Type | Already exists | Notes |
|---|---|---|---|
| `DietaryPreference` | `varchar(200)?` | yes | Sentinel for "answered": `!string.IsNullOrEmpty(...)` ⇒ nudge done |
| `Allergies` | `jsonb` (List&lt;string&gt;) | yes | Stored as Postgres `jsonb` via `ConfigureJsonbList`, surfaced as `List<string>` |
| `Intolerances` | `jsonb` (List&lt;string&gt;) | yes | Stored as Postgres `jsonb` via `ConfigureJsonbList`, surfaced as `List<string>` |
| `AllergyOtherText` | `varchar(500)?` | yes | Required iff `Other` ∈ Allergies |
| `IntoleranceOtherText` | `varchar(500)?` | yes | Required iff `Other` ∈ Intolerances |
| `MedicalConditions` | `varchar(4000)?` | yes | Restricted visibility |

`UpdatedAt` is bumped on save (existing pattern).

## Triggers (state changes that update the nudge)

| Event | Effect |
|---|---|
| User saves any non-empty `DietaryPreference` | Nudge disappears |
| User clears `DietaryPreference` back to null | Nudge reappears (if qualifying shift still active) |
| User signs up for a qualifying shift | Nudge appears (if dietary still empty) |
| User bails / refuses / no-shows their last qualifying signup | Nudge disappears |
| Last qualifying shift ends (passes in time) | Nudge disappears |
| User is voluntold into a qualifying shift | Nudge appears (if dietary still empty) |

## Cross-section dependencies

`VolunteerEventProfile` lives behind `IShiftManagementService` today (`GetShiftProfileAsync(userId, includeMedical)`, `UpdateShiftProfileAsync(profile)`, backed by `IShiftManagementRepository`). This feature **reuses that surface** — no new repository, no new service, no Profile→Shifts coupling.

- **Shifts** (reads, gate): `ShiftSignup` status + `Shift.Duration`/`IsAllDay` to compute qualifying-shift gate. New method `IShiftManagementService.HasQualifyingCantinaSignupAsync(Guid userId, CancellationToken ct)` on the existing service. Pure-query, no `Include` of `User`. Internally calls `Shift.QualifiesForCantinaMeal()` (new pure helper on the entity).
- **Shifts** (reads, profile): `IShiftManagementService.GetShiftProfileAsync(userId, includeMedical: true)` — already exists. The new dietary/medical form view uses this to pre-populate.
- **Shifts** (writes, profile): `IShiftManagementService.UpdateShiftProfileAsync(profile)` — already exists. The form POST in `ProfileController` mutates `DietaryPreference` / `Allergies` / `Intolerances` / `AllergyOtherText` / `IntoleranceOtherText` / `MedicalConditions` on the loaded entity and calls update. No new persistence method needed.
- **Profile** (controller only): `ProfileController` gets a new action pair `DietaryMedical` (GET form / POST save). The controller depends on `IShiftManagementService` directly — `IProfileService` is **not** involved (VEP is not Profile-service territory).
- **Dashboard / ThingsToDo**: `ThingsToDoViewComponent` gets a new branch that calls `IShiftManagementService.HasQualifyingCantinaSignupAsync` for the gate and reuses the already-loaded `GetShiftProfileAsync(includeMedical: false)` result for the dietary-empty check. The current `IsShiftProfileEmpty(...)` helper is **narrowed** to skills/quirks/languages only — dietary/medical move into the new branch and are not part of the generic shift-info-empty check anymore.

## Negative access rules

- A user **cannot** see another user's `MedicalConditions` via the form page, the badges partial, or any read API unless they are `NoInfoAdmin` or `Admin`. The dietary/medical save endpoint is owner-scoped (action filter on `ProfileController` already enforces this on edit routes).
- A coordinator viewing volunteer search results sees the dietary preference badge but never the medical conditions string unless `ShowMedical` is true (already wired).

## Out of scope

- No email reminder if the user ignores the nudge — surfacing on dashboard is the entire intervention.
- No bulk export for the cantina in this feature. That's a separate cantina-roster report.
- No per-event override — these fields are global to the user, not per-event. (If a user's diet changes between events, they edit before the new event.)
- No "I have nothing to declare" flag separate from picking Omnivore — selecting any DietaryPreference value (including Omnivore with empty allergies) counts as answered.

## Related Features

- [33 — Shift Preference Wizard](../shifts/shift-preference-wizard.md): removed dietary/medical from `/Profile/ShiftInfo`, creating the need for this nudge.
- [25 — Shift Management](../shifts/shift-management.md): supplies the `ShiftSignup` rows the gate queries.
- [Issue #273](https://github.com/nobodies-collective/Humans/issues/273): Dashboard "Things to do" card pattern — this feature adds one more item type.
- [Issue #279](https://github.com/nobodies-collective/Humans/issues/279): tracking issue (this spec fleshes it out and unblocks).

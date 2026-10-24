<!-- freshness:triggers
  src/Sections/Humans.Cantina/Domain/DietaryProfile.cs
  src/Sections/Humans.Cantina/Domain/DietaryOptions.cs
  src/Sections/Humans.Cantina/Services/DietaryProfileService.cs
  src/Sections/Humans.Cantina/Controllers/CantinaProfileController.cs
  src/Sections/Humans.Cantina/SectionThingsToDo.cs
  src/Sections/Humans.Users/SectionThingsToDo.cs
  src/Sections/Humans.Shifts/Services/ShiftManagementService.cs
  src/Sections/Humans.Shifts/ViewComponents/DietaryMissingBannerViewComponent.cs
  src/Humans.Base/ViewComponents/ThingsToDoViewComponent.cs
-->
<!-- freshness:flag-on-change
  Qualifying-shift threshold, dietary field shape, medical visibility rule, or Things-to-do card composition.
-->

# 35 — Dietary Nudge (and the Medical prompt)

## Business Context

The cantina feeds humans working shifts of 6+ hours. Meal preference, allergies and intolerances matter once someone signs up for a qualifying shift, so `/Shifts/ShiftInfo` (the preference wizard) does not collect them.

**Dietary info is opt-in** (nobodies-collective/Humans#2104, Peter's decision). Missing dietary info never blocks a shift signup. We prompt while it is missing, and every prompt offers **"I don't want to answer"**; once chosen, the prompts stop.

The food answers are Cantina's (`cantina_dietary_profiles`, Users decomposition commit 10). Medical conditions stay on the Users `Profile` and have their own form; they are GDPR Art. 9 health data and never enter Cantina.

## Authorization

- **Dietary form** (`/Cantina/Dietary`, Cantina's `CantinaProfileController`): any authenticated user, own answers only — the controller takes the user from the principal, never from the route.
- **Medical form** (`/Profile/Me/Medical`, Users' `ProfileController`): any authenticated user, own conditions only.
- **Reading medical conditions:** owner, `NoInfoAdmin`, `Admin` (`MedicalDataViewer`). Shifts' badges keep their `ShowMedical` gate; Cantina never reads or returns medical data.
- **Reading dietary answers:** any surface that already shows the volunteer's shift profile (coordinators, shift admins, volunteer search) and the `CantinaAdminOrAdmin` roster, all through `ICantinaService`.

Both forms are exempt from `MembershipRequiredFilter` so a not-yet-Active member can still answer them.

## GDPR

- **Dietary:** Cantina's `DietaryProfileService` exports the `DietaryProfile` slice (including `DeclinedToAnswer`) and deletes the row on erasure; merges keep the survivor's answers (the source's move only when the survivor has none).
- **Medical:** stays in Users' `Profile` export/erasure. The legacy food columns on `profiles` stay in Users' export and are erased with the profile until the retirement drops them.

## User Stories

### US-35.1: Things-to-do nudge
**Acceptance Criteria:**
- Cantina's `SectionThingsToDo` contributes a `"dietary"` item while the `ICantinaService.GetDietaryProfileAsync` read shows no dietary preference on file (falling back to the legacy profile columns until copied) and not declined.
- Title `Todo_DietaryMedical_Title`; description `Todo_DietaryMedical_Pending` when `IShiftService.HasQualifyingCantinaSignupAsync` is true, else `Todo_DietaryMedical_NoShift_Pending`; action `Todo_DietaryMedical_Action` → `CantinaProfile/Dietary`; icon `fa-solid fa-utensils`.
- Any non-empty preference (including Omnivore with no allergies) or "I don't want to answer" removes it.
- Self-signups and voluntold signups count alike (`Pending`/`Confirmed`, future, qualifying shift). No email or push.

### US-35.2: Dietary form
**Acceptance Criteria:**
- `GET`/`POST /Cantina/Dietary`; full page, works without JS.
- Fields: **Dietary preference** (required radio: Omnivore, Vegetarian, Vegan, Pescatarian); **Allergies** (chips from `DietaryOptions.AllergyOptions`, `Other` reveals `AllergyOtherText`, ≤ 500); **Intolerances** (chips, `Other` reveals `IntoleranceOtherText`, ≤ 500).
- POST refuses `Other` without text; chips are filtered against `DietaryOptions`; Other text is kept only while `Other` is selected. The preference is not re-validated beyond being non-blank.
- Says the form is optional and never affects shift signups; shows a note when the member previously declined.
- A secondary **"I don't want to answer"** button (`DeclineToAnswer=true`, `formnovalidate`) records the opt-out: answers cleared, `DeclinedToAnswer` set, no validation.
- On success: redirect to `/Shifts` when `returnAction=shifts`, else Home; on failure re-render with values kept. Saving answers clears a prior decline.

### US-35.3: Medical prompt (Users)
**Acceptance Criteria:**
- Users' `SectionThingsToDo` contributes a `"medical"` item (`Todo_Medical_Title`, `Todo_Medical_Pending`, `fa-solid fa-notes-medical`) → `Profile/Medical`, only while `MedicalConditions` is empty **and** Cantina's profile shows neither a preference nor `DeclinedToAnswer` (Users composes this itself) — it rides alongside the dietary nudge and disappears with it (an answer or a decline).
- `/Profile/Me/Medical` is a free-text textarea (≤ 4000), trimmed, blank saves as null. Hint: only visible to the member and No-Info Admins.

### US-35.4: Edit later from profile
- `/Profile/Me` quick actions link to both forms unconditionally; `/Profile/Me/Edit` links to the dietary form instead of embedding it.

### US-35.5: Gentle banner on /Shifts, never a gate
**Acceptance Criteria:**
- No signup path checks dietary info: every Sign-Up button stays enabled and `ToggleDay` signs up regardless (nobodies-collective/Humans#2104).
- `DietaryMissingBannerViewComponent` on `/Shifts` and `/Shifts/Mine` shows an info banner while the member holds a qualifying signup **and** Cantina's profile shows neither a preference nor `DeclinedToAnswer` (Shifts composes this itself).
- The banner offers **Tell us now** (`/Cantina/Dietary?returnAction=shifts`) and **I don't want to answer** (a POST to `/Cantina/Dietary` with `DeclineToAnswer=true`, back to `/Shifts`), after which it no longer renders.

## Qualifying Shift

A shift qualifies when `Shift.IsAllDay` (08:00–18:00) **or** `Shift.Duration ≥ 6h` (`Shift.QualifiesForCantinaMeal()`), over the member's `Pending`/`Confirmed` signups that have not yet ended.

## Data Model

`cantina_dietary_profiles` (Cantina's [section doc](../Cantina.md)), one row per user:

| Column | Type | Notes |
|---|---|---|
| `UserId` | `uuid` PK | |
| `DietaryPreference` | `varchar(200)?` | Non-empty ⇒ answered |
| `DeclinedToAnswer` | `bool?` (null = unanswered) | "I don't want to answer"; food fields empty; cleared by a save |
| `Allergies` | `jsonb` list | |
| `AllergyOtherText` | `varchar(500)?` | Only with `Other` |
| `Intolerances` | `jsonb` list | |
| `IntoleranceOtherText` | `varchar(500)?` | Only with `Other` |
| `UpdatedAt` | `timestamptz` | |

The legacy `profiles` food columns are read only by Cantina's fallback and the `cantina.profile-dietary` copy; a later retirement PR drops them. `MedicalConditions` (`varchar(4000)?`) stays on `profiles`.

## Triggers

| Event | Effect |
|---|---|
| Member saves a dietary preference | Dietary nudge, medical prompt and banner clear |
| Member chooses "I don't want to answer" | Answers cleared; dietary nudge, medical prompt and banner clear for good |
| Member saves medical conditions | Medical prompt clears |
| Member signs up / is voluntold into a qualifying shift | Nudge copy switches to the shift variant |
| Last qualifying signup bails, is refused, or ends | Nudge copy switches back to the no-shift variant |

## Cross-section dependencies

- **Shifts** → Cantina: `ICantinaService` for the banner (`GetDietaryProfileAsync`), volunteer search and admin badges. Cantina → Shifts: `IShiftService.HasQualifyingCantinaSignupAsync` (nudge copy).
- **Users** → Cantina: `ICantinaService.GetDietaryProfileAsync` for the medical prompt's "dietary still open" check. Cantina → Users: `IUserService` for the merge chain and legacy fallback.

## Negative access rules

- No one sees another member's `MedicalConditions` unless `MedicalDataViewer`; Cantina's DTOs carry no medical field.
- Neither form takes a user id from the request; there is no admin edit of another member's answers.

## Out of scope

- No email reminder; no per-event override (answers are global to the member).

## Related Features

- [Cantina weekly roster](daily-roster.md): reads the answers for meal planning.
- [33 — Shift Preference Wizard](../../../Humans.Shifts/Docs/features/shift-preference-wizard.md)
- [25 — Shift Management](../../../Humans.Shifts/Docs/features/shift-management.md)
- [Users](../../../Humans.Users/Docs/Users.md): owns `MedicalConditions` and the medical form.

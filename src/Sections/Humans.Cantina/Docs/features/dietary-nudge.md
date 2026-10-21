<!-- freshness:triggers
  src/Sections/Humans.Cantina/Domain/DietaryProfile.cs
  src/Sections/Humans.Cantina/Domain/DietaryOptions.cs
  src/Sections/Humans.Cantina/Services/DietaryProfileService.cs
  src/Sections/Humans.Cantina/Controllers/CantinaProfileController.cs
  src/Sections/Humans.Cantina/SectionThingsToDo.cs
  src/Sections/Humans.Users/SectionThingsToDo.cs
  src/Sections/Humans.Shifts/Services/ShiftManagementService.cs
  src/Sections/Humans.Shifts/Controllers/ShiftsController.cs
  src/Sections/Humans.Shifts/ViewComponents/DietaryMissingBannerViewComponent.cs
  src/Humans.Base/ViewComponents/ThingsToDoViewComponent.cs
-->
<!-- freshness:flag-on-change
  Qualifying-shift threshold, dietary field shape, medical visibility rule, or Things-to-do card composition.
-->

# 35 — Dietary Nudge (and the Medical prompt)

## Business Context

The cantina feeds humans working shifts of 6+ hours. Meal preference, allergies and intolerances matter once someone signs up for a qualifying shift, so `/Profile/Me/ShiftInfo` (the preference wizard) does not collect them.

The food answers are Cantina's (`cantina_dietary_profiles`, Users decomposition commit 10). Medical conditions stay on the Users `Profile` and have their own form; they are GDPR Art. 9 health data and never enter Cantina.

## Authorization

- **Dietary form** (`/Profile/Me/Dietary`, Cantina's `CantinaProfileController`): any authenticated user, own answers only — the controller takes the user from the principal, never from the route.
- **Medical form** (`/Profile/Me/Medical`, Users' `ProfileController`): any authenticated user, own conditions only.
- **Reading medical conditions:** owner, `NoInfoAdmin`, `Admin` (`MedicalDataViewer`). Shifts' badges keep their `ShowMedical` gate; Cantina never reads or returns medical data.
- **Reading dietary answers:** any surface that already shows the volunteer's shift profile (coordinators, shift admins, volunteer search) and the `CantinaAdminOrAdmin` roster, all through `ICantinaService`.

Both forms are exempt from `MembershipRequiredFilter` so a not-yet-Active member can still answer them.

## GDPR

- **Dietary:** Cantina's `DietaryProfileService` exports the `DietaryProfile` slice and deletes the row on erasure; merges keep the survivor's answers (the source's move only when the survivor has none).
- **Medical:** stays in Users' `Profile` export/erasure. The legacy food columns on `profiles` stay in Users' export and are erased with the profile until the retirement drops them.

## User Stories

### US-35.1: Things-to-do nudge
**Acceptance Criteria:**
- Cantina's `SectionThingsToDo` contributes a `"dietary"` item whenever the member has no dietary preference on file (`ICantinaService`, which falls back to the legacy profile columns until copied).
- Title `Todo_DietaryMedical_Title`; description `Todo_DietaryMedical_Pending` when `IShiftManagementService.HasQualifyingCantinaSignupAsync` is true, else `Todo_DietaryMedical_NoShift_Pending`; action `Todo_DietaryMedical_Action` → `CantinaProfile/Dietary`; icon `fa-solid fa-utensils`.
- Any non-empty preference (including Omnivore with no allergies) removes it.
- Self-signups and voluntold signups count alike (`Pending`/`Confirmed`, future, qualifying shift). No email or push.

### US-35.2: Dietary form
**Acceptance Criteria:**
- `GET`/`POST /Profile/Me/Dietary`; full page, works without JS. The old `/Profile/Me/DietaryMedical` URL redirects permanently here, keeping the replay query.
- Fields: **Dietary preference** (required radio: Omnivore, Vegetarian, Vegan, Pescatarian); **Allergies** (chips from `DietaryOptions.AllergyOptions`, `Other` reveals `AllergyOtherText`, ≤ 500); **Intolerances** (chips, `Other` reveals `IntoleranceOtherText`, ≤ 500).
- POST refuses `Other` without text; chips are filtered against `DietaryOptions`; Other text is kept only while `Other` is selected. The preference is not re-validated beyond being non-blank.
- On success: replay the interrupted signup (below) or redirect to `/Profile/Me`; on failure re-render with values kept.

### US-35.3: Medical prompt (Users)
**Acceptance Criteria:**
- Users' `SectionThingsToDo` contributes a `"medical"` item (`Todo_Medical_Title`, `Todo_Medical_Pending`, `fa-solid fa-notes-medical`) → `Profile/Medical`, only while `MedicalConditions` is empty **and** the dietary preference is still open — it rides alongside the dietary nudge and disappears with it.
- `/Profile/Me/Medical` is a free-text textarea (≤ 4000), trimmed, blank saves as null. Hint: only visible to the member and No-Info Admins.

### US-35.4: Edit later from profile
- `/Profile/Me` quick actions link to both forms unconditionally; `/Profile/Me/Edit` links to the dietary form instead of embedding it.

### US-35.5: Hard gate on qualifying-shift signup
**Acceptance Criteria:**
- `ShiftsController.ToggleDay` redirects to `/Profile/Me/Dietary?returnAction=signup&shiftId=...` when the target shift `QualifiesForCantinaMeal()` and `ICantinaService` has no preference.
- On save, `CantinaProfileController` replays the signup through `IShiftSignups` (single shift, range, or back to Shifts/Home per `returnAction`).
- `DietaryMissingBannerViewComponent` on `/Shifts` and `/Shifts/Mine` plus disabled Sign-Up buttons catch members who already hold a qualifying signup without a preference.

## Qualifying Shift

A shift qualifies when `Shift.IsAllDay` (08:00–18:00) **or** `Shift.Duration ≥ 6h` (`Shift.QualifiesForCantinaMeal()`), over the member's `Pending`/`Confirmed` signups that have not yet ended.

## Data Model

`cantina_dietary_profiles` (Cantina's [section doc](../Cantina.md)), one row per user:

| Column | Type | Notes |
|---|---|---|
| `UserId` | `uuid` PK | |
| `DietaryPreference` | `varchar(200)?` | Non-empty ⇒ answered |
| `Allergies` | `jsonb` list | |
| `AllergyOtherText` | `varchar(500)?` | Only with `Other` |
| `Intolerances` | `jsonb` list | |
| `IntoleranceOtherText` | `varchar(500)?` | Only with `Other` |
| `UpdatedAt` | `timestamptz` | |

The legacy `profiles` food columns are read only by Cantina's fallback and the `cantina.profile-dietary` copy; a later retirement PR drops them. `MedicalConditions` (`varchar(4000)?`) stays on `profiles`.

## Triggers

| Event | Effect |
|---|---|
| Member saves a dietary preference | Dietary nudge, medical prompt, banner and gate clear |
| Member saves medical conditions | Medical prompt clears |
| Member signs up / is voluntold into a qualifying shift | Nudge copy switches to the shift variant |
| Last qualifying signup bails, is refused, or ends | Nudge copy switches back to the no-shift variant |

## Cross-section dependencies

- **Shifts** → Cantina: `ICantinaService` for the gate, banner, volunteer search and admin badges. Cantina → Shifts: `IShiftManagementService.HasQualifyingCantinaSignupAsync` (nudge copy) and `IShiftSignups` (replay).
- **Users** → Cantina: `ICantinaService` for the medical prompt's "dietary still open" check. Cantina → Users: `IUserService` for the merge chain and legacy fallback.

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

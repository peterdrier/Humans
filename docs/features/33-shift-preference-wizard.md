# 33 — Shift Preference Wizard

## Business Context

Volunteers need to tell coordinators about their skills, work style preferences, and languages so they can be matched with appropriate shifts. The current `/Profile/ShiftInfo` page is a flat form with bare checkboxes — functional but uninviting, especially on mobile. This feature replaces it with a guided 3-step wizard that collects the same data in a more engaging, mobile-friendly flow.

This is the first of three related features:
- **33 — Shift Preference Wizard** (this feature): Collect skills, work style, languages
- **34 — Shift Recommendation Engine**: Fuzzy-match preferences against available shifts (separate spec)
- **35 — Dietary & Medical Nudge Modal**: Collect dietary/allergy/medical info when a human signs up for a 6+ hour shift where the cantina provides meals (separate spec)

## Authorization

Same as current `/Profile/ShiftInfo` — any authenticated user can view and edit their own shift preferences. No role-based restrictions.

## User Stories

### US-33.1: Set Shift Preferences via Wizard
**As a** human
**I want to** set my shift preferences through a guided wizard
**So that** coordinators can match me with shifts that fit my skills and availability

**Acceptance Criteria:**
- Wizard replaces the existing flat form at `GET /Profile/ShiftInfo`
- 3 steps: Skills, Work Style, Languages
- Step 1 (Skills): emoji-prefixed chip multi-select — Bartending, Cooking, Sound, DJ, First Aid, Electrical, Driving, Construction, Art, Other
- Step 2 (Work Style): radio cards for time preference (Early Bird, Night Owl, All Day, No Preference) + Bootstrap toggle switches for quirks (Sober Shift, Work In Shade, No Heights, Physical Work OK, Quiet Work)
- Step 3 (Languages): emoji-prefixed chip multi-select — English, Spanish, French, German, Italian, Portuguese, Other
- Progress dots in header show current/completed/future steps
- Step label, title, and subtitle update per step
- Back/Continue navigation buttons; "Save" on final step
- Single form POST on save — all selections submitted together
- Pre-populated with existing `VolunteerEventProfile` data on return visits
- Mobile-first: chip grid wraps with generous tap targets, radio cards stack vertically on small screens, full-width buttons on mobile

### US-33.2: Accessible Wizard Interactions
**As a** human using assistive technology
**I want to** navigate and interact with the wizard using keyboard and screen reader
**So that** I can set my preferences regardless of how I access the site

**Acceptance Criteria:**
- Chips backed by hidden checkbox inputs (screen reader accessible)
- Chips have `role="checkbox"` and `aria-checked`
- Radio cards backed by hidden radio inputs
- Keyboard navigation: chips/cards are focusable, spacebar/enter toggles
- Step transitions do not break focus management

## Data Model

**No schema changes. No migration.**

All data stores into the existing `VolunteerEventProfile` entity:
- Skills → `Skills[]` (string array)
- Time preference (Early Bird / Night Owl / All Day / No Preference) → stored as a value in `Quirks[]`
- Toggle quirks (Sober Shift, etc.) → `Quirks[]`
- Languages → `Languages[]`

### New Quirk Values

"All Day" and "No Preference" are **new values** being added to the quirk vocabulary. "Early Bird" and "Night Owl" already exist. No downstream impact — the volunteer search builder and shift admin views display quirks as-is from the string array, so new values render without code changes.

### Time Preference Mutual Exclusivity

Time preferences (Early Bird, Night Owl, All Day, No Preference) are mutually exclusive — a human picks exactly one. These are stored in the same `Quirks[]` array alongside toggle quirks, so the save logic must:

1. **On POST:** Remove any existing time preference value from `Quirks[]` before adding the newly selected one. The set of time preference values is: `["Early Bird", "Night Owl", "All Day", "No Preference"]`.
2. **On GET:** Extract the time preference from `Quirks[]` to pre-select the correct radio card. Toggle quirks populate the switches separately.

The view model should expose `TimePreference` (string, nullable) separately from `SelectedQuirks` (list of toggle quirks) to make this split explicit in the view. The controller merges them back into a single `Quirks[]` on save.

**null vs "No Preference":** `TimePreference = null` means the user has never set a time preference (no radio card pre-selected). "No Preference" is a deliberate choice ("I'll take whatever's needed") and is stored as a quirk value. The wizard allows submitting without selecting a time preference — it's not required.

The set of time preference values should be a `static readonly` array (like the existing `SkillOptions`) so both the strip-on-save and extract-on-load logic reference the same source of truth.

### Fields Removed From This Page (Not Deleted)

The following fields are no longer editable on `/Profile/ShiftInfo` but remain on `VolunteerEventProfile` and in the database. Existing data is preserved. These will move to the Dietary & Medical Nudge Modal (Feature 35):
- `DietaryPreference`
- `Allergies[]` + `AllergyOtherText`
- `Intolerances[]` + `IntoleranceOtherText`
- `MedicalConditions`

## Visual Design

**Style: Light Subtle** — Bootstrap color palette with custom chip components:
- Unselected chip: `#f8f9fa` background, `#dee2e6` border, `#495057` text
- Selected chip: `#e7f1ff` background, `#0d6efd` border, `#0d6efd` text, `font-weight: 500`
- Emoji prefix on each chip as visual anchor
- No visible checkboxes — selection indicated by color change
- Radio cards: larger format with icon, label, description text; 2x2 grid on desktop, single column on mobile
- Toggle switches: standard Bootstrap `form-check form-switch`
- Progress: 3 dots (blue filled = active/completed, grey = future)

## Step Content

| Step | Label | Title | Subtitle |
|------|-------|-------|----------|
| 1 | Step 1 of 3 | What are you good at? | Select skills you'd like to use. You can change these anytime. |
| 2 | Step 2 of 3 | How do you like to work? | Help coordinators match you with shifts that fit your style and availability. |
| 3 | Step 3 of 3 | Which languages do you speak? | This helps us match you with teams where you can communicate well. |

## Files Modified

| File | Change |
|------|--------|
| `Views/Profile/ShiftInfo.cshtml` | Full rewrite — flat form → wizard |
| `Models/ShiftViewModels.cs` | Remove dietary/allergy/medical properties; add `TimePreference` property (string); split `SelectedQuirks` to exclude time preferences |
| `Controllers/ProfileController.cs` | POST stops writing dietary/medical fields; GET stops loading them into view model |

## Files NOT Modified

- `VolunteerEventProfile` entity — untouched
- EF mappings — no migration
- No new JS files — inline `<script>` in the view
- No new CSS files — Bootstrap utilities + small `<style>` block

## Entry Points

- Profile page → "Shift Info" link (existing)
- Dashboard "Things to do" card → "Set up" action (issue #273)
- Any future contextual links can point to `/Profile/ShiftInfo`

## Related Features

- [25 — Shift Management](25-shift-management.md): parent shift system
- [Issue #273](https://github.com/nobodies-collective/Humans/issues/273): Dashboard "Things to do" wizard card
- [Issue #186](https://github.com/nobodies-collective/Humans/issues/186): Custom labels/tags on rotas (future matching input)
- Feature 34 — Shift Recommendation Engine (not yet specced)
- [Feature 35 — Dietary & Medical Nudge Modal](35-dietary-medical-nudge.md) (placeholder)

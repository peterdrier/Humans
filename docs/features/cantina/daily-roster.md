<!-- freshness:triggers
  src/Humans.Domain/Entities/VolunteerEventProfile.cs
  src/Humans.Infrastructure/Data/Configurations/Shifts/VolunteerEventProfileConfiguration.cs
  src/Humans.Application/Services/Cantina/CantinaRosterService.cs
  src/Humans.Application/Interfaces/Repositories/IShiftManagementRepository.cs
  src/Humans.Web/Controllers/CantinaController.cs
  src/Humans.Web/Views/Cantina/Roster.cshtml
-->
<!-- freshness:flag-on-change
  "On-site" definition (which signup statuses count, all-day single-day semantics), allergy/intolerance option sets, authorization roles, or MedicalConditions exclusion rule.
-->

# 36 — Cantina Daily Roster

## Business Context

Cantina coordinators feed everyone on site during the build and event week. They need a per-day view of who's on site so they can plan meals, batch-buy ingredients, and account for dietary restrictions, allergies, and intolerances.

Until now, the data was collected via the [Dietary & Medical Nudge](../profiles/dietary-medical-nudge.md) (#279) but had no aggregated read path — coordinators had to ask each volunteer individually, or chase down the data through per-profile badges. This feature surfaces a printable per-day roster behind a single URL, with a CSV download for offline planning, and tightens the GDPR boundary by excluding medical-condition data that the cantina doesn't need.

## Authorization

View access to `/Cantina/Roster*`:

- **Full access:** `Admin`, `NoInfoAdmin`, `VolunteerCoordinator` — these roles already see dietary data on individual profiles per the existing `_VolunteerProfileBadges` pattern.
- **Cantina team members:** any authenticated user who is a member of a team whose name contains "Cantina" (case-insensitive). Cantina-team coordinators check the roster as their core workflow; gating by team membership avoids minting a dedicated role.
- **Other authenticated users:** 403 Forbidden.
- **Unauthenticated:** redirected to login per the global `[Authorize]` policy.

The two access paths (role-based and team-membership-based) compose with OR — possessing either is sufficient.

## GDPR (special-category data)

`MedicalConditions` (GDPR Art. 9 health data) is **excluded entirely** from this page and from the CSV, regardless of viewer role. The cantina plans around food, not medical history; medical conditions remain visible only via the per-volunteer `_VolunteerProfileBadges` partial with `ShowMedical = true` (existing path, unchanged).

This tightens the Art. 9 boundary: cantina coordinators don't need health data and don't get it. The exclusion happens at the DTO boundary (not at query time) so the field can never reach the view layer.

`DietaryPreference`, `Allergies`, `Intolerances`, `AllergyOtherText`, `IntoleranceOtherText` are not special-category data — they're personal data already disclosed to coordinators via the existing badges path, and the roster aggregates them. No new retention policy; data lives only as long as the underlying `VolunteerEventProfile` rows do, which are erased with the account.

## User Stories

### US-36.1: Cantina coordinator views today's roster
**As a** cantina coordinator
**I want to** see who's on site today, grouped with their dietary needs
**So that** I can plan meals and quantities for the day

**Acceptance Criteria:**
- Route: `GET /Cantina/Roster` — defaults `dayOffset` to today's offset relative to `EventSettings.GateOpeningDate`.
- Page renders an aggregates panel at the top:
  - **Total on-site:** integer count of distinct humans with at least one qualifying signup on that day.
  - **Dietary breakdown:** `Omnivore N · Vegetarian N · Vegan N · Pescatarian N · Unanswered N`.
  - **Allergy roll-up:** one row per allergy in the standard set (`Peanut`, `Tree nut`, `Dairy`, `Egg`, `Shellfish`, `Wheat/Gluten`, `Soy`, `Sesame`), with count. Free-text `AllergyOtherText` entries surface as a numbered list under "Other (N): …".
  - **Intolerance roll-up:** same shape as allergies, over the standard set (`Lactose`, `Gluten`, `Histamine`, `FODMAP`), with the same "Other (N): …" treatment.
  - **Unanswered cohort:** prominent count + link to the per-person table filtered to humans whose `DietaryPreference` is null/empty.
- Below the aggregates, a per-person table with columns: **Burner Name** · **Dietary chip** · **Allergies (chips)** · **Other allergy text** · **Intolerances (chips)** · **Other intolerance text**.
- Table is sortable by name (default ascending).
- Table does **not** include a `MedicalConditions` column under any role.
- If there is no active event (no `EventSettings` with a `GateOpeningDate` resolvable), the page renders an empty-state message ("no active event") instead of throwing.

### US-36.2: Coordinator filters to a different day
**As a** cantina coordinator planning ahead (or looking back)
**I want to** switch the roster to another day
**So that** I can plan tomorrow's lunch or reconcile yesterday's headcount

**Acceptance Criteria:**
- Page exposes a day-offset selector with computed calendar-date labels, e.g.:
  - `"Day -3 (Tue, Jul 14)"`
  - `"Day 0 — Gate opening (Fri, Jul 17)"`
  - `"Day 5 (Wed, Jul 22)"`
- Selector range: any `DayOffset` value that has at least one `Shift` in the system (no artificial min/max).
- Selector renders as Prev/Next buttons plus a dropdown.
- Changing the day updates the URL to `?dayOffset=<int>` so the page is shareable / bookmarkable.
- Past shifts are **not** excluded — historical lookups are intentional. Future shifts are included.

### US-36.3: Coordinator downloads CSV for offline planning
**As a** cantina coordinator doing shopping or kitchen prep without a screen
**I want to** download the per-person roster as CSV
**So that** I can hand it to whoever's running the kitchen, paste it into a spreadsheet, or print it

**Acceptance Criteria:**
- Route: `GET /Cantina/Roster/Csv?dayOffset=<int>` — same data scope as the HTML page, no UI chrome, no aggregates.
- One row per human; same column set as the HTML table, minus chip styling:
  ```
  Name,Dietary,Allergies,AllergyOther,Intolerances,IntoleranceOther
  "Dev Human 007",Vegetarian,"Peanut, Tree nut","","Other","MSG"
  ```
- UTF-8 BOM (`﻿`) prepended for Excel-friendliness.
- RFC 4180 quoting: fields containing commas, quotes, or newlines are wrapped in double quotes; embedded double quotes are escaped by doubling.
- `Allergies` / `Intolerances` cells contain the chip values comma-and-space-separated.
- No `MedicalConditions` column.
- Same authorization gate as the HTML route — unauthorized requests get 403.

### US-36.4: Unauthorized user attempts access
**As a** regular volunteer (no cantina-team membership, no admin role)
**I want** the roster URL to refuse my access
**So that** other volunteers' dietary data isn't broadcast to anyone who guesses the URL

**Acceptance Criteria:**
- Authenticated user without `Admin` / `NoInfoAdmin` / `VolunteerCoordinator` and not a member of any "Cantina"-named team gets a 403 Forbidden response on `GET /Cantina/Roster` and `GET /Cantina/Roster/Csv`.
- Unauthenticated user is redirected to the login page (global `[Authorize]` policy).
- A 403 must **not** leak any data (no headcount, no day labels) — only the standard forbidden response.

## "On-site" Definition

A volunteer is **on-site for day X** iff they have at least one `ShiftSignup` with status `Pending` or `Confirmed` on a `Shift` whose `DayOffset == X`.

- **Statuses that count:** `Pending`, `Confirmed`.
- **Statuses that do NOT count:** `Refused`, `Bailed`, `NoShow`, `Cancelled`.
- **All-day shifts are single-day** (08:00–18:00 per existing `Shift.AllDayWindowStart/End`). There is **no multi-day expansion** — an all-day shift on `DayOffset = 3` contributes to day 3 only, never to day 2 or day 4.
- **Past shifts are NOT excluded** — the cantina may need historical lookups (reconciliation, postmortem). Future shifts ARE included.
- **Active event only.** The query filters to signups whose `Shift` belongs to the currently active event.

The roster does NOT use the qualifying-shift gate (6+ hours) from the dietary-medical nudge — that gate decides who gets prompted; the roster simply shows who's present.

## Data Model

No new entities, no new columns, no migration.

Reads:

| Source | Used for | Notes |
|---|---|---|
| `ShiftSignup.Status`, `Shift.DayOffset` | Filter on-site cohort for day X | Existing fields |
| `VolunteerEventProfile.DietaryPreference` | Dietary chip + breakdown counts | Existing field |
| `VolunteerEventProfile.Allergies` (`List<string>`) | Allergy chips + roll-up | `jsonb` via `ConfigureJsonbList`, existing |
| `VolunteerEventProfile.AllergyOtherText` | "Other (N): …" list | Existing field |
| `VolunteerEventProfile.Intolerances` (`List<string>`) | Intolerance chips + roll-up | `jsonb` via `ConfigureJsonbList`, existing |
| `VolunteerEventProfile.IntoleranceOtherText` | "Other (N): …" list | Existing field |
| `User.BurnerName` (or fallback display name) | Per-person table "Burner Name" column | Existing |
| `EventSettings.GateOpeningDate` | Convert `DayOffset` to calendar label | Existing |

Explicitly **excluded** at the DTO boundary regardless of viewer role:

| Field | Reason |
|---|---|
| `VolunteerEventProfile.MedicalConditions` | GDPR Art. 9; cantina doesn't need it |

## Cross-section dependencies

This feature introduces a new section (`Cantina/`) but reuses existing repositories.

- **Shifts (read, on-site cohort):** new method on `IShiftManagementRepository` returning the set of `ShiftSignup` rows (with `Shift` nav included) where `Status ∈ {Pending, Confirmed}`, `Shift.DayOffset == @day`, and `Shift` belongs to the active event. **Do NOT include the `User` nav** — same cross-section rule as the dietary-nudge query. The roster service joins to `VolunteerEventProfile` (and `User` for display name) in its own query layer.
- **Volunteers/Profiles (read, dietary):** reads `VolunteerEventProfile` for the on-site cohort. `MedicalConditions` is dropped at the `CantinaRosterRowDto` boundary (not at query) so the data never reaches the view layer. No new Profile-service method; the new `CantinaRosterService` projects directly.
- **Event settings:** reads `EventSettings.GateOpeningDate` (and active-event marker) to compute calendar-date labels and the available `dayOffset` range.
- **Teams (authorization):** authorization handler checks whether the current user is a member of any team whose `Name` matches `"*Cantina*"` (case-insensitive). Reuses the existing team-membership read surface; no new entity.
- **No new Domain entity, no schema change, no migration.**

New components:

| Layer | Component | Purpose |
|---|---|---|
| Application | `CantinaRosterService` | Build per-day aggregate + per-person DTOs; enforce `MedicalConditions` exclusion |
| Application | `CantinaRosterDto`, `CantinaRosterRowDto`, `CantinaRosterAggregatesDto` | View-model contracts; no `MedicalConditions` field |
| Web | `CantinaController` | `GET /Cantina/Roster`, `GET /Cantina/Roster/Csv` |
| Web | `Roster.cshtml`, `_RosterAggregates.cshtml`, `_RosterTable.cshtml` | View + partials |
| Web | `CantinaAccessHandler` (or equivalent) | Role-or-team authorization gate |

## Negative access rules

- A user **cannot** see the roster page or CSV unless they are `Admin` / `NoInfoAdmin` / `VolunteerCoordinator` **or** a member of a "Cantina"-named team. No other path grants access.
- The roster page and CSV **never** include `MedicalConditions`, even for `Admin` / `NoInfoAdmin`. To see medical conditions, viewers use the existing per-profile badges path with `ShowMedical = true`.
- The 403 response **must not** leak any roster data, including the day's headcount or the day label.
- `Refused` / `Bailed` / `NoShow` / `Cancelled` signups **must not** contribute to any count or row, even if the volunteer also has a qualifying signup on a different day.
- The roster reads only the currently active event's signups — no cross-event aggregation.

## Out of scope (v1)

- **Email reminders to "unanswered" volunteers.** The unanswered-cohort link is just a filter, not a send action. A separate feature can build the notify path.
- **Per-meal granularity** (lunch vs. dinner vs. breakfast). Cantina plans per-day in v1.
- **Historical export** (last year's data, multi-event archive). The freshness:flag-on-change covers shape changes; archival export is a separate concern.
- **Multi-event scope.** Reads only the active event's signups.
- **Editing dietary data from the roster.** Coordinators who need to correct an entry go through the volunteer's profile.
- **Printable PDF layout.** The HTML view is browser-print-friendly; a dedicated PDF generator is not in scope.
- **Real-time push updates.** The page is a normal request/response; coordinators refresh to pick up new signups.

## Related Features

- [35 — Dietary & Medical Nudge](../profiles/dietary-medical-nudge.md): the data-collection counterpart. This feature is its read consumer — the nudge collects `DietaryPreference` / `Allergies` / `Intolerances` / `*OtherText`, and the roster aggregates and surfaces them.
- [25 — Shift Management](../shifts/shift-management.md): supplies the `ShiftSignup` rows and `Shift.DayOffset` that define the per-day cohort.
- [Issue #279](https://github.com/nobodies-collective/Humans/issues/279): tracking issue for the dietary-nudge + roster bundle. This spec is the roster half.

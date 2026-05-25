<!-- freshness:triggers
  src/Humans.Domain/Entities/VolunteerEventProfile.cs
  src/Humans.Infrastructure/Data/Configurations/Shifts/VolunteerEventProfileConfiguration.cs
  src/Humans.Application/Services/Cantina/CantinaRosterService.cs
  src/Humans.Application/Interfaces/Repositories/IShiftManagementRepository.cs
  src/Humans.Web/Controllers/CantinaController.cs
  src/Humans.Web/Views/Cantina/Roster.cshtml
-->
<!-- freshness:flag-on-change
  "On-site" definition (which signup statuses count, all-day single-day semantics), week boundary policy (Monday-Sunday in event tz), allergy/intolerance option sets, authorization roles, or MedicalConditions exclusion rule.
-->

# 36 — Cantina Weekly Roster

## Business Context

Cantina coordinators feed everyone on site during the build and event week. They need a week-at-a-glance view of who's on site so they can plan meals, batch-buy ingredients across multiple days, and account for dietary restrictions, allergies, and intolerances.

Until now, the data was collected via the [Dietary & Medical Nudge](../profiles/dietary-medical-nudge.md) (#279) but had no aggregated read path — coordinators had to ask each volunteer individually, or chase down the data through per-profile badges. This feature surfaces a printable per-week roster behind a single URL, with a CSV download for offline planning, and tightens the GDPR boundary by excluding medical-condition data that the cantina doesn't need.

A previous iteration produced a per-day roster; coordinators rejected it as too granular for shopping/prep — a single day doesn't justify a separate page download. The weekly view is the only roster now: it surfaces a per-day mini-summary (counts only) plus a unique-humans cohort for the whole week.

## Authorization

View access to `/Cantina/Roster*`:

- **Access:** `Admin` or the grantable `CantinaAdmin` role, via the `CantinaAdminOrAdmin` authorization policy — aligned with every other per-area `<Area>AdminOrAdmin` policy. `CantinaAdmin` is granted on the permissions page like any other admin role (no team-name heuristic).
- **Other authenticated users:** 403 Forbidden.
- **Unauthenticated:** redirected to login per the global `[Authorize]` policy.

The two access paths (role-based and team-membership-based) compose with OR — possessing either is sufficient.

## GDPR (special-category data)

`MedicalConditions` (GDPR Art. 9 health data) is **excluded entirely** from this page and from the CSV, regardless of viewer role. The cantina plans around food, not medical history; medical conditions remain visible only via the per-volunteer `_VolunteerProfileBadges` partial with `ShowMedical = true` (existing path, unchanged).

This tightens the Art. 9 boundary: cantina coordinators don't need health data and don't get it. The exclusion happens at the DTO boundary (not at query time) so the field can never reach the view layer.

`DietaryPreference`, `Allergies`, `Intolerances`, `AllergyOtherText`, `IntoleranceOtherText` are not special-category data — they're personal data already disclosed to coordinators via the existing badges path, and the roster aggregates them. No new retention policy; data lives only as long as the underlying `VolunteerEventProfile` rows do, which are erased with the account.

## Week Boundary

A **week** is Monday 00:00 through Sunday 23:59 in the **active event's timezone** (`EventSettings.TimeZoneId`). The week is identified by a single integer:

> `weekStartOffset` = the day-offset (relative to `EventSettings.GateOpeningDate`) of that week's Monday.

Examples (gate opening on a Friday, `DayOffset = 0`):

- The week containing gate opening has its Monday at `DayOffset = -4` → `weekStartOffset = -4`.
- The week before that → `weekStartOffset = -11`.
- Strike week (Monday after a Friday-start event) → `weekStartOffset = 3`.

`weekStartOffset` can be negative (build weeks), zero-ish (event week), or positive (strike weeks). The default for `GET /Cantina/Roster` is the week containing **today** in the event's timezone — computed in the service as:

1. Convert `_clock.GetCurrentInstant()` into the event timezone → `todayLocal`.
2. Walk back to the most recent Monday → `monday`.
3. `weekStartOffset = Period.Between(GateOpeningDate, monday, PeriodUnits.Days).Days`.

If there is no active event, the service returns an empty DTO with `WeekStartDate = null` and the controller renders an empty-state.

## User Stories

### US-36.1: Cantina coordinator views the current week's roster
**As a** cantina coordinator
**I want to** see who's on site this week, with dietary aggregates over the whole week
**So that** I can plan meals and shopping for the next several days at once

**Acceptance Criteria:**
- Route: `GET /Cantina/Roster` — defaults `weekStartOffset` to the Monday of today's week (event tz).
- Page renders an aggregates panel at the top, computed over the **unique humans on-site any day this week** (a person on-site Mon and Wed is counted **once**):
  - **Total unique on-site this week:** integer count.
  - **Dietary breakdown:** `Omnivore N · Vegetarian N · Vegan N · Pescatarian N · Unanswered N` — each unique human contributes once.
  - **Allergy roll-up:** one row per allergy in the standard set (`Peanut`, `Tree nut`, `Dairy`, `Egg`, `Shellfish`, `Wheat/Gluten`, `Soy`, `Sesame`), counted over unique humans (a person on Mon+Wed with "Peanut" contributes 1, not 2). Free-text `AllergyOtherText` entries surface as a numbered list under "Other (N): …", **deduplicated** across the week (a person whose "Other = MSG" appears Mon–Wed contributes "MSG" once).
  - **Intolerance roll-up:** same shape as allergies, over the standard set (`Lactose`, `Gluten`, `Histamine`, `FODMAP`), same "Other (N): …" treatment, same week-level dedup.
  - **Unanswered cohort:** prominent count of unique humans with no `DietaryPreference`.
- Below the aggregates, a **per-day mini-summary table** with 7 rows (Mon–Sun): each row shows the day's calendar date, `TotalOnSite` (count of distinct humans signed up that day), and `UnansweredOnDay` (count of that day's on-site humans with no `DietaryPreference`).
- Below that, a **per-person table** with one row per unique human on-site any day that week. Columns: **Burner Name** · **Arrives on** (single short day label, e.g. "Mon 27 May") · **No shift** (list of short day labels for days within the week with no scheduled shift) · **Dietary chip** · **Allergies (chips)** · **Other allergy text** · **Intolerances (chips)** · **Other intolerance text**.
- Per-person table default sort: first arrival day asc → has-allergies first → canonical dietary order → cultural-collation burner name (Spanish event, names with `ñ`/`á` sort correctly).
- Per-person table does **not** include a `MedicalConditions` column under any role.
- If there is no active event (no `EventSettings` with a `GateOpeningDate` resolvable), the page renders an empty-state message ("no active event") instead of throwing.

### US-36.2: Coordinator navigates to another week
**As a** cantina coordinator planning ahead (or looking back)
**I want to** switch the roster to another week
**So that** I can plan next week's shopping or reconcile last week's headcount

**Acceptance Criteria:**
- Page exposes a **Prev / This Week / Next** nav linking to `?weekStartOffset=<int>`.
  - **Prev** = `weekStartOffset - 7`
  - **This Week** = the offset of the Monday of today's week (resets to default)
  - **Next** = `weekStartOffset + 7`
- The page header shows the week's calendar range, e.g. `"Mon 27 May — Sun 2 Jun"`.
- Changing the week updates the URL to `?weekStartOffset=<int>` so the page is shareable / bookmarkable.
- No artificial min/max; coordinators may navigate to weeks before gate opening (build) or after strike — empty weeks render the empty-state copy.
- Past weeks are **not** excluded — historical lookups are intentional.

### US-36.3: Coordinator downloads CSV for offline planning
**As a** cantina coordinator doing shopping or kitchen prep without a screen
**I want to** download the weekly roster as CSV
**So that** I can hand it to whoever's running the kitchen, paste it into a spreadsheet, or print it

**Acceptance Criteria:**
- Route: `GET /Cantina/Roster/Csv?weekStartOffset=<int>` — same data scope as the HTML page's per-person table, no UI chrome, no aggregates.
- One row per unique human across the week; columns:
  ```
  Name,ArrivesOn,NoShift,Dietary,Allergies,AllergyOther,Intolerances,IntoleranceOther
  "Dev Human 007","Mon 27 May","Wed 29 May, Sat 1 Jun",Vegetarian,"Peanut, Tree nut","","Other","MSG"
  ```
- `ArrivesOn` is a single short calendar label (e.g. `Mon 27 May`) for the human's earliest on-site day within the week.
- `NoShift` lists the calendar dates within the week on which the human had no scheduled shift — formatted as short weekday + day + short month (e.g. `Wed 29 May`), comma-and-space-separated, wrapped in double quotes. Empty when the human has a scheduled shift every day of the week.
- UTF-8 BOM (`﻿`) prepended for Excel-friendliness.
- RFC 4180 quoting: fields containing commas, quotes, or newlines are wrapped in double quotes; embedded double quotes are escaped by doubling.
- `Allergies` / `Intolerances` cells contain the chip values comma-and-space-separated.
- Filename: `cantina-roster-week-of-<yyyy-MM-dd>.csv` (the Monday of the week, ISO date).
- No `MedicalConditions` column.
- Same authorization gate as the HTML route — unauthorized requests get 403.

### US-36.4: Unauthorized user attempts access
**As a** regular volunteer (no `CantinaAdmin` or `Admin` role)
**I want** the roster URL to refuse my access
**So that** other volunteers' dietary data isn't broadcast to anyone who guesses the URL

**Acceptance Criteria:**
- Authenticated user without `Admin` or `CantinaAdmin` gets a 403 Forbidden response on the `/Cantina/Roster*` routes (the `CantinaAdminOrAdmin` policy fails).
- Unauthenticated user is redirected to the login page (global `[Authorize]` policy).
- A 403 must **not** leak any data (no headcount, no week label) — only the standard forbidden response.

## "On-site" Definition

A volunteer is **on-site for day X** iff they have at least one `ShiftSignup` with status `Pending` or `Confirmed` on a `Shift` whose `DayOffset == X`.

- **Statuses that count:** `Pending`, `Confirmed`.
- **Statuses that do NOT count:** `Refused`, `Bailed`, `NoShow`, `Cancelled`.
- **All-day shifts are single-day** (08:00–18:00 per existing `Shift.AllDayWindowStart/End`). There is **no multi-day expansion** — an all-day shift on `DayOffset = 3` contributes to day 3 only, never to day 2 or day 4.
- **Past days are NOT excluded** — the cantina may need historical lookups. Future days ARE included.
- **Active event only.** The query filters to signups whose `Shift` belongs to the currently active event.

A volunteer is **on-site for week W** iff they are on-site for at least one of the 7 days Monday–Sunday in W. Per-week aggregates dedupe by `UserId`: a person on-site Mon + Wed contributes 1 to `TotalUniqueOnSite` and 1 to every aggregate row they qualify for.

The roster does NOT use the qualifying-shift gate (6+ hours) from the dietary-medical nudge — that gate decides who gets prompted; the roster simply shows who's present.

## Routes

| Route | Method | Notes |
|---|---|---|
| `/Cantina/Roster` | GET | Optional `?weekStartOffset=<int>`. Default = Monday of today's week (event tz). |
| `/Cantina/Roster/Csv` | GET | Optional `?weekStartOffset=<int>`. Same default. |

There is no per-day route. The earlier `?dayOffset=<int>` parameter has been removed.

## Data Model

No new entities, no new columns, no migration. The week-level view consumes the same source tables as the previous daily view; aggregation is in-memory in `CantinaRosterService`.

Reads:

| Source | Used for | Notes |
|---|---|---|
| `ShiftSignup.Status`, `Shift.DayOffset` | Filter on-site cohort per day; service unions across the 7 days of the week | Existing fields |
| `VolunteerEventProfile.DietaryPreference` | Per-unique-human dietary chip + breakdown counts | Existing field |
| `VolunteerEventProfile.Allergies` (`List<string>`) | Allergy chips + roll-up | `jsonb` via `ConfigureJsonbList`, existing |
| `VolunteerEventProfile.AllergyOtherText` | "Other (N): …" list, deduped across the week | Existing field |
| `VolunteerEventProfile.Intolerances` (`List<string>`) | Intolerance chips + roll-up | `jsonb` via `ConfigureJsonbList`, existing |
| `VolunteerEventProfile.IntoleranceOtherText` | "Other (N): …" list, deduped across the week | Existing field |
| `User.BurnerName` (or fallback display name) | Per-person table "Burner Name" column | Existing |
| `EventSettings.GateOpeningDate`, `EventSettings.TimeZoneId` | Compute calendar dates for each day in the week + default week | Existing |

At ~500-user scale, the service issues 7 sequential per-day cohort queries (`GetOnSiteUserIdsForDayAsync`, one per day) plus a single batched `IUserServiceRead.GetUserInfosAsync` for the week's unique cohort (dietary + names from the cached `UserInfo`).

Explicitly **excluded** at the DTO boundary regardless of viewer role:

| Field | Reason |
|---|---|
| `VolunteerEventProfile.MedicalConditions` | GDPR Art. 9; cantina doesn't need it |

## Aggregates

All weekly aggregates are computed over the **unique-humans cohort** for the week (union of `UserId`s across the 7 days).

| Aggregate | Definition |
|---|---|
| `TotalUniqueOnSite` | Distinct `UserId` count across Mon–Sun. |
| `DietaryBreakdown` | One entry per canonical preference plus `"Unanswered"`. Each unique human contributes once based on their VEP's `DietaryPreference` (or "Unanswered" if null/empty). |
| `AllergyRollup` | One entry per canonical allergy label. Each unique human contributes once per label they ticked. |
| `AllergyOtherEntries` | List of free-text `AllergyOtherText` values, deduplicated across the week by trimmed text (case-sensitive). |
| `IntoleranceRollup` | Same shape as `AllergyRollup`. |
| `IntoleranceOtherEntries` | Same dedup rule as `AllergyOtherEntries`. |

In addition, a **per-day mini-summary** carries 7 entries:

| Field | Definition |
|---|---|
| `DayOffset` | Day index relative to `GateOpeningDate`. |
| `CalendarDate` | `GateOpeningDate + DayOffset` in event tz. Null if no active event. |
| `TotalOnSite` | Distinct on-site humans **that day**. |
| `UnansweredOnDay` | Distinct on-site humans that day with no `DietaryPreference`. |

The mini-summary is the only place daily numbers surface. The per-person table groups by human, not by day.

## CSV

Header:
```
Name,ArrivesOn,NoShift,Dietary,Allergies,AllergyOther,Intolerances,IntoleranceOther
```

`ArrivesOn` is the human's earliest on-site day within the requested week, formatted with NodaTime pattern `ddd d MMM` (invariant culture), e.g. `Mon 27 May`. `NoShift` lists the calendar dates within the same week on which the human had no scheduled shift, same format, comma-and-space-separated, wrapped in double quotes whenever the cell contains a comma. `NoShift` is empty when the human has a scheduled shift every day of the week.

(Note: "no shift" is the cantina semantic — the human could still be on-site that day, working informally or at barrio. The earlier "off-site / days off" wording was renamed to avoid that misread.)

One row per unique human. Otherwise the same RFC 4180 quoting rules, UTF-8 BOM, and chip-join behaviour as before.

Filename: `cantina-roster-week-of-<yyyy-MM-dd>.csv`, where `<yyyy-MM-dd>` is the ISO date of the week's Monday.

## Cross-section dependencies

This feature lives in the `Cantina/` section and reads **only through section services** — it never touches a repository.

- **Shifts (read, on-site cohort):** `IShiftManagementService.GetOnSiteUserIdsForDayAsync`, called in a 7-day loop and unioned into the unique-humans cohort.
- **Users/Identity (read, dietary + names):** `IUserServiceRead.GetUserInfosAsync` — batched, cached `UserInfo`. Dietary lives on `Profile`; `CantinaRosterService` never reads `MedicalConditions`.
- **Users/Identity (read, burner names):** `IUserServiceRead.GetUserInfosAsync` — batched, cached `UserInfo`. No entity reads, no new surface.
- **Event settings:** `IShiftManagementService.GetActiveAsync` for `GateOpeningDate` / `TimeZoneId` (week-boundary computation and per-day calendar labels).
- **Authorization:** the `CantinaAdminOrAdmin` policy (Admin or the grantable `CantinaAdmin` role). No team-name heuristic, no bespoke access service.
- **No new Domain entity, no schema change, no migration.**

New / updated components:

| Layer | Component | Purpose |
|---|---|---|
| Application | `CantinaRosterService` | Build weekly aggregates + per-day mini-summary + unique-humans table; reads via `IShiftManagementService` + `IUserServiceRead` |
| Application | `WeeklyRosterDto`, `DayRosterSummaryDto`, `RosterPersonDto`, `RollupItemDto` | View-model contracts; no `MedicalConditions` field |
| Domain | `RoleNames.CantinaAdmin` | Grantable admin role; wired into `RoleNames.All` + `AnyAdminRole` + the `CantinaAdminOrAdmin` policy |
| Web | `CantinaController` | `[Authorize(Policy = CantinaAdminOrAdmin)]`; `GET /Cantina/Roster(/Csv)` + `/Roster/Day(/Csv)` |
| Web | `Roster.cshtml` | View (aggregates panel + per-day mini-table + per-person table) |

## Negative access rules

- A user **cannot** see the roster page or CSV unless they hold `Admin` or `CantinaAdmin`. No other path grants access.
- The roster page and CSV **never** include `MedicalConditions` — `CantinaRosterService` never reads it and the cantina DTOs have no such field. To see medical conditions, viewers use the per-profile badges path with `ShowMedical = true`.
- The 403 response **must not** leak any roster data, including the week's headcount or label.
- `Refused` / `Bailed` / `NoShow` / `Cancelled` signups **must not** contribute to any count or row, even if the volunteer also has a qualifying signup on a different day.
- The roster reads only the currently active event's signups — no cross-event aggregation.
- Aggregates are computed over **unique humans** for the week, not by summing daily counts: a person on-site Mon + Wed is counted **once** for `TotalUniqueOnSite` and once for each aggregate row they qualify for.

## Out of scope (v1)

- **Email reminders to "unanswered" volunteers.** The unanswered-cohort count is informational, not a send action. A separate feature can build the notify path.
- **Per-meal granularity** (lunch vs. dinner vs. breakfast). Cantina plans per-day in v1.
- **Historical export** (last year's data, multi-event archive). The freshness:flag-on-change covers shape changes; archival export is a separate concern.
- **Multi-event scope.** Reads only the active event's signups.
- **Editing dietary data from the roster.** Coordinators who need to correct an entry go through the volunteer's profile.
- **Printable PDF layout.** The HTML view is browser-print-friendly; a dedicated PDF generator is not in scope.
- **Real-time push updates.** The page is a normal request/response; coordinators refresh to pick up new signups.
- **Per-day drill-down page.** The mini-summary surfaces per-day numbers; a separate per-day page was rejected as low-value.

## Related Features

- [35 — Dietary & Medical Nudge](../profiles/dietary-medical-nudge.md): the data-collection counterpart. This feature is its read consumer — the nudge collects `DietaryPreference` / `Allergies` / `Intolerances` / `*OtherText`, and the roster aggregates them.
- [25 — Shift Management](../shifts/shift-management.md): supplies the `ShiftSignup` rows and `Shift.DayOffset` that define the per-day cohort.
- [Issue #279](https://github.com/nobodies-collective/Humans/issues/279): tracking issue for the dietary-nudge + roster bundle. This spec is the roster half.

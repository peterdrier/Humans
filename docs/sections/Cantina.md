<!-- freshness:triggers
  src/Humans.Application/Interfaces/Cantina/**
  src/Humans.Application/Services/Cantina/**
  src/Humans.Web/Cantina/**
  src/Humans.Web/Controllers/CantinaController.cs
  src/Humans.Web/Views/Cantina/**
  tests/Humans.Application.Tests/Services/Cantina/**
-->
<!-- freshness:flag-on-change
  Cantina access gate, weekly-roster aggregation, on-site definition, and the MedicalConditions exclusion — review when Cantina services/controllers/views change, or when Shifts changes the shape of `volunteer_event_profiles` / `shift_signups`.
-->

# Cantina — Section Invariants

Read-only weekly roster surface for the food-service team — who is on site each day of the week and what they can/cannot eat. Composes over Shifts data; owns no tables.

## Concepts

- The **Cantina** is the food-service team. It plans meals around who is on site for the week, not who is medically vulnerable.
- A human is **on site for a day** when they hold a Pending or Confirmed `ShiftSignup` on a Shift whose `DayOffset` matches that calendar day (relative to `EventSettings.GateOpeningDate`). All-day shifts cover one day each.
- The **Weekly Roster** is the page payload: the cohort of unique humans on site at any point in the Mon–Sun window, their `ArrivesOn` date, their `NoShift` dates (days within the week with no on-site signup), and their non-medical dietary fields (preference, allergies, intolerances, "Other" free-text). Aggregates (dietary preference roll-up, allergy/intolerance counts) are computed over **unique humans** for the week — never summed per day.
- The **Daily Mini-Summary** lists the same per-day cohort counts as a sanity check; same uniqueness rule applies within the day.

## Data Model

None — Cantina owns no tables. The section is a pure read/aggregate composition over:

- `shift_signups` — owned by **Shifts** ([`Shifts.md`](Shifts.md)). Filtered to `Status ∈ {Pending, Confirmed}` joined to `shifts` by `DayOffset`.
- `volunteer_event_profiles` — owned by **Shifts** ([`Shifts.md`](Shifts.md)). Read for `DietaryPreference`, `Allergies`, `AllergyOtherText`, `Intolerances`, `IntoleranceOtherText`. **`MedicalConditions` is never projected into Cantina DTOs.**
- `profiles` — owned by **Profiles** ([`Profiles.md`](Profiles.md)). Read via `IProfileService` for `BurnerName` stitching only.
- `users` — owned by **Users/Identity**. Read via `IUserService` for `DisplayName` fallback only.
- `teams` / team membership — owned by **Teams**. Read via `ITeamService` for the access-gate team-membership probe.

## Routing

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/Cantina/Roster?weekStartOffset=<int>` | GET | `[Authorize]` + `ICantinaAccessService.CanViewRosterAsync` | HTML weekly roster page |
| `/Cantina/Roster/Csv?weekStartOffset=<int>` | GET | same as above | CSV download of the same aggregate |

`weekStartOffset` is the day-offset of the week's Monday relative to `EventSettings.GateOpeningDate`. When omitted, the controller computes the current week via `ICantinaRosterService.GetCurrentWeekStartOffsetForActiveEvent` (returns `0` and an empty roster when no active event).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin, NoInfoAdmin, VolunteerCoordinator | View weekly roster and download CSV (role short-circuit; no DB hit for team lookup) |
| Member of any active team whose `Name` contains "Cantina" (case-insensitive) | View weekly roster and download CSV |
| All other authenticated humans | **HTTP 403** on both routes; CANTINA nav link is hidden |
| Anonymous | Standard `[Authorize]` challenge — redirected to sign-in |

## Invariants

- **`MedicalConditions` is never surfaced via this section, regardless of viewer role.** The Cantina plans around food, not medical history (GDPR Article 9 boundary). The DTO contract excludes the field by construction (`RosterPersonDto` has no `MedicalConditions` property) and `CantinaRosterService` never reads it. Medical data continues to flow through the existing `_VolunteerProfileBadges` partial with `ShowMedical=true`, gated to NoInfoAdmin / Admin — not through Cantina.
- "On site" is strictly defined as a Pending or Confirmed `ShiftSignup` on a Shift with matching `DayOffset`. Refused, Bailed, Cancelled, and NoShow signups do not count. All-day shifts are single-day (one row per signup per day per shift, per Shifts §all-day-window).
- Weekly aggregates (dietary preference roll-up, allergy / intolerance counters, total head count) are computed over **unique humans** for the week, not summed day-by-day. A human on site Mon + Wed counts once.
- The section is **read-only** — no writes to any table, no audit entries, no notifications.
- The roster is rendered live on every request — no cached aggregates. CSV exports the same in-memory aggregate produced for the HTML view.
- Every `RosterPersonDto` in the cohort has at least one on-site day in the window by construction; `ArrivesOn` is therefore non-nullable.
- Burner-name stitching falls through in order: `Profile.BurnerName` → `User.DisplayName` → `"(unknown)"`. No further look-ups.

## Negative Access Rules

- Pre-volunteer humans (Guest dashboard, profile not yet active) **cannot** see the CANTINA nav link — the link is gated by the same `ICantinaAccessService.CanViewRosterAsync` probe used by the controller.
- Any human (including Cantina-team members and Cantina coordinators) **cannot** see another human's `MedicalConditions` through this section — the field is not in the DTO and not in the view. The only surface for medical data remains `_VolunteerProfileBadges` with `ShowMedical=true` (NoInfoAdmin / Admin only).
- Authenticated humans who fail the access gate **cannot** read the roster or download the CSV — both routes return HTTP 403 (`Forbid()`), not a redirect.
- Coordinators of non-Cantina teams (with no Admin / NoInfoAdmin / VolunteerCoordinator role and no Cantina-team membership) **cannot** see the roster — being a coordinator elsewhere is not sufficient.
- No actor **can** write to any table from this section — there are no POST routes.

## Triggers

- View renders on each request; no cache, no background job, no scheduled invalidation. Data is live as of the request.
- CSV export computes the same in-memory aggregate as the HTML view and streams it as `text/csv; charset=utf-8` with filename `cantina-roster-week-of-<yyyy-MM-dd>.csv`.
- No audit entries, no notifications, no outbox events.

## Cross-Section Dependencies

- **Shifts:** `IShiftManagementRepository` — per-day reads of `shift_signups` joined to `shifts` (Pending / Confirmed only) and of `volunteer_event_profiles` for the cohort. Intentional cross-section read; documented in the cross-section baseline at [`tests/Humans.Application.Tests/Architecture/Baselines/CrossSectionRepositoryInjection.baseline.txt`](../../tests/Humans.Application.Tests/Architecture/Baselines/CrossSectionRepositoryInjection.baseline.txt).
- **Profiles:** `IProfileService` — `BurnerName` look-up for the per-person rows (no medical, no contact fields).
- **Users/Identity:** `IUserService` — `DisplayName` fallback when no `Profile.BurnerName` is set.
- **Teams:** `ITeamService` — active-membership probe used by `CantinaAccessService` to evaluate the "any team with 'Cantina' in its name (case-insensitive)" gate.

## Architecture

**Owning services:** `CantinaRosterService`, `CantinaAccessService`
**Owned tables:** None — orchestrator over `IShiftManagementRepository`, `IProfileService`, `IUserService`, `ITeamService`.
**Status:** (A) Migrated — new section in feature [#36](../features/cantina/daily-roster.md); built directly on the §15 pattern from day one.

- Services live in `Humans.Application.Services.Cantina/` and never import `Microsoft.EntityFrameworkCore`.
- **No dedicated repository.** Cantina is a read-side aggregator over `IShiftManagementRepository` (owned by Shifts). The cross-section read is intentional and pinned by the baseline file referenced above; do not introduce a new `ICantinaRepository` that reaches into Shifts-owned tables.
- **Decorator decision — no caching decorator.** Roster data is live per request; the page is low-traffic (coordinator surface, ~handful of viewers per day) and the aggregate cost is bounded by the on-site cohort size for one week.
- **Cross-domain navs** — none declared; the section owns no entities. All cross-section linkage is via service interfaces, by id.
- **Cross-section calls** — `IShiftManagementRepository` (read), `IProfileService`, `IUserService`, `ITeamService`. Plus `IShiftManagementService.GetActiveAsync` from the controller for the default `weekStartOffset` computation.
- **Architecture test** — `tests/Humans.Application.Tests/Services/Cantina/CantinaRosterServiceTests.cs` and `CantinaAccessServiceTests.cs` pin the aggregation rules and the access gate. The cross-section read is additionally pinned by `CrossSectionRepositoryInjection.baseline.txt`.

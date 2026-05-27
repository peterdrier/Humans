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

- `shift_signups` — owned by **Shifts** ([`Shifts.md`](Shifts.md)). Filtered to `Status ∈ {Pending, Confirmed}` joined to `shifts` by `DayOffset`. Read **through `IShiftManagementService`** (`GetOnSiteUserIdsForDayAsync`), never the Shifts repository directly.
- Dietary (`DietaryPreference`, `Allergies`, `AllergyOtherText`, `Intolerances`, `IntoleranceOtherText`) — `Profile` fields owned by **Users/Identity**, read through the cached **`IUserServiceRead.GetUserInfosAsync`** (`UserInfo.Profile`). **`MedicalConditions` is never read by the cantina** — the cantina DTOs have no such field.
- `profiles` / `users` — owned by **Users/Identity**. Burner names are read via the cross-section **`IUserServiceRead.GetUserInfosAsync`** (cached `UserInfo`); no entity reads, no new surface.

## Routing

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/Cantina/Roster?weekStartOffset=<int>` | GET | `[Authorize(Policy = CantinaAdminOrAdmin)]` | HTML weekly roster page |
| `/Cantina/Roster/Csv?weekStartOffset=<int>` | GET | same as above | CSV download of the same aggregate |
| `/Cantina/Roster/Day?dayOffset=<int>` | GET | same as above | Per-day drill-down matrix |
| `/Cantina/Roster/Day/Csv?dayOffset=<int>` | GET | same as above | CSV of the per-day matrix |

`weekStartOffset` is the day-offset of the week's Monday relative to `EventSettings.GateOpeningDate`. When omitted, the controller computes the current week via `ICantinaRosterService.GetCurrentWeekStartOffsetForActiveEvent` (returns `0` and an empty roster when no active event).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin, CantinaAdmin | View weekly/daily roster and download CSV |
| All other authenticated humans | **HTTP 403** on the routes; Cantina admin sidebar entry is hidden |
| Anonymous | Standard `[Authorize]` challenge — redirected to sign-in |

`CantinaAdmin` is a grantable role (the permissions page surfaces it via `RoleNames.All`), aligned with the other per-area `<Area>AdminOrAdmin` policies. There is no team-name-based access path.

## Invariants

- **`MedicalConditions` is never surfaced via this section, regardless of viewer role.** The Cantina plans around food, not medical history (GDPR Article 9 boundary). `MedicalConditions` lives on the cached `UserInfo`/`ProfileInfo`, but `CantinaRosterService` simply never reads it, and the output DTOs (`RosterPersonDto`, `DailyPersonRowDto`) have no `MedicalConditions` property. Medical data continues to flow only through the `_VolunteerProfileBadges` partial with `ShowMedical=true`, gated to NoInfoAdmin / Admin — not through Cantina.
- "On site" is strictly defined as a Pending or Confirmed `ShiftSignup` on a Shift with matching `DayOffset`. Refused, Bailed, Cancelled, and NoShow signups do not count. All-day shifts are single-day (one row per signup per day per shift, per Shifts §all-day-window).
- Weekly aggregates (dietary preference roll-up, allergy / intolerance counters, total head count) are computed over **unique humans** for the week, not summed day-by-day. A human on site Mon + Wed counts once.
- The section is **read-only** — no writes to any table, no audit entries, no notifications.
- The roster is rendered live on every request — no cached aggregates. CSV exports the same in-memory aggregate produced for the HTML view.
- Every `RosterPersonDto` in the cohort has at least one on-site day in the window by construction; `ArrivesOn` is therefore non-nullable.
- Burner-name stitching reads `UserInfo.BurnerName` (from `IUserServiceRead`), falling through to `"(unknown)"` when absent. `UserInfo.BurnerName` itself derives from `Profile.BurnerName` with the legacy `DisplayName` fallback handled inside the Users section.

## Negative Access Rules

- Pre-volunteer humans (Guest dashboard, profile not yet active) **cannot** see the Cantina admin sidebar entry — it lives under the `Cantina` group in `AdminNavTree` and is gated by the same `CantinaAdminOrAdmin` policy used by the controller. The entry is reachable only via the Admin shell (`/Admin/*`); there is no member-shell top-nav link.
- Any human (including Cantina-team members and Cantina coordinators) **cannot** see another human's `MedicalConditions` through this section — the field is not in the DTO and not in the view. The only surface for medical data remains `_VolunteerProfileBadges` with `ShowMedical=true` (NoInfoAdmin / Admin only).
- Authenticated humans who fail the access gate **cannot** read the roster or download the CSV — both routes return HTTP 403 (`Forbid()`), not a redirect.
- Team coordinators and Cantina-team members **cannot** see the roster on that basis alone — access requires the `Admin` or `CantinaAdmin` role. Team membership grants nothing here.
- No actor **can** write to any table from this section — there are no POST routes.

## Triggers

- View renders on each request; no cache, no background job, no scheduled invalidation. Data is live as of the request.
- CSV export computes the same in-memory aggregate as the HTML view and streams it as `text/csv; charset=utf-8` with filename `cantina-roster-week-of-<yyyy-MM-dd>.csv`.
- No audit entries, no notifications, no outbox events.

## Cross-Section Dependencies

- **Shifts:** `IShiftManagementService` — `GetOnSiteUserIdsForDayAsync` (on-site cohort) + `GetActiveAsync` (active event). Service-layer reads only; the cantina never touches the Shifts repository.
- **Users/Identity:** `IUserServiceRead.GetUserInfosAsync` — batched, cached `UserInfo` for burner-name stitching. No entity reads.

## Architecture

**Owning services:** `CantinaRosterService`
**Owned tables:** None — orchestrator over `IShiftManagementService` and `IUserServiceRead`.
**Status:** (A) Migrated — new section in feature [#36](../features/cantina/daily-roster.md); built directly on the §15 pattern from day one.

- Services live in `Humans.Application.Services.Cantina/` and never import `Microsoft.EntityFrameworkCore`.
- **No dedicated repository, no repository reads.** Cantina is a read-side aggregator that calls **only section services** (`IShiftManagementService`, `IUserServiceRead`) — never a repository. This keeps the reads cacheable via the owning sections' decorators and avoids cross-section repository coupling.
- **Access is a policy, not a service.** `CantinaAdminOrAdmin` (Admin or the grantable `CantinaAdmin` role) gates the controller and the nav link; there is no `ICantinaAccessService`.
- **Decorator decision — no caching decorator on the roster itself.** Roster aggregation is live per request; the page is low-traffic (coordinator surface). The user reads it composes ride on the Users-section cache via `IUserServiceRead`.
- **Cross-domain navs** — none declared; the section owns no entities. All cross-section linkage is via service interfaces, by id.
- **Cross-section calls** — `IShiftManagementService` (on-site cohort + active event), `IUserServiceRead` (burner names + dietary, from the cached `UserInfo`).
- **Architecture test** — `tests/Humans.Application.Tests/Services/Cantina/CantinaRosterServiceTests.cs` and `CantinaAccessServiceTests.cs` pin the aggregation rules and the access gate. The cross-section read is additionally pinned by `CrossSectionRepositoryInjection.baseline.txt`.

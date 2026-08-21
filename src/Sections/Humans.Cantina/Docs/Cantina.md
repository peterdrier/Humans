<!-- freshness:triggers
  src/Sections/Humans.Cantina/**
  tests/Humans.Cantina.Tests/**
-->
<!-- freshness:flag-on-change
  Cantina access gate, weekly-roster aggregation, on-site definition, and the MedicalConditions exclusion — review when Cantina services/controllers/views change, or when Shifts changes the shape of `volunteer_event_profiles` / `shift_signups`.
-->

# Cantina — Section Invariants

Read-only weekly roster surface for the food-service team — who is on site each day of the week and what they can/cannot eat. Composes over Shifts data; owns no tables.

## Concepts

- The **Cantina** is the food-service team. It plans meals around who is on site for the week, not who is medically vulnerable.
- A human is **on site for a day** when they hold a Confirmed `ShiftSignup` on a Shift whose `DayOffset` matches that calendar day (relative to `EventSettings.GateOpeningDate`). All-day shifts cover one day each.
- **Arrival-day rule:** a human is also counted as on site (fed) on the day before their first confirmed shift: `arrivalDay = firstConfirmedShiftDay − 1`. This arrival day is stored as `ArrivesOn` on the roster person. The rule applies to the weekly roster, the per-day summary, the daily drill-down, and the CSV export.
- The **Weekly Roster** is the page payload: the cohort of unique humans on site at any point in the Mon–Sun window, their `ArrivesOn` date, their `NoShift` dates (days within the week with no on-site signup), and their non-medical dietary fields (preference, allergies, intolerances, "Other" free-text). Aggregates (dietary preference roll-up, allergy/intolerance counts) are computed over **unique humans** for the week — never summed per day.
- The **Daily Mini-Summary** lists the same per-day cohort counts as a sanity check; same uniqueness rule applies within the day.

## Data Model

None — Cantina owns no tables. The section is a pure read/aggregate composition over:

- `shift_signups` — owned by **Shifts** ([`Shifts.md`](../../Humans.Shifts/Docs/Shifts.md)). Filtered to `Status = Confirmed` joined to `shifts` by `DayOffset`. Read **through `IShiftManagementServiceRead`** (`GetOnSiteUserIdsForDayAsync`), never the Shifts repository directly.
- Dietary (`DietaryPreference`, `Allergies`, `AllergyOtherText`, `Intolerances`, `IntoleranceOtherText`) — `Profile` fields owned by **Users/Identity**, read through the cached **`IUserServiceRead.GetUserInfosAsync`** (`UserInfo.Profile`). **`MedicalConditions` is never read by the cantina** — the cantina DTOs have no such field.
- `profiles` / `users` — owned by **Users/Identity**. Burner names are read via the cross-section **`IUserServiceRead.GetUserInfosAsync`** (cached `UserInfo`); no entity reads, no new surface.

## Routing

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/Cantina/Roster?weekStartOffset=<int>` | GET | `[Authorize(Policy = CantinaAdminOrAdmin)]` | HTML weekly roster page |
| `/Cantina/Roster/Csv?weekStartOffset=<int>` | GET | same as above | CSV download of the same aggregate |
| `/Cantina/Roster/Day?dayOffset=<int>` | GET | same as above | Per-day drill-down matrix |
| `/Cantina/Roster/Day/Csv?dayOffset=<int>` | GET | same as above | CSV of the per-day matrix |

`weekStartOffset` is the day-offset of the week's Monday relative to `BurnSettingsInfo.GateOpeningDate`. When omitted, the controller computes the current week via `ICantinaRosterService.GetCurrentWeekStartOffsetForActiveEvent` (returns `0` and an empty roster when no active event).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin, CantinaAdmin | View weekly/daily roster and download CSV |
| All other authenticated humans | Redirected to `/Account/AccessDenied`; Cantina admin sidebar entry is hidden |
| Anonymous | Standard `[Authorize]` challenge — redirected to sign-in |

`CantinaAdmin` is a grantable role (the permissions page surfaces it via `RoleNames.All`), aligned with the other per-area `<Area>AdminOrAdmin` policies. There is no team-name-based access path.

## Invariants

- The roster tolerates humans with empty dietary fields. The upstream signup gate — `ShiftSignupService.ToggleDayAsync` short-circuits with `ToggleDaySignupOutcome.NeedsDietaryFirst` when the shift `QualifiesForCantinaMeal()` and the human has no `DietaryPreference` — covers **only** the self-service day toggle on `/Shifts`. `IShiftSignupService.VoluntellAsync` / `VoluntellRangeAsync` (coordinator on-behalf-of signup) and `OnboardingWidgetController.SignUp` call `SignUpAsync` directly and are not gated, so a Confirmed signup can exist with no dietary on file. Those humans are chased by the dashboard nudge and the `/Shifts` banner, not blocked — see [`dietary-medical-nudge`](../../Humans.Users/Docs/features/dietary-medical-nudge.md).
- **`MedicalConditions` is never surfaced via this section, regardless of viewer role.** The Cantina plans around food, not medical history (GDPR Article 9 boundary). `MedicalConditions` lives on the cached `UserInfo`/`ProfileInfo`, but `CantinaRosterService` simply never reads it, and the output DTOs (`RosterPersonDto`, `DailyPersonRowDto`) have no `MedicalConditions` property. Medical data continues to flow only through the `_VolunteerProfileBadges` partial with `ShowMedical=true`, gated to NoInfoAdmin / Admin — not through Cantina.
- "On site" is strictly defined as a Confirmed `ShiftSignup` on a Shift with matching `DayOffset`, **plus the arrival day** (`arrivalDay = firstConfirmedShiftDay − 1`). Refused, Bailed, Cancelled, NoShow, and Pending signups do not count. All-day shifts are single-day (one row per signup per day per shift, per Shifts §all-day-window).
- Weekly aggregates (dietary preference roll-up, allergy / intolerance counters, total head count) are computed over **unique humans** for the week, not summed day-by-day. A human on site Mon + Wed counts once.
- The section is **read-only** — no writes to any table, no audit entries, no notifications.
- The roster is rendered live on every request — no cached aggregates. CSV exports the same in-memory aggregate produced for the HTML view.
- Every `RosterPersonDto` in the cohort has at least one on-site day in the window by construction; `ArrivesOn` is therefore non-nullable. The arrival day is a real on-site day, so the `ArrivesOn`-is-non-nullable invariant holds for all roster humans including arrival-day humans.
- Burner-name stitching reads `UserInfo.BurnerName` (from `IUserServiceRead`), falling through to `"(unknown)"` when absent. `UserInfo.BurnerName` itself derives from `Profile.BurnerName` with the legacy `DisplayName` fallback handled inside the Users section.

## Negative Access Rules

- Pre-volunteer humans (Guest dashboard, profile not yet active) **cannot** see the Cantina admin sidebar entry — it lives under the `Cantina` group in `AdminNavTree` and is gated by the same `CantinaAdminOrAdmin` policy used by the controller. The entry is reachable only via the Admin shell (`/Admin/*`); there is no member-shell top-nav link.
- Any human (including Cantina-team members and Cantina coordinators) **cannot** see another human's `MedicalConditions` through this section — the field is not in the DTO and not in the view. The only surface for medical data remains `_VolunteerProfileBadges` with `ShowMedical=true` (NoInfoAdmin / Admin only).
- Authenticated humans who fail the access gate **cannot** read the roster or download the CSV — every route answers `302 → /Account/AccessDenied` (cookie authentication's `AccessDeniedPath`, set app-wide in `Program.cs`). Pinned by `CantinaPageRenderTests.A_volunteer_cannot_reach_the_roster_or_the_export`. This doc previously claimed a bare `403 (Forbid())`; the redirect is what the app has always done and is not a G5 change.
- Team coordinators and Cantina-team members **cannot** see the roster on that basis alone — access requires the `Admin` or `CantinaAdmin` role. Team membership grants nothing here.
- No actor **can** write to any table from this section — there are no POST routes.

## Triggers

- View renders on each request; no cache, no background job, no scheduled invalidation. Data is live as of the request.
- CSV export computes the same in-memory aggregate as the HTML view and streams it as `text/csv; charset=utf-8` with filename `cantina-roster-week-of-<yyyy-MM-dd>.csv`.
- No audit entries, no notifications, no outbox events.

## Cross-Section Dependencies

- **Shifts:** `IShiftManagementServiceRead.GetOnSiteUserIdsForDayAsync` (on-site cohort) + `IBurnSettingsService.GetActiveAsync` (active event/burn). Service-layer reads only; the cantina never touches the Shifts repository.
- **Users/Identity:** `IUserServiceRead.GetUserInfosAsync` — batched, cached `UserInfo` for burner-name stitching. No entity reads.

## Architecture

**Project:** `src/Sections/Humans.Cantina` (G5, nobodies-collective/Humans#866)
**Owning services:** `CantinaRosterService`
**Owned tables:** None — orchestrator over `IShiftManagementServiceRead`, `IBurnSettingsService`, and `IUserServiceRead`.
**Status:** (A) Migrated — new section in feature [#36](features/daily-roster.md); built directly on the §15 pattern from day one, moved into its own project unchanged.

- Everything but `Section` and `CantinaResource` is `internal` (HUM0034). `Contracts/` is an empty folder: nothing outside the section names a Cantina type, and `ICantinaRosterService` stayed in `Services/`, `internal`, because its only consumer is the section's own controller.
- **No `Humans.Infrastructure` reference and no EF Core package.** The section owns no tables, so there is no `DbContext`, no repository, no migration and no `AddSectionDbContext` line — Scanner's shape. `CantinaArchitectureTests.SectionAssemblyDoesNotReferenceEntityFrameworkCore` is what keeps it that way.
- **No dedicated repository, no repository reads.** Cantina is a read-side aggregator that calls **only section services** (`IShiftManagementServiceRead`, `IBurnSettingsService`, `IUserServiceRead`) — never a repository. This keeps the reads cacheable via the owning sections' decorators and avoids cross-section repository coupling.
- **Access is a policy, not a service.** `CantinaAdminOrAdmin` (Admin or the grantable `CantinaAdmin` role) gates the controller and the nav link; there is no `ICantinaAccessService`. The policy registration stays in Shell's `AuthorizationPolicyExtensions` (design §8's asymmetry), and `RoleNames.CantinaAdmin` / `PolicyNames.CantinaAdminOrAdmin` stay in `Humans.Base` — they are `string` constants, not references to this project.
- **Display sort is presentation.** `CantinaRosterAssembler`, `CantinaRosterCsvWriter` and `CantinaDailyMatrixCsvWriter` moved from `Humans.Web/Cantina/` into the section's `Models/` (`memory/architecture/display-sort-in-controllers.md`).
- **Decorator decision — no caching decorator on the roster itself.** Roster aggregation is live per request; the page is low-traffic (coordinator surface). The user reads it composes ride on the Users-section cache via `IUserServiceRead`.
- **Cross-domain navs** — none declared; the section owns no entities. All cross-section linkage is via service interfaces, by id.
- **Cross-section calls** — `IShiftManagementServiceRead` (on-site cohort), `IBurnSettingsService` (active event/burn), `IUserServiceRead` (burner names + dietary, from the cached `UserInfo`).
- **Tests** — `tests/Humans.Cantina.Tests/Services/CantinaRosterServiceTests.cs` and `CantinaDailyRosterServiceTests.cs` pin the aggregation rules; `Models/CantinaRosterAssemblerTests.cs` and `Models/CantinaCsvWritersTests.cs` pin the display sort and the export layout; `CantinaArchitectureTests.cs` pins the section shape, including that no roster DTO grows a `Medical*` property. `tests/Humans.Integration.Tests/Controllers/CantinaPageRenderTests.cs` renders both pages, both CSV exports and the access gate, in English and Spanish.

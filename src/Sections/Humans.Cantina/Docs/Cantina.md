<!-- freshness:triggers
  src/Sections/Humans.Cantina/**
  tests/Humans.Cantina.Tests/**
-->
<!-- freshness:flag-on-change
  Cantina access gate, weekly-roster aggregation, on-site definition, the dietary-profile store/legacy fallback, and the MedicalConditions exclusion — review when Cantina services/controllers/views change, or when Shifts changes the shape of `volunteer_event_profiles` / `shift_signups`.
-->

# Cantina — Section Invariants

Weekly roster surface for the food-service team — who is on site each day of the week and what they can/cannot eat — plus the members' own dietary answers (`cantina_dietary_profiles`), collected by the dietary form and nudge.

## Concepts

- The **Cantina** is the food-service team. It plans meals around who is on site for the week, not who is medically vulnerable.
- A human is **on site for a day** when they hold a Confirmed `ShiftSignup` on a Shift whose `DayOffset` matches that calendar day (relative to `EventSettings.GateOpeningDate`). All-day shifts cover one day each.
- **Arrival-day rule:** a human is also counted as on site (fed) on the day before their first confirmed shift: `arrivalDay = firstConfirmedShiftDay − 1`. Every roster person's `ArrivesOn` is this date, even when it falls before the requested week. The rule applies to the weekly roster, the per-day summary, the daily drill-down, and the CSV export.
- The **Weekly Roster** is the page payload: the cohort of unique humans on site at any point in the Mon–Sun window, their `ArrivesOn` date, their `NoShift` dates (days within the week with no on-site signup), and their non-medical dietary fields (preference, allergies, intolerances, "Other" free-text). Aggregates (dietary preference roll-up, allergy/intolerance counts) are computed over **unique humans** for the week — never summed per day.
- The **Daily Mini-Summary** lists the same per-day cohort counts as a sanity check; same uniqueness rule applies within the day.
- A **Dietary Profile** is one member's food answers: meal preference, allergies, intolerances and their "Other" texts. A non-empty preference means "answered". Answering is opt-in: a member may instead choose **"I don't want to answer"** (`DeclinedToAnswer`), which stops the prompts. It never holds medical conditions — those stay on the Users `Profile`.

## Data Model

### `cantina_dietary_profiles` (`DietaryProfile`, owned)

| Column | Notes |
|---|---|
| `UserId` | PK, never generated |
| `DietaryPreference` | `varchar(200)?`, free text (the UI offers `DietaryOptions.DietaryPreferences`) |
| `Allergies`, `Intolerances` | `jsonb` lists, filtered against `DietaryOptions` on save |
| `AllergyOtherText`, `IntoleranceOtherText` | `varchar(500)?`, kept only while "Other" is selected |
| `DeclinedToAnswer` | `bool?`, null = unanswered. True after "I don't want to answer": every food field is then empty. Saving answers resets it. |
| `UpdatedAt` | `Instant` |

Context: `CantinaDbContext` (history table `__EFMigrationsHistory_Cantina`). The data-migration registry entry `cantina.profile-dietary` copies the legacy `profiles` food columns for members without a row (idempotent).

**Legacy fallback.** Until the retirement PR drops the Users columns, a member without a row reads their legacy `ProfileInfo` food fields (`[Obsolete]`, `HUM_PROFILE_DIETARY`). `DietaryProfileService.LegacyProfile` is the one suppressed reader. A saved row — even an emptied one — always wins, so a cleared answer never resurrects.

### Read sources

- `shift_signups` — owned by **Shifts** ([`Shifts.md`](../../Humans.Shifts/Docs/Shifts.md)). Filtered to `Status = Confirmed` joined to `shifts` by `DayOffset`. Read **through `IShiftService`** (`GetOnSiteUserIdsForDayAsync`), never the Shifts repository directly.
- `profiles` / `users` — owned by **Users/Identity**. Burner names, the merge chain and the legacy food fallback are read via **`IUserService`** (cached `UserInfo`). **`MedicalConditions` is never read by the cantina** — no Cantina DTO has such a field.

## Routing

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/Cantina/Roster?weekStartOffset=<int>` | GET | `[Authorize(Policy = CantinaAdminOrAdmin)]` | HTML weekly roster page |
| `/Cantina/Roster/Csv?weekStartOffset=<int>` | GET | same as above | CSV download of the same aggregate |
| `/Cantina/Roster/Day?dayOffset=<int>` | GET | same as above | Per-day drill-down matrix |
| `/Cantina/Roster/Day/Csv?dayOffset=<int>` | GET | same as above | CSV of the per-day matrix |
| `/Cantina/Dietary` | GET/POST | `[Authorize]`, own answers only | The member's dietary form. POST saves answers, or with `DeclineToAnswer=true` (the form's and the `/Shifts` banner's "I don't want to answer" button) records the opt-out without validating answers. `returnAction=shifts` lands on `/Shifts`, otherwise Home. |

`weekStartOffset` is the day-offset of the week's Monday relative to `EventSettingsInfo.GateOpeningDate`. When omitted, the service resolves the week containing today in the event's timezone, off the active event it has already loaded. With no active event the offset is `0` and the page renders its empty state. `/Cantina/Roster/Day` resolves `dayOffset` the same way.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin, CantinaAdmin | View weekly/daily roster and download CSV |
| Any authenticated member (incl. not-yet-Active; `MembershipRequiredFilter` exempts the form) | Read and save **their own** dietary answers, or decline to answer |
| All other authenticated humans | Redirected to `/Account/AccessDenied`; Cantina admin sidebar entry is hidden |
| Anonymous | Standard `[Authorize]` challenge — redirected to sign-in |

`CantinaAdmin` is a grantable role (declared through `ISectionRoles`, so the role-assignment page and `AnyAdminRole` pick it up), aligned with the other per-area `<Area>AdminOrAdmin` policies. There is no team-name-based access path.

## Invariants

- The section’s things-to-do contribution forwards caller cancellation through all data reads. Aborted reads propagate instead of being logged or converted into missing prompts.

- **Dietary info is opt-in and never blocks a shift signup** (nobodies-collective/Humans#2104). No signup path — the `/Shifts` day toggle, range signup, onboarding widget, Voluntell — consults Cantina. The roster tolerates humans with empty dietary fields (declined members count as unanswered).
- **Cantina reports facts; callers decide whether to ask.** `ICantinaService` exposes only the profile (`HasPreference`, `DeclinedToAnswer`); it has no "should prompt" read. Each prompt (Cantina's dashboard nudge, Users' medical prompt, the Shifts `/Shifts` banner) composes its own gate from those facts plus what it owns: today all three hide on a preference or a decline. See [`dietary-nudge`](features/dietary-nudge.md).
- **`MedicalConditions` is never surfaced via this section, regardless of viewer role.** The Cantina plans around food, not medical history (GDPR Article 9 boundary). `MedicalConditions` lives on the cached `UserInfo`/`ProfileInfo`, but `CantinaRosterService` simply never reads it, and the output DTOs (`RosterPersonDto`, `DailyPersonRowDto`) have no `MedicalConditions` property. Medical data continues to flow only through the `_VolunteerProfileBadges` partial with `ShowMedical=true`, gated to ShiftsNoInfo / Admin — not through Cantina.
- "On site" is strictly defined as a Confirmed `ShiftSignup` on a Shift with matching `DayOffset`, **plus the arrival day** (`arrivalDay = firstConfirmedShiftDay − 1`). Refused, Bailed, Cancelled, NoShow, and Pending signups do not count. All-day shifts are single-day (one row per signup per day per shift, per Shifts §all-day-window).
- Weekly aggregates (dietary preference roll-up, allergy / intolerance counters, total head count) are computed over **unique humans** for the week, not summed day-by-day. A human on site Mon + Wed counts once. Empty weeks use the same aggregation path, retaining every dietary/allergy/intolerance bucket with a zero count.
- The roster stays read-only: no membership, schedule or dietary writes, and no notifications. Weekly and daily CSV downloads append `CantinaRosterExported` through AuditLog with the authenticated actor, event/date scope and emitted person-row count, including empty/repeated downloads. No dietary values are logged; HTML reads do not add export audits.
- The roster is rendered live on every request — no cached aggregates. CSV exports the same in-memory aggregate produced for the HTML view.
- Every `RosterPersonDto` in the cohort has a confirmed shift no later than the day after the week, so its arrival day always exists and `ArrivesOn` is non-nullable.
- A dietary save is the member's own: the form takes the user from the principal, refuses "Other" without its text, filters chips against `DietaryOptions`, and keeps no medical field. A save clears `DeclinedToAnswer`; a decline clears every food field. Member self-saves and declines are not audited.
- Merge (`IUserMerge`): the survivor's answers win. When the survivor has none, the source's effective answers move, including legacy-only answers. Saved empty answers and declines suppress that account's legacy answers. The source's row is removed; retries preserve the result. Reads under an archived id resolve to the survivor.
- GDPR: export slice `DietaryProfile` (row or legacy fallback, including `DeclinedToAnswer`; null when none); erasure deletes the row. Users' erasure still nulls the legacy columns.
- Burner-name stitching reads `UserInfo.BurnerName` — the value Users already resolved (`User.BurnerName` → `Profile.BurnerName` → legacy display name, nobodies-collective/Humans#1097) — off the cached `UserInfo` from `IUserService`. The `"(unknown)"` fall-through covers a user id the cohort read did not return, not a case the roster is expected to render.

## Negative Access Rules

- Pre-volunteer humans (Guest dashboard, profile not yet active) **cannot** see the Cantina admin sidebar entry — it lives under the `Cantina` group in `AdminNavTree` and is gated by the same `CantinaAdminOrAdmin` policy used by the controller. The entry is reachable only via the Admin shell (`/Admin/*`); there is no member-shell top-nav link.
- Any human (including Cantina-team members and Cantina coordinators) **cannot** see another human's `MedicalConditions` through this section — the field is not in the DTO and not in the view. The only surface for medical data remains `_VolunteerProfileBadges` with `ShowMedical=true` (ShiftsNoInfo / Admin only).
- Authenticated humans who fail the access gate **cannot** read the roster or download the CSV — every route answers `302 → /Account/AccessDenied` (cookie authentication's `AccessDeniedPath`, set app-wide in `Program.cs`). Pinned by `CantinaPageRenderTests.A_volunteer_cannot_reach_the_roster_or_the_export`.
- Team coordinators and Cantina-team members **cannot** see the roster on that basis alone — access requires the `Admin` or `CantinaAdmin` role. Team membership grants nothing here.
- No actor **can** change membership or schedule data from this section, and no one **can** edit another member's dietary answers — the only POST is the member's own form. Export audit entries are the roster's only writes.

## Triggers

- View renders on each request; no cache, no background job, no scheduled invalidation. Data is live as of the request.
- CSV export computes the same in-memory aggregate as the HTML view and streams it as `text/csv; charset=utf-8` with filename `cantina-roster-week-of-<yyyy-MM-dd>.csv`.
- The dietary things-to-do (`SectionThingsToDo`, key `dietary`) shows while the member has neither a preference nor `DeclinedToAnswer`; its copy switches on `IShiftService.HasQualifyingCantinaSignupAsync`. Saving a preference or choosing "I don't want to answer" clears it, Users' medical prompt and the `/Shifts` banner.
- Weekly/daily CSV callers pass their actor to the existing roster service, which audits after the personal rows are built. No notifications or outbox events.

## Cross-Section Dependencies

- **AuditLog:** `IAuditLogService` records existing weekly/daily personal-data CSV disclosures.

- **Shifts:** `IShiftService.GetOnSiteUserIdsForDayAsync` (on-site cohort). Service-layer reads only; the cantina never touches the Shifts repository.
- **Settings:** `ISettingsService.GetActiveEventSettingsAsync` (active event/burn).
- **Users/Identity:** `IUserService.GetUserInfosAsync` / `GetUserInfoAsync` — cached `UserInfo` for burner names, the merge chain and the legacy food fallback. No entity reads.
- **Gdpr:** implements `IUserDataContributor` and `IUserMerge` (`DietaryProfileService`).
- **Inbound:** Shifts (banner, volunteer search, admin badges) and Users (medical prompt) read `ICantinaService`.

## Architecture

**Project:** `src/Sections/Humans.Cantina` (nobodies-collective/Humans#866)
**Owning services:** `CantinaRosterService`, `DietaryProfileService`
**Owned tables:** `cantina_dietary_profiles` (nobodies-collective/Humans#1113).
**Status:** (A) Migrated — new section in feature [#36](features/daily-roster.md); built on the §15 pattern.
**Health target:** [`health.md`](health.md) — the shape this section is converging on, its seams and its deliberately-not-done list.

- Everything but `Section`, `CantinaResource` and `Contracts/` is `internal` (HUM0034). `Contracts/ICantinaService` (+ `DietaryProfileInfo`) is the public read surface; a folder, not a `.Contracts` project, because no consumer closes a cycle (of Shifts and Users, Cantina references only their `.Contracts` leaves). There is no `ICantinaWrite`: only the section's own form writes. `IDietaryProfileManagement` (internal) adds the save, the decline and the legacy copy. `ICantinaRosterService` stays in `Services/`, `internal`.
- **`CantinaDbContext`** registered via `AddSectionDbContext` (sentinel `cantina_dietary_profiles`); `DietaryProfileRepository` is its only reader/writer and only `DietaryProfileService` calls it.
- **The roster reads no repository.** `CantinaRosterService` calls **only section services** (`IShiftService`, `ISettingsService`, `IUserService`, `ICantinaService`). This keeps the reads cacheable via the owning sections' decorators and avoids cross-section repository coupling.
- **Access is a policy, not a service.** `CantinaAdminOrAdmin` (Admin or the grantable `CantinaAdmin` role) gates the controller and the nav link; there is no `ICantinaAccessService`. The section registers it itself, in `SectionPolicies.cs` via `ISectionPolicies` (not in the Shell's `AuthorizationPolicyExtensions`). `CantinaRoles.CantinaAdmin` / `PolicyNames.CantinaAdminOrAdmin` stay in `Humans.Base` — they are `string` constants, not references to this project.
- **Display sort is presentation.** `CantinaRosterAssembler`, `CantinaRosterCsvWriter` and `CantinaDailyMatrixCsvWriter` live in the section's `Models/` (`memory/architecture/display-sort-in-controllers.md`).
- **Decorator decision — no caching decorators.** Roster aggregation is live per request; dietary reads are one keyed row (or one batched query) on a handful of pages — a `TrackedCache` decorator buys nothing at this scale.
- **Cross-domain navs** — none declared; `DietaryProfile` keys on `UserId` only. All cross-section linkage is via service interfaces, by id.
- **Cross-section calls** — `IShiftService` (on-site cohort), `ISettingsService` (active event/burn), `IUserService` (burner names, merge chain, legacy fallback), `ICantinaService` (dietary).
- **Tests** — `tests/Humans.Cantina.Tests/Services/CantinaRosterServiceTests.cs` and `CantinaDailyRosterServiceTests.cs` pin the aggregation rules; `Models/CantinaRosterAssemblerTests.cs` and `Models/CantinaCsvWritersTests.cs` pin the display sort and the export layout; `CantinaArchitectureTests.cs` pins the section shape, including that no roster DTO grows a `Medical*` property; `DietaryProfileTests.cs` pins fallback, save rules, merge, migration, export/erase and the nudge; `Controllers/CantinaProfileControllerTests.cs` pins sign-in and antiforgery, prefill, the save and decline redirects, and validation (including Other without text). The opt-in `tests/Humans.Integration.Tests/Controllers/CantinaPageRenderTests.cs` (not run in CI) renders the roster, day and dietary pages, both CSV exports and the volunteer deny path, in English and Spanish.

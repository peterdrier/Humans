<!-- freshness:triggers
  src/Sections/Humans.Cantina/**
  tests/Humans.Cantina.Tests/**
  src/Sections/Humans.Users/Domain/Profile.cs
  src/Sections/Humans.Users.Contracts/UserInfo.cs
  src/Sections/Humans.Shifts/Services/ShiftManagementService.cs
  src/Sections/Humans.Shifts/Data/ShiftRepository.Signups.cs
  src/Sections/Humans.Shifts/Domain/VolunteerEventProfile.cs
-->

# Cantina — Health

Last assessed: 2026-09-12 (section-doctor).

## 1. What the section does

Tells the people who cook how many mouths are on site, and what those mouths can eat.

A coordinator opens a week and sees, for each of its seven days, how many humans are around
and how many of them never said what they eat. They can step back to last week or forward to
next week, click any day to get the full person-by-person picture for that day's meal, and
download either view as a spreadsheet to take to the shops or the kitchen.

It also holds what each member eats: the dietary form and its things-to-do nudge write the
member's own answers, and Shifts asks it whether a member has answered before a long-shift signup.

Nobody's medical history appears anywhere in this, on purpose, for anybody.

## 2. The shapes

Everything the section exposes answers one of these questions.

| Shape | The question | Surfaces |
|---|---|---|
| **Week overview** | How many are here each day this week, and how many haven't answered? | `GET /Cantina/Roster` |
| **Day detail** | Exactly who is here on this one day, and what does each of them eat? | `GET /Cantina/Roster/Day` |
| **Take it away** | The same two answers, as a file. | `GET /Cantina/Roster/Csv`, `GET /Cantina/Roster/Day/Csv` |
| **What do I eat?** | A member records their own food answers. | `GET`/`POST /Profile/Me/Dietary`, things-to-do `dietary` |
| **Has this member answered?** | Other sections read the answers. | `ICantinaService` |

Five shapes, and "which week did you mean?" is not one of them — resolving a missing offset to
"now" is the service's own default, not a question a coordinator asks.

## 3. Structure

The roster is a read-side aggregator; the dietary store is a small owned table beside it. Written fresh, it is:

- **One service** answering the two real questions, each taking an optional offset and
  resolving "now" itself when the offset is absent. It owns the event lookup and the clock
  because resolving "now" is its job, not its caller's.
- **One controller** that parses a nullable `int` off the query string, calls one service
  method, and picks a view or a CSV writer. No date arithmetic, no event lookup, no clock.
- **Presentation-only helpers** — one sort-for-display assembler and two CSV writers — that
  take a finished payload and arrange it.
- **Two payload records**, one per real question, plus the small row/rollup records they carry.
- **One dietary store**: `cantina_dietary_profiles` behind `DietaryProfileRepository` and
  `DietaryProfileService` (reads, save, merge, export/erase, legacy fallback and copy), one
  member-facing controller and one things-to-do contributor.

The layout matches. Each entry point takes a nullable offset and resolves the default itself
from the event it has already loaded, so the controller passes the query parameter straight
through and owns nothing but sorting and file naming.

## 4. Invariants

- **On site** = a Confirmed shift signup on a shift whose `DayOffset` is that day. Pending,
  Refused, Bailed, NoShow and Cancelled never count. All-day shifts cover one day only.
- **Arrival day** = the day before a human's *first confirmed shift of the whole event*. It is a
  real on-site day: it feeds the person into the cohort, sets `ArrivesOn`, and is never listed
  under `NoShift`. It is computed from a scan that starts at build start, so a human whose only
  relation to the visible week is their arrival still appears.
- **No clamp.** An arrival day before the event's first day is shown as-is, negative offset and all.
- **Weekly aggregates count unique humans**, never a sum of days. On site Monday and Wednesday is
  one human, once, in every total and every roll-up row.
- **`MedicalConditions` is never read, never mapped, never rendered** — no viewer role changes
  this. The payload records have no such property, and that absence is pinned by a test.
- **The roster writes nothing.** Its only side effect: weekly/daily CSV disclosures append AuditLog entries with actor, scope and person-row count. The only domain write is a member saving their own dietary answers.
- **A saved row always beats the legacy fallback**, even an emptied one — a cleared answer never resurrects from the old `profiles` columns.
- **Live on every request.** No cached aggregate and no cache on the dietary reads; burner names
  ride the Users section's cache, behind `IUserService`.
- **Access is `Admin` or `CantinaAdmin`, by policy.** An authenticated human without either is
  redirected to `/Account/AccessDenied` — not a bare 403. Team membership grants nothing.
  That gate is also what makes these pages localization-exempt
  ([`localization-admin-exempt`](../../../../memory/code/localization-admin-exempt.md) exempts a
  view by the roles that can open it, not by membership of its illustrative route list). The
  2026-09-12 run read that list as the gate and queued a localization finding that does not
  exist; the reasoning is recorded here so the next run does not re-derive it.

## 5. Seams

- **Unknown dietary values have no defined home.** The breakdown buckets the four canonical
  preferences and an "Unanswered" pseudo-bucket; a stored value outside that set is currently
  counted as neither, so the breakdown sums to less than the on-site total whenever one
  exists. The code comment used to claim the opposite (that unknowns are folded into
  Unanswered) and now states the actual behaviour; what remains undecided is which bucket
  they *should* land in. `DietaryPreference` is an unvalidated `string?` and the write
  path does not filter it (the move into Cantina kept it free text so legacy values stay
  readable), so the fix is a value-model change, not a count fix. Still open alongside the
  missing "Other" preference; deliberately not patched here.
- **`VolunteerEventProfile.DietaryPreference` / `.Allergies` / `.Intolerances` are marked
  "RETAINED for prod-soak drop"** in Shifts. When they go, every doc that still names them as
  Cantina's source (and this section's freshness triggers) should be re-checked in the same pass.
- **The legacy food columns on `profiles` are still there.** Users decomposition commit 10
  (nobodies-collective/Humans#1113) moved the answers into `cantina_dietary_profiles`; the old
  columns stay, `[Obsolete]` on `ProfileInfo` (`HUM_PROFILE_DIETARY`), read only by
  `DietaryProfileService.LegacyProfile` and the `cantina.profile-dietary` copy, and still exported
  and erased by Users. A retirement PR, after prod has run the copy, removes the fallback and drops
  the columns.

## 6. Deliberately not done

- **No caching decorator on the roster or on `ICantinaService`.** The page is a low-traffic
  coordinator surface and the numbers must be live; dietary reads are one keyed row or one
  batched query.
- **No `ICantinaWrite`.** Only the section's own form writes dietary answers.
- **No `MIN(DayOffset) GROUP BY UserId` query for the first-confirmed-shift scan.** It would be
  cheaper than the per-day loop, and it was rejected on purpose: it needs a new repository and a
  new cross-section interface method, and Cantina is not allowed to reach a repository. The
  round-trip count is the accepted price of the boundary. Do not "optimize" this without
  Peter — the cost is round-trips, not data volume, at our small scale.
- **No `.Contracts` project.** `ICantinaService` lives in the `Contracts/` folder: Shifts and Users
  reference the section project directly because Cantina reaches back only into their
  `.Contracts` leaves, so no cycle needs breaking. `ICantinaRosterService` stays `internal` in
  `Services/`; the decision follows the consumer list, never the name.
- **No access service.** `CantinaAdminOrAdmin` is a policy; there is no `ICantinaAccessService`.
- **No gap-filling between arrival and departure, and no departure-day feeding.** Only the single
  arrival day is added. Asked and answered when the arrival rule was designed.
- **No per-meal granularity, no PDF, no live push, no multi-event scope, no editing from the
  roster, no notify-the-unanswered action.**

## Load-bearing weirdness

- **`GetOnSiteUserIdsForDayAsync` is confirmed-only, and Cantina is its only production caller.**
  The Shifts method was narrowed from Pending-or-Confirmed *for* the cantina. A second caller
  expecting pending signups back would be silently wrong — that is a landmine, not a preference.
- **The first-confirmed-shift scan is deliberately separate from the visible-week load.** They
  look duplicative and are not: the week load must stay decoupled from the event bounds, while
  the arrival scan must start at build start to find a *true* first shift. Merging them naively
  changes which humans get an arrival day.
- **`ArrivesOn` is the earliest on-site day *within the requested week*** — which equals the true
  arrival day only for humans who arrive that week. For a multi-week attendee it is simply their
  first day in the window. The name overpromises; the CSV spec is the accurate description.
- **`CantinaResource.cs` must stay in `namespace Humans.Cantina`.** The SDK derives the resx
  manifest name from the adjacent same-named `.cs` file's namespace. A tidier
  `Humans.Cantina.Resources` makes every Cantina string render as its raw key at runtime.
- **`Views/_ViewImports.cshtml` is not inherited from the Shell.** A missing `@using` there ships
  broken markup with a green build and no runtime error.
- **The weekly page is overview-only.** Per-person detail lives on the day drill-down. The weekly
  multi-key sort therefore shapes only the CSV, not any on-screen table — and since 2026-09-12
  the HTML action no longer runs it at all.
- **The burner name comes off `UserInfo`, never off `ProfileInfo`.**
  nobodies-collective/Humans#1097 moved the name onto the user row and made
  `UserInfo.BurnerName` the resolved value (`User.BurnerName` → `Profile.BurnerName` → legacy
  display name). Reading `ProfileInfo.BurnerName` renders `"(unknown)"` for a human whose name
  only ever landed on the user row. This section is a pure read-side consumer of Users'
  read-model, so a change on that side lands here silently — the 2026-09-12 run found exactly
  this, three weeks after Cantina's own code last changed. Pinned by
  `GetDailyRoster_BurnerName_ComesFromTheResolvedUserName`.

## History

| Date | Run | Headline |
|---|---|---|
| 2026-08-22 | [run](../../../../docs/health/runs/2026-08-22-Cantina.md) | First doctor pass — docs described the inverse of the shipped UI; 72 dead resx entries removed; two behavior defects found and queued. PR [#1453](https://github.com/peterdrier/Humans/pull/1453) |
| 2026-09-12 | [run](../../../../docs/health/runs/2026-09-12-Cantina.md) | Re-doctor. Cantina's own code had not changed; every finding was drift from Users. Coordinators were seeing `"(unknown)"` instead of names (nobodies-collective/Humans#1097) — fixed and pinned. A false GDPR retention claim (closed by nobodies-collective/Humans#1446) removed. Dead sort on the weekly page cut; comment truth across nine files. PR [#1661](https://github.com/peterdrier/Humans/pull/1661) |

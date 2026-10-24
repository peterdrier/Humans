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

Last assessed: 2026-10-24 (section-doctor).

## 1. What the section does

Tells the people who cook how many mouths are on site, and what those mouths can eat.

A coordinator opens a week and sees, for each of its seven days, how many humans are around
and how many of them never said what they eat. They can step back to last week or forward to
next week, click any day to get the full person-by-person picture for that day's meal, and
download either view as a spreadsheet to take to the shops or the kitchen.

It also holds what each member eats. A member fills in the dietary form, or says they would
rather not answer, and a things-to-do nudge reminds them until they do one or the other. Shifts
and Users read those answers to show a banner, a badge or a prompt; nothing a member signs up
for waits on them.

Nobody's medical history appears anywhere in this, on purpose, for anybody.

## 2. The shapes

Everything the section exposes answers one of these questions.

| Shape | The question | Surfaces |
|---|---|---|
| **Week overview** | How many are here each day this week, and how many haven't answered? | `GET /Cantina/Roster` |
| **Day detail** | Exactly who is here on this one day, and what does each of them eat? | `GET /Cantina/Roster/Day` |
| **Take it away** | The same two answers, as a file. | `GET /Cantina/Roster/Csv`, `GET /Cantina/Roster/Day/Csv` |
| **What do I eat?** | A member records their own food answers, or declines. | `GET`/`POST /Cantina/Dietary`, things-to-do `dietary` |
| **Has this member answered?** | Other sections read the answers. | `ICantinaService` |

"Which week did you mean?" is not a shape: resolving a missing offset to "now" is the
service's own default, not a question a coordinator asks.

## 3. Structure

The roster is a read-side aggregator; the dietary store is a small owned table beside it.
Written fresh, it is:

- **One roster service** answering the two roster questions, each taking an optional offset and
  resolving "now" itself. It owns the event lookup and the clock because resolving "now" is its
  job, not its caller's. Each question is one pipeline: an empty week or day goes through the
  same code as a full one and comes out as zeros.
- **One roster controller** that parses a nullable `int` off the query string, calls one service
  method, and picks a view or a CSV writer. No date arithmetic, no event lookup, no clock.
- **Presentation-only helpers**: one sort-for-display assembler and two CSV writers that take a
  finished payload and arrange it.
- **Two payload records**, one per roster question, plus the small row and rollup records they
  carry.
- **One dietary store**: `cantina_dietary_profiles` behind `DietaryProfileRepository` and
  `DietaryProfileService` (reads, save, decline, merge, export and erasure, legacy fallback and
  copy), one member-facing controller, one things-to-do contributor and one data migration.
- **One option list**, `DietaryOptions`, feeding the form's chips, the save's filter, the
  roster's buckets and both views' column order.

The layout matches.

## 4. Invariants

- **On site** = a Confirmed shift signup on a shift whose `DayOffset` is that day. Pending,
  Refused, Bailed, NoShow and Cancelled never count. The roster reads the cohort only through
  `IShiftService.GetOnSiteUserIdsForDayAsync`, which asks for confirmed signups only
  (`src/Sections/Humans.Shifts/Services/ShiftManagementService.cs:2066`).
- **Arrival day** = the day before a human's first confirmed shift of the whole event. It feeds
  the person into the cohort, sets `ArrivesOn`, and is never listed under `NoShift`. The scan
  starts at the event's build start, so a human whose only relation to the visible week is their
  arrival still appears (`src/Sections/Humans.Cantina/Services/CantinaRosterService.cs:309`).
- **No clamp.** An arrival day before the event's first day is shown as-is, negative offset and
  all (`src/Sections/Humans.Cantina/Services/CantinaRosterService.cs:87`).
- **Weekly aggregates count unique humans**, never a sum of days: the cohort is a dictionary keyed
  by user id before any total is taken
  (`src/Sections/Humans.Cantina/Services/CantinaRosterService.cs:102`).
- **A member counts as answered only with a non-empty preference** (`DietaryProfileInfo.HasPreference`,
  `src/Sections/Humans.Cantina/Contracts/ICantinaService.cs:27`). A member who declined counts as
  unanswered on the roster.
- **`MedicalConditions` is never read, never mapped, never rendered** by any viewer role. The
  payload records have no such property, pinned by `RosterDtosCarryNoMedicalConditions`
  (`tests/Humans.Cantina.Tests/CantinaArchitectureTests.cs:28`).
- **The roster writes nothing but its disclosure audit.** A weekly or daily CSV download appends
  `CantinaRosterExported` with actor, scope and person-row count; page reads append nothing
  (`src/Sections/Humans.Cantina/Services/CantinaRosterService.cs:51`).
- **A dietary write is the member's own.** The form takes the user from the principal
  (`src/Sections/Humans.Cantina/Controllers/CantinaProfileController.cs:70`), unknown chips are
  dropped and "Other" without its text is refused
  (`src/Sections/Humans.Cantina/Services/DietaryProfileService.cs:51`). A save clears the decline;
  a decline clears every food field.
- **A saved row always beats the legacy fallback**, even an emptied one: a cleared answer never
  resurrects from the old `profiles` columns
  (`src/Sections/Humans.Cantina/Services/DietaryProfileService.cs:26`).
- **Access to the roster is `Admin` or `CantinaAdmin`, by policy**
  (`src/Sections/Humans.Cantina/Controllers/CantinaController.cs:15`). An authenticated human
  without either is redirected to `/Account/AccessDenied`, not given a bare 403. Team membership
  grants nothing. That gate is also what makes the roster pages localization-exempt:
  [`localization-admin-exempt`](../../../../memory/code/localization-admin-exempt.md) exempts a
  view by the roles that can open it, not by its illustrative route list.

## 5. Seams

- **Unknown dietary values have no defined home.** The breakdown buckets the four canonical
  preferences and an "Unanswered" pseudo-bucket; a stored value outside that set is counted as
  answered but lands in no bucket, so the breakdown sums to less than the on-site total whenever
  one exists. `DietaryPreference` is free text on write (kept so legacy values stay readable),
  so the fix is a value-model change, not a count fix. Decided on
  nobodies-collective/Humans#1113 together with an "Other" preference and write validation; not
  built.
- **The `VolunteerEventProfile` dietary columns in Shifts are kept only until a prod-verified
  drop.** When they go, every doc that still names them (and this section's freshness
  triggers) is re-checked in the same pass.
- **The legacy food columns on `profiles` are still there.** The answers moved into
  `cantina_dietary_profiles`; the old columns stay, `[Obsolete]` on `ProfileInfo`
  (`HUM_PROFILE_DIETARY`), read only by `DietaryProfileService.LegacyProfile` and the
  `cantina.profile-dietary` copy, and still exported and erased by Users. A retirement PR, after
  production has run the copy, removes the fallback and drops the columns (ledger `CENTRAL-90`).

## 6. Deliberately not done

- **No caching decorator on the roster or on `ICantinaService`.** The page is a low-traffic
  coordinator surface and the numbers must be live; dietary reads are one keyed row or one
  batched query.
- **No `ICantinaWrite`.** Only the section's own form writes dietary answers.
- **No signup gate.** Dietary answers never block a shift signup (nobodies-collective/Humans#2104).
- **No `MIN(DayOffset) GROUP BY UserId` query for the first-confirmed-shift scan.** It would be
  cheaper than the per-day loop, and it was rejected on purpose: it needs a new cross-section
  interface method, and Cantina is not allowed to reach another section's repository. The
  round-trip count is the accepted price of the boundary. Do not "optimize" this without
  Peter: the cost is round-trips, not data volume, at our small scale.
- **No `.Contracts` project.** `ICantinaService` lives in the `Contracts/` folder: Shifts and
  Users reference the section project directly because Cantina reaches back only into their
  `.Contracts` leaves, so no cycle needs breaking. `ICantinaRosterService` stays `internal` in
  `Services/`; the decision follows the consumer list, never the name.
- **No access service.** `CantinaAdminOrAdmin` is a policy; there is no `ICantinaAccessService`.
- **No gap-filling between arrival and departure, and no departure-day feeding.** Only the single
  arrival day is added. Asked and answered when the arrival rule was designed.
- **No profile piece (`IUserPart`).** The profile page links to the dietary form; Cantina
  contributes no card.
- **No per-meal granularity, no PDF, no live push, no multi-event scope, no editing from the
  roster, no notify-the-unanswered action, no admin edit of another member's answers.**

## Load-bearing weirdness

- **`GetOnSiteUserIdsForDayAsync` is confirmed-only, and Cantina is its only production caller.**
  The Shifts method was narrowed from Pending-or-Confirmed *for* the cantina. A second caller
  expecting pending signups back would be silently wrong: that is a landmine, not a preference.
- **The first-confirmed-shift scan is deliberately separate from the visible-week load.** They
  look duplicative and are not: the week load must stay decoupled from the event bounds, while
  the arrival scan must start at build start to find a *true* first shift. Merging them naively
  changes which humans get an arrival day.
- **`ArrivesOn` is the earliest on-site day *within the requested week***, which equals the true
  arrival day only for humans who arrive that week. For a multi-week attendee it is simply their
  first day in the window. The name overpromises; the CSV spec is the accurate description.
- **`CantinaResource.cs` must stay in `namespace Humans.Cantina`.** The SDK derives the resx
  manifest name from the adjacent same-named `.cs` file's namespace. A tidier
  `Humans.Cantina.Resources` makes every Cantina string render as its raw key at runtime.
- **`Views/_ViewImports.cshtml` is not inherited from the Shell.** A missing `@using` there ships
  broken markup with a green build and no runtime error.
- **The weekly page is overview-only.** Per-person detail lives on the day drill-down, so the
  weekly multi-key sort shapes only the CSV; the HTML action does not run it.
- **The burner name comes off `UserInfo`, never off `ProfileInfo`.** `UserInfo.BurnerName` is the
  resolved value (`User.BurnerName` → `Profile.BurnerName` → legacy display name,
  nobodies-collective/Humans#1097); `ProfileInfo.BurnerName` renders `"(unknown)"` for a human
  whose name only ever landed on the user row. This section is a pure read-side consumer of
  Users' read-model, so a change on that side lands here silently. Pinned by
  `GetDailyRoster_BurnerName_ComesFromTheResolvedUserName`.
- **The "Other" text rule is checked twice on save.** The controller checks first so the error
  lands on the field, in the member's language; the service refuses the same case for any other
  caller. Collapsing them needs a field-aware return, which is new surface.
- **The dietary option values are the stored values.** `DietaryOptions` strings are written to
  the table and read back as resource-key suffixes in `Dietary.cshtml`; renaming one orphans every
  stored answer and its translation.

## History

| Date | Run | Headline |
|---|---|---|
| 2026-08-22 | [run](../../../../docs/health/runs/2026-08-22-Cantina.md) | First doctor pass — docs described the inverse of the shipped UI; 72 dead resx entries removed; two behavior defects found and queued. PR [#1453](https://github.com/peterdrier/Humans/pull/1453) |
| 2026-09-12 | [run](../../../../docs/health/runs/2026-09-12-Cantina.md) | Re-doctor. Cantina's own code had not changed; every finding was drift from Users. Coordinators were seeing `"(unknown)"` instead of names (nobodies-collective/Humans#1097) — fixed and pinned. A false GDPR retention claim (closed by nobodies-collective/Humans#1446) removed. Dead sort on the weekly page cut; comment truth across nine files. PR [#1661](https://github.com/peterdrier/Humans/pull/1661) |
| 2026-10-24 | [run](../../../../docs/health/runs/2026-10-24-Cantina.md) | Re-doctor after the dietary store moved in. Docs still described the retired signup gate; the roster's empty-week path and its answered rule each existed twice. PR pending |

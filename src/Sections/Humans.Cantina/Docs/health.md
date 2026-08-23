<!-- freshness:triggers
  src/Sections/Humans.Cantina/**
  tests/Humans.Cantina.Tests/**
  src/Sections/Humans.Users/Domain/Profile.cs
  src/Sections/Humans.Users.Contracts/UserInfo.cs
  src/Sections/Humans.Users.Contracts/DietaryOptions.cs
  src/Sections/Humans.Shifts/Services/ShiftManagementService.cs
  src/Sections/Humans.Shifts/Data/ShiftRepository.Signups.cs
  src/Sections/Humans.Shifts/Domain/VolunteerEventProfile.cs
-->

# Cantina — Health

Last assessed: 2026-08-22 (section-doctor).

## 1. What the section does

Tells the people who cook how many mouths are on site, and what those mouths can eat.

A coordinator opens a week and sees, for each of its seven days, how many humans are around
and how many of them never said what they eat. They can step back to last week or forward to
next week, click any day to get the full person-by-person picture for that day's meal, and
download either view as a spreadsheet to take to the shops or the kitchen.

Nobody's medical history appears anywhere in this, on purpose, for anybody.

## 2. The shapes

Everything the section exposes answers one of four questions.

| Shape | The question | Surfaces |
|---|---|---|
| **Week overview** | How many are here each day this week, and how many haven't answered? | `GET /Cantina/Roster` |
| **Day detail** | Exactly who is here on this one day, and what does each of them eat? | `GET /Cantina/Roster/Day` |
| **Take it away** | The same two answers, as a file. | `GET /Cantina/Roster/Csv`, `GET /Cantina/Roster/Day/Csv` |

Three shapes, and "which week did you mean?" is not one of them — resolving a missing offset to
"now" is the service's own default, not a question a coordinator asks.

## 3. Structure

The section is a read-side aggregator with no storage of its own. Written fresh, it is:

- **One service** answering the two real questions, each taking an optional offset and
  resolving "now" itself when the offset is absent. It owns the event lookup and the clock
  because resolving "now" is its job, not its caller's.
- **One controller** that parses a nullable `int` off the query string, calls one service
  method, and picks a view or a CSV writer. No date arithmetic, no event lookup, no clock.
- **Presentation-only helpers** — one sort-for-display assembler and two CSV writers — that
  take a finished payload and arrange it.
- **Two payload records**, one per real question, plus the small row/rollup records they carry.

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
- **The section never writes.** No tables, no audit rows, no notifications, no POST routes.
- **Live on every request.** No cached aggregate; the only cache it benefits from is the Users
  section's, behind `IUserServiceRead`.
- **Access is `Admin` or `CantinaAdmin`, by policy.** An authenticated human without either is
  redirected to `/Account/AccessDenied` — not a bare 403. Team membership grants nothing.

## 5. Seams

- **Unknown dietary values have no defined home.** The breakdown buckets the four canonical
  preferences and an "Unanswered" pseudo-bucket; a stored value outside that set is currently
  counted as neither. The intended rule — treat unknown as Unanswered — is stated in a code
  comment but not implemented. `DietaryPreference` is an unvalidated `string?` and the write
  path does not filter it, so the fix is a value-model change, not a count fix. Tracked in
  **nobodies-collective/Humans#1113** along with the missing "Other" preference and write
  validation; deliberately not patched here.
- **`VolunteerEventProfile.DietaryPreference` / `.Allergies` / `.Intolerances` are marked
  "RETAINED for prod-soak drop"** in Shifts. When they go, every doc that still names them as
  Cantina's source (and this section's freshness triggers) should be re-checked in the same pass.
- **The section is expected to stop being table-less.** **nobodies-collective/Humans#1113** moves
  the food preferences into Cantina end-to-end — its own table, `DbContext`, repository and
  cache — with Users and Shifts no longer reading food data, and `MedicalConditions` staying in
  Users as Art. 9 data. It also carries the GDPR erasure gap. Everything below about owning no
  tables describes today, not the destination: **do not defend it against #1113.**
  **nobodies-collective/Humans#1112** (`IUserPart` fanout) is the seam that makes it possible.

## 6. Deliberately not done

- **No caching decorator on the roster.** The page is a low-traffic coordinator surface and the
  numbers must be live; the expensive part (user reads) already rides the Users cache.
- **No repository, no `DbContext`, no EF reference.** The section owns no tables *today*, and
  the architecture test `SectionAssemblyDoesNotReferenceEntityFrameworkCore` pins that. This is
  a description of the current shape, not a principle to defend —
  nobodies-collective/Humans#1113 deliberately reverses it, and that test changes with it.
- **No `MIN(DayOffset) GROUP BY UserId` query for the first-confirmed-shift scan.** It would be
  cheaper than the per-day loop, and it was rejected on purpose: it needs a new repository and a
  new cross-section interface method, and Cantina is not allowed to reach a repository. The
  round-trip count is the accepted price of the boundary. Do not "optimize" this without
  Peter — the cost is round-trips, not data volume, at ~500 humans.
- **No `Contracts` project or promoted interface.** Nothing outside the section names a Cantina
  type. `ICantinaRosterService` stays `internal` in `Services/`; the decision follows the
  consumer list, never the name.
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
  multi-key sort therefore now shapes only the CSV, not any on-screen table.

## History

| Date | Run | Headline |
|---|---|---|
| 2026-08-22 | [run](../../../../docs/health/runs/2026-08-22-Cantina.md) | First doctor pass — docs described the inverse of the shipped UI; 72 dead resx entries removed; two behavior defects found and queued. PR [#1453](https://github.com/peterdrier/Humans/pull/1453) |

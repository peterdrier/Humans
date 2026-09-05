<!-- freshness:triggers
  src/Sections/Humans.EarlyEntry/**
  tests/Humans.EarlyEntry.Tests/**
  src/Sections/Humans.Camps/Section.cs
  src/Sections/Humans.Camps/Services/CachingCampService.cs
  src/Sections/Humans.Camps/Services/CampService.cs
  src/Sections/Humans.Shifts/Section.cs
  src/Sections/Humans.Shifts/SectionPolicies.cs
  src/Sections/Humans.Shifts/Services/VolunteerTrackingExportService.cs
  src/Sections/Humans.Shifts/Services/ShiftEarlyEntryProjection.cs
  src/Sections/Humans.Shifts/Services/ShiftSignupService.cs
  src/Sections/Humans.Shifts/Services/ShiftManagementService.cs
  src/Sections/Humans.Teams/Section.cs
  src/Sections/Humans.Teams/Services/TeamService.cs
  src/Sections/Humans.Teams/Services/TeamEarlyEntryProjection.cs
  src/Sections/Humans.Gate/Services/GateService.cs
  src/Sections/Humans.Scanner/Controllers/ScannerController.cs
  src/Sections/Humans.Tickets/Controllers/TicketTransferController.cs
  src/Sections/Humans.Tickets/ViewComponents/MyTicketStubsViewComponent.cs
  src/Sections/Humans.Tickets/ViewComponents/TicketHoldingsViewComponent.cs
  tests/Humans.Integration.Tests/Controllers/EarlyEntryPageRenderTests.cs
-->

# EarlyEntry — Health

Last assessed: 2026-09-05 (section-doctor).

## 1. What the section does

Answers one question about the days before the gates open: **who may come onto site early,
from which day, and because of what.**

It does not decide any of that itself. Three other parts of the app hand out early entry for
their own reasons — a camp lead grants it to a camp member, a confirmed build shift earns it
for the volunteer, a team coordinator grants it for a project — and this section is the one
place those answers are added up. A person with early entry from two places gets the earliest
of their dates and both reasons listed; a person with none gets nothing.

Two audiences read the sum. The person themself sees their own date beside their ticket, and
gate staff see the scanned attendee's date on the gate card. A volunteer coordinator sees the
whole list at once, with the people who hold early entry from more than one place marked, so a
redundant slot can be given to someone else.

## 2. The shapes

| Shape | The question | Surfaces |
|---|---|---|
| **Everyone's early entry** | Who holds early entry, from when, and why — all of them, live? | `GET /Shifts/Admin/EarlyEntry`; `IEarlyEntryService.GetRosterAsync` |
| **One person's early entry** | Does this person hold early entry, from when, and why? | `IEarlyEntryService.GetForUserAsync` (Gate card, Scanner card, the three ticket-stub surfaces) |
| **Here is what I grant** | A contributing section's grants for the active event. | `IEarlyEntryProvider.GetEarlyEntriesAsync` (Camps, Shifts, Teams) |
| **Someone's grant changed** | Forget what you remembered about this person / about everyone. | `IEarlyEntryInvalidator.InvalidateUser` / `InvalidateAll` (called by Camps, Shifts, Teams) |

The first two are the same collapse — earliest date, distinct reasons — applied to all
people or to one. The last two are inbound: the section owns the contract, other sections
call it.

## 3. Structure

A read-side aggregator with no storage of its own. Written fresh:

- **Three contracts, one folder.** The read service, the provider, and the invalidator, in
  `Contracts/`, with the three small records they carry (a grant; a roster row; one person's
  entry). Contributors and readers reference the section project and see nothing else, because
  everything outside `Contracts/` is internal.
- **One orchestrator** holding the whole business rule: fan out over every registered
  provider, collapse per person. Both read methods are the same collapse over a different
  subset. It injects the providers and nothing else.
- **One caching decorator**, Singleton, over the orchestrator, remembering the per-person
  answer — including "none" — and forgetting it when a contributor says so. The roster is
  never remembered. The decorator resolves the scoped orchestrator per call through a keyed
  registration.
- **One controller and one admin view**: sort the roster, stitch the legal name from Users,
  render a table. No business rule.

The layout matches this. What differs from the fresh form is small: `HasMultiple` travels as
a field when it is `Sources.Count > 1`.

## 4. Invariants

- The section owns no tables and injects no repository (pinned:
  `EarlyEntryArchitectureTests.OrchestratorInjectsOnlyTheProviderFanout`).
- Fan-out is sequential. Contributors read through their own section's context, so this is a
  simplicity choice, not a thread-safety requirement (design-rules §8b).
- Per person: **earliest date wins**; reasons are **distinct, ordinal-compared**, in provider
  order; **more than one reason** is what "multiple" means.
- `GetRosterAsync` is live on every call. `GetForUserAsync` is cached per person, negative
  answers included, and only eviction refreshes it — the cache has no warmup and no expiry.
- A person sees only their own early entry on every holder-facing surface. Gate and Scanner
  staff see the scanned attendee's. The roster needs `ShiftDashboardAccess`
  (Admin, NoInfoAdmin, VolunteerCoordinator); anyone else is redirected to
  `/Account/AccessDenied`.
- The section never writes anything: no tables, no audit rows, no notifications, no POST.
- No localized copy: the roster is an admin page with inline English
  ([`localization-admin-exempt`](../../../../memory/code/localization-admin-exempt.md)).

## 5. Seams

- **Eviction on a global Shifts setting change.** The section's contract says a change to the
  gate-opening date or build-start offset moves every shift-derived date and should evict
  everyone; no Shifts write path calls `InvalidateAll` and none ever has. Until that is
  decided (this run's Needs-Peter), a shift-derived early-entry date can stay stale until the
  person's own next signup change or a restart.
- **`settings_event` cutover** (nobodies-collective/Humans#1104): the gate date, build offset
  and early-entry window Shifts' provider reads today will move to Settings. When they do, the
  "global change evicts everyone" trigger moves with them.
- **`IEarlyEntryInvalidator` is a grandfathered HUM0028 invalidator**
  (nobodies-collective/Humans#805): contributors flush this section's cache. Peter's ruling
  (2026-06-13, `debt-ledger.yml`) is to leave it; the decorator cannot own invalidation
  end-to-end because it never sees the contributors' writes.

## 6. Deliberately not done

- **No `Humans.EarlyEntry.Contracts` project.** The `Contracts/` folder is the public surface
  and the six referencing sections see only it. A leaf project would matter only if this
  section had to reference a contributor — it references Users.Contracts alone, so there is
  no cycle to break.
- **No per-user provider method.** `GetForUserAsync` gathers every contributor's full list to
  answer for one person. The dataset is a few hundred grants; the per-person cache is what
  makes the holder surfaces cheap, not a narrower query.
- **No parallel fan-out.** Sequential is the house shape for contributor orchestrators
  (Gdpr, Calendar); nothing here is slow enough to justify a second shape.
- **No batch legal-name read on the roster.** One `IUserServiceRead` lookup per row through
  `HumansControllerBase`, served from the Users cache; tens of rows, not thousands.
- **No warmup, no expiry, no size bound on the per-person cache.** Eviction is the contract;
  the key space is the user table.
- **No repository, no DbContext, no EF reference** — description of today's shape, not a
  pinned absence ([`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md)).

## Load-bearing weirdness

- **The route is `/Shifts/Admin/EarlyEntry` and the nav entry sits in the "Tickets" admin
  group.** Both predate the section; the prefix is where coordinators look for it, not an
  ownership claim. Moving either is a nav change, not a cleanup.
- **Camps forwards its caching decorator as the provider; Shifts and Teams forward scoped
  services.** Which instance to forward follows where the read is served from
  (design-rules §8b): Camps projects from its cached snapshot, the other two read the
  repository per call. Registering a decorator that does not serve the read adds a hop and
  no cache.
- **Negative results are cached by hand** (`TryGet` / `Set`), because `TrackedCache.GetAsync`
  never stores a null. "This person has no early entry" is the common answer and must be
  remembered (design-rules §15).
- **The Singleton decorator resolves the Scoped orchestrator per call via a keyed
  registration** (`CachingEarlyEntryService.InnerServiceKey`). Unkeyed, the decorator would
  resolve itself.
- **Shifts derives one grant per person from their earliest confirmed build shift**, entry date
  = that shift's local day minus one, so a shift-derived date is never later than the day
  before the person's first shift. Camps grants a single global `EeStartDate` per member;
  Teams grants a per-grant date.
- **`Views/_ViewImports.cshtml` is not inherited from the Shell.** A missing `@using` there
  ships broken markup with a green build.

## History

| Date | Run | Headline |
|---|---|---|
| 2026-09-05 | [run](../../../../docs/health/runs/2026-09-05-EarlyEntry.md) | First doctor pass. peterdrier/Humans#1593 |

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
  src/Sections/Humans.Settings/Contracts/IEventSettingsChangeListener.cs
  src/Sections/Humans.Settings/Services/Service.cs
  src/Sections/Humans.Teams/Section.cs
  src/Sections/Humans.Teams/Services/TeamService.cs
  src/Sections/Humans.Teams/Services/TeamEarlyEntryProjection.cs
  src/Sections/Humans.Gate/Services/GateService.cs
  src/Sections/Humans.Scanner/Controllers/ScannerController.cs
  src/Sections/Humans.Tickets.Contracts/TicketStubInfo.cs
  src/Sections/Humans.Tickets/Controllers/TicketTransferController.cs
  src/Sections/Humans.Tickets/ViewComponents/MyTicketStubsViewComponent.cs
  src/Sections/Humans.Tickets/ViewComponents/TicketHoldingsViewComponent.cs
  tests/Humans.Integration.Tests/Controllers/EarlyEntryPageRenderTests.cs
-->

# EarlyEntry — Health

## 1. What the section does

Answers one question about the days before the gates open: **who may come onto site early,
from which day, and because of what.**

It does not decide any of that itself. Other parts of the app hand out early entry for
their own reasons — a camp lead grants it to a camp member, a confirmed build shift earns it
for the volunteer, a team coordinator grants it for a project — and this section is the one
place those answers are added up. A person with early entry from two places gets the earliest
of their dates and both reasons listed; a person with none gets nothing.

The person themself sees their own date beside their ticket, and
gate staff see the scanned attendee's date on the gate card. A volunteer coordinator sees the
whole list at once, with the people who hold early entry from more than one place marked, so a
redundant slot can be given to someone else.

## 2. The shapes

| Shape | The question | Surfaces |
|---|---|---|
| **Everyone's early entry** | Who holds early entry, from when, and why — all of them, live? | `GET /Shifts/Admin/EarlyEntry`; `IEarlyEntryService.GetRosterAsync` |
| **One person's early entry** | Does this person hold early entry, from when, and why? | `IEarlyEntryService.GetForUserAsync` (Gate card, Scanner card, the ticket-stub surfaces) |
| **Here is what I grant** | A contributing section's grants for the active event. | `IEarlyEntryProvider.GetEarlyEntriesAsync` (Camps, Shifts, Teams) |
| **Someone's grant changed** | Forget what you remembered about this person / about everyone. | `IEarlyEntryInvalidator.InvalidateUser` / `InvalidateAll` (called by Camps, Shifts, Teams) |
| **The event's dates moved** | An event-settings save shifted every derived date. | `IEventSettingsChangeListener.EventSettingsChanged` (Settings' fan-out; this section listens) |

The first two are the same collapse — earliest date, distinct reasons — applied to all
people or to one. The last three are inbound: other sections supply grants or say when to
forget.

## 3. Structure

A read-side aggregator with no storage of its own. Written fresh:

- **The contracts in one folder.** The read service, the provider, and the invalidator, in
  `Contracts/`, with the small records they carry (a grant; a roster row; one person's
  entry). Contributors and readers reference the section project and see nothing else, because
  everything outside `Contracts/` is internal.
- **An orchestrator** holding the whole business rule: fan out over every registered
  provider, collapse per person. Both read methods are the same collapse over a different
  subset. It injects the providers and nothing else.
- **A caching decorator**, Singleton, over the orchestrator, remembering the per-person
  answer — including "none" — and forgetting it when a contributor says so or when Settings
  reports an event-settings save. The roster is never remembered. The decorator resolves the
  scoped orchestrator per call through a keyed registration.
- **A controller and an admin view**: sort the roster, stitch the legal name from Users,
  render a table. No business rule.

The layout matches this. What differs from the fresh form: one person's entry is its own
record (`UserEarlyEntry`) rather than the roster row's shape, and `HasMultiple` travels as a
field when it is `Sources.Count > 1`.

## 4. Invariants

- The orchestrator's only dependency is the provider fan-out — no repository, no tables
  (`src/Sections/Humans.EarlyEntry/Services/EarlyEntryService.cs:10`; pinned:
  `EarlyEntryArchitectureTests.OrchestratorInjectsOnlyTheProviderFanout`).
- Per person: **earliest date wins**; reasons are **distinct, ordinal-compared**, in provider
  order (`src/Sections/Humans.EarlyEntry/Services/EarlyEntryService.cs:33`); **more than one
  reason** is what "multiple" means (`src/Sections/Humans.EarlyEntry/Services/EarlyEntryService.cs:20`).
- `GetRosterAsync` is live on every call
  (`src/Sections/Humans.EarlyEntry/Services/CachingEarlyEntryService.cs:50`). `GetForUserAsync`
  is cached per person, negative answers included
  (`src/Sections/Humans.EarlyEntry/Services/CachingEarlyEntryService.cs:30`), and only eviction
  refreshes it; a load begun before an eviction never writes its answer back
  (`src/Sections/Humans.EarlyEntry/Services/CachingEarlyEntryService.cs:40`).
- Every event-settings save evicts every cached answer
  (`src/Sections/Humans.EarlyEntry/Services/CachingEarlyEntryService.cs:76`).
- A holder sees only their own early entry: the ticket-stub surfaces ask for the signed-in
  user (`src/Sections/Humans.Tickets/Controllers/TicketTransferController.cs:30`); the gate card
  asks for the scanned attendee (`src/Sections/Humans.Gate/Services/GateService.cs:74`).
- The roster needs `ShiftDashboardAccess` (Admin, NoInfoAdmin, VolunteerCoordinator)
  (`src/Sections/Humans.EarlyEntry/Controllers/EarlyEntryRosterController.cs:12`; pinned:
  `EarlyEntryArchitectureTests.RosterRequiresShiftDashboardAccess`).
- The section exposes no write: its one action is a GET
  (`src/Sections/Humans.EarlyEntry/Controllers/EarlyEntryRosterController.cs:17`).

## 5. Seams

- **No `Humans.EarlyEntry.Contracts` leaf project** — open debt, not a settled shape
  (`debt-ledger.yml`, `review: panel`). Camps, Shifts, Teams, Gate, Scanner and Tickets each
  take a `ProjectReference` on the whole section, so what stops them reaching past the
  contracts today is that everything outside `Contracts/` is `internal` — accessibility, not a
  project boundary. Treat the current shape as debt, never as precedent.
- **`IEarlyEntryInvalidator` is a grandfathered HUM0028 invalidator**
  (nobodies-collective/Humans#805): contributors flush this section's cache, so a contributor
  write path that forgets to call it leaves a stale answer. Peter's ruling (`debt-ledger.yml`)
  is to leave it until the invalidator family is replaced.
- **`UserEarlyEntry` folds into the roster-row shape** — ruled, not yet built (`Docs/debt.yml`).

## 6. Deliberately not done

- **No per-user provider method.** `GetForUserAsync` gathers every contributor's full list to
  answer for one person. The dataset is a few hundred grants; the per-person cache is what
  makes the holder surfaces cheap, not a narrower query.
- **No parallel fan-out.** Sequential is the house shape for contributor orchestrators
  (Gdpr, Calendar) and a simplicity choice, not a thread-safety requirement (design-rules §8b);
  nothing here is slow enough to justify a second shape.
- **No batch legal-name read on the roster.** One `IUserServiceRead` lookup per row through
  `HumansControllerBase`, served from the Users cache; tens of rows, not thousands.
- **No warmup, no expiry, no size bound on the per-person cache.** Eviction is the contract;
  the key space is the user table.
- **No localized copy.** The roster is an admin page with inline English
  ([`localization-admin-exempt`](../../../../memory/code/localization-admin-exempt.md)).

## Load-bearing weirdness

- **The route is `/Shifts/Admin/EarlyEntry`.** It predates the section; the prefix is where
  coordinators look for it, not an ownership claim. Moving it is a nav change, not a cleanup.
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
- **The decorator references the Settings section** to implement its
  `IEventSettingsChangeListener`. Settings fans its saves out over that listener and names no
  consumer, so the gate date, the build offset and `EarlyEntryStartOffset` evict this cache
  without Settings knowing early entry exists.
- **Shifts derives one grant per person from their earliest confirmed build shift**, entry date
  = that shift's local day minus one, so a shift-derived date is never later than the day
  before the person's first shift. Camps grants a single global date, resolved from
  `EventSettings.EarlyEntryStartOffset` (nobodies-collective/Humans#1633), per member; Teams
  grants a per-grant date.
- **`Views/_ViewImports.cshtml` is not inherited from the Shell.** A missing `@using` there
  ships broken markup with a green build.

## History

| Date | Run | Headline |
|---|---|---|
| 2026-09-05 | [run](../../../../docs/health/runs/2026-09-05-EarlyEntry.md) | First doctor pass. peterdrier/Humans#1593 |
| 2026-10-06 | [run](../../../../docs/health/runs/2026-10-06-EarlyEntry.md) | Docs describe the settings-listener eviction and the section's own nav group. pending |

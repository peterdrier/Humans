<!-- freshness:triggers
  src/Sections/Humans.EarlyEntry/**
  tests/Humans.EarlyEntry.Tests/**
  src/Sections/Humans.Camps/Section.cs
  src/Sections/Humans.Camps/Services/CachingCampService.cs
  src/Sections/Humans.Camps/Services/CampService.cs
  src/Sections/Humans.Shifts/Section.cs
  src/Sections/Humans.Shifts/Section.cs
  src/Sections/Humans.Shifts/Services/VolunteerTrackingExportService.cs
  src/Sections/Humans.Shifts/Services/ShiftEarlyEntryProjection.cs
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

The first two are the same collapse — earliest date, distinct reasons — applied to all
people or to one, over the same once-per-request gather. The third is inbound: other sections
supply grants.

## 3. Structure

A read-side aggregator with no storage of its own. Written fresh:

- **The contracts in one folder.** The read service and the provider, in `Contracts/`, with
  the two records they carry: a grant, and a roster row that answers both reads. Everything
  outside `Contracts/` is internal.
- **An orchestrator** holding the whole business rule: gather every registered provider's
  grants once per request, collapse per person. Both read methods are the same collapse over a
  different subset. It injects the providers and nothing else.
- **A controller and an admin view**: sort the roster, stitch the legal name from Users,
  render a table. No business rule.

The layout matches this.

## 4. Invariants

- The orchestrator's only dependency is the provider fan-out — no repository, no tables
  (`src/Sections/Humans.EarlyEntry/Services/EarlyEntryService.cs:6`; pinned:
  `EarlyEntryArchitectureTests.OrchestratorInjectsOnlyTheProviderFanout`).
- Per person: **earliest date wins**
  (`src/Sections/Humans.EarlyEntry/Services/EarlyEntryService.cs:28`); reasons are **distinct,
  ordinal-compared**, in provider order
  (`src/Sections/Humans.EarlyEntry/Services/EarlyEntryService.cs:27`; pinned:
  `EarlyEntryServiceTests.Source_labels_differing_only_in_case_are_two_sources`); **more than
  one reason** is what "multiple" means
  (`src/Sections/Humans.EarlyEntry/Services/EarlyEntryService.cs:28`).
- **Nothing outlives the request.** The providers are read at most once per request scope
  (`src/Sections/Humans.EarlyEntry/Services/EarlyEntryService.cs:35`) and the orchestrator is
  scoped (`src/Sections/Humans.EarlyEntry/Section.cs:14`), so a write in any contributing
  section shows on the next request and never has to signal this one (pinned:
  `EarlyEntryArchitectureTests.SectionRegistersTheOrchestratorPerRequest`,
  `EarlyEntryServiceTests.GetForUserAsync_GathersOncePerScope_AndFreshInTheNext`).
- The transfer wizard asks for the signed-in user's own early entry
  (`src/Sections/Humans.Tickets/Controllers/TicketTransferController.cs:28`, reaching
  `src/Sections/Humans.Tickets/Controllers/TicketTransferController.cs:79`); the gate card asks
  for the scanned attendee's (`src/Sections/Humans.Gate/Services/GateService.cs:73`). The stub
  and holdings view components answer for the user they are invoked with, so who sees that
  answer is the invoking page's gate, not this section's.
- The roster needs `ShiftsPolicies.Manage` (Admin, NoInfoAdmin, VolunteerCoordinator)
  (`src/Sections/Humans.EarlyEntry/Controllers/EarlyEntryRosterController.cs:12`; pinned:
  `EarlyEntryArchitectureTests.RosterRequiresShiftDashboardAccess`).
- The section exposes no write: the roster controller serves GET only
  (`src/Sections/Humans.EarlyEntry/Controllers/EarlyEntryRosterController.cs:17`).

## 5. Seams

None.

## 6. Deliberately not done

- **No `Humans.EarlyEntry.Contracts` leaf project.** A section publishes from its `Contracts/`
  folder and takes a leaf only where the folder would close a project cycle (design-rules §1);
  nothing here does, so consumers reference the section and `internal` keeps them off the rest.
- **No per-user provider method.** `GetForUserAsync` gathers every contributor's full list to
  answer for one person. The dataset is a few hundred grants; one full pass is cheap.
- **No parallel fan-out.** Sequential is the house shape for contributor orchestrators
  (Gdpr, Calendar) and a simplicity choice, not a thread-safety requirement (design-rules §8b);
  nothing here is slow enough to justify a second shape.
- **No batch legal-name read on the roster.** One `IUserService` lookup per row through
  `HumansControllerBase`, served from the Users cache; tens of rows, not thousands.
- **No cache beyond the request.** A cross-request cache stays true only if every contributing
  section evicts it on its writes; one provider pass per request (a few small queries) costs less than that coupling at this scale.
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
| 2026-10-06 | [run](../../../../docs/health/runs/2026-10-06-EarlyEntry.md) | Docs describe the settings-listener eviction and the section's own nav group. peterdrier/Humans#1915 |
| 2026-10-25 | [run](../../../../docs/health/runs/2026-10-25-EarlyEntry.md) | Docs describe the once-per-request gather; the Contracts folder is no longer called debt. peterdrier/Humans#2158 |

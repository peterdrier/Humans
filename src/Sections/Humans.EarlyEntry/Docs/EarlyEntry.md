<!-- freshness:triggers
  src/Sections/Humans.EarlyEntry/**
  src/Sections/Humans.Camps/Section.cs
  src/Sections/Humans.Camps/Services/CachingCampService.cs
  src/Sections/Humans.Camps/Services/CampService.cs
  src/Sections/Humans.Shifts/Section.cs
  src/Sections/Humans.Shifts/SectionPolicies.cs
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
-->
<!-- freshness:flag-on-change
  Re-read the provider fan-out contract: EE is derived data with no table of its own, so
  every invariant here is about who contributes grants.
-->

# Early Entry — Section Invariants

Cross-source Early Entry (EE): who may enter the site before gates open, on which day, and
because of what. The section owns no data — it fans out over every contributing section.

## Concepts

- An **EE grant** (`EarlyEntryGrant`) is one source's claim that a human may enter on a given
  `LocalDate`, with a display label `Source` (`"Camp: Flaming Lotus"`, `"Shift: Flags"`,
  `"{TeamName}: {ProjectName}"`) rendered verbatim. A projection, never a row this section stores.
- An **EE provider** (`IEarlyEntryProvider`) is a section that contributes grants; one with
  nothing to contribute returns an empty list.
- A **roster row** (`EarlyEntryRosterRow`) is one human's grants collapsed: earliest date, the
  distinct source labels, and `HasMultiple` when more than one source grants them EE — the flag
  the roster uses to surface reallocatable slots.
- **User EE** uses the same `EarlyEntryRosterRow` as the roster, including `UserId` and `HasMultiple`, or `null` when the human holds none.

## Data Model

None — no tables, no `DbContext`. Every grant is derived at read time from the contributing
sections' own data.

## Routing

`/Shifts/Admin/EarlyEntry` — the cross-source roster. The URL predates the section and is kept
verbatim; the nav entry is the section's own "Early Entry" group. The route is no ownership claim.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | Sees their own EE date on ticket-stub surfaces (homepage strip, holdings, transfer wizard). The stub and holdings view components answer for the user they are invoked with; an admin profile view shows the profiled user's. |
| Gate / Scanner staff | Sees the *scanned attendee's* EE on the gate card (`ScannerController`, `GateService`). |
| `ShiftDashboardAccess` (Admin, NoInfoAdmin, VolunteerCoordinator) | Reads the full cross-source roster at `/Shifts/Admin/EarlyEntry`. |

## Invariants

- The section owns no tables and injects no repository; the orchestrator's only dependency is
  the provider fan-out (`EarlyEntryArchitectureTests.OrchestratorInjectsOnlyTheProviderFanout`).
- The fan-out is sequential — a simplicity choice, not a thread-safety requirement
  (design-rules §8b); each provider reads through its own section.
- **Nothing outlives the request.** The scoped service fans out over the providers once per
  request scope (the home page asks twice), so a grant written in any contributing section shows
  on the next request and no section has to tell this one about its writes
  (`EarlyEntryServiceTests.GetForUserAsync_GathersOncePerScope_AndFreshInTheNext`,
  `EarlyEntryArchitectureTests.SectionRegistersTheOrchestratorPerRequest`).
- Per human: earliest date wins, sources are distinct and ordinal-compared in provider order,
  and `HasMultiple` is `Sources.Count > 1`.
- The roster is gated by `ShiftDashboardAccess`
  (`EarlyEntryArchitectureTests.RosterRequiresShiftDashboardAccess`).
- No `Resources/` folder: the roster is an admin page with inline English
  ([`localization-admin-exempt`](../../../../memory/code/localization-admin-exempt.md)).

## Negative Access Rules

- The transfer wizard asks only for the signed-in user's own EE. The stub and holdings view
  components answer for the user they are invoked with, so who may see another member's EE
  there is the invoking page's gate, not this section's. The gate card is the deliberate
  staff-facing exception.
- A human without `ShiftDashboardAccess` **cannot** reach `/Shifts/Admin/EarlyEntry`.
- The section exposes no write path, so no write **cannot**-clause applies.

## Triggers

None. Every read derives grants live from the providers, so a camp EE flag, a build-shift
signup, a team grant, a team flag or rename, a shift edit or an event-settings save shows on
the next read with no signal to this section.

## Cross-Section Dependencies

Outbound (the `.csproj` references `Humans.Base` and `Humans.Users.Contracts` only):

- **Users** — `IUserService` through `HumansControllerBase.FindUserInfoByIdAsync`, for the
  roster's legal-name column.

Inbound, all through `Contracts/`:

- **Camps** — `CachingCampService` implements `IEarlyEntryProvider`.
- **Shifts** — `VolunteerTrackingExportService` implements `IEarlyEntryProvider`.
- **Teams** — `TeamService` implements `IEarlyEntryProvider`.
- **Gate** — `GateService` calls `IEarlyEntryService.GetForUserAsync` for the scanned attendee.
- **Scanner** — `ScannerController` calls `GetForUserAsync` for the scanned attendee.
- **Tickets** — `TicketTransferController`, `MyTicketStubsViewComponent` and
  `TicketHoldingsViewComponent` call `GetForUserAsync` for the viewer.

## Architecture

**Owning services:** `EarlyEntryService` (orchestrator, Scoped, no decorator)
**Owned tables:** None — orchestrator over every registered `IEarlyEntryProvider`.
**Status:** (A) Migrated.

### Cross-section read interface

The whole outward surface is read-only, so there is no read/write split to make. The
contracts live in `Contracts/`, a folder rather than a leaf project — known debt with a
queued carve-out decision (`debt-ledger.yml`), not a settled shape: every
consumer references the whole section, and only `internal` keeps them off the internals.

| Read interface | Methods | Notes |
|---|---:|---|
| `IEarlyEntryService` | 2 | `GetRosterAsync`, `GetForUserAsync` — both live |

### For (A) Migrated sections

- **No repository, no `DbContext`.**
- **Decorator decision** — Option A, no decorator. A per-human cache here could only be kept
  true by every contributing section evicting it on its writes; a read costs one provider
  pass (a few small queries), which this scale affords on every ticket page and gate scan.
- **Cross-section calls** — `IUserService`.
- **Architecture test** — `tests/Humans.EarlyEntry.Tests/EarlyEntryArchitectureTests.cs`; the
  rendered page is pinned by `tests/Humans.Integration.Tests/Controllers/EarlyEntryPageRenderTests.cs`
  (local-only, never runs in CI).

<!-- freshness:triggers
  src/Sections/Humans.EarlyEntry/**
  src/Sections/Humans.Camps/Section.cs
  src/Sections/Humans.Camps/Services/CachingCampService.cs
  src/Sections/Humans.Camps/Services/CampService.cs
  src/Sections/Humans.Shifts/Section.cs
  src/Sections/Humans.Shifts/SectionPolicies.cs
  src/Sections/Humans.Shifts/Services/VolunteerTrackingExportService.cs
  src/Sections/Humans.Shifts/Services/ShiftSignupService.cs
  src/Sections/Humans.Shifts/Services/ShiftManagementService.cs
  src/Sections/Humans.Teams/Section.cs
  src/Sections/Humans.Teams/Services/TeamService.cs
  src/Sections/Humans.Gate/Services/GateService.cs
  src/Sections/Humans.Scanner/Controllers/ScannerController.cs
  src/Sections/Humans.Tickets/Controllers/TicketTransferController.cs
  src/Sections/Humans.Tickets/ViewComponents/MyTicketStubsViewComponent.cs
  src/Sections/Humans.Tickets/ViewComponents/TicketHoldingsViewComponent.cs
-->
<!-- freshness:flag-on-change
  Re-read the provider fan-out contract and the eviction rules: EE is derived data with no
  table of its own, so every invariant here is about who contributes grants and who evicts.
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
- **User EE** (`UserEarlyEntry`) is the same collapse for one human, or `null` when they hold none.

## Data Model

None — no tables, no `DbContext`. Every grant is derived at read time from the contributing
sections' own data.

## Routing

`/Shifts/Admin/EarlyEntry` — the cross-source roster. The URL predates the section and is kept
verbatim; the nav entry sits in the "Tickets" admin group. Neither is an ownership claim.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | Sees their own EE date on ticket-stub surfaces (homepage strip, holdings, transfer wizard) — never anyone else's. |
| Gate / Scanner staff | Sees the *scanned attendee's* EE on the gate card (`ScannerController`, `GateService`). |
| `ShiftDashboardAccess` (Admin, NoInfoAdmin, VolunteerCoordinator) | Reads the full cross-source roster at `/Shifts/Admin/EarlyEntry`. |

## Invariants

- The section owns no tables and injects no repository; the orchestrator's only dependency is
  the provider fan-out (`EarlyEntryArchitectureTests.OrchestratorInjectsOnlyTheProviderFanout`).
- The fan-out is sequential — a simplicity choice, not a thread-safety requirement
  (design-rules §8b); each provider reads through its own section.
- `GetRosterAsync` is **never cached**. `GetForUserAsync` is cached per human, negative results
  included; only eviction refreshes it (no warmup, no expiry).
- The Singleton decorator resolves the Scoped inner service per call through the keyed
  registration `CachingEarlyEntryService.InnerServiceKey`, never a repository.
- Per human: earliest date wins, sources are distinct and ordinal-compared in provider order,
  and `HasMultiple` is `Sources.Count > 1`.
- The roster is gated by `ShiftDashboardAccess`
  (`EarlyEntryArchitectureTests.RosterRequiresShiftDashboardAccess`).
- No `Resources/` folder: the roster is an admin page with inline English
  ([`localization-admin-exempt`](../../../../memory/code/localization-admin-exempt.md)).

## Negative Access Rules

- A holder **cannot** see another holder's EE on any holder-facing stub surface — all three go
  through `TicketStubInfo.From(row, holderEarlyEntry)` with the *viewer's* value. The gate card
  is the deliberate staff-facing exception.
- A human without `ShiftDashboardAccess` **cannot** reach `/Shifts/Admin/EarlyEntry`.
- The section exposes no write path, so no write **cannot**-clause applies.

## Triggers

- When a contributing section changes a single human's EE-relevant data, it evicts that human
  through `IEarlyEntryInvalidator.InvalidateUser` — Camps on `SetEarlyEntryAsync`, member
  removal, GDPR erasure and account merge; Shifts on every build-shift
  confirm/bail/remove/reassign, erasure and merge; Teams on every EE grant add/edit/remove,
  erasure and merge.
- When a global setting moves every holder's date at once, the contributor calls
  `InvalidateAll` — the camps' global `EeStartDate`, and Shifts' EventSettings gate /
  build-offset edits. Teams also calls it when a team's `EarlyEntryEnabled` flag flips, because
  that changes *who* contributes.
- Eviction is pure: the next read lazy-reloads.

## Cross-Section Dependencies

Outbound (the `.csproj` references `Humans.Base` and `Humans.Users.Contracts` only):

- **Users** — `IUserServiceRead` through `HumansControllerBase.FindUserInfoByIdAsync`, for the
  roster's legal-name column.

Inbound, all through `Contracts/`:

- **Camps** — `CachingCampService` implements `IEarlyEntryProvider`; `CampService` injects
  `IEarlyEntryInvalidator`.
- **Shifts** — `VolunteerTrackingExportService` implements `IEarlyEntryProvider`;
  `ShiftSignupService` injects `IEarlyEntryInvalidator`.
- **Teams** — `TeamService` implements `IEarlyEntryProvider` and injects `IEarlyEntryInvalidator`.
- **Gate** — `GateService` calls `IEarlyEntryService.GetForUserAsync` for the scanned attendee.
- **Scanner** — `ScannerController` calls `GetForUserAsync` for the scanned attendee.
- **Tickets** — `TicketTransferController`, `MyTicketStubsViewComponent` and
  `TicketHoldingsViewComponent` call `GetForUserAsync` for the viewer.

## Architecture

**Owning services:** `EarlyEntryService` (orchestrator), `CachingEarlyEntryService` (§15 decorator)
**Owned tables:** None — orchestrator over every registered `IEarlyEntryProvider`.
**Status:** (A) Migrated — moved into `src/Sections/Humans.EarlyEntry` 2026-08-14
(nobodies-collective/Humans#866).

### Cross-section read interface

The whole outward surface is read-only, so there is no read/write split to make. The three
contracts live in `Contracts/`, a folder rather than a leaf project: the section references no
contributor, so there is no cycle to break.

| Read interface | Methods | Notes |
|---|---:|---|
| `IEarlyEntryService` | 2 | `GetRosterAsync` (live), `GetForUserAsync` (cached) |

### For (A) Migrated sections

- **No repository, no `DbContext`.** `TrackedCache` comes from `Humans.Base.Caching`,
  `ICacheStats` from `Humans.Base.Interfaces.Caching`.
- **Decorator decision** — caching decorator, Singleton, `TrackedCache`-backed,
  `warmOnStartup: false`; not a hosted service, there is nothing to warm. Negative results are
  cached by hand (`TryGet` / `Set`) because `TrackedCache.GetAsync` never stores a null.
- **Cross-section calls** — `IUserServiceRead` only.
- **Architecture test** — `tests/Humans.EarlyEntry.Tests/EarlyEntryArchitectureTests.cs`; the
  rendered page is pinned by `tests/Humans.Integration.Tests/Controllers/EarlyEntryPageRenderTests.cs`
  (local-only, never runs in CI).
- **Known debt** — `IEarlyEntryInvalidator` carries `[Grandfathered(HUM0028)]`
  (nobodies-collective/Humans#805): the contributing sections flush this section's cache rather
  than the decorator owning invalidation end-to-end.

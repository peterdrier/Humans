<!-- freshness:triggers
  src/Sections/Humans.EarlyEntry/**
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
  `LocalDate`, with a human-readable `Source` label (`"Camp: Flaming Lotus"`, `"Shift: Flags"`,
  `"{TeamName}: {ProjectName}"`). It is a projection, never a row this section stores.
- An **EE provider** (`IEarlyEntryProvider`) is a section that contributes grants. Registered as
  `IEnumerable<IEarlyEntryProvider>`; a section with nothing to contribute returns an empty list.
- A **roster row** (`EarlyEntryRosterRow`) is one human's grants collapsed: earliest date, the
  distinct source labels, and `HasMultiple` when more than one source grants them EE — the flag
  the roster page uses to surface reallocatable slots.
- **User EE** (`UserEarlyEntry`) is the same collapse for a single human: earliest date + sources,
  or `null` when they hold none.

## Data Model

None — the section owns no tables and has no `DbContext`. Every grant is derived at read time
from the contributing sections' own tables.

## Routing

`/Shifts/Admin/EarlyEntry` — the cross-source roster. The URL predates the section and is kept
verbatim: the page is reached from the shift-dashboard nav and the route prefix is a nav
location, not an ownership claim.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | Sees their own EE date on ticket-stub surfaces (homepage strip, holdings, transfer wizard) — never anyone else's. |
| Gate / Scanner staff | Sees the *scanned attendee's* EE on the gate card (`ScannerController`, `GateService`). |
| `ShiftDashboardAccess` (Admin, NoInfoAdmin, VolunteerCoordinator) | Reads the full cross-source roster at `/Shifts/Admin/EarlyEntry`. |

## Invariants

- The section owns no tables and injects no repository. It is an orchestrator by the hard rules'
  own definition, and the territory it orchestrates is its own (Peter, 2026-08-14).
- The fan-out is **sequential**, not `Task.WhenAll`: providers share the scoped section
  `DbContext`s, which are not thread-safe (the same reason `GdprService` is sequential).
- `GetRosterAsync` is **never cached** — the admin roster must see live data.
  `GetForUserAsync` is cached per user, negative results included, so the no-EE majority does
  not re-fan-out on every render.
- The caching decorator calls the inner service through `IEarlyEntryService` and never a
  repository (`peters-hard-rules.md`). The Singleton decorator resolves the Scoped inner service
  per call through the keyed registration `CachingEarlyEntryService.InnerServiceKey`.
- The roster collapses to **one row per human**: earliest date wins, sources are distinct and
  ordinal-compared, and `HasMultiple` is `Sources.Count > 1`.
- No `Resources/` folder: the roster page's copy is inline English. Pinned structurally by
  `EarlyEntryArchitectureTests.SectionTypesTakeNoStringLocalizer`.

## Negative Access Rules

- A holder **cannot** see another holder's EE on any holder-facing stub surface — all three go
  through `TicketStubInfo.From(row, holderEarlyEntry)` with the *viewer's* value. The Scanner
  gate card is the deliberate staff-facing exception.
- A human without `ShiftDashboardAccess` **cannot** reach `/Shifts/Admin/EarlyEntry`.
- No section **cannot**-clause needed for writes: this section exposes no write path at all.

## Triggers

- When a contributing section changes a single human's EE-relevant data, it evicts that human
  through `IEarlyEntryInvalidator.InvalidateUser` — Camps on `SetEarlyEntryAsync` and the
  member-removal cascade, Shifts on every build-shift confirm/bail/remove/reassign, Teams on
  every EE grant add/edit/remove.
- When a global setting moves every holder's date at once, the contributor calls
  `InvalidateAll` — the camps' global `EeStartDate`, and Shifts' EventSettings gate /
  build-offset edits. Teams also calls it when a team's `EarlyEntryEnabled` flag flips, because
  that changes *who* contributes.
- Eviction is pure: the cache has no warmup, so the next read lazy-reloads.

## Cross-Section Dependencies

- **Users** — `IUserServiceRead` (through `HumansControllerBase.FindUserInfoByIdAsync`) for the
  legal name column on the roster. The only outbound reference the section has.
- Inbound, all through `Contracts/`:
  - **Camps** — `CachingCampService` implements `IEarlyEntryProvider`; `CampService` injects
    `IEarlyEntryInvalidator`.
  - **Shifts** — `VolunteerTrackingExportService` implements `IEarlyEntryProvider`;
    `ShiftSignupService` injects `IEarlyEntryInvalidator`.
  - **Teams** — `TeamService` implements `IEarlyEntryProvider` and injects
    `IEarlyEntryInvalidator`.
  - **Gate** — `GateService` calls `IEarlyEntryService.GetForUserAsync`.
  - **Scanner** — `ScannerController` calls `GetForUserAsync` for the scanned attendee.
  - **Tickets** — `TicketTransferController` calls `GetForUserAsync` for the viewer.
  - **Shell** — `MyTicketStubsViewComponent`, `TicketHoldingsViewComponent`.

## Architecture

**Owning services:** `EarlyEntryService` (orchestrator), `CachingEarlyEntryService` (§15 decorator)
**Owned tables:** None — orchestrator over every registered `IEarlyEntryProvider`.
**Status:** (A) Migrated — moved into `src/Sections/Humans.EarlyEntry` 2026-08-14
(nobodies-collective/Humans#866, G5 lane 4b-2b).

### Cross-section read interface

The whole outward surface is read-only already, so there is no read/write split to make:
`IEarlyEntryService` has two read members and no writes. It lives in `Contracts/` beside
`IEarlyEntryProvider` (the inbound contribution contract) and `IEarlyEntryInvalidator` (the §15e
one-way staleness signal).

| Read interface | Methods | Notes |
|---|---:|---|
| `IEarlyEntryService` | 2 | `GetRosterAsync` (live), `GetForUserAsync` (cached) |

### For (A) Migrated sections

- The section takes **no `Humans.Infrastructure` reference**: it owns no tables, has no `DbContext`
  and no G4 gate. `TrackedCache` comes from `Humans.Base.Caching`; `ICacheStats` from `Humans.Base.Interfaces.Caching`.
- **No repository.** The hard rules' orchestrator clause applies: this service calls services,
  never repositories.
- **Decorator decision** — caching decorator, Singleton, `TrackedCache`-backed, `warmOnStartup: false`.
  Not registered as a hosted service: there is nothing to warm.
- **Cross-section calls** — `IUserServiceRead` only.
- **Architecture test** — `tests/Humans.EarlyEntry.Tests/EarlyEntryArchitectureTests.cs`; the page
  itself is pinned by `tests/Humans.Integration.Tests/Controllers/EarlyEntryPageRenderTests.cs`.
- **Known debt** — `IEarlyEntryInvalidator` carries `[Grandfathered(HUM0028)]`
  (nobodies-collective/Humans#805): the contributing sections flush this section's cache rather
  than the decorator owning invalidation end-to-end.

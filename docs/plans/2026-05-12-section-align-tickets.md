# Section Align - Tickets
**Run started:** 2026-05-12 | **Mode:** existing-section | **Worktree:** `H:\source\humans`
**Branch:** `align/tickets-run-fresh` (off `origin/main` @ `2ddb825ef`)
**Canonical section name proposal:** Tickets

## Axis 1 - Boundary integrity

### 1.1 Section name consistency - clean
Canonical name `Tickets` is consistent across:
- `docs/sections/Tickets.md`
- Controller names:
  - `TicketController` (`/src/Humans.Web/Controllers/TicketController.cs`)
  - `TicketTransferController` (`/src/Humans.Web/Controllers/TicketTransferController.cs`)
  - `TicketTransferAdminController` (`/src/Humans.Web/Controllers/TicketTransferAdminController.cs`)
- Views:
  - `src/Humans.Web/Views/Ticket/`
  - `src/Humans.Web/Views/TicketTransfer/`
  - `src/Humans.Web/Views/TicketTransferAdmin/`
- ViewModels:
  - `src/Humans.Web/Models/TicketViewModels.cs`
  - `src/Humans.Web/Models/TicketTransferViewModels.cs`
- DI extension:
  - `src/Humans.Web/Extensions/Sections/TicketsSectionExtensions.cs`

No naming collisions were found.

### 1.2 Controller existence/placement - clean
All Tickets routes are hosted under `Tickets*` controllers; no tickets data/actions were found in unrelated controllers.

### 1.3 URL surface - mostly clean
Current routes are all under expected prefixes:
- `/Tickets/*` for ticket dashboard/admin list pages
- `/Tickets/Admin/*` for admin transfer review pages
- `/Tickets/Transfers/*` for user transfer UX

Current exception:
- `/Welcome` is implemented by `WelcomeController` and documented as a Tickets post-purchase landing route in `docs/sections/Tickets.md`.
  This is a known section-owned exception and should be signed off explicitly in this run.

### 1.4 Views folder - clean
`Views/Ticket*` and `Views/TicketTransfer*` folders exist and contain Tickets-owned page views.

### 1.5 ViewModel placement - clean
`TicketViewModels` and `TicketTransferViewModels` are section-local.

### 1.6 Controller-base leak - clean
No Tickets-specific helpers or models were found on `HumansControllerBase` or related base classes.

### 1.7 Extensions placement - clean
`TicketsSectionExtensions` exists and contains Tickets DI registrations in the expected location.

### 1.8 Role surface - clean
Routes are covered by `TicketAdminBoardOrAdmin`, `TicketAdminOrAdmin`, or `AdminOnly` as documented in the section doc.

### 1.9 / 1.10 Inbound cross-section DB access and EF navigations - clean
No direct consumer writes/reads were found in other sections against Tickets-owned `ticket_*` tables via `DbSet`/EF usage.
No cross-section-owned inbound section entity nav usage was identified beyond expected FK to `Users`.

### 1.11 Outbound cross-section access - clean
Tickets services and controllers call owning sections’ service interfaces (`IUserService`, `ITeamService`, `ICampaignService`, `IBudgetService`, etc.) instead of directly using other-section repositories.

### 1.12 Controller -> DbContext - clean
No Ticket controllers inject `HumansDbContext`.

### 1.13 Migrations - clean
No hand-edited migrations in this section.

### 1.14 Section doc shape - clean
`docs/sections/Tickets.md` exists and aligns with section behavior.

### 1.15 Operational docs/routing gaps - none
No additional routing/docs gaps surfaced in this pass.

## Axis 2 - Internal cohesion

### 2.1 EF leakage from service layer - clean
`TicketQueryService`, `TicketSyncService`, and `TicketingBudgetService` remain in `Humans.Application.Services.Tickets` and do not import EF types; repositories are accessed via interfaces.  
`TicketSyncService` and `TicketTransferService` comments reference EF terms only.

### 2.2 Caching placement - clean
Caching is implemented in service layer only (`TicketQueryService`/`TicketSyncService`).

### 2.3 DI lifetimes - clean
Repository/service registrations use the expected singleton/separate-scope patterns in `TicketsSectionExtensions` and cross-section bridge registration in `BudgetSectionExtensions`.

### 2.4 Repository pattern - clean
`ITicketRepository` and `ITicketingBudgetRepository` are owned in `Infrastructure/Repositories/Tickets` and implemented as sealed types.

### 2.5 Shared visual components - review only
No Tickets-specific reusable ViewComponent/TagHelper components were identified yet for cross-page rendering. No cross-section rendering debt surfaced in this pass.

### 2.6 Interface budget + consolidation - action needed (ticket interfaces)
Interface budgets are currently absent on large interfaces, especially `ITicketQueryService` and `ITicketTransferService`.

This violates the project’s budget ratchet guidance:
- Tighten large public interfaces with `[SurfaceBudget]`.
- Keep budgets exact to avoid analyzer slack diagnostics (`HUM0016`).

### 2.7 Architecture tests - partial coverage
Tickets has focused architecture tests for service shape and vendor connector boundaries:
- `tests/Humans.Application.Tests/Architecture/TicketQueryArchitectureTests.cs`
- `tests/Humans.Application.Tests/Architecture/TicketSyncArchitectureTests.cs`
- `tests/Humans.Application.Tests/Architecture/TicketingBudgetArchitectureTests.cs`
- `tests/Humans.Application.Tests/Architecture/TicketVendorArchitectureTests.cs`

Coverage is good for boundary shape; budget-ratchet coverage is missing and should be addressed in Phase 2.

## Axis 3 - Test focus

### 3.1 Test placement
Tickets-related tests are present and reasonably distributed, but not all under one dedicated canonical folder:
- `tests/Humans.Application.Tests/Services/*Tickets*`
- `tests/Humans.Application.Tests/Services/Tickets/*`
- `tests/Humans.Application.Tests\Repositories/*`
- `tests/Humans.Application.Tests/Architecture/*`

### 3.2 Coverage map
Primary invariant coverage is in:
- service tests (`TicketSyncServiceTests`, `TicketQueryServiceTests`, `TicketingBudgetServiceTests`, `TicketTransferServiceTests`)
- repository tests (`TicketRepositoryTests`, `TicketingBudgetRepositoryTests`)
- e2e (`tests/e2e/tests/tickets.spec.ts`)

### 3.3 Redundancy
No obvious redundant coverage was identified in this pass.

### 3.4 Mutation testing
No tickets-specific `local/stryker-runs/tickets` report was found. No Stryker delta was added in this phase.

## Test-attribute gate
- Baseline from `docs/testing/mutation-testing.md`: 2139 attributes.
- Phase 0 net delta: +0 / -0 = 0.

## Stop conditions tripped

None.

## Follow-up /section-align targets

None found that require separate ownership from this run.

## Phase plan

### Phase 1 - Surface alignment
No route/view/controller/model moves required.

### Phase 2 - Architecture and boundary cleanup
1. [done] Added `[SurfaceBudget]` to large Tickets public interfaces:
   - `ITicketQueryService` (method count: 25)
   - `ITicketTransferService` (method count: 11)
2. [done] Kept budgets exact to avoid HUM0016 slack diagnostics.
3. [done] Re-ran relevant architecture/build checks:
   - `dotnet build src/Humans.Application/Humans.Application.csproj -p:TreatWarningsAsErrors=false`
   - `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter FullyQualifiedName~Humans.Application.Tests.Architecture.Ticket --no-build`
   - `dotnet test ... --filter FullyQualifiedName~Humans.Application.Tests.Services.Ticket --no-build`

### Phase 3 - Simplify / prune
3. [done] Rechecked budgets against interface method counts in code; both interface signatures match budget values exactly and no excess slack or overflow remains.

### Phase 4 - Docs
1. [done] Annotated in `docs/sections/Tickets.md` that `/Welcome` is an intentional, documented routing exception.

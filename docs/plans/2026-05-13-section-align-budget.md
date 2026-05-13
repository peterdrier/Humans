# Section-align: Budget (phase 0)

- **Date**: 2026-05-13
- **Section**: Budget
- **Branch**: `align/budget`
- **Status**: inventory only

## What exists for this section

### Controllers
- `src/Humans.Web/Controllers/BudgetController.cs`
  - `[Route("Budget")]`
  - `GET /Budget`, `/Budget/Summary`, `/Budget/Category/{id:guid}`
- `src/Humans.Web/Controllers/FinanceController.cs`
  - `[Route("Finance")]`
  - `Index`, `Years/{id}`, `Categories/{id}`, `AuditLog/{yearId?}`, `CashFlow`, `Admin`
  - CRUD POST endpoints for year/group/category/line-item sync, plus budget-related sync actions

### Section extension / DI
- `src/Humans.Web/Extensions/Sections/BudgetSectionExtensions.cs`
  - Registers `IBudgetRepository` -> `BudgetRepository` (singleton)
  - Registers `IBudgetService` -> `BudgetBudgetService` (scoped)
  - Registers `IUserDataContributor`
  - Registers ticketing bridge service: `ITicketingBudgetRepository` -> `TicketingBudgetRepository` and `ITicketingBudgetService` -> `TicketsTicketingBudgetService`

### Views
- Budget views:
  - `src/Humans.Web/Views/Budget/Index.cshtml`
  - `src/Humans.Web/Views/Budget/Summary.cshtml`
  - `src/Humans.Web/Views/Budget/NoActiveBudget.cshtml`
  - `src/Humans.Web/Views/Budget/CategoryDetail.cshtml`
- Finance views consumed by budget feature:
  - `src/Humans.Web/Views/Finance/Admin.cshtml`
  - `src/Humans.Web/Views/Finance/AuditLog.cshtml`
  - `src/Humans.Web/Views/Finance/CashFlow.cshtml`
  - `src/Humans.Web/Views/Finance/CategoryDetail.cshtml`
  - `src/Humans.Web/Views/Finance/NoActiveYear.cshtml`
  - `src/Humans.Web/Views/Finance/YearDetail.cshtml`

### Domain / data surface
- `src/Humans.Application/Interfaces/Budget/IBudgetService.cs`
- `src/Humans.Infrastructure/Repositories/Budget/BudgetRepository.cs`
  - Uses `IDbContextFactory` and short-lived `DbContext` instances per call
- Domain entities:
  - `src/Humans.Domain/Entities/BudgetLineItem.cs`
  - `src/Humans.Domain/Entities/BudgetCategory.cs`
  - `src/Humans.Domain/Entities/BudgetAuditLog.cs`
- View models:
  - `src/Humans.Web/Models/BudgetViewModels.cs`

## Boundary and invariants check

### What Budget currently consumes from other sections
- Team APIs:
  - `ITeamService.GetBudgetableTeamsAsync()`
  - `ITeamService.GetEffectiveBudgetCoordinatorTeamIdsAsync()`
  - `ITeamService.GetActiveTeamOptionsAsync()`
- User API:
  - `IUserService` for merged source IDs and label enrichment

### What other sections consume from Budget
- Ticketing uses budget bridge interface:
  - `ITicketingBudgetRepository`
  - `ITicketingBudgetService`
  - See `TicketsTicketingBudgetService` implementation path in Budget section extensions

### Architectural notes
- No `Budget`-specific architecture tests were found under `tests/Humans.Application.Tests/Architecture`.
- No direct EF types were identified in budget application service layer during this pass.
- Repository uses navigation includes for related entities; there is a notable TODO comment around loading team navigation on line items.

## Follow-up candidates (from section-alignment pass)

1. Resolve canonical route tension between `/Budget/*` and `/Finance/*` admin workflows for budget admin/audit operations.
2. Add/confirm `[SurfaceBudget]` annotations (if expected by conventions) on exposed budget contract interfaces.
3. Add budget-specific architecture tests and controller coverage gaps.
4. Validate any remaining team navigation dependencies in budget line items and remove legacy references where possible.
5. Review `BudgetAuditLog` and reporting endpoints for cross-section contract consistency.

## Test coverage status

- Existing tests:
  - `tests/Humans.Application.Tests/Services/BudgetServiceTests.cs`
  - `tests/Humans.Application.Tests/Services/TicketingBudgetServiceTests.cs`
- No section-specific controller or architecture test suite was identified for Budget during this phase.

## Files referenced

- `docs/sections/Budget.md`
- `src/Humans.Web/Controllers/BudgetController.cs`
- `src/Humans.Web/Controllers/FinanceController.cs`
- `src/Humans.Web/Extensions/Sections/BudgetSectionExtensions.cs`
- `src/Humans.Web/Models/BudgetViewModels.cs`
- `src/Humans.Infrastructure/Repositories/Budget/BudgetRepository.cs`
- `src/Humans.Application/Interfaces/Budget/IBudgetService.cs`
- `src/Humans.Domain/Entities/BudgetLineItem.cs`
- `src/Humans.Domain/Entities/BudgetCategory.cs`
- `src/Humans.Domain/Entities/BudgetAuditLog.cs`
- `src/Humans.Web/Views/Budget/*`
- `src/Humans.Web/Views/Finance/*`
- `tests/Humans.Application.Tests/Services/BudgetServiceTests.cs`
- `tests/Humans.Application.Tests/Services/TicketingBudgetServiceTests.cs`
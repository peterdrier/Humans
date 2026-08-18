# Budget — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `BudgetController` | Class | `[Authorize]` (authenticated) | — |
| `BudgetController` runtime guards | In-method | `authService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)` and `authService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` | Resource-based (see handler below) |
| `BudgetAdminController` (`[Route("Finance")]`) | Class | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` (Budget years/groups/categories/line-items CRUD, cash flow, audit log, ticketing-budget sync; shares the `/Finance` route prefix with `Humans.Finance`'s `FinanceController`) |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `BudgetAuthorizationHandler` | `BudgetOperationRequirement` (`Edit`) | `BudgetCategorySnapshot` | `Authorization/BudgetAuthorizationHandler.cs` (registered in `Section.cs`) |

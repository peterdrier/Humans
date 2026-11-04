# Budget — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `BudgetController` | Class | `[Authorize]` (authenticated) | — |
| `BudgetController` runtime guards | In-method | `authService.AuthorizeAsync(User, FinancePolicies.Manage)` and `authService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit)` | Resource-based (see handler below) |
| `BudgetAdminController` (`[Route("Budget/Admin")]`) | Class | `FinanceAdmin, Board, Admin` to read; every POST `FinanceAdmin, Admin` | `FinancePolicies.Read` on the class, `FinancePolicies.Manage` on every POST (Budget years/groups/categories/line-items CRUD, audit log, ticketing-budget sync) |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `BudgetAuthorizationHandler` | `BudgetOperationRequirement` (`Edit`) | `BudgetCategorySnapshot` | `Authorization/BudgetAuthorizationHandler.cs` (registered in `Section.cs`) |

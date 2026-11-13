# Budget — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `BudgetController` | Class | `[Authorize]` (authenticated) | — |
| `BudgetController` runtime guards | In-method | `authService.AuthorizeAsync(User, FinancePolicies.Manage)` and `authService.AuthorizeAsync(User, category, BudgetOperationRequirement.View)` / `Edit` | Resource-based (see handler below) |
| `BudgetAdminController` (`[Route("Budget/Admin")]`) | Class | `FinanceAdmin, Board, Admin` to read; every POST `FinanceAdmin, Admin` | `FinancePolicies.Read` on the class, `FinancePolicies.Manage` on every POST (Budget years/groups/categories/line-items CRUD, audit log, ticketing-budget sync) |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `BudgetAuthorizationHandler` | `BudgetOperationRequirement` (`View`, `Edit`) | `BudgetCategorySnapshot` | `Authorization/BudgetAuthorizationHandler.cs` (registered in `Section.cs`) |

Finance may view every category. Other viewers must coordinate at least one effective team and cannot view restricted or ticketing categories. Viewing other departments and archived years remains allowed; Edit additionally requires coordinating the category’s team and a non-archived year. The controller authorizes View before loading active team options.

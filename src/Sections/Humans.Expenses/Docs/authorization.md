# Expenses — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ExpensesController` | Class | `[Authorize]` (authenticated) | — |
| `ExpensesController.Review` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |
| `ExpensesController.Approve` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |
| `ExpensesController.Reject` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |
| `ExpensesController.HoldedRetry` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` (re-queues a stuck Holded push for an approved report; `ExpenseReportOperation.RequeueHoldedPush`) |
| `ExpensesController` runtime guards | In-method | `authService.AuthorizeAsync(User, report, new ExpenseReportOperationRequirement(ExpenseReportOperation.X))` — `View` (Detail + Attachment), `Endorse`, `CoordinatorReject`, `Approve`, `FinanceReject`, `RequeueHoldedPush` | Resource-based (see handler below) |
| `ExpensesController` owner guards | In-method | Submitter-side actions (`Edit` GET/POST, `AddLine`, `UpdateLine`, `RemoveLine`, `AttachFile`, `RemoveAttachment`, `Submit`, `Withdraw`, `Iban` GET/POST) gate on `report.SubmitterUserId != user.Id → Forbid()` | Inline owner check |

## Resource-Based Authorization Handlers

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `ExpenseReportAuthorizationHandler` | `ExpenseReportOperationRequirement` (`View`, `Edit`, `Submit`, `Withdraw`, `Endorse`, `CoordinatorReject`, `Approve`, `FinanceReject`, `CategoryOverride` — unused, no call site, `RequeueHoldedPush`) | `ExpenseReportDto` | `Authorization/ExpenseReportAuthorizationHandler.cs` (registered in `Section.cs`) |
| `IbanAccessHandler` | `IbanAccessRequirement` | (intrinsic — `TargetUserId` / `ReportId` / `IsAdminPageContext` fields on requirement) | `Authorization/IbanAccessHandler.cs` — registered in DI but no production call site today (only `IbanAccessHandlerTests`); `UsersAdminController.RevealIban` (`Humans.Users`) uses `[Authorize(Policy = AdminOnly)]` instead |

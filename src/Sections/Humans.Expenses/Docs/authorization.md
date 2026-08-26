# Expenses — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `ExpensesController` | Class | `[Authorize]` (authenticated) | — |
| `ExpensesController.Review` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |
| `ExpensesController.Approve` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |
| `ExpensesController.Reject` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |
| `ExpensesController.HoldedRetry` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` (re-queues a stuck Holded push for an approved report; `ExpenseReportOperation.RequeueHoldedPush`) |
| `ExpensesController` runtime guards | In-method | `authService.AuthorizeAsync(User, report, new ExpenseReportOperationRequirement(ExpenseReportOperation.X))` — `View` (Detail + Attachment), `Edit` (`Edit` GET/POST, `NewLine`, `AddLine`, `UpdateLine`, `RemoveLine`, `AttachFile`, `RemoveAttachment`, and the render flag on `LineEdit`/`LineProofs`), `Submit`, `Endorse`, `CoordinatorReject`, `Approve`, `FinanceReject`, `RequeueHoldedPush` | Resource-based (see handler below) |
| `ExpensesController` owner guards | In-method | `Withdraw` alone still gates on `report.SubmitterUserId != user.Id → Forbid()` — withdrawal is never done on a member's behalf. `LineEdit`/`LineProofs` add the submitter as a read-only viewer past the `Edit` grant, and `Iban` GET/POST admits the submitter at any status or whoever `Edit` covers (`CanSetReportIbanAsync`) | Inline owner check |
| `ExpensesController.New` | In-method | The **For member** picker renders, and a non-self `SubmitterUserId` is accepted, only for `PolicyNames.FinanceAdminOrAdmin`; anything else posting one gets `Forbid()` | Policy check (`IsFinanceAdminAsync`) |

## Resource-Based Authorization Handlers

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `ExpenseReportAuthorizationHandler` | `ExpenseReportOperationRequirement` (`View`, `Edit`, `Submit`, `Withdraw`, `Endorse`, `CoordinatorReject`, `Approve`, `FinanceReject`, `CategoryOverride` — unused, no call site, `RequeueHoldedPush`). `Edit` grants the submitter their own Draft, and a finance admin any report in Draft / Submitted / CoordinatorEndorsed; `Submit` grants either of them a Draft | `ExpenseReportDto` | `Authorization/ExpenseReportAuthorizationHandler.cs` (registered in `Section.cs`) |
| `IbanAccessHandler` | `IbanAccessRequirement` | (intrinsic — `TargetUserId` / `ReportId` / `IsAdminPageContext` fields on requirement) | `Authorization/IbanAccessHandler.cs` — registered in DI but no production call site today (only `IbanAccessHandlerTests`); `UsersAdminController.RevealIban` (`Humans.Users`) uses `[Authorize(Policy = AdminOnly)]` instead |

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
| `ExpenseReportAuthorizationHandler` | `ExpenseReportOperationRequirement` (`View`, `Edit`, `Submit`, `Withdraw` — granted but unused, no call site; the controller's own owner check is the live gate, `Endorse`, `CoordinatorReject`, `Approve`, `FinanceReject`, `RequeueHoldedPush`). `Edit` grants the submitter their own Draft, and a finance admin any report in Draft / Submitted / CoordinatorEndorsed; `Submit` grants either of them a Draft | `ExpenseReportDto` | `Authorization/ExpenseReportAuthorizationHandler.cs` (registered in `Section.cs`) |

Raw-IBAN access has no resource handler. `IbanAccessHandler` / `IbanAccessRequirement` were deleted
once it was clear nothing constructed the requirement: they duplicated `[Authorize(Policy = AdminOnly)]`
on `/Users/Admin/{id}/RevealIban` — the only page that reveals a raw account number, and one that
already audits every reveal — and their finance grant (any non-Draft, non-Withdrawn report) did not
match `/Expenses/{id}/Iban`, which renders masked and gates *setting* the value on submitter-or-`Edit`.

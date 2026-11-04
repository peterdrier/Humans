# Finance — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `FinanceController` | Class | `FinanceAdmin, Board, Admin` to read; every POST `FinanceAdmin, Admin` | `FinancePolicies.Read` on the class, `FinancePolicies.Manage` on every POST: the Holded, creditor and SEPA-payout surface ([routing table](Finance.md#routing)) |
| `PaymentDetailsAdminController` | Class | `Admin` | `PolicyNames.AdminOnly` — `POST /Finance/Admin/Members/{id}/RevealIban`. FinanceAdmin may **not** reveal a raw IBAN. |
| `PaymentDetailsViewComponent` | In-component | `Admin` | `AuthorizeAsync(User, PolicyNames.AdminOnly)` plus `ProfileCardViewMode.Admin`: its host `/Users/Admin/{id}` also admits Board and UsersAdmin, who get empty content |

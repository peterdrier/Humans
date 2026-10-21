# Finance — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `FinanceController` | Class | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` — every action on the class: the Holded, creditor and SEPA-payout surface ([routing table](Finance.md#routing)) |
| `PaymentDetailsAdminController` | Class | `Admin` | `PolicyNames.AdminOnly` — `POST /Finance/Admin/Members/{id}/RevealIban`. FinanceAdmin may **not** reveal a raw IBAN. |
| `PaymentDetailsViewComponent` | In-component | `Admin` | `AuthorizeAsync(User, PolicyNames.AdminOnly)` plus `ProfileCardViewMode.Admin`: its host `/Users/Admin/{id}` also admits Board and HumanAdmin, who get empty content |

`/Finance/Holded` is deliberately on the same policy as the rest of the prefix rather than AdminOnly: a
finance admin who can already see creditor balances should be able to see the connector health those
balances depend on (decided 2026-08-07, nobodies-collective/Humans#1000).

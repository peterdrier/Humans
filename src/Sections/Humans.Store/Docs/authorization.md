<!-- freshness:triggers
  src/Sections/Humans.Store/**
  src/Humans.Base/Authorization/RoleChecks.cs
-->

# Store — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `StoreController` | Class | `[Authorize]` (authenticated) | — |
| `StoreController` runtime guards | In-method | `authService.AuthorizeAsync(User, order/resource, OrderOperationRequirement.{View, AddLine, RemoveLine, EditCounterparty, Pay, Delete, IssueInvoice, RecordPayment, Refund, DeletePayment})` for existing orders (`AddLine`/`RemoveLine` authorize against an `OrderLineContext` carrying the product's order deadline when known, else the plain order) and `OrderCreateContext` for `Create` (camp orders) / `CreateTeamOrder` (team orders). `Index` also seeds `isPrivilegedReader = StoreRoles.CanAdministerStore(User) \|\| TeamsRoles.IsTeamsAdmin(User)`. | Resource-based (see handler below) |
| `StoreAdminController` | Class | `StoreAdmin, FinanceAdmin, Board, Admin` | `StorePolicies.Read`; every POST and the catalog edit forms carry `StorePolicies.Manage` (`StoreAdmin, FinanceAdmin, Admin`) |
| `StoreAdminController.Payments` | Action | `StoreAdmin, FinanceAdmin, Board, Admin` inherited (`[HttpGet("Payments")]`) | `StorePolicies.Read` (Stripe ↔ Store ledger reconciliation report) |
| `StoreAdminController.RecordMissingPayments` | Action | `StoreAdmin, FinanceAdmin, Admin` (`[HttpPost("Payments/RecordMissing")]`) | `StorePolicies.Manage` (records missing Stripe payments) |
| `StoreAdminController.OrderYears` | Action | `StoreAdmin, FinanceAdmin, Board, Admin` inherited (`[HttpGet("OrderYears")]`) | `StorePolicies.Read` (reviews legacy order-year repairs) |
| `StoreAdminController.RepairOrderYears` | Action | `StoreAdmin, FinanceAdmin, Admin` (`[HttpPost("OrderYears/Repair")]`) | `StorePolicies.Manage` (repairs reviewed legacy order years) |
| `StoreAdminController.PaymentMethods` | Action | `StoreAdmin, FinanceAdmin, Board, Admin` inherited (`[HttpGet("PaymentMethods")]`) | `StorePolicies.Read` (reviews payments missing a method name) |
| `StoreAdminController.RepairPaymentMethods` | Action | `StoreAdmin, FinanceAdmin, Admin` (`[HttpPost("PaymentMethods/Repair")]`) | `StorePolicies.Manage` (backfills `Payment.MethodName`) |
| `StoreStripeWebhookController` | Class | `AllowAnonymous` (Stripe signature-verified) | — |

`IssueInvoice` is Store-admin-only on every order — no camp lead, department coordinator or
TeamsAdmin path succeeds it. `Delete` is granted to Store admins on any order and to a TeamsAdmin
on team orders; camp leads and coordinators never delete. `IssueInvoice` is additionally denied
on team orders even for admins, alongside `EditCounterparty` and `Pay`: team orders are non-billable.
`RecordPayment` (deposit returns) is Store-admin-only; the `Refund` requirement, checked by
`StoreController.RecordPayment` when the method is `Refund`, is narrower — Admin and FinanceAdmin
only, a StoreAdmin is denied (and the Refund option is hidden from the order page's form).
`DeletePayment` (`StoreController.DeletePayment`, a per-row button in the payments grid) is full Admin only — FinanceAdmin and StoreAdmin are denied, as is every non-admin path and every team order.

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `OrderAuthorizationHandler` | `OrderOperationRequirement` (`View`, `Create`, `AddLine`, `RemoveLine`, `EditCounterparty`, `Pay`, `Delete`, `IssueInvoice`, `RecordPayment`, `Refund`, `DeletePayment`) | `OrderDto` / `OrderCreateContext` / `OrderLineContext` (deadline-aware line checks) | `Authorization/OrderAuthorizationHandler.cs` (registered in `Section.cs`) |

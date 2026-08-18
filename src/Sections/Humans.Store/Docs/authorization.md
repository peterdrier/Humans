# Store — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `StoreController` | Class | `[Authorize]` (authenticated) | — |
| `StoreController` runtime guards | In-method | `authService.AuthorizeAsync(User, order/resource, OrderOperationRequirement.{View, AddLine, RemoveLine, EditCounterparty, Pay, Delete})` for existing orders (`AddLine`/`RemoveLine` authorize against an `OrderLineContext` carrying the product's order deadline when known, else the plain order) and `OrderCreateContext` for `Create` (camp orders) / `CreateTeamOrder` (team orders). `Index` also seeds `isPrivilegedReader = RoleChecks.CanAdministerStore(User) \|\| RoleChecks.IsTeamsAdmin(User)`. | Resource-based (see handler below) |
| `StoreAdminController` | Class | `StoreAdmin, FinanceAdmin, Admin` | `PolicyNames.StoreCatalogAdmin` |
| `StoreAdminController.Payments` | Action | `StoreAdmin, FinanceAdmin, Admin` inherited (`[HttpGet("Payments")]`) | `PolicyNames.StoreCatalogAdmin` (Stripe ↔ Store ledger reconciliation report) |
| `StoreAdminController.RecordMissingPayments` | Action | `StoreAdmin, FinanceAdmin, Admin` inherited (`[HttpPost("Payments/RecordMissing")]`) | `PolicyNames.StoreCatalogAdmin` (records missing Stripe payments) |
| `StoreStripeWebhookController` | Class | `AllowAnonymous` (Stripe signature-verified) | — |

## Resource-Based Authorization Handler

| Handler | Requirement | Resource | Path |
|---|---|---|---|
| `OrderAuthorizationHandler` | `OrderOperationRequirement` (`View`, `Create`, `AddLine`, `RemoveLine`, `EditCounterparty`, `Pay`, `Delete`) | `OrderDto` / `OrderCreateContext` / `OrderLineContext` (deadline-aware line checks) | `Authorization/OrderAuthorizationHandler.cs` (registered in `Section.cs`) |

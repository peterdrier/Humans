# Stripe — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `StripeController` | Class | `FinanceAdmin, Board, Admin` to read; `SyncNow` `FinanceAdmin, Admin` | `FinancePolicies.Read` on the class, `FinancePolicies.Manage` on the POST (same policies as `/Pleo` and `/Holded`) |

Every `/Stripe` route inherits the class-level policy; the POST also carries `[ValidateAntiForgeryToken]`.
No action opts out with `[AllowAnonymous]`. The Store webhook (`/Store/StripeWebhook`) is Store's route, not this section's.

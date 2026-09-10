# Holded — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `HoldedController` | Class | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` (the ledger mirror's own admin screen at `/Holded` — overview, per-account statement, per-entry legs, `SyncNow`/`FullSync` triggers; same policy as `FinanceController`) |

Every route inherits the class-level policy; both POSTs (`SyncNow`, `FullSync`) additionally
carry `[ValidateAntiForgeryToken]`. No action opts out with `[AllowAnonymous]`.

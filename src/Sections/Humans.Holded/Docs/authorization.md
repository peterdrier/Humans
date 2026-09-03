# Holded — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `HoldedController` | Class | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` (the ledger mirror's own admin screen at `/Holded` — overview, per-account statement, per-entry legs, `SyncNow`/`FullSync` triggers; same policy as `FinanceController`) |

All five routes inherit the class-level policy; the two POSTs (`SyncNow`, `FullSync`) additionally
carry `[ValidateAntiForgeryToken]`. No action opts out with `[AllowAnonymous]`.

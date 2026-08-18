# Holded — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `HoldedController` | Class | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` (the ledger mirror's own admin screen at `/Holded` — overview, per-account statement, `SyncNow`/`FullSync` triggers; same policy as `FinanceController`) |

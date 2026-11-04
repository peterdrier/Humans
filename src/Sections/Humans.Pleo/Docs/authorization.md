# Pleo — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `PleoController` | Class | `FinanceAdmin, Board, Admin` to read; `SyncNow`, `Link`, `Unlink`, `Add` `FinanceAdmin, Admin` | `FinancePolicies.Read` on the class, `FinancePolicies.Manage` on the POSTs (same policies as `/Holded`) |

Every route inherits the class-level policy, receipts included; every POST also carries `[ValidateAntiForgeryToken]`.
No action opts out with `[AllowAnonymous]`.

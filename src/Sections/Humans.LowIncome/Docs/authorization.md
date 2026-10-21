# LowIncome — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `LowIncomeAdminController` | Class | Admin | `PolicyNames.AdminOnly` — `/LowIncome/Admin` |
| `LowIncomeAdminController.Grant` / `Remove` | Action | Admin | `PolicyNames.AdminOnly` repeated on each POST |

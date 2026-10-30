# Development — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `DevLoginController` | Class | `AllowAnonymous` | Sign-in runs before there is a principal |
| `DevSeedController` | Class | `[Authorize]` (authenticated) | — |
| `DevSeedController.Index` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `DevSeedController.SeedBudget` | Action | `FinanceAdmin, Admin` | `FinancePolicies.Manage` |
| `DevSeedController.SeedCampRoles` | Action | `CampsAdmin, Admin` | `CampsPolicies.Manage` |
| `DevSeedController.SeedDashboard` | Action | `Admin, ShiftsNoInfo, ShiftsAdmin` | `ShiftsPolicies.Manage` |
| `DevSeedController.ResetDashboard` | Action | `Admin` | `PolicyNames.AdminOnly` |

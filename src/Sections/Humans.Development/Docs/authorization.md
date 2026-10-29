# Development — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `DevLoginController` | Class | (no class-level `[Authorize]`) | — |
| `DevSeedController` | Class | `[Authorize]` (authenticated) | — |
| `DevSeedController.Index` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `DevSeedController.SeedBudget` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |
| `DevSeedController.SeedCampRoles` | Action | `CampsAdmin, Admin` | `PolicyNames.CampAdminOrAdmin` |
| `DevSeedController.SeedDashboard` | Action | `Admin, ShiftsNoInfo, ShiftsAdmin` | `PolicyNames.ShiftDashboardAccess` |
| `DevSeedController.ResetDashboard` | Action | `Admin` | `PolicyNames.AdminOnly` |

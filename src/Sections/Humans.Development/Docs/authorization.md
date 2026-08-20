# Development — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `DevLoginController` | Class | (no class-level `[Authorize]`) | — |
| `DevSeedController` | Class | `[Authorize]` (authenticated) | — |
| `DevSeedController.SeedBudget` | Action | `FinanceAdmin, Admin` | `PolicyNames.FinanceAdminOrAdmin` |
| `DevSeedController.SeedCampRoles` | Action | `CampAdmin, Admin` | `PolicyNames.CampAdminOrAdmin` |
| `DevSeedController.SeedDashboard` | Action | `Admin, NoInfoAdmin, VolunteerCoordinator` | `PolicyNames.ShiftDashboardAccess` |
| `DevSeedController.ResetDashboard` | Action | `Admin` | `PolicyNames.AdminOnly` |

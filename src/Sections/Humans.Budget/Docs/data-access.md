# Budget — Data Access

## Budget

Project: `src/Sections/Humans.Budget` — services under `Services/`,
repository under `Data/`. **DbContext:** `BudgetDbContext`.
`BudgetRepository` injects `IDbContextFactory<BudgetDbContext>` directly.
Owns `BudgetYears`, `BudgetGroups`, `BudgetCategories`, `BudgetLineItems`,
`BudgetAuditLogs`, `TicketingProjections`.

### BudgetService (Scoped)

Repository: `IBudgetRepository`.

| Table | R/W |
|-------|-----|
| BudgetYears | R/W |
| BudgetGroups | R/W |
| BudgetCategories | R/W |
| BudgetLineItems | R/W |
| BudgetAuditLogs | R/W |
| TicketingProjections | R/W |

Cross-section calls via `ITeamService`, `IUserServiceRead`, plus `IClock`.
Team labels are stitched at the service layer via `ITeamService`.
Implements `IUserDataContributor`. No `IMemoryCache`.

---



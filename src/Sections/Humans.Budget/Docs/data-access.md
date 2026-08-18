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

### TicketingBudgetService (Scoped)

No repository, no DB access of its own, no cache. The Tickets→Budget bridge:
reads paid ticket sales through `ITicketServiceRead.GetTicketOrdersAsync` (the
cached read surface), aggregates them into weekly ticketing actuals, and hands
those to `IBudgetService.SyncTicketingActualsAsync`, which owns every resulting
`BudgetLineItem` / `TicketingProjection` write. Cross-section calls via
`ITicketServiceRead`, plus `IClock` and `ILogger`.

The class is `internal` per HUM0034 and its contract
`ITicketingBudgetService` is a single-member internal interface, driven by
`TicketingBudgetSyncJob` in `Humans.Budget/Jobs/`. The job's constructor is
internal too, so its DI registration lives in Budget's own `Section.Register`;
Hangfire scheduling names the public job class from Shell's roll-call.

---



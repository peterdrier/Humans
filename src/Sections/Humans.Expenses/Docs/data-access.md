# Expenses — Data Access

## Expenses

Folder: `src/Sections/Humans.Expenses/Services/` (namespace
`Humans.Expenses.Services`). **DbContext:** `ExpensesDbContext`.
`ExpenseRepository`
(`src/Sections/Humans.Expenses/Data/ExpenseRepository.cs`) injects
`IDbContextFactory<ExpensesDbContext>` directly. Owns `ExpenseReports`,
`ExpenseLines`, `ExpenseAttachments`, `HoldedExpenseOutboxEvents`.

### ExpenseReportService (Scoped)

Repository: `IExpenseRepository`.

| Table | R/W |
|-------|-----|
| ExpenseReports | R/W |
| ExpenseLines | R/W |
| ExpenseAttachments | R/W |
| HoldedExpenseOutboxEvents | R/W (outbox to Holded) |

Cross-section calls via `IFileStorage`, `IBudgetServiceRead`, `ITeamServiceRead`,
`IUserService`, `IAuditLogService`, `IHoldedClient`,
`IHoldedFinanceService` (Finance section — creditor balance exposure to
expense submitters). Implements `IUserDataContributor`. No
`IMemoryCache`.

Expense lines can be travel reimbursements (mileage / per-diem; an
`ExpenseLineType` column on `ExpenseLines` — same table, no new DbSet).
`PerDiemKind` is a service-side argument, not a persisted column: the rate
it selects is baked into the line's amount at creation. The service has an
`IOptions<TravelReimbursementConfig>` dependency (rates from
`appsettings.json`); rate math happens in the service, not the DB. Travel
lines can no longer be created — see `health.md` §5.

---



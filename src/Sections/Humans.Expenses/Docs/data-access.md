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

Cross-section calls via `IFileStorage`, `IBudgetServiceRead`, `ITeamService`,
`IUserService`, `IAuditLogService`, `IHoldedClient`,
`IHoldedFinanceService` (Finance section — creditor balance exposure to
expense submitters). Implements `IUserDataContributor`. No
`IMemoryCache`.

Expense lines can be travel reimbursements (mileage / per-diem;
`ExpenseLineType` + `PerDiemKind` columns on `ExpenseLines` — same table,
no new DbSet). The service has an `IOptions<TravelReimbursementConfig>`
dependency (rates from `appsettings.json`); rate math happens in the
service, not the DB. The personal IOU view reads through the existing
surface.

---



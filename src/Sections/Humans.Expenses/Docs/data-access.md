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

Cross-section calls via `IFileStorage`, `IBudgetService`, `ITeamService`,
`IUserService`, `IFinanceService` / `IFinanceWrite` (Finance section — `GetIbanAsync` /
`SetIbanAsync`: the submitter's IBAN is read and written through its owner), `IUserEmailService`, `IEmailService`, `IAuditLogService`,
`IHoldedService.IsConfigured`, `IHoldedFinanceService` (Finance section — creditor balance exposure to
expense submitters; `ListExpenseAccountsAsync` / `GetCategoryMapAsync` resolve the
account a report books to and the budget category it maps to), `IPreapprovalService`
(Preapprovals section — `GetGrantAsync` / `ListClaimableForUserAsync` for the grant a report
files against) and `IPreapprovalWrite` (`ClaimAsync` / `ReleaseAsync` / `ConsumeAsync` — the
grant's lifecycle follows the report's), plus the section's own `ExpensesEmails` builder,
`IClock` and `IOptions<TravelReimbursementConfig>`. Implements `IUserDataContributor`. No
`IMemoryCache`.

### ExpenseReportBackgroundProcessor (Scoped — `IExpenseReportBackgroundProcessor`)

Repository: `IExpenseRepository` (reads approved reports, updates
`HoldedExpenseOutboxEvents`). Selection, the in-process single-drain gate, retry/backoff, permanent-failure audit and the
configured-key gate live here; the section's `HoldedExpenseOutboxJob` drives it. Cross-section
calls via `IFileStorage`, `IUserService`, `IHoldedClient`, `IHoldedFinanceService`,
`IHoldedFinanceWrite`, `IAuditLogService`, plus `IClock`. No `IMemoryCache`.

### ExpenseHoldedPublisher (internal — `IApplicationService`, constructed by `ExpenseReportBackgroundProcessor`)

Not DI-registered: the processor news it up with its own dependencies. The resumable Holded
document + attachment conversation for one approved report. Repository: `IExpenseRepository`
(contact/document links, pushed-attachment stamps, outbox completion).

| Table | R/W |
|-------|-----|
| ExpenseReports | R/W (submitter's reports; Holded contact link) |
| ExpenseLines | W (per-line Holded document id) |
| ExpenseAttachments | W (pushed-attachment stamps) |
| HoldedExpenseOutboxEvents | W (completion) |

Cross-section calls via `IFileStorage`, `IUserService` (submitter, survivor after merge),
`IHoldedClient` (Holded section — vendor API), `IHoldedFinanceService` / `IHoldedFinanceWrite`
(Finance section owns creditor identity and the contact binding), `IAuditLogService`. No
`IMemoryCache`.

Expense lines can be travel reimbursements (mileage / per-diem; an
`ExpenseLineType` column on `ExpenseLines` — same table, no new DbSet).
`PerDiemKind` is a service-side argument, not a persisted column: the rate
it selects is baked into the line's amount at creation. The service has an
`IOptions<TravelReimbursementConfig>` dependency (rates from
`appsettings.json`); rate math happens in the service, not the DB. Travel
lines can no longer be created — see `health.md` §5.

### ExpenseHoldedBookingService (Scoped — `IExpenseHoldedBookingService`)

Repository: `IExpenseRepository`. Read-only: every booked expense line (Finance's duplicate
check) and every expected Holded document with its report's outbox state (Finance's expense
reconciliation, outbox events read in one query); it carries no `ExpenseReportService`
dependency so Finance can inject it without a DI cycle.

| Table | R/W |
|-------|-----|
| ExpenseReports | R |
| ExpenseLines | R |
| ExpenseAttachments | R (included with the lines) |
| HoldedExpenseOutboxEvents | R (latest create event per report) |

Cross-section calls via `IUserService` (resolves the submitter to the survivor after a merge,
the creditor as `ExpenseHoldedPublisher` binds it) and `IHoldedService.IsConfigured` (the
`NotConfigured` sync state). No `IMemoryCache`.

---



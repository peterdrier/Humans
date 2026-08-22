# Expenses — Data Access

## Expenses

Folder: `src/Sections/Humans.Expenses/Services/` (namespace
`Humans.Expenses.Services`). **DbContext:** `ExpensesDbContext`.
`ExpenseRepository`
(`src/Sections/Humans.Expenses/Data/ExpenseRepository.cs`) injects
`IDbContextFactory<ExpensesDbContext>` directly. Owns `ExpenseReports`,
`ExpenseLines`, `ExpenseAttachments`, `HoldedExpenseOutboxEvents`.
`VendorCommitmentRepository`
(`src/Sections/Humans.Expenses/Data/VendorCommitmentRepository.cs`) is the
section's second repository over the same context and owns
`VendorCommitments`, `VendorCommitmentPayments` and
`VendorCommitmentMatchCandidates`.

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

### VendorCommitmentService (Scoped)

Repository: `IVendorCommitmentRepository`.

| Table | R/W |
|-------|-----|
| VendorCommitments | R/W |
| VendorCommitmentPayments | R/W |
| VendorCommitmentMatchCandidates | R/W (human review queue) |

Cross-section calls via `IFileStorage` (quote files, key prefix
`uploads/vendor-commitment-quotes/`), `IAuditLogService` and `IHoldedClient`
(`ListPurchaseDocumentsAsync` only — read-only, and the section's existing
Holded dependency). No `IMemoryCache`.

Commitment ↔ purchase-document matching is a pure function,
`VendorCommitmentMatcher`: amount-first and exact, vendor name as a
constraint, and no tie-break of any kind — ties and documents that would be a
second booking against an already-invoiced commitment go to the review queue
for a human (nobodies-collective/Humans#1030).

---



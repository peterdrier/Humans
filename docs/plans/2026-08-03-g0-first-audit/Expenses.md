# Expenses — G0 First Audit

Section: Expenses · Kind: vertical · Audited 2026-08-03 @ 5a9bbe198

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository | PASS | `reforge ownership-violations --owner Expenses --tables expense_reports,expense_lines,expense_attachments,holded_expense_outbox_events` → `0 ownership-violations`. |
| 2 | One writer-service per table (no interceptor workarounds) | PASS | `reforge injected IExpenseRepository` → single consumer `ExpenseReportService` (`src/Humans.Application/Services/Expenses/ExpenseReportService.cs:29`). Docs: "no caching decorator" and no interceptor use mentioned. |
| 3 | No EF entity leaks across the boundary | PASS | `docs/sections/Expenses.md`: "Cross-domain navs — none declared. All cross-section linkage is scalar FK only." Cross-section calls are outbound only (`IBudgetService`, `ITeamService`, `IProfileService`, `IUserService`, `IAuditLogService`, `IHoldedFinanceService`) — Expenses has no read-surface interface for others to consume, so no entity-leak surface exists. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | Grep for `Grandfathered`/`Obsolete` across `Expense*.cs` entities and `Data/Configurations/**/Expense*.cs` configs → zero matches. |
| 5 | No `[Obsolete]` cross-section navs, no `[Grandfathered]`, no owned baseline rows | PASS | Same grep as #4; no `ExpenseReportService`/`ExpenseRepository` rows in any of the 5 present baseline files (`ApplicationServiceEntityReadReturns`, `DisplaySortInControllers`, `NoDestructiveMigrationOps`, `NoLinqAtDbLayer`, `NoStartupGuards`). |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | `grep HUM0031 src/Humans.Web/Controllers/Expenses*.cs` → zero matches. |
| 7 | `docs/sections/Expenses.md` exists and matches reality | PASS | Exists, current, detailed (state machine, IBAN masking, Holded outbox, Feature 2 creditor ledger). Matches code structure verified above. |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repository tests use real Postgres, zero EF-InMemory | FAIL | `tests/Humans.Application.Tests/Repositories/Expenses/ExpenseRepositoryTests.cs` uses `.UseInMemoryDatabase(...)`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | **FAIL — corrected 2026-08-03** | The original grep only searched for the literal string `HumansDbContext`, missing that Expenses has its own section `DbContext`. `ExpenseReportServiceTests : ServiceTestHarness` (`ExpenseReportServiceTests.cs:28`) builds `_expensesOptions` via `ServiceTestHarness.NewSectionDbOptions<ExpensesDbContext>()` (`:41-42`), which uses `.UseInMemoryDatabase(...)` (`ServiceTestHarness.cs:71`), then constructs a **real** `ExpenseRepository` over it (`:47`: `new ExpenseRepository(new TestDbContextFactory<ExpensesDbContext>(_expensesOptions))`) rather than mocking `IExpenseRepository`. Same EF-InMemory-through-a-real-repository anti-pattern already correctly caught for Campaigns in this batch — Expenses' own version was missed because the grep pattern didn't cover `ExpensesDbContext`. |
| 3 | Invariants/triggers each have a test (spot-check) | PASS (spot-check) | State machine transitions (`SubmitAsync_FlipsToSubmitted...`, `WithdrawAsync_FlipsToWithdrawn`, `CoordinatorEndorseAsync_FlipsToCoordinatorEndorsed`, `ApproveAsync_FlipsToApproved_AndAudits`, `FinanceRejectAsync_ReturnsToDraft_AndAudits`), receipt-attachment-required (`SubmitAsync_Throws_WhenLineHasNoAttachment`, `SubmitWithResultAsync_Succeeds_WithOnlyMileageLine_NoAttachment`), travel-line-immutability (`UpdateLineWithResultAsync_ReturnsFailure_AndKeepsAmount_ForTravelLine`), IBAN-required-at-submit (`SubmitAsync_Throws_WhenSubmitterHasNoIban`) all present (58 test methods total in the main file). IBAN-masking-in-logs invariant not directly spot-checked here (would need `IbanFormatterTests` or similar — not opened). |
| 4 | No skipped tests without an issue ref | PASS | `Skip\s*=` grep on `Services/Expenses/` and `Repositories/Expenses/` → no matches. |
| 5 | Tests grouped under the section | PASS | `tests/Humans.Application.Tests/Services/Expenses/` (3 files) + `Repositories/Expenses/` (1 file). Consistently grouped, unlike Feedback. |

## G1 Gap List

None — Expenses is clean on G1.

## G3 Gap List

1. **`ExpenseRepositoryTests.cs` uses `UseInMemoryDatabase`** — where: `tests/Humans.Application.Tests/Repositories/Expenses/ExpenseRepositoryTests.cs`. Suggested fix: convert to the shared Postgres fixture per #764/#766. No-migration-needed: **y**.
2. **Added 2026-08-03: `ExpenseReportServiceTests.cs` builds a real `ExpenseRepository` over EF-InMemory `ExpensesDbContext` instead of mocking `IExpenseRepository`** — where: `tests/Humans.Application.Tests/Services/Expenses/ExpenseReportServiceTests.cs:41-47`. Suggested fix: convert to `Substitute.For<IExpenseRepository>()`, matching the pattern already used for `ITeamService`/`IUserService` mocks in the same file. In scope of #766, same pattern as Campaigns' G3 gap #2 in this batch. No-migration-needed: **y**.

## Schema demolition queue (light)

- `docs/sections/Expenses.md` flags its own tech debt: `IHoldedFinanceService` is consumed as the "full interface for now, read-split to `IHoldedFinanceServiceRead` noted as future tech debt" — this is a Finance-side G1 item (see Finance audit), not an Expenses gap.
- No dead columns/tables spotted in the data model.


# Holded ledger as single source of truth — design

**Status:** Draft — awaiting Peter's sign-off
**Date:** 2026-06-15
**Depends on:** PR #1021 (`HoldedCreditorContact` person→account binding) merged to `main` first.
**Sequencing:** focused follow-up PR, branched off merged `origin/main` (not stacked on #1021).

## Problem

The current Holded-creditor read model stores **derived data** and makes **live API calls on page loads**:

- `HoldedCreditorBalance` caches a per-account balance — but a balance is just `Σdebit − Σcredit` of the account's journal lines. It's derived data sitting in the DB, able to drift from the journal.
- `HoldedPayment` caches payment rows — but those are exactly the **debit (out) lines** of the same journal. A subset, stored twice.
- The PR #1021 ledger view reads Holded's `dailyledger` **live** on every page load. `dailyledger` has no per-account server filter, so it sweeps the whole org daybook (≈3,879 lines today) at 250/page = **~16 sequential Holded calls per view**, scaling with org bookkeeping volume and traffic. Holded rate-limits the API; this is the main risk.
- Expense reports carry `SepaSent` and `Paid` statuses flipped by a polling job — but payment is **aggregate at the account level** (you pay a person's balance once), so a payment can't be attributed to a specific report. Per-report paid state fakes an attribution that doesn't exist.

## Decision

**The Holded daybook (journal lines) is the single source of truth. Cache it once; derive everything else; reports end at `Approved`.**

### End-state data model

| Table | Role | Change |
|---|---|---|
| `HoldedCreditorContact` | person → `400000xx` binding | **keep** (from #1021) |
| `HoldedLedgerLine` | cached daybook journal lines (the facts) | **new** |
| `HoldedCreditorBalance` | per-account balance rollup | **delete** (derived) |
| `HoldedPayment` | payment rows | **delete** (= debit lines of the daybook) |

`HoldedLedgerLine` (Finance-owned): `Id`, `EntryNumber`, `Line`, `AccountNum` (literal `400000xx`), `Date` (Instant), `Type`, `Description`, `Debit` (decimal), `Credit` (decimal), plus sync bookkeeping. Unique key on `(EntryNumber, Line)` for idempotent upsert; index on `AccountNum`.

### Everything derives from the lines

For an account's cached lines (sign confirmed against live data — Daniela `40000001`: credit 12720 − debit 9540 = 3180 owed, chart showed −3180):

- **Balance** (chart-equivalent) = `Σdebit − Σcredit`
- **OwedToMember** = `max(0, Σcredit − Σdebit)`
- **Payments / outs** = the debit lines
- **Ins** = the credit lines (the ERs as booked)

### Sync (zero Holded calls per page view)

- **Backfill once:** sweep inception→now in ≤1-year `dailyledger` windows (the API caps each call at one year) into `HoldedLedgerLine`. Full history is required so the derived balance is correct for accounts with activity older than a year.
- **Nightly incremental:** append journal lines newer than the last synced entry. Journal lines are immutable facts, so incremental append is safe; a periodic full re-sweep covers back-dated/edited entries.
- Page loads read `HoldedLedgerLine` from Postgres and aggregate — **0 Holded calls**. Holded API cost is a fixed nightly job, independent of traffic.

### Report lifecycle

`Draft → Submitted → [CoordinatorEndorsed] → Approved` (+ `Withdrawn` side-exit). `Approved` = booked into Holded as a credit on the person's account — **terminal for the report**. Paid/unpaid is read from the account ledger, never stamped on the report.

**Removed:**
- `ExpenseReportStatus.SepaSent`, `ExpenseReportStatus.Paid` (enum is string-backed, so removal is safe once existing rows are re-mapped — see migration).
- `ExpenseReport.SepaSentAt`, `ExpenseReport.PaidAt` columns.
- `ExpensePaidPollingJob` and `PollHoldedPaidStatusAsync`; `MarkPaidAsync`, `MarkSepaSentAsync`, `ReopenSepaAsync`.
- The entire SEPA-file subsystem — `GenerateSepaPayoutAsync`, `ISepaPaymentFileBuilder`/`SepaPaymentFileBuilder`, `SepaConfig`, the `Expenses/Sepa/*` endpoints and UI. **Payment is done externally** (pull the account balances, pay in the bank/Holded); the app only shows the ledger. (Decision: option (a).)

### Data migration (Peter-approved, forward-only)

One existing report is in `SepaSent`; QA/prod may hold a few `SepaSent`/`Paid` rows. The migration:

```sql
UPDATE expense_reports SET status = 'Approved' WHERE status IN ('SepaSent', 'Paid');
```

then drops `SepaSentAt`/`PaidAt`. `Down()` is the normal schema reversal — it re-adds the (nullable, empty) `SepaSentAt`/`PaidAt` columns and **does not reverse the data**: rolled-back rows simply stay `Approved`. The prior `SepaSent`/`Paid` can't be reconstructed, and that's fine. The data re-map is an additive `migrationBuilder.Sql(...)` in `Up()` only — the one data transform, deterministic (no guessed prior state).

## Read-path rewrites

- `GetCreditorStatusAsync` / `GetCreditorLedgerAsync` → compute balance, owed, payments, and lines from `HoldedLedgerLine` (no live call, no balance/payments tables).
- `GetHoldedTimelineAsync` and the member `/Expenses` view → derive owed/paid from the ledger.
- Admin `/Finance/Creditors` + statement → same derived source.
- `IHoldedRepository`: drop balance/payments methods, add `HoldedLedgerLine` upsert + per-account query. `IHoldedClient`: keep `ListDailyLedgerAsync`; drop `ListChartOfAccountsAsync`/`ListPaymentsAsync` if nothing else uses them.

## Risks / open items

- **Backfill volume** as the org ages: bounded by total journal size, run nightly off the request path — acceptable. Log if a sweep is truncated (no silent caps).
- **Editing/voiding past entries in Holded:** incremental-append misses mutations to old lines; mitigated by a periodic full re-sweep (cadence TBD in implementation).
- **`HoldedExpenseDoc` / actuals sync** (the budget-actuals path) is a separate concern and out of scope here.

## Out of scope

- The person→account binding itself (shipped in #1021).
- Budget actuals / purchase-doc matching.

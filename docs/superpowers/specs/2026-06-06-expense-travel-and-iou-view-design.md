# Expense Reports — Travel Lines (Mileage / Per Diem) + Personal IOU View

**Date:** 2026-06-06
**Section:** Expenses (with a small read-only DTO extension in Finance)
**Status:** Design approved (Peter), pre-implementation

## Problem

Two gaps in the Expenses section:

1. **No travel reimbursement.** Every `ExpenseLine` is a receipt-backed purchase, and
   `ExpenseReportService.SubmitAsync` hard-requires an attachment on *every* line
   (`"Every line must have an attachment before submitting."`). There is no way to book
   mileage or a per-diem allowance — reimbursements that, by Spanish tax rules, are
   justified by the trip itself, not by a receipt.

2. **No payout visibility for the submitter.** A member with many approved-but-unpaid
   reports has no way to see what the org owes them. In practice the org books these to a
   single creditor (IOU) account in Holded and settles in one transfer when funding lands.
   The balance already lives in Holded (`HoldedCreditorBalance`, `HoldedPayment`) and is
   surfaced *per report* on the detail page, but never as an account-level view the member
   can read at a glance.

## Goals

- A submitter can add **mileage** and **per-diem** lines via a small wizard that computes
  the amount server-side and writes a human-readable description onto the line. These lines
  never require an attachment.
- The `/Expenses` dashboard shows the submitter their **Holded IOU balance**, total paid,
  last payment date, and a **running ledger** of their reports and Holded payments — with an
  explicit note that the balance may include items unrelated to these reports, so the rows
  need not sum to it.

## Non-goals

- **Google Maps auto-distance** — explicitly a "bonus, not necessary." v1 takes manual km.
  The wizard's origin/destination fields leave a clean seam to add it later. (YAGNI.)
- **Structured travel data columns** (`DistanceKm` / `RatePerKm` / `Days`). Per Peter's
  steer, the computed amount + the description string *are* the record.
- **Writing to Holded** — Feature 2 is read-only over the existing creditor-balance cache.
  Humans never writes debt-reassignment journal entries (existing HARD boundary, `Finance.md`).
- **Reconciling the ledger to the balance** — the IOU legitimately includes external items;
  the view states this rather than forcing the math to tie out.
- **`IHoldedFinanceServiceRead` read-split** — the Expenses→Finance dependency is existing
  acknowledged tech debt; this work does not add a new cross-section method, so the split
  stays separate future work.

---

## Feature 1 — Travel lines (mileage + per diem)

### Data model

Add an enum discriminator to the line; nothing else structural.

- **`ExpenseLineType` enum** (`src/Humans.Domain/Enums/ExpenseLineType.cs`):
  `Receipt = 0`, `Mileage`, `PerDiem`.
- **`ExpenseLine.LineType`** (`ExpenseLineType`, default `Receipt`).

*Alternative considered:* a bare `RequiresReceipt` bool. Rejected — the enum maps 1:1 to the
three real concepts, drives a per-row badge, and is no more costly. (Decision flagged for sign-off.)

### Config — 2026 Spanish tax-exempt rates

New `TravelReimbursementConfig` bound via `IOptions` (mirrors the existing `SepaConfig` DTO
+ registration pattern). Values are the 2026 Spanish IRPF-exempt limits:

| Setting | Value | Source |
|---|---|---|
| `MileageRatePerKm` | `0.26` | Orden HFP/792/2023 — €0.19→€0.26 since Jul-2023, unchanged 2026 |
| `PerDiemDayTripRate` | `26.67` | manutención sin pernocta (España) |
| `PerDiemOvernightRate` | `53.34` | manutención con pernocta (España) |

The rate is captured into the line's description string at creation, so historical lines keep
their rate even if config changes.

### Wizards (server computes; submitter cannot tamper with rate/amount)

Two new paths on the **Edit** page, alongside the existing "Add line" form:

- **Mileage:** inputs `origin`, `destination`, `km` (decimal).
  - `amount = Math.Round(km × MileageRatePerKm, 2, AwayFromZero)`
  - description: `"{origin} to {destination}, {km:0.#} km @ €{rate:0.00} = €{amount:0.00}"`
    e.g. `"Berlin to Barcelona, 1281 km @ €0.26 = €333.06"`
- **Per diem:** inputs `type` (`DayTrip` | `Overnight`), `days` (int ≥ 1), optional `note`.
  - `rate` = the matching config value; `amount = Math.Round(days × rate, 2, AwayFromZero)`
  - description: `"Per diem: {days} day(s) {type} @ €{rate:0.00} = €{amount:0.00}"`, with
    `" — {note}"` appended when a note is given.

Both insert a normal line carrying the computed `Amount`, the formatted `Description`, and the
right `LineType`, reusing the existing internal line-insert path (`AddLineAsync`). Editing a
travel line later = re-run the wizard, or hand-edit description/amount like any line (Finance
reviews regardless). No structured inputs are stored, so there is nothing to "edit in place."

### Submit-rule change

`SubmitAsync` today: rejects if any line has a null `AttachmentId`. New rule:

> A report needs ≥ 1 line. Every **`Receipt`** line must have an attachment at submit time.
> `Mileage` / `PerDiem` lines never require one.

So a **pure-mileage report submits with zero attachments**. The submit failure message is
updated to say receipts are required only on receipt lines.

### Migration

`AddColumn` `LineType` (string, `HasConversion<string>()`, not-null, `defaultValue: "Receipt"`).
Existing lines were all receipt-backed, so the default is semantically correct — a schema
default, **not** a data backfill (respects the no-data-migrations rule).

### Holded push — unchanged

Travel lines have no attachment; `ProcessHoldedCreateAsync` already `continue`s past
null-attachment lines for the upload step. The line still becomes a product line on the
purchase doc carrying its description + amount. No special handling.

### Display

- Edit / Detail line rows show a small badge for `Mileage` / `PerDiem` lines and **no**
  "missing attachment" warning for them.
- `ExpenseLineDto` gains `LineType` so views can branch.

---

## Feature 2 — Personal IOU view on `/Expenses`

### Reuse — the math already exists

`IExpenseReportServiceRead.GetHoldedTimelineAsync(report)` already returns, for the
submitter's creditor account:

- `OwedToMember` — the Holded creditor balance = the **IOU**
- `OtherAmount` — the part of the IOU **not** explained by the member's expense reports
  (`max(0, owed − registeredReportTotal)`) → exactly the "external items" case
- `TotalPaid`, `PaidOn`

The balance is account-level (keyed on the member's supplier-account / contact id), so it is
the same regardless of which report it is read through.

### Only new data — individual payment rows

`GetCreditorStatusAsync` already *fetches* the per-contact `HoldedPayment` rows
(`repo.GetPaymentsByContactAsync`) but collapses them to `TotalPaid` / `LastPaymentDate`.
Surface the rows without adding an interface method:

- New small record `HoldedPaymentInfo(LocalDate Date, decimal Amount, string? DocumentType)`
  (Finance DTOs).
- Extend `HoldedCreditorStatus` with `IReadOnlyList<HoldedPaymentInfo> Payments` (map the
  rows it already holds).
- Extend `ExpenseHoldedTimeline` with `IReadOnlyList<HoldedPaymentInfo> Payments`
  (`GetHoldedTimelineAsync` maps `status.Payments` through).

**No new method on any `*ServiceRead` interface** → `IExpenseReportServiceRead`'s
`[SurfaceBudget(7)]` is preserved. (Decision flagged for sign-off.)

### Controller / view

`ExpensesController.Index` already loads the member's reports. Additionally:

- Pick the member's most-recent report with a non-null `HoldedContactId`; if one exists, call
  `GetHoldedTimelineAsync(thatReport)` to get balance + payments. If none, no IOU panel (the
  member isn't registered in Holded yet). *(Accepts one extra `GetForSubmitter` query inside
  the timeline call — negligible at ~500 users; avoids adding a budgeted read method.)*
- `ExpensesIndexViewModel` gains a nullable `HoldedSummary` (balance owed, total paid, last
  payment date, unexplained `OtherAmount`, the payments list).
- The view renders:
  - an **IOU summary card**: "Owed to you: €X · Paid to date: €Y · Last payment: date",
  - a **running ledger** interleaving reports and payments by date (desc): report rows show
    category / status / total + a "View" link; payment rows show "Payment received" + amount,
  - the note: *"Your Holded balance may include amounts unrelated to these reports
    (€{OtherAmount} unexplained) — the rows below won't necessarily sum to it."*

### Cross-section integrity

The controller calls only `IExpenseReportServiceRead` (own section). The Expenses *service*
calls `IHoldedFinanceService.GetCreditorStatusAsync` — a dependency that **already exists**
(documented tech debt in `Expenses.md` / `Finance.md`). No new cross-section call is added;
only the returned DTO grows. Holded boundary unchanged (read-only cache).

---

## Architecture — new surface (flagged for Peter's sign-off)

**Domain**
- `Enums/ExpenseLineType.cs` (new enum) · `ExpenseLine.LineType` property.

**Application**
- `Services/Expenses/Dtos/TravelReimbursementConfig.cs` (new config DTO).
- `Services/Finance/Dtos/HoldedPaymentInfo.cs` (new record) ·
  `HoldedCreditorStatus.Payments` · `ExpenseHoldedTimeline.Payments` (record extensions).
- `ExpenseLineDto.LineType` (DTO field).
- `IExpenseReportService` (mutation interface) gains
  `AddMileageLineWithResultAsync(...)` and `AddPerDiemLineWithResultAsync(...)`.
  *(No change to `IExpenseReportServiceRead`.)*

**Infrastructure**
- EF mapping for `ExpenseLine.LineType` + one migration.
- `HoldedFinanceService.GetCreditorStatusAsync` maps payment rows into the extended DTO.

**Web**
- `ExpensesController`: `POST {id}/Lines/AddMileage`, `POST {id}/Lines/AddPerDiem` + input
  models; `Index` builds `HoldedSummary`.
- `TravelReimbursementConfig` registration (DI + appsettings keys, env-overridable).
- Edit-view wizard UI (two collapsible mini-forms); Index-view summary card + ledger; line
  badges in Edit/Detail.

Layering respected throughout: DbContext → Repository → Service → Controller. Computation and
description formatting live in `ExpenseReportService` (Application), not the controller.

## Authorization

Unchanged. The wizards are owner-only Draft-line mutations (same gate as `AddLine`). The IOU
panel shows only the caller's own data (`Index` is already self-scoped). No new policy.

## Testing

- **Service:** mileage/per-diem amount rounding + description formatting; submit succeeds with
  only travel lines (no attachment); submit still fails when a *receipt* line lacks an
  attachment; per-diem rate selection by type.
- **Service:** `GetHoldedTimelineAsync` carries payment rows through; `HoldedSummary`
  composition (owed / paid / otherAmount / payments) for a member with and without a Holded
  contact.
- **Architecture:** existing `ExpensesArchitectureTests` / `FinanceArchitectureTests` continue
  to pin shape; no new cross-section violation introduced.
- No analyzer/SurfaceBudget changes.

## Docs to update on implementation

`docs/sections/Expenses.md` (line types, submit rule, IOU dashboard) and the freshness
trigger list; `docs/sections/Finance.md` (payment rows now exposed via the existing read DTO).

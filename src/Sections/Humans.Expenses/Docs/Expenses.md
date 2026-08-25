<!-- freshness:triggers
  src/Sections/Humans.Expenses/**
  src/Sections/Humans.Finance.Contracts/**
-->
<!-- freshness:flag-on-change
  Expense lifecycle, IBAN access rules, Holded sync, and resource-based authorization — review when Expenses services/entities/controllers/auth handlers change.
-->

# Expenses — Section Invariants

Members submit expense reports for reimbursement. Finance Admin reviews and approves; approval books the report into Holded (async). **`Approved` is terminal for the report** — payment happens externally (pull account balances, pay in the bank/Holded), and paid/unpaid is read back from the member's Holded creditor ledger, never stamped on the report. Full workflow and field-level detail in `src/Sections/Humans.Expenses/Docs/2026-05-10-expense-reports-design.md`; the ledger side — why the daybook is the single source and paid state is derived from it rather than stamped — is in `src/Sections/Humans.Holded/Docs/2026-08-10-holded-v2-migration-design.md` and `src/Sections/Humans.Finance/Docs/Finance.md`.

## Concepts

- An **ExpenseReport** is the top-level reimbursement request. It moves through a state machine (see Invariants) and is owned by the submitter until submitted.
- An **ExpenseLine** is one line item within a report — a description, amount, and optional attachment. Each line has a `LineType` (Receipt / Mileage / PerDiem / Invoice). **Receipt** lines require an attachment at submit time. **Mileage** lines are computed server-side as km × the configured per-km rate (€0.26/km, 2026 Spanish IRPF tax-exempt rate); **PerDiem** lines are computed as days × the Spanish day-trip (€26.67) or overnight (€53.34) rate. Both travel types have their amount and rate written into the description at creation time and never require an attachment. Rates live in `TravelReimbursementConfig` (bound from `appsettings.json` `TravelReimbursement` section; defaults are the 2026 values).
- An **Invoice** line is a supplier invoice from a payee who invoices the org (ZZP / autónomo contractor). It requires the invoice file attached at submit time and is what gets booked into Holded. Because these are reimbursements (not just payments), an invoice line can carry **proof rows**: Receipt lines with `ParentLineId` pointing at the invoice line, each with its own attachment, showing the underlying expenses behind the invoiced amount. Proof rows are reviewed with the report but are **excluded from `Total`** and **never pushed to Holded** (neither as document lines nor as attachments). The detail view shows the proof sum vs the invoice amount to the approvers — display-only, never enforced (VAT and fees mean the two need not match).
- **Travel lines can no longer be created.** The Add mileage / Add per diem forms and the `Lines/AddMileage` + `Lines/AddPerDiem` endpoints have been removed, so no new Mileage or PerDiem line can enter the system. The service-layer plumbing (`AddMileageLineWithResultAsync`, `AddPerDiemLineWithResultAsync`, `ExpenseLineType.Mileage`/`PerDiem`, `PerDiemKind`, `TravelReimbursementConfig`) is retained: pre-existing travel lines still render, submit, and total correctly, and the feature is re-enabled by restoring the two controller actions and the two `Edit.cshtml` forms.
- An **ExpenseAttachment** is a receipt or supporting document uploaded to a line item. Files are stored on disk via the shared `IFileStorage` abstraction (key `uploads/expense-attachments/{attachmentId}{.ext}`); the download route at `/Expenses/Attachment/{id}` re-authorizes the caller and streams bytes with the original filename via `Content-Disposition`. Metadata only in the DB.
- A **HoldedExpenseOutboxEvent** is an async task queued when a report is approved or its category tag changes — drained by `HoldedExpenseOutboxJob` to create/update Holded purchase documents.
- **Payment is external to this section.** No report state is ever stamped by a payment. Once a report is `Approved` (and booked into Holded as a payable), the treasurer pays the member's *creditor balance* — SEPA-file generation lives in the Finance section on `/Finance/Creditors` and operates on balances, never on reports (nobodies-collective/Humans#1134). This section only *shows* the ledger: paid/owed is derived from the member's Holded daybook lines (Finance section) via `IHoldedFinanceServiceRead.GetCreditorStatusAsync` (balance ≥ 0 = settled).
- **IBAN** — snapshotted from `Profile.Iban` at submit time into `ExpenseReport.PayeeIban`. Raw IBAN appears only in Holded API request bodies. All log/audit/error output goes through `IbanFormatter.Mask`.
- **Payable vs Total.** `Total` is the receipts total; `MaxAmount` is an optional cap a decider authorizes. `ExpenseReportDto.Payable` = `min(Total, MaxAmount)` and is the only amount payment math may use.

## Data Model

### ExpenseReport

**Table:** `expense_reports`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| SubmitterUserId | Guid | FK → Users (cross-domain, scalar only) |
| BudgetCategoryId | Guid | FK → Budget.BudgetCategory (cross-domain, scalar only) |
| BudgetYearId | Guid | FK → Budget.BudgetYear (cross-domain, scalar only) |
| Status | ExpenseReportStatus | see enum below |
| Note | string? | optional submitter-written **Subject**; becomes the Holded document `Description` |
| PayeeName | string | snapshotted at submit |
| PayeeIban | string | snapshotted at submit; MUST be masked in all log/audit output |
| Total | decimal | sum of line amounts — the receipts total, not what is paid |
| MaxAmount | decimal? | payout cap authorized by a decider; null = uncapped |
| SubmittedAt | Instant? | |
| CoordinatorEndorsedByUserId | Guid? | scalar FK |
| CoordinatorEndorsedAt | Instant? | |
| ApprovedByUserId | Guid? | scalar FK |
| ApprovedAt | Instant? | |
| HoldedDocId | string? | Holded purchase document id |
| HoldedContactId | string? | Holded contact id for this submitter; set on first push; links to creditor cache |
| HoldedSupplierAccountNum | int? | 40000000–41999999 supplier-account number (supplierRecord.num), cached at push time |
| LastRejectionReason / LastRejectedByUserId / LastRejectedAt | — | last rejection details |
| CreatedAt / UpdatedAt | Instant | |

**Aggregate-local navs:** `ExpenseReport.Lines` (includes `ExpenseLine.Attachment`).

### ExpenseLine

**Table:** `expense_lines`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| ExpenseReportId | Guid | FK → expense_reports |
| Description | string | |
| Amount | decimal | |
| LineType | ExpenseLineType | Receipt \| Mileage \| PerDiem \| Invoice; default Receipt |
| AttachmentId | Guid? | FK → expense_attachments |
| ParentLineId | Guid? | self-FK → expense_lines; non-null marks a proof row backing that Invoice line |
| SortOrder | int | |

### ExpenseAttachment

**Table:** `expense_attachments`

Metadata only; bytes on disk managed by the shared `IFileStorage` (key `uploads/expense-attachments/{Id}{Extension}`). See `memory/architecture/one-ifilestorage.md`.

### HoldedExpenseOutboxEvent

**Table:** `holded_expense_outbox_events`

Append-on-approve, drained by `HoldedExpenseOutboxJob`. Fields: `EventType` (CreateIncomingDoc | UpdateIncomingDocTag), `RetryCount`, `NextRetryAt`, `FailedPermanently`, `ProcessedAt`, `LastError`.

### ExpenseReportStatus

| Value | Description |
|-------|-------------|
| Draft | Being built; not yet submitted |
| Submitted | Submitted, awaiting coordinator endorsement (if required) or Finance review |
| CoordinatorEndorsed | Coordinator has endorsed; awaiting Finance review |
| Approved | Finance has approved; booked into Holded as a payable. **Terminal** — paid/unpaid is read from the creditor ledger, not the report |
| Withdrawn | Withdrawn by submitter |

## Routing

| Route | Method | Auth | Action |
|-------|--------|------|--------|
| `/Expenses` | GET | Authenticated | Submitter dashboard — shows member's reports, plus their Holded creditor-account statement (`AccountLedger`) once bound to a 40000000–41999999 account. The statement is the cached daybook lines for that account verbatim, both sides; it is not mixed with locally-held report rows. Unbound members get an explanatory note instead. |
| `/Expenses/New` | GET/POST | Authenticated | Create draft |
| `/Expenses/{id}` | GET | Authenticated (resource-based: owner + Finance) | Detail |
| `/Expenses/{id}/Edit` | GET/POST | Authenticated (owner, Draft only) | Edit draft |
| `/Expenses/{id}/Lines/New` | GET | Authenticated (owner, Draft only) | Focused add-line page (receipt default; `?type=Invoice` for the invoice flow). One submit creates line + attachment together |
| `/Expenses/{id}/Lines/{lineId}` | GET | Authenticated (owner) | Focused line page — edit description/amount, view/replace/remove the file, remove the line |
| `/Expenses/{id}/Lines/{lineId}/Proofs` | GET | Authenticated (owner) | Invoice line's proofs page — coverage vs invoice amount, list, add/remove proof rows |
| `/Expenses/{id}/Lines/*` | POST | Authenticated (owner) | Line mutations |
| `/Expenses/{id}/Submit` | POST | Authenticated (owner) | Submit |
| `/Expenses/{id}/Withdraw` | POST | Authenticated (owner, submitted states) | Withdraw |
| `/Expenses/{id}/Iban` | GET/POST | Authenticated (resource-based: self, FinanceAdmin with report context) | View/set IBAN |
| `/Expenses/Attachment/{id}` | GET | Authenticated (resource-based) | Download attachment |
| `/Expenses/Attachment/{id}/View` | GET | Authenticated (resource-based) | Same file inline (images + PDFs render in the tab; other types fall back to download) |
| `/Expenses/{id}/Endorse` | POST | Authenticated (coordinator, resource-based) | Endorse |
| `/Expenses/{id}/CoordinatorReject` | POST | Authenticated (coordinator, resource-based) | Coordinator reject |
| `/Expenses/Review` | GET | Authenticated | The review queue, scoped to the viewer — see Invariants. Admin shell for finance admins, member shell for everyone else |
| `/Expenses/{id}/Approve` | POST | FinanceAdminOrAdmin (resource-based) | Approve. Carries the max-amount and category-override inputs, so it is also how a wrong cap is corrected |
| `/Expenses/{id}/Reject` | POST | FinanceAdminOrAdmin (resource-based) | Finance reject |
| `/Expenses/{id}/HoldedRetry` | POST | FinanceAdminOrAdmin (resource-based, Approved only) | Re-queue a failed or backing-off Holded push |
| `/Users/Admin/{id}/RevealIban` | POST | AdminOnly | Reveal raw IBAN (audit-logged) |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Authenticated member | Submit, edit, withdraw own reports. View own reports. Set own IBAN. |
| Budget Coordinator | All member capabilities. Additionally: endorse or coordinator-reject reports in categories they coordinate. |
| FinanceAdmin, Admin | All coordinator capabilities. Additionally: the unscoped review queue, approve, finance-reject, category override, view Holded sync status, bind a submitter to a Holded creditor account (400000xx) on the expense detail view. |
| Admin | All FinanceAdmin capabilities. Additionally: reveal raw IBAN on admin user page (audit-logged). |

## Invariants

- A report follows the lifecycle: Draft → Submitted → (CoordinatorEndorsed →) Approved. `Approved` is terminal for the report — paid/unpaid is read from the member's Holded creditor ledger, never stamped on the report. Terminal alternate: Withdrawn (from Submitted/CoordinatorEndorsed/Approved). `ExpenseReportService` enforces all transitions; `IExpenseRepository` persists them atomically.
- A report cannot be submitted without at least one line. Every **Receipt** line (proof rows included) and every **Invoice** line must have an attachment at submit time; Mileage/PerDiem lines never require one (a pure-travel report submits with zero attachments).
- A proof row must reference an Invoice line on the same report, must itself be a Receipt line, and nests one level only (enforced at add time). Removing an invoice line removes its proof rows and their attachments. Proof rows never contribute to `Total`, never appear as Holded document lines, and their files are never uploaded to the Holded doc. Proof coverage vs the invoice amount is displayed to reviewers but never enforced.
- Travel lines (Mileage/PerDiem) cannot be edited after creation — their amounts are computed from their inputs and the receipt requirement is waived on that basis, so `UpdateLineAsync` rejects them. To change one, remove it and re-add it so the amount is recomputed. Only Receipt lines accept free-text description/amount edits.
- Only the deciders set `MaxAmount` — the coordinator on Endorse, a finance admin on Approve. Each decision form is prefilled with the current cap and its submitted value replaces it outright: blank clears the cap (the approve form's "Leave blank for no cap" is literal). The submitter can never set or see a cap input, and neither decider path may lower it below 0.01 or above 1,000,000. A cap is recorded in the decision's own `ExpenseEndorse` / `ExpenseApprove` audit entry, not a separate action.
- A cap entered wrongly is corrected on the approve form, which overrides whatever the coordinator authorized. There is no path to change it afterwards: approval queues the Holded push in the same transaction, and nothing about an approved report may diverge from the purchase document already in Holded.
- **Every decision is taken from `/Expenses/{id}`**, never from a queue row, so a report cannot be approved, rejected or endorsed without its lines and receipts on screen. `/Expenses/Review` lists and links; it carries no decision controls.
- `/Expenses/Review` is one queue for three audiences, scoped by the viewer: a finance admin sees every non-draft, non-withdrawn report; anyone else sees their own plus those booked to a budget category they coordinate. Drafts and withdrawals never appear — they belong to `/Expenses`.
- Payable is `min(Total, MaxAmount)` (`ExpenseReportDto.Payable`). Owed/paid math, the review queue, and the detail view all read the payable; `Total` renders only as the receipts total.
- A capped report pushes to Holded with one extra negative line ("Authorized maximum €X — adjustment", amount `MaxAmount − Total`, same account as the receipt lines) so the purchase document totals the payable. No adjustment line when the report is uncapped or the cap is at or above `Total`.
- `Profile.Iban` must be non-null at submit time. `PayeeIban` is snapshotted at that moment; later IBAN changes do not affect in-flight reports.
- The `/Expenses/{id}` **Payee** card renders the report's own `PayeeName` (unmasked legal name) and masked `PayeeIban` — the submit-time snapshot, i.e. who Holded actually pays. It is scoped to the submitter and finance admins (`ExpenseDetailViewModel.CanSeePayee`); a coordinator endorsing a report does not see it, because the legal name is unmasked and burner names are the norm elsewhere. The card never shows the *viewer's* own IBAN on someone else's report, and the Set/Change IBAN buttons render only for the submitter (the `Iban` action Forbids everyone else).
- `PayeeIban` (snapshotted) and `Profile.Iban` (current) MUST pass through `IbanFormatter.Mask` before appearing in any log, audit entry, or error message (enforced by convention; memory atom `memory/code/iban-mask-in-logs.md`).
- The coordinator endorsement step is required only if the report's category has at least one budget coordinator (`CategoryRequiresCoordinatorEndorsementAsync`). Finance Admin may approve directly from Submitted if no coordinator is assigned.
- Resource-based authorization (`IbanAccessRequirement` / `IbanAccessHandler`) gates raw IBAN access: self, FinanceAdmin with non-Draft/non-Withdrawn report context, or Admin on admin page.
- `HoldedExpenseOutboxJob` drains the `holded_expense_outbox_events` in order. A transient error increments `RetryCount` and sets `NextRetryAt` to `now + 2^(RetryCount+1)` minutes, so the event is held back rather than re-hitting Holded every minute; the tenth failure, and any permanent error, sets `FailedPermanently`. A written-off event is never silently dropped — it shows on `/Expenses/Review` as a banner and on `/Expenses/{id}` as the Holded sync card, where a finance admin re-queues it.
- Attachment uploads are stamped on `ExpenseAttachment.HoldedUploadedAt`, so a re-run after a partial failure or a re-queue resumes rather than adding a second copy of every earlier file to the same Holded document.
- The drain does nothing at all when no `HOLDED_API_KEY_V2` is configured (`IHoldedClient.IsConfigured`): every call would 401, which is a permanent error, so draining would write off the whole queue. The sync card reports that state as "Not configured" rather than "Queued".
- Holded API request bodies are the only code path that may contain a raw IBAN (not masked).

## Negative Access Rules

- Regular members **cannot** see other users' expense reports or attachments.
- Regular members **cannot** approve, reject, or endorse (unless they are a coordinator for the relevant category).
- Coordinators **cannot** approve — that requires FinanceAdmin/Admin.
- Submitters **cannot** set `MaxAmount` — there is no submitter-facing input and no service path that accepts one outside Endorse/Approve.
- FinanceAdmin **cannot** reveal a raw IBAN on the admin user page — that action is Admin-only.
- No role **can** transition a report backwards in the state machine (e.g., un-approve, un-submit).
- No code path **may** log or emit a raw IBAN in logs, audit entries, or error messages — only masked form via `IbanFormatter.Mask`.

## Triggers

- On **submit**: `Profile.Iban` and the profile legal name (`FirstName` + `LastName`) are snapshotted into `PayeeIban` / `PayeeName`. Audit entry `ExpenseSubmit` written.
- On **endorse**: any max amount the coordinator supplied is stored on the report and named in the `ExpenseEndorse` audit entry.
- On **approve**: `HoldedExpenseOutboxEvent` (CreateIncomingDoc) queued. Audit entry `ExpenseApprove` written, naming any max amount the finance admin supplied (which overrides the coordinator's).
- On **category override**: `HoldedExpenseOutboxEvent` (UpdateIncomingDocTag) queued. Audit entry `ExpenseCategoryOverride` written.
- On **IBAN reveal (admin page)**: `AuditAction.IbanReveal` written recording actor + target user.
- On **Holded push success**: audit entry `ExpenseHoldedPushed` written (actor: the job). On **write-off**: `ExpenseHoldedFailed`. On **finance re-queue**: `ExpenseHoldedRequeued` (actor: the admin). These carry the push history past outbox-row cleanup — the outbox columns themselves are not readable outside the database.
- **`HoldedExpenseOutboxJob`** runs every minute.
- **GDPR export** (`IUserDataContributor`): contributes `ExpenseReports` and `ExpenseAuditLog` slices. Chain-follows merge tombstones. (Historical `ExpenseSepaSent` / `ExpenseSepaReopened` / `ExpensePaid` audit entries are still surfaced for accounts that have them — the audit log is immutable; only the writers were removed.)
- **Article 17 erasure retains everything, by design.** `EraseForUserAsync` is a no-op; `ErasureDeclaration` maps both `ExpenseReports` and `ExpenseAuditLog` to the same fiscal-retention reason — a reimbursement is an accounting voucher, and Spanish law requires the books and supporting documents (payee legal name, IBAN unmasked in the row, amounts, dates, notes, approval trail, receipts) be kept 6 years (Código de Comercio Art. 30) / 4 years for tax purposes (Ley 58/2003 Art. 66), GDPR Art. 17(3)(b).

## Cross-Section Dependencies

- **Budget**: `IBudgetService.GetCategoryByIdAsync` — category metadata and coordinator team resolution. `ITeamService.GetEffectiveBudgetCoordinatorTeamIdsAsync` — coordinator-scope check.
- **Teams**: `ITeamService.IsUserCoordinatorOfTeamAsync` — coordinator endorsement gate.
- **Profiles**: `IProfileService.GetProfileAsync` — IBAN snapshot at submit time; masked IBAN for GDPR export.
- **Users/Identity**: `IUserServiceRead.GetUserInfoAsync` / `GetUserInfosAsync` — display names for Holded contact name. `IUserService.GetMergedSourceIdsAsync` — GDPR merge-tombstone chain-follow.
- **AuditLog**: `IAuditLogService.LogAsync` — all lifecycle transitions logged. The section never reads audit itself: `/Expenses/{id}` shows the report's history by emitting `<vc:audit-log entity-type="ExpenseReport" entity-id="…">` and letting the AuditLog section own the read and the render. Everyone who may open the report sees it — the entries are that report's own history. The GDPR export does not re-read audit here.
- **Finance**: `IHoldedFinanceServiceRead.GetCreditorStatusAsync` and `IHoldedFinanceServiceRead.GetCreditorLedgerAsync` — creditor status/statement derived from the cached Holded daybook ledger, for the submitter's owed/paid timeline.
- **Admin (Users section)**: `/Users/Admin/{id}/RevealIban` lives in `UsersAdminController` and calls `IProfileService.GetProfileAsync` + `IAuditLogService.LogAsync`.

## Architecture

**Owning services:** `ExpenseReportService`
**Owned tables:** `expense_reports`, `expense_lines`, `expense_attachments`, `holded_expense_outbox_events`
**Status:** (A) Migrated (2026-05-10). Moved into its own project `src/Sections/Humans.Expenses` at G5 (nobodies-collective/Humans#866); the cross-section leaf `Humans.Expenses.Contracts` later folded into the project's own `Contracts/` folder (ruling 44).

- `ExpenseReportService` lives in `Humans.Expenses.Services` and depends only on Application-layer abstractions. `IExpenseReportServiceRead` is the public cross-section read surface in `Contracts/` (no `[SurfaceBudget]` — budgets are off for the duration of the #866 migration); `IExpenseReportService` adds the mutations. The public surface is `Section` plus everything under `Contracts/`: `IExpenseReportBackgroundProcessor` (`DrainHoldedOutboxAsync`, how `HoldedExpenseOutboxJob` reaches the section), `IExpenseReportServiceRead`, and its DTO/enum graph. The job moved into this project's `Jobs/` folder at G5 lane 5b-5; only its DI registration and roll-call entry stay in Shell, because recurring jobs are named by concrete type there.
- `ExpenseRepository` (impl `src/Sections/Humans.Expenses/Data/ExpenseRepository.cs`, §15b Singleton + `IDbContextFactory<ExpensesDbContext>`) is the only file that touches expense tables via `DbContext`.
- **DbContext** — `ExpensesDbContext` (`src/Sections/Humans.Expenses/Data/ExpensesDbContext.cs`, `internal sealed`) is the section's own per-section EF model (nobodies-collective/Humans#858 split): maps only `expense_reports`, `expense_lines`, `expense_attachments`, `holded_expense_outbox_events`, with its own `__EFMigrationsHistory_Expenses` table and migrations under `Data/Migrations/` (baseline `20260715101338_BaselineExpenses`). Same database and connection as `HumansDbContext` — the split partitions the EF model, not the database.
- **DI registration** lives in `Section.Register` at the project root, discovered by Shell through `ISection`. It also registers the section's `ExpenseReportStatus` badge colours into `EnumBadgeMap` rather than Base holding a literal row per section enum.
- **Decorator decision — no caching decorator.** Expense data is mutable and user-specific; low-traffic at ~500 users.
- **Cross-domain navs** — none declared. All cross-section linkage is scalar FK only.
- **Cross-section calls** route through `IBudgetService`, `ITeamService`, `IProfileService`, `IUserService`, `IAuditLogService`, `IHoldedFinanceServiceRead` (Finance, Feature 2).
- **Architecture test** — `tests/Humans.Expenses.Tests/ExpensesArchitectureTests.cs` pins the shape.

### Feature 2 — Holded contact enrichment and payment status

When a report is pushed to Holded (`HoldedExpenseOutboxJob`), the submitter's Holded contact is upserted with: legal name as `Name`, trade name only for "burner" identities (legal name required first), `CustomId` = `UserId`, `type = creditor`, IBAN. The returned contact id and resolved `supplierRecord.num` are stored on `ExpenseReport.HoldedContactId` / `HoldedSupplierAccountNum` for subsequent creditor look-ups.

The submitter's expense detail view (`/Expenses/{id}`) shows a **payment status timeline**: registered / owed / settled, derived from `GetCreditorStatusAsync`. Paid detection reads the nightly-cached creditor daybook (balance = Σdebit − Σcredit ≥ 0 = settled) — zero live Holded calls on page load.

The submitter dashboard (`/Expenses`) shows the member's Holded creditor-account statement (`GetCreditorLedgerAsync`) — the cached daybook lines for their 400000xx account, rendered verbatim. It deliberately does **not** blend local `ExpenseReport` rows into that table: doing so nets a locally-held claim against a Holded debit while the Holded credit pairing with it is never shown, which is why the earlier "IOU ledger" card could not reconcile and was removed.

**`TravelReimbursementConfig`** (bound from `appsettings.json` → `TravelReimbursement` section, registered in `Section.Register`) holds the 2026 Spanish IRPF tax-exempt rates: 0.26 €/km, 26.67 €/day (day trip), 53.34 €/day (overnight). Defaults are the live 2026 values; the section works without explicit configuration.

<!-- freshness:triggers
  src/Sections/Humans.Expenses/**
  src/Sections/Humans.Finance.Contracts/**
  src/Sections/Humans.Users/Controllers/UsersAdminController.cs
  src/Sections/Humans.Backdoor/Controllers/BackdoorFinanceController.cs
  src/Sections/Humans.Holded/Contracts/**
-->
<!-- freshness:flag-on-change
  Expense lifecycle, IBAN access rules, Holded sync, and resource-based authorization — review when Expenses services/entities/controllers/auth handlers change.
-->

# Expenses — Section Invariants

Members submit expense reports for reimbursement. Finance Admin reviews and approves; approval books the report into Holded (async). **`Approved` is terminal for the report** — payment happens externally (pull account balances, pay in the bank/Holded), and paid/unpaid is read back from the member's Holded creditor ledger, never stamped on the report. Current health and target shape are in `health.md`; the 2026-05-10 design record (`2026-05-10-expense-reports-design.md`) is historical; the ledger side — why the daybook is the single source and paid state is derived from it rather than stamped — is in `src/Sections/Humans.Holded/Docs/2026-08-10-holded-v2-migration-design.md` and `src/Sections/Humans.Finance/Docs/Finance.md`.

## Concepts

- An **ExpenseReport** is the top-level reimbursement request. It moves through a state machine (see Invariants) and is owned by the submitter until submitted.
- An **ExpenseLine** is one line item within a report — a description, amount, and optional attachment. Each line has a `LineType` (Receipt / Mileage / PerDiem / Invoice). **Receipt** lines require an attachment at submit time. **Mileage** lines are computed server-side as km × the configured per-km rate (€0.26/km, 2026 Spanish IRPF tax-exempt rate); **PerDiem** lines are computed as days × the Spanish day-trip (€26.67) or overnight (€53.34) rate. Both travel types have their amount and rate written into the description at creation time and never require an attachment. Rates live in `TravelReimbursementConfig` (bound from `appsettings.json` `TravelReimbursement` section; defaults are the 2026 values).
- An **Invoice** line is a supplier invoice from a payee who invoices the org (ZZP / autónomo contractor). It requires the invoice file attached at submit time and is what gets booked into Holded. Because these are reimbursements (not just payments), an invoice line can carry **proof rows**: Receipt lines with `ParentLineId` pointing at the invoice line, each with its own attachment, showing the underlying expenses behind the invoiced amount. Proof rows are reviewed with the report but are **excluded from `Total`** and **never pushed to Holded** (neither as document lines nor as attachments). The detail view shows the proof sum vs the invoice amount to the approvers — display-only, never enforced (VAT and fees mean the two need not match).
- **Travel lines can no longer be created.** The Add mileage / Add per diem forms and the `Lines/AddMileage` + `Lines/AddPerDiem` endpoints have been removed, so no new Mileage or PerDiem line can enter the system. The service-layer plumbing (`AddMileageLineWithResultAsync`, `AddPerDiemLineWithResultAsync`, `ExpenseLineType.Mileage`/`PerDiem`, `PerDiemKind`, `TravelReimbursementConfig`) is retained: pre-existing travel lines still render, submit, and total correctly, and the feature is re-enabled by restoring the two controller actions and the two `Edit.cshtml` forms.
- An **ExpenseAttachment** is a receipt or supporting document uploaded to a line item. Files are stored on disk via the shared `IFileStorage` abstraction (key `uploads/expense-attachments/{attachmentId}{.ext}`); the download route at `/Expenses/Attachment/{id}` re-authorizes the caller and streams bytes with the original filename via `Content-Disposition`. Metadata only in the DB.
- Attachment filenames are reduced to their basename and must fit the 255-character database column; an overlong name is rejected before the file is stored.
- Audit descriptions use the member ID if display-name enrichment fails or is cancelled after an edit commits; the audit and attachment cleanup still run, and the successful edit stays successful.
- Removing a line's attachment writes its audit entry before deleting the stored file; the file delete ignores request cancellation, and a failed delete only logs.
- Replacing an attachment inserts the new metadata, changes the line link and removes superseded metadata in one save. The old file is deleted only after the upload audit succeeds, independently of request cancellation; failed cleanup logs without undoing the replacement. A failed save or audit restores the previous metadata and linkage before cleaning up the new file.
- A **HoldedExpenseOutboxEvent** is an async task queued when a report is approved or its category tag changes — drained by `HoldedExpenseOutboxJob` to create/update Holded purchase documents.
- **Payment is external to this section.** No report state is ever stamped by a payment. Once a report is `Approved` (and booked into Holded as a payable), the treasurer pays the member's *creditor balance* — SEPA-file generation lives in the Finance section on `/Finance/Creditors` and operates on balances, never on reports (nobodies-collective/Humans#1134). This section only *shows* the ledger: paid/owed is derived from the member's Holded daybook lines (Finance section) via `IHoldedFinanceServiceRead.GetCreditorStatusAsync` (balance ≥ 0 = settled).
- **IBAN** — snapshotted from `Profile.Iban` at submit time into `ExpenseReport.PayeeIban`. Raw IBAN appears only in Holded API request bodies. All log/audit/error output goes through `IbanFormatter.Mask`.
- **Payable vs Total.** `Total` is the *accepted* total — top-level line amounts minus any line a reviewer has declined; `MaxAmount` is an optional cap a decider authorizes. `ExpenseReportDto.Payable` = `min(Total, MaxAmount)` and is the only amount payment math may use. A decline never touches the cap: €500 of receipts, a €400 cap, a €50 line declined → accepted total €450, payable €400.
- A **declined line** is one a reviewer marked unacceptable, with a reason. It stays on the report for the record and stays visible, with its reason, to the submitter, but is out of `Total` and `Payable` and is never pushed to Holded. A decline is undoable by a reviewer and is cleared when the submitter removes the line or replaces its file. Proof rows are not declinable; declining an Invoice line shows its proofs as declined for display only.

## Data Model

### ExpenseReport

**Table:** `expense_reports`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| SubmitterUserId | Guid | FK → Users (cross-domain, scalar only) |
| HoldedAccountNumber | int | The Holded expense account the report books to, chosen on the New/Edit form from `IHoldedFinanceServiceRead.ListExpenseAccountsAsync(activeOnly: true)`. `0` only on rows created before the column existed; those still book through their category's mapping |
| HoldedAccountId | string? | Holded's id for `HoldedAccountNumber`, captured when the account was chosen; what the push books `items[].account` to |
| BudgetCategoryId | Guid? | FK → Budget.BudgetCategory (cross-domain, scalar only). Derived from the account through Finance's category map; **null when the account is not a budget category** |
| BudgetYearId | Guid? | FK → Budget.BudgetYear (cross-domain, scalar only). Null whenever `BudgetCategoryId` is |
| Status | ExpenseReportStatus | see enum below |
| Note | string? | submitter-written **Subject** (the project name); **required non-blank at submit** (`Expenses_Validation_SubjectRequired`) — Expenses has no link to a Preapprovals grant, so there is no grant-label prefill and the rule applies to every report; stays in Humans — the Holded docs' `Description` is `ER: ` plus each line's description, with the report reference in the doc's `Notes` |
| PayeeName | string | snapshotted at submit |
| PayeeIban | string | snapshotted at submit, refreshable from `/Expenses/{id}/Iban` until approval; MUST be masked in all log/audit output |
| Total | decimal | sum of the top-level line amounts a reviewer has not declined — the accepted total, not what is paid |
| MaxAmount | decimal? | payout cap authorized by a decider; null = uncapped |
| SubmittedAt | Instant? | |
| CoordinatorEndorsedByUserId | Guid? | scalar FK |
| CoordinatorEndorsedAt | Instant? | |
| ApprovedByUserId | Guid? | scalar FK |
| ApprovedAt | Instant? | |
| HoldedDocId | string? | **Legacy** single-doc pushes only (pre per-line docs); never set by new pushes — read via `ExpenseReportDto.HoldedDocIds`, which folds it with the per-line ids |
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
| HoldedDocId | string? | the Holded purchase doc this line booked to (one doc per bookable line); null until pushed, on proof rows, on declined lines, and on lines the cap zeroed out |
| DeclinedAt | Instant? | when a reviewer declined the line; null while accepted. The declined flag (`ExpenseLine.Declined` / `ExpenseLineDto.Declined`) is derived from it, so there is no separate column to disagree |
| DeclinedReason | string? | the reviewer's reason (≤1000 chars); shown to the submitter |
| DeclinedByUserId | Guid? | the reviewer; scalar FK, never shown to the submitter (the audit entry names the actor) |

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
| `/Expenses/New` | GET/POST | Authenticated; the **For member** picker only for FinanceAdminOrAdmin | Create draft. Default submitter is self; a finance admin may file for another member, who owns the report from that moment |
| `/Expenses/{id}` | GET | Authenticated (resource-based: owner + Finance) | Detail |
| `/Expenses/{id}/Edit` | GET/POST | Authenticated (resource-based `Edit`: owner in Draft, FinanceAdmin in Draft/Submitted/CoordinatorEndorsed) | Edit header |
| `/Expenses/{id}/Lines/New` | GET | Authenticated (resource-based `Edit`) | Focused add-line page (receipt default; `?type=Invoice` for the invoice flow). One submit creates line + attachment together |
| `/Expenses/{id}/Lines/{lineId}` | GET | Authenticated (resource-based `Edit`, or the owner read-only) | Focused line page — edit description/amount, view/replace/remove the file, remove the line |
| `/Expenses/{id}/Lines/{lineId}/Proofs` | GET | Authenticated (resource-based `Edit`, or the owner read-only) | Invoice line's proofs page — coverage vs invoice amount, list, add/remove proof rows |
| `/Expenses/{id}/Lines/*` | POST | Authenticated (resource-based `Edit`) | Line mutations |
| `/Expenses/{id}/Submit` | POST | Authenticated (resource-based `Submit`: owner or FinanceAdmin, Draft only) | Submit |
| `/Expenses/{id}/Withdraw` | POST | Authenticated (owner, submitted states) | Withdraw — submitter only, never on behalf |
| `/Expenses/{id}/Iban` | GET/POST | Authenticated (the report's submitter at any status, or whoever the `Edit` grant covers) | View/set the **submitter's** profile IBAN, and refresh this report's `PayeeIban` while it is pre-approval |
| `/Expenses/Attachment/{id}` | GET | Authenticated (resource-based) | Download attachment |
| `/Expenses/Attachment/{id}/View` | GET | Authenticated (resource-based) | Same file inline (images + PDFs render in the tab; other types fall back to download) |
| `/Expenses/{id}/Endorse` | POST | Authenticated (coordinator or FinanceAdmin, resource-based) | Endorse |
| `/Expenses/{id}/CoordinatorReject` | POST | Authenticated (coordinator, resource-based) | Coordinator reject |
| `/Expenses/{id}/Lines/{lineId}/Decline` | POST | Authenticated (resource-based `DeclineLine`: finance admin while Submitted/CoordinatorEndorsed, the category's coordinator while Submitted) | Decline one line, with a required reason |
| `/Expenses/{id}/Lines/{lineId}/UndoDecline` | POST | Same as Decline | Undo a decline |
| `/Expenses/Review` | GET | Authenticated | The review queue, scoped to the viewer — see Invariants. Member layout for everyone, admins included |
| `/Expenses/{id}/Approve` | POST | FinanceAdminOrAdmin (resource-based) | Approve. Carries the max-amount and account-override inputs, so it is also how a wrong cap is corrected |
| `/Expenses/{id}/Reject` | POST | FinanceAdminOrAdmin (resource-based) | Finance reject |
| `/Expenses/{id}/HoldedRetry` | POST | FinanceAdminOrAdmin (resource-based, Approved only) | Re-queue a failed or backing-off Holded push |
| `/Users/Admin/{id}/RevealIban` | POST | AdminOnly | Reveal raw IBAN (audit-logged) |

The submitter-facing `/Expenses/{id}/Edit` and `/Expenses/{id}/Lines/New` views use `ExpensesResource` for every label, status, action, confirmation, explanatory message, and accessibility label in all supported cultures.
The submitter-facing `/Expenses/New` form uses `ExpensesResource` for all labels, actions, placeholder text, guidance, and accessibility labels in all supported cultures.
The submitter-facing `/Expenses/{id}` detail view localizes report labels and status, attachments, payment status, submission guidance, withdrawal confirmation, and history headings in all supported cultures, reusing shared labels where appropriate. The submitter-facing IBAN form also localizes its example placeholder. Member form annotations use shared required, length, and numeric-range messages in all six cultures; their validation limits are unchanged. Finance/coordinator-only controls retain their operator exemption.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Authenticated member | Submit, edit, withdraw own reports. View own reports. Set own IBAN. |
| Budget Coordinator | All member capabilities. Additionally: endorse or coordinator-reject reports in categories they coordinate, and decline/undo-decline a line on them while Submitted. |
| FinanceAdmin, Admin | All coordinator capabilities. Additionally: the unscoped review queue, approve, finance-reject, decline/undo-decline a line while Submitted or CoordinatorEndorsed, category override, view Holded sync status, bind a submitter to a Holded creditor account (400000xx) on the expense detail view. **On a member's behalf:** file a report, edit its header/lines/attachments while it is Draft, Submitted or CoordinatorEndorsed, submit it from Draft, and set the member's profile IBAN. Never withdraw — that stays the submitter's. |
| Admin | All FinanceAdmin capabilities. Additionally: reveal raw IBAN on admin user page (audit-logged). |

## Invariants

- Withdrawal POST and its detail-page button use `ExpenseReportOperation.Withdraw`: only the submitter of a Submitted, CoordinatorEndorsed, or Approved report may withdraw. Draft and already Withdrawn reports return 403 before mutation; FinanceAdmin/Admin cannot withdraw another person's report.

- The Holded outbox drain is marked as an external write; HTTP mutations must not pass browser cancellation into its vendor operations. Report transitions only queue the work.

- Successful Holded push audit summaries are bounded to 3500 UTF-16 units without splitting surrogate pairs, so Unicode receipt descriptions remain valid for database encoding.
- Holded outbox retries and write-offs bound saved error summaries to 2000 UTF-16 units without splitting surrogate pairs. Permanent-failure audits use the same bounded summary; logs retain the full exception.

- Member report-list status badges reuse the section’s existing labels in all six cultures and the shared badge-color registry; stored status values and workflow rules are unchanged.

- Report-subject previews retain their 60-character member-list and 80-character review-queue limits without splitting UTF-16 surrogate pairs; full notes remain unchanged.

- Shared table currency and number cells use the selected UI culture; numeric sort values stay invariant.

- Review queues retain reports whose submitter is missing or has no usable name. Their name cells use the view’s shared localized Unknown label; server name projections contain only actual nonblank burner names. The member-accessible queue uses the section's six-culture breadcrumb navigation label and the shared table's localized department-filter label and all-options caption.
- Report detail/edit, new-line, line/proof, IBAN, and review GETs propagate request cancellation through existing token-capable report, profile, timeline, creditor, name, and failed-push reads. Shared report/IBAN helpers keep non-cancellable defaults for mutation callers; tokenless Budget and authorization interfaces are unchanged.
- New-report form user resolution and attachment download/inline reads honor request cancellation. Abandoned reads do not become error flashes or 404s; ordinary failures and attachment denial responses retain their existing behavior.

- The Expenses index GET passes request cancellation to token-aware member, report, coordinator queue and cached creditor reads. Cancellation propagates rather than becoming a load-failure toast and empty page; existing tokenless Budget reads retain their contract.

- A report follows the lifecycle: Draft → Submitted → (CoordinatorEndorsed →) Approved. `Approved` is terminal for the report — paid/unpaid is read from the member's Holded creditor ledger, never stamped on the report. Terminal alternate: Withdrawn (from Submitted/CoordinatorEndorsed/Approved). `ExpenseReportService` enforces all transitions; `IExpenseRepository` persists them atomically.
- A report cannot be submitted without at least one line. Every **Receipt** line (proof rows included) and every **Invoice** line must have an attachment at submit time; Mileage/PerDiem lines never require one (a pure-travel report submits with zero attachments).
- New lines append after the highest remaining `SortOrder` within their report (starting at zero). Removing a line preserves surviving positions; a later append never reuses a surviving line's position.
- A proof row must reference an Invoice line on the same report, must itself be a Receipt line, and nests one level only (enforced at add time). Removing an invoice line removes its proof rows and their attachments. Proof rows never contribute to `Total`, never appear as Holded document lines, and their files are never uploaded to the Holded doc. Proof coverage vs the invoice amount is displayed to reviewers but never enforced.
- Once line removal commits, its on-behalf audit and attachment-file cleanup ignore request cancellation. The audit precedes cleanup; storage failures are logged without reversing the committed deletion.
- Travel lines (Mileage/PerDiem) cannot be edited after creation — their amounts are computed from their inputs and the receipt requirement is waived on that basis, so `UpdateLineAsync` rejects them. To change one, remove it and re-add it so the amount is recomputed. Only Receipt lines accept free-text description/amount edits.
- Only the deciders set `MaxAmount` — the coordinator on Endorse, a finance admin on Approve. Each decision form is prefilled with the current cap and its submitted value replaces it outright: blank clears the cap (the approve form's "Leave blank for no cap" is literal). The submitter can never set or see a cap input, and neither decider path may lower it below 0.01 or above 1,000,000. A cap is recorded in the decision's own `ExpenseEndorse` / `ExpenseApprove` audit entry, not a separate action.
- A cap entered wrongly is corrected on the approve form, which overrides whatever the coordinator authorized. There is no path to change it afterwards: approval queues the Holded push in the same transaction, and nothing about an approved report may diverge from the purchase document already in Holded.
- **A rejection does not clear `MaxAmount`, by design.** Neither `FinanceRejectAsync` nor `CoordinatorRejectAsync` touches the cap, so a report rejected at €40 against a €120 total goes back to the member still carrying the €40 cap, and `Payable` renders it. The cap is the last figure a decider authorized and it stands until a decider changes it — on the next Endorse or Approve form, both of which are prefilled with the current value and replace it outright (blank clears it). Nothing else may: the submitter has no cap input at any status.
- **Every decision is taken from `/Expenses/{id}`**, never from a queue row, so a report cannot be approved, rejected or endorsed without its lines and receipts on screen. `/Expenses/Review` lists and links; it carries no decision controls.
- `/Expenses/Review` is one queue for three audiences, scoped by the viewer: a finance admin sees every non-draft, non-withdrawn report; anyone else sees their own plus those booked to an active-year category whose team they coordinate, by the same `IsUserCoordinatorOfTeamAsync` rule that grants endorse — whoever can sign a claim off sees it. Drafts and withdrawals never appear — they belong to `/Expenses`.
- Payable is `min(Total, MaxAmount)` (`ExpenseReportDto.Payable`), where `Total` is the accepted total. Owed/paid math, the review queue, and the detail view all read the payable; `Total` renders only as the accepted total.
- **Declining a line is a reviewer decision with a bounded window.** `ExpenseReportOperation.DeclineLine` (and the service re-check in `ExpenseReportService`) grants it to whoever holds Endorse or Approve while it can still be exercised: a finance admin while the report is `Submitted` or `CoordinatorEndorsed`, the coordinator of the report's category while it is `Submitted`. Never the submitter, never a random member, never in Draft/Approved/Withdrawn. A reason is required. Only top-level lines can be declined (proof rows are display-only); declining an already-declined line, or undoing a line that is not declined, is refused. The repository moves the stored `Total` by the line's amount on decline/undo; editing a declined line's amount does not move it, and removing a declined line does not subtract it a second time.
- **A decline is answered by the submitter, not erased by them.** After a rejection the report is a Draft the submitter edits: they see each declined line and its reason on `/Expenses/{id}`, the edit page and the line page. Removing the line, or replacing its file, clears the decline (and puts a replaced line back in `Total`); a replacement clears it only after the upload and its audit succeed, so a rolled-back upload leaves the line declined; editing its description/amount does not. Declines otherwise survive rejection and resubmission.
- **The Holded push books accepted lines only.** `PayableAllocation` skips declined lines, so they get no document, no upload, no "not booked" note, and no share of the cap. A report whose lines are all declined has `Total` 0 and nothing to book; nothing forbids approving it.
- **The push books one Holded purchase document per bookable line**, in `SortOrder`, each carrying that line's description (as `ER: ` + description, with the report reference in the doc's notes), amount, category account, and its one attachment. Proof rows get neither doc lines nor uploads. Reports pushed before per-line docs keep their single report-level `HoldedDocId`; a re-queue of one resumes onto that doc (remaining uploads + approve) and never mints per-line docs beside it.
- **The authorized cap allocates greedily in `SortOrder`** (`PayableAllocation` — the push, the detail view, and the audit message all read the same allocation): each line books in full until the payable runs out. The line the cap lands inside books its receipt at face value plus a negative "Authorized maximum €X — adjustment" line on the same account, so that doc totals what the cap lets it book while still matching its attached receipt. A line entirely past the cap gets **no doc and no upload** — Holded holds no record of it — and the skip is named in the `ExpenseHoldedPushed` audit entry and badged on the detail view's lines table (trimmed lines show "books X of Y" there too, from endorsement onward). Where the reduction lands is presentation only: the whole report books to one category account. No adjustment or skip when the report is uncapped or the cap is at or above `Total`.
- `Profile.Iban` must be non-null at submit time. `PayeeIban` is snapshotted at that moment. A later profile-level IBAN change (`/Profile`) never touches an in-flight report. A change made through **the report's own** `/Expenses/{id}/Iban` page does: while the report is `Submitted` or `CoordinatorEndorsed` it rewrites that report's `PayeeIban` too, because the point of the page is fixing the payment before it goes out. `Draft` has no snapshot to fix (submit takes it), and `Approved` / `Withdrawn` are never touched — approval has already queued the Holded push. Clearing the IBAN is **refused** on `Submitted` / `CoordinatorEndorsed`: a report awaiting payment needs one, and half-applying would leave profile and snapshot disagreeing about who gets paid. The form stops offering the removal on those statuses too (`ExpenseIbanViewModel.CanRemoveIban`), so the page never invites what the service will reject.
- The `/Expenses/{id}` **Payee** card renders the report's own `PayeeName` (unmasked legal name) and masked `PayeeIban` — the submit-time snapshot, i.e. who Holded actually pays. It is scoped to the submitter and finance admins (`ExpenseDetailViewModel.CanSeePayee`) — the only audience for the IBAN. A coordinator endorsing a report does not see this card. Separately, a **Submitter** card shows the submitter's burner name and legal name (snapshot once submitted, else current profile `FirstName` + `LastName`, read through `IUserService`) to anyone holding the `View` grant — the submitter, the category coordinator and finance admins (`ExpenseDetailViewModel.CanSeeNames`); it never carries an IBAN. The card never shows the *viewer's* own IBAN on someone else's report: what it renders is always the *report submitter's* state, and the Set/Change IBAN buttons render for the submitter and for whoever the `Edit` grant covers (the `Iban` action Forbids everyone else).
- **A report belongs to its `SubmitterUserId`; whoever is changing it is only the actor.** Everything payee-shaped reads the submitter's profile, never the acting user's — `SubmitAsync` snapshots the submitter's IBAN and legal name, and the `Iban` page writes the submitter's profile. The actor appears only where it means the actor: `ExpenseAttachment.UploadedByUserId` and the audit entries.
- IBAN edits require two ASCII check digits and ASCII account letters/digits before checksum validation. Malformed Unicode input returns the existing invalid-format feedback without changing the profile or report snapshot.
- **A finance admin's edit does not send the report back a step.** The on-behalf edit window covers Draft, Submitted and CoordinatorEndorsed; correcting an endorsed report leaves the endorsement standing. `Approved` and `Withdrawn` are closed to everyone — approval has already queued the Holded push. `ExpenseReportAuthorizationHandler` decides this and `ExpenseReportService.GetEditableReportAsync` re-checks it; the repository does not re-decide status (that is why `ExpenseRepository.UpdateDraftAsync` has no Draft filter of its own).
- **A report names a Holded account; its category is derived, never chosen.** The New/Edit form and the approval override all offer Finance's active expense accounts (`ListExpenseAccountsAsync(activeOnly: true)`, grouped into budget-category accounts and other accounts). `ExpenseReportService.ResolveBookingAsync` turns the posted number into an `ExpenseBooking`: a budget-category account carries the category Finance's map (`GetCategoryMapAsync`) binds it to, plus that category's year; any other account carries no category and no year. An account not in the active list is refused (`Expenses_Validation_AccountNotFound`); a category account whose category is gone is refused (`Expenses_Validation_CategoryNotFound`).
- **A header edit never moves a report between budget years.** A `Draft` resolves its category through the *active* year and is stamped with it — it is not booked anywhere yet. Once `Submitted` or `CoordinatorEndorsed`, the report is booked: `ResolveBookingAsync` is pinned to **the report's own** `BudgetYearId`, so a category account from any other year is refused (`Expenses_Validation_CategoryDifferentBudgetYear`) and `BudgetYearId` is left as it was. Without this, editing a report from a previous year silently reclassified its accounting into the current one. The account override at approval runs through the same pinned resolution; a report with no year (an account outside the budget) resolves through the active year.
- **A report outside the budget skips coordinator endorsement.** With `BudgetCategoryId` null there is no coordinator team: `ExpenseReportAuthorizationHandler` grants no coordinator rights on it, the service refuses endorse/coordinator-reject on it, and it is absent from every coordinator queue. It goes `Submitted` → Finance approval directly.
- `PayeeIban` (snapshotted) and `Profile.Iban` (current) MUST pass through `IbanFormatter.Mask` before appearing in any log, audit entry, or error message (enforced by convention; memory atom `memory/code/iban-mask-in-logs.md`). **One exception:** an `IbanSet` audit entry written by somebody other than its subject carries the IBAN unmasked, because tracing a wrongly-entered account back to who typed it needs the value typed (`memory/code/audit-pii-subject-allowed.md`). A member setting their own stays masked.
- **Coordinator endorsement is the expected route, not a gate.** When the report's category has a budget coordinator, the intended path is Submitted → the coordinator endorses (they know their department) → CoordinatorEndorsed → the finance admin approves and pays. But a finance admin may approve straight from `Submitted` at any time, coordinator or not: paying is theirs to decide and sometimes it is a rush. Nothing in the code refuses it, deliberately — the `ExpenseApprove` audit entry records who decided and from which status, which is the control. `CategoryRequiresCoordinatorEndorsementAsync` computes whether a category has a coordinator and has no production caller; it is the seam for surfacing "waiting on the coordinator" vs "waiting on finance" in the review queue, not an enforcement point.
- **Raw-IBAN access has no resource handler, deliberately.** The two gates are declarative and narrow: `[Authorize(Policy = AdminOnly)]` on `/Users/Admin/{id}/RevealIban`, which is the only page that reveals a raw account number and audits every reveal, and `CanSetReportIbanAsync` on `/Expenses/{id}/Iban`, which renders masked and gates *setting* the value on submitter-or-`Edit`. A resource handler (`IbanAccessRequirement` / `IbanAccessHandler`) once existed for this and was deleted: it duplicated `AdminOnly`, nothing ever constructed the requirement, and its finance grant did not match what the `/Iban` page actually does. See `authorization.md`.
- The internal `ExpenseHoldedPublisher` owns the resumable contact/document/attachment conversation, using Expenses' repository and Finance/Holded contracts. The internal `ExpenseReportBackgroundProcessor` owns outbox selection, retry/backoff, write-off, and the configured-key gate through the existing `IExpenseReportBackgroundProcessor` contract; report transitions remain in `ExpenseReportService`.
- `HoldedExpenseOutboxJob` drains the `holded_expense_outbox_events` in order. A transient error increments `RetryCount` and sets `NextRetryAt` to `now + 2^(RetryCount+1)` minutes, so the event is held back rather than re-hitting Holded every minute; the tenth failure, and any permanent error, sets `FailedPermanently`. A written-off event is never silently dropped — it shows on `/Expenses/Review` as a banner listing each affected report (and a flagged "Holded" column in its table) and on `/Expenses/{id}` as the Holded sync card, where a finance admin re-queues it.
- Attachment uploads are stamped on `ExpenseAttachment.HoldedUploadedAt`, so a re-run after a partial failure or a re-queue resumes rather than adding a second copy of every earlier file to the same Holded document.
- Submitter-facing report, line, upload, submission and withdrawal rejections use Expenses resources in the request culture, including upload limits and report status. Member mutations never expose an unexpected persistence or dependency exception to the submitter. Unexpected faults are logged with their exception and report/line identifiers; the controller renders its localized generic failure instead.
- Each mutation has one result-returning entry point; there are no throwing transition twins or localized validation exceptions. Shared line creation and compensated attachment storage are private composition paths that return explicit refusals.
- Result-returning mutations preserve caller cancellation after any required rollback. An aborted request is not an ordinary failure result or an Error log entry; dependency cancellation without an aborted caller remains a logged fault.
- Attachment removal unlinks the report-scoped line and deletes its metadata in one repository save. Audit and file cleanup follow commit and ignore request cancellation; a disconnect at commit cannot leave orphan metadata or skip the audit/cleanup. The repository returns the row actually removed for cleanup.
- Attachment removal uses the same result contract: known refusals carry resource keys; unexpected faults log the exception and show a localized generic error.
- The drain does nothing at all when no `HOLDED_API_KEY_V2` is configured (`IHoldedClient.IsConfigured`): every call would 401, which is a permanent error, so draining would write off the whole queue. The sync card reports that state as "Not configured" rather than "Queued".
- Holded API request bodies are the only code path that may contain a raw IBAN (not masked).

## Negative Access Rules

- Regular members **cannot** see other users' expense reports or attachments.
- Regular members **cannot** approve, reject, or endorse (unless they are a coordinator for the relevant category).
- Coordinators **cannot** approve — that requires FinanceAdmin/Admin.
- Coordinators **cannot** edit, submit, or file a report for anyone else — endorsement is as far as the role goes. Neither can a plain member on another member's report.
- FinanceAdmin/Admin **cannot** edit an `Approved` or `Withdrawn` report, nor submit one that is past Draft, nor withdraw anyone's report.
- A finance admin filing for a member **cannot** make the report their own — `SubmitterUserId` is the picked member, so the payout, the dashboard row and the GDPR export all follow the member.
- Submitters **cannot** set `MaxAmount` — there is no submitter-facing input and no service path that accepts one outside Endorse/Approve.
- Submitters and plain members **cannot** decline or undo the decline of a line, and a coordinator cannot once the report is `CoordinatorEndorsed`; no one can on a Draft, Approved or Withdrawn report.
- FinanceAdmin **cannot** reveal a raw IBAN on the admin user page — that action is Admin-only.
- No role **can** transition a report backwards in the state machine (e.g., un-approve, un-submit).
- No code path **may** log or emit a raw IBAN in logs, audit entries, or error messages — only masked form via `IbanFormatter.Mask`.

## Triggers

- On **create for another member**: audit entry `ExpenseCreatedOnBehalf` written, naming the actor and the member. A self-created draft stays unaudited — the report is its own record.
- On **any successful edit to another member's report**: audit entry `ExpenseEditedOnBehalf` written per change — header update, line add (proof rows included), line update, line remove — with a description saying what changed. A combined line-and-attachment add writes the line audit only after the attachment is stored; a failed upload rolls the line back without claiming the edit happened. Self-edits stay unaudited, exactly as before.
- Every on-behalf entry carries `relatedEntityId` = the member with `relatedEntityType` `"User"`. That is what puts the entry in the *member's* GDPR export: the entity is the report (or their Profile) and the actor is the admin, so without it the row never reaches the person it is about.
- A **Draft** carrying `LastRejectionReason` shows the submitter a banner on `/Expenses/{id}` with the reason and guidance that individual receipts can be edited, replaced or removed on their line page (anchor link to the lines) before resubmitting; the report need not be deleted and rebuilt.
- On **submit**: the *submitter's* `Profile.Iban` and profile legal name (`FirstName` + `LastName`) are snapshotted into `PayeeIban` / `PayeeName`. Audit entry `ExpenseSubmit` written, naming the member when the actor is not the submitter.
- On **IBAN set/remove for another member**: `IbanSet` / `IbanRemove` written against the member as subject with the admin as actor, naming the member and carrying the IBAN unmasked. A member setting their own gets the bare masked-convention entry it always did.
- On **payee snapshot refresh**: `ExpensePayeeIbanUpdated` written against the **report** (so it shows in that report's on-page history), actor = whoever set it, related entity = the submitter. Same masking rule as `IbanSet`: unmasked when the actor is not the submitter, bare when it is.
- On **decline / undo decline of a line**: `ExpenseLineDeclined` / `ExpenseLineDeclineUndone` written against the **report** (so it shows in that report's on-page history), actor = the reviewer, naming the line, its amount and the reason. They are in the GDPR audit slice's action list, so they reach the member's export.
- On **endorse**: any max amount the coordinator supplied is stored on the report and named in the `ExpenseEndorse` audit entry.
- On **approve**: `HoldedExpenseOutboxEvent` (CreateIncomingDoc) queued. Audit entry `ExpenseApprove` written, naming any max amount the finance admin supplied (which overrides the coordinator's). Then the `expense_approved` email goes to the submitter (`ExpensesEmails.ReportApproved`, `MessageCategory.System`, in their supported preferred language, with English fallback for blank, malformed or unsupported preferences) naming the payable amount, the masked payee IBAN and a link to the report — after the save, so a refused approval sends nothing; a submitter with no notification email is logged and skipped (peterdrier/Humans#1820).
- On **account override** at approval: the report is rebooked (account, category and year together) and the audit entry `ExpenseCategoryOverride` names both account numbers. Posting the report's current account is not an override and writes nothing.
- On **IBAN reveal (admin page)**: `AuditAction.IbanReveal` written recording actor + target user.
- On **Holded push success**: audit entry `ExpenseHoldedPushed` written (actor: the job). On **write-off**: `ExpenseHoldedFailed`. On **finance re-queue**: `ExpenseHoldedRequeued` (actor: the admin). These carry the push history past outbox-row cleanup — the outbox columns themselves are not readable outside the database.
- **`HoldedExpenseOutboxJob`** runs every minute.
- **GDPR export** (`IUserDataContributor`): contributes `ExpenseReports` and `ExpenseAuditLog` slices. Report creation, submission and approval dates serialize as UTC ISO 8601 strings; absent submission or approval dates stay null. Chain-follows merge tombstones. The report slice carries the cap, the endorsement date and the last rejection's reason and date, and each line's declined flag, decline reason and decline date (not who declined it — the audit slice names the actor); the audit slice adds every entry recorded against the member's own reports, so decisions others took on them (endorse, reject, approve, category override) reach the member. (Historical `ExpenseSepaSent` / `ExpenseSepaReopened` / `ExpensePaid` audit entries are still surfaced for accounts that have them — the audit log is immutable; only the writers were removed.)
- **Article 17 erasure retains everything, by design.** `EraseForUserAsync` is a no-op; `ErasureDeclaration` maps both `ExpenseReports` and `ExpenseAuditLog` to the same fiscal-retention reason — a reimbursement is an accounting voucher, and Spanish law requires the books and supporting documents (payee legal name, IBAN unmasked in the row, amounts, dates, notes, approval trail, receipts) be kept 6 years (Código de Comercio Art. 30) / 4 years for tax purposes (Ley 58/2003 Art. 66), GDPR Art. 17(3)(b).

## Cross-Section Dependencies

- **Budget**: `IBudgetService` — `GetCategoryByIdAsync` (category metadata and coordinator team resolution), `GetActiveYearAsync` / `GetYearByIdAsync` (budget-year binding), `GetEffectiveCoordinatorTeamIdsAsync` (coordinator-scope check).
- **Teams**: `ITeamService` — `IsUserCoordinatorOfTeamAsync` (coordinator endorsement gate and review-queue scope), `GetTeamAsync` / `GetTeamsAsync`.
- **Users/Identity**: `IUserService` (also `IUserService`, which the Holded publisher reads users through) — `GetUserInfoAsync` / `GetUserInfosAsync`, display names for the Holded contact, and the submitter's profile, which is where the IBAN and legal name are read from at submit time and masked for the GDPR export. `SetProfileIbanAsync` writes the member's profile IBAN from `/Expenses/{id}/Iban`. `UserInfo.AllUserIds` off `GetUserInfoAsync`, GDPR merge-tombstone chain-follow, and the Holded push binds the creditor contact to the resolved submitter (`UserInfo.Id`) and seeds it from reports under any id in that list. There is no `IProfileService` dependency.
- **Email**: `IEmailService.SendAsync` for the approval email. The template, its resx keys and its `/Email/EmailPreview` sample are this section's (`ExpensesEmails`, `ExpensesEmailPreviews`); Email keeps the mechanics (memory/architecture/email-templates-live-in-sender.md). `IUserEmailService.GetNotificationTargetEmailsAsync` resolves the submitter's address.
- **AuditLog**: `IAuditLogService.LogAsync` — all lifecycle transitions logged. The section never reads audit for the report history page: `/Expenses/{id}` emits `<vc:audit-log entity-type="ExpenseReport" entity-id="…">` and lets the AuditLog section own the read and the render. Everyone who may open the report sees it — the entries are that report's own history. The GDPR export is the one place the section reads audit back, through `IAuditLogService.GetFilteredEntriesAsync`, for its `ExpenseAuditLog` slice.
- **Finance**: `IHoldedFinanceServiceRead.GetCreditorStatusAsync` and `IHoldedFinanceServiceRead.GetCreditorLedgerAsync` — creditor status/statement derived from the cached Holded daybook ledger, for the submitter's owed/paid timeline. `ListExpenseAccountsAsync` (the accounts a report may book to, and their labels) and `GetCategoryMapAsync` (account → budget category) for booking resolution; `GetHoldedAccountIdForCategoryAsync` only for rows that predate `HoldedAccountNumber`. Also `IHoldedFinanceService.EnsureCreditorContactAsync` (creditor contact on push), and `IHoldedFinanceServiceRead.GetCreditorContactByUserAsync` / `ListCreditorAccountsAsync` (creditor binding and account lookups).
- **Holded**: `IHoldedClient` — purchase-document create, attachment upload, approve (the vendor API; Holded owns it).
- **Gdpr**: implements `IUserDataContributor` (export slice; erasure is a retained no-op, see GDPR section).
- **Admin (Users section)**: `/Users/Admin/{id}/RevealIban` lives in `UsersAdminController` (`Humans.Users`) and is gated by `[Authorize(Policy = AdminOnly)]`. It is Users' page over Users' data; this section gates nothing there.

## Architecture

**Owning services:** `ExpenseReportService`, `ExpenseHoldedPublisher`, `ExpenseReportBackgroundProcessor`
**Owned tables:** `expense_reports`, `expense_lines`, `expense_attachments`, `holded_expense_outbox_events`
**Status:** (A) Migrated. Own project `src/Sections/Humans.Expenses`; cross-section surface is its `Contracts/` folder.

- `ExpenseReportService` lives in `Humans.Expenses.Services` and depends only on Application-layer abstractions. `IExpenseReportManagement` (internal) carries its mutations and inherits `IExpenseReportService`, which carries the read-only subset — `GetAsync`, `GetHoldedTimelineAsync`, `GetReportOwningAttachmentAsync`, `TryReadAttachmentAsync`, `GetReviewQueueAsync` — for cross-section callers. Backdoor's `BackdoorFinanceController` reads reports and attachments, authorizing with `PolicyNames.ExpenseReportView` — a named policy this section's `SectionPolicies` registers, wrapping the internal `ExpenseReportOperation`/`ExpenseReportOperationRequirement` pair and `ExpenseReportAuthorizationHandler`, all of which stay under `Authorization/` and internal; `ExpensesController` authorizes against the requirement type directly, being in the same assembly. The public surface is `Section` plus everything under `Contracts/`: `IExpenseReportService`, `IExpenseReportBackgroundProcessor` (`DrainHoldedOutboxAsync`, how `HoldedExpenseOutboxJob` reaches the section), and the DTO/enum graph. The job lives in this project's `Jobs/` folder; the section registers and schedules it itself (`Section.cs`, `SectionJobs.cs`).
- `ExpenseRepository` (impl `src/Sections/Humans.Expenses/Data/ExpenseRepository.cs`, §15b Singleton + `IDbContextFactory<ExpensesDbContext>`) is the only file that touches expense tables via `DbContext`.
- **DbContext** — `ExpensesDbContext` (`src/Sections/Humans.Expenses/Data/ExpensesDbContext.cs`, `internal sealed`) is the section's own per-section EF model (nobodies-collective/Humans#858 split): maps only `expense_reports`, `expense_lines`, `expense_attachments`, `holded_expense_outbox_events`, with its own `__EFMigrationsHistory_Expenses` table and migrations under `Data/Migrations/` (baseline `20260715101338_BaselineExpenses`). Same database and connection as `HumansDbContext` — the split partitions the EF model, not the database.
- **DI registration** lives in `Section.Register` at the project root, discovered by Shell through `ISection`. It also registers the section's `ExpenseReportStatus` badge colours into `EnumBadgeMap` rather than Base holding a literal row per section enum.
- **Decorator decision — no caching decorator.** Expense data is mutable and user-specific; low-traffic at our scale.
- **Badge colours** — `Models/StatusBadgeExtensions.GetBadgeClass` is the single source; `Section.Register` projects it into `EnumBadgeMap` for the table-column half of the same page.
- **Cross-domain navs** — none declared. All cross-section linkage is scalar FK only.
- **Cross-section calls** route through `IBudgetService`, `ITeamService`, `IUserService`, `IUserWrite`, `IEmailService`, `IUserEmailService`, `IAuditLogService`, `IFileStorage`, `IHoldedClient`, `IHoldedFinanceService`, and `IHoldedFinanceServiceRead` (Finance, Feature 2).
- **Member mutation messages** — controller-owned header/line/file/submit/withdraw text and every IBAN mutation outcome use the section resources in all six cultures. `ExpenseIbanSaveResult.MessageKey` carries a resource key resolved by the controller for success toasts, field validation, and failure toasts. Every member mutation carries an `ExpenseError` resource key and format arguments, resolved by the controller; gated coordinator/finance actions retain their English operator feedback. Expected refusals return results and log at Warning; dependency faults log their exception and return a generic failure.
- **Report action preamble** — one private controller helper resolves the current user, loads the report and optionally checks the existing operation requirement. Missing users/reports return 404; operation denial returns 403. Line-read, IBAN, withdrawal and non-editable-page rules remain action-specific.
- **Architecture test** — `tests/Humans.Expenses.Tests/ExpensesArchitectureTests.cs` pins the shape.

### Feature 2 — Holded contact enrichment and payment status

When a report is pushed to Holded (`HoldedExpenseOutboxJob`), this section calls Finance's `IHoldedFinanceService.EnsureCreditorContactAsync`, which owns creditor identity: a member already linked (or lazy-seeded from a prior report) keeps their existing contact id as is — Holded's v2 contact PUT is a full replacement, so a linked contact is never updated — and only a member with no contact gets a new one created, with legal name as `Name`, trade name only for "burner" identities (legal name required first), `CustomId` = `UserId`, `type = creditor`, IBAN. The returned contact id and resolved `supplierRecord.num` are stored on `ExpenseReport.HoldedContactId` / `HoldedSupplierAccountNum` for subsequent creditor look-ups.

The submitter's expense detail view (`/Expenses/{id}`) shows a **payment status timeline**: registered / owed / settled, derived from `GetCreditorStatusAsync`. Paid detection reads the nightly-cached creditor daybook (balance = Σdebit − Σcredit ≥ 0 = settled) — zero live Holded calls on page load. The registered total counts actual Holded documents, including those retained after withdrawal; only booked lines contribute during a partial push, and reimbursement caps still apply. It sums the reports under every id in the submitter's `AllUserIds`, as the creditor contact does.

The submitter dashboard (`/Expenses`) shows the member's Holded creditor-account statement (`GetCreditorLedgerAsync`) — the cached daybook lines for their 400000xx account, rendered verbatim. It deliberately does **not** blend local `ExpenseReport` rows into that table: doing so nets a locally-held claim against a Holded debit while the Holded credit pairing with it is never shown, which is why the earlier "IOU ledger" card could not reconcile and was removed.

**`TravelReimbursementConfig`** (bound from `appsettings.json` → `TravelReimbursement` section, registered in `Section.Register`) holds the 2026 Spanish IRPF tax-exempt rates: 0.26 €/km, 26.67 €/day (day trip), 53.34 €/day (overnight). Defaults are the live 2026 values; the section works without explicit configuration.

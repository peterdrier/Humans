# Expenses section — design

**Status:** Draft, brainstorm complete, awaiting plan.
**Date:** 2026-05-10
**Owner:** Peter Drier

## Purpose

A new `/Expenses` section that lets any active member file an expense report against a budget category, with one or more receipts/invoices attached. Reports flow through coordinator endorsement (when the category has a coordinator) and FinanceAdmin approval. On approval, the report is pushed to Holded as an incoming purchase document via an outbox-driven Hangfire job. FinanceAdmin pays approved reports by selecting them in a queue and downloading a generated SEPA pain.001 XML file to upload to the bank. The `Paid` state is set automatically when the existing `HoldedSyncJob` detects the matching Holded transaction has been fully paid.

This is a new section. It does not extend or replace anything existing. It depends on Budget (read-only category lookup), Teams (read-only coordinator resolution), Users/Identity (display names), Audit Log (write-only), and Finance (existing Holded sync infrastructure plus a new `IFinanceService.GetTransactionByHoldedDocIdAsync` method for the back-flow to `Paid`).

## Glossary

- **Expense Report** — A request for reimbursement filed by a member, scoped to one `BudgetCategory`. Header carries submitter, category, payee snapshot (name + IBAN at submit time), free-text note, status, and lifecycle timestamps.
- **Expense Line** — A row on a report: description, amount in EUR, and one attached receipt/invoice file. A report has one or more lines at submit time. Total = SUM of line amounts.
- **Expense Attachment** — The on-disk file (PDF or image) attached to a line. Stored as `<guid>.<ext>` in a configured filesystem root outside `wwwroot`. Streamed only through an authorized controller endpoint.
- **Holded Outbox Event** — A deferred Holded sync row written when a report reaches `Approved` (or when category is overridden after approval). Drained by a recurring Hangfire job.
- **SEPA Payout** — The act of generating a pain.001.001.09 XML file over a multi-selected set of approved-and-unpaid reports. Not stored. Reports in the selection flip to `SepaSent` atomically; the XML streams to the treasurer's browser.
- **Coordinator** — A user who coordinates a budget category (resolved via `ITeamService.GetEffectiveBudgetCoordinatorTeamIdsAsync`). Required to endorse reports filed against their category before FinanceAdmin sees them.

## Scope

In v1:

- Submitter creates, edits, withdraws their own reports.
- Coordinator endorse / reject (with comment) when the category has a coordinator.
- FinanceAdmin approve / reject (with comment) / category-override-on-approval.
- Holded outbox push on approval, with attachments uploaded to Holded.
- FinanceAdmin queue with status filters and multi-select for SEPA generation.
- SEPA pain.001 XML download.
- Automatic `Approved → SepaSent → Paid` transitions via existing Holded sync.
- IBAN field on `Profile`, lazy-surfaced through the expense-report flow only. User can set/edit/remove. FinanceAdmin reads in report context only. Admin reads on `/Admin/Users/{id}` with a "Reveal" toggle that audits.
- IBAN masking (`NL75****123`) in all logs, error messages, and audit-log entries via a centralized `IbanFormatter.Mask` helper.
- GDPR `IUserDataContributor` integration; merge-tombstone follow-through.

Out of v1:

- Cash advances and reconciliation.
- Invoice forwarding (org pays a vendor directly on behalf of a member).
- Multi-currency `Amount` field. (Submitter notes the original currency in the line description, e.g. "17 GBP = 19.52 EUR".)
- Manual "mark as paid" admin action — `Paid` is set only by the Holded back-flow.
- Automatic local-attachment cleanup. (Documented design only; manual-trigger first runs.)
- Discovery placement on `/Budget` (a button or CTA pointing into `/Expenses`). Wired up later; not blocking v1.
- "Re-download SEPA file" — treasurer keeps their downloaded copy; no batch entity.

## Concepts

- A report is scoped to exactly **one** budget category. Multi-category spend = multiple reports.
- A report has one or more **lines**, each with an amount and exactly one attachment.
- The **payee** for v1 is always the submitter. Payee name and IBAN are snapshotted at first submit and frozen for the lifetime of the report.
- A **rejection** (from coordinator or FinanceAdmin) returns the report to `Draft` with the rejection comment attached. The submitter edits and resubmits the same report. Rejected is not a separate state.
- A **withdrawal** is terminal. Submitters file a new report rather than reviving a withdrawn one.
- The **SEPA payout** is virtual — there is no batch entity. The XML is a transient generated artifact; the treasurer keeps their downloaded copy.

## Data Model

### ExpenseReport

**Table:** `expense_reports`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| SubmitterUserId | Guid | FK → User. FK only, no nav. |
| BudgetCategoryId | Guid | FK → BudgetCategory. FK only, no nav. `OnDelete: Restrict`. |
| BudgetYearId | Guid | Denormalized for queue filters and cross-year detection. |
| Status | ExpenseReportStatus | Draft / Submitted / CoordinatorEndorsed / Approved / SepaSent / Paid / Withdrawn. Stored as string via `HasConversion<string>()`. |
| Note | string? | Free-text from submitter (≤ 500 chars). |
| PayeeName | string | Snapshot at first submit; frozen thereafter. |
| PayeeIban | string | Snapshot at first submit; frozen thereafter. |
| Total | decimal | EUR. Persisted denormalization of `SUM(Lines.Amount)`. Recomputed on every line edit; never written directly by callers. |
| SubmittedAt | Instant? | Stamped on first submit. |
| CoordinatorEndorsedByUserId | Guid? | |
| CoordinatorEndorsedAt | Instant? | |
| ApprovedByUserId | Guid? | FinanceAdmin/Admin who approved. |
| ApprovedAt | Instant? | |
| SepaSentAt | Instant? | Stamped at SEPA generation time. |
| PaidAt | Instant? | Stamped by Holded back-flow when the matching Holded transaction has `PaymentsPending == 0`. |
| LastRejectionReason | string? | Last reject comment shown to submitter. Cleared on next submit. |
| LastRejectedByUserId | Guid? | |
| LastRejectedAt | Instant? | |
| HoldedDocId | string? | Set by the Holded outbox processor on success. |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Indexes / constraints:**
- `(SubmitterUserId, Status)`
- `(Status)` — for the FinanceAdmin queue
- `(BudgetCategoryId)` — for category-scoped views
- `(HoldedDocId)` — for the back-flow join

**Aggregate-local navs:** `ExpenseReport.Lines`. No cross-domain navs.

### ExpenseLine

**Table:** `expense_lines`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| ExpenseReportId | Guid | FK → ExpenseReport. Cascade delete. |
| Description | string | Required at submit. ≤ 500 chars. Free text; submitter notes original currency here when not EUR. |
| Amount | decimal | EUR. > 0. |
| AttachmentId | Guid? | FK → ExpenseAttachment. Nullable in `Draft`; required on submit. |
| SortOrder | int | For deterministic display order. |

**Aggregate-local navs:** `ExpenseLine.ExpenseReport`, `ExpenseLine.Attachment`.

### ExpenseAttachment

**Table:** `expense_attachments`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK. Doubles as the on-disk filename stem. |
| OriginalFileName | string | What the user uploaded. ≤ 255 chars. |
| Extension | string | Normalized lowercase. One of `.pdf`, `.jpg`, `.jpeg`, `.png`, `.heic`. |
| ContentType | string | Sniffed and validated server-side; not blindly trusted from the client. |
| SizeBytes | long | |
| UploadedByUserId | Guid | FK only, no nav. |
| UploadedAt | Instant | |

**Bytes:** stored at `<configured-root>/<Id><Extension>`. Configurable via `ExpenseAttachments:Root` (default `/var/lib/humans/expense-attachments`). Never inside `wwwroot`. Streamed via `GET /Expenses/Attachment/{id}` with auth re-check on every call.

**Limits:** max 20 MB per file (configurable `ExpenseAttachments:MaxBytes`). MIME sniff + extension whitelist enforced.

### HoldedExpenseOutboxEvent

**Table:** `holded_expense_outbox_events`

Mirrors `GoogleSyncOutboxEvent`'s shape:

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| ExpenseReportId | Guid | FK → ExpenseReport. |
| EventType | string | `CreateIncomingDoc` or `UpdateIncomingDocTag`. |
| OccurredAt | Instant | |
| ProcessedAt | Instant? | Null = unprocessed. |
| RetryCount | int | |
| LastError | string? | |
| FailedPermanently | bool | True after a 4xx response from Holded. Surfaced as a banner in `/Expenses/Review`. |

Indexes: `(ProcessedAt, FailedPermanently)` for the job's polling query.

### Profile.Iban (new column on `profiles`)

`string?` — nullable. Validated as a syntactically-correct IBAN (length per country, mod-97 checksum) on write. Never returned by profile read DTOs or views; surfaced only inside the expense-report flow and on `/Admin/Users/{id}` (Admin-only, masked by default with a "Reveal" toggle that writes an `AuditLogEntry`).

### ExpenseReportStatus

| Value | Description |
|-------|-------------|
| Draft | Submitter editing. Coordinator/FinanceAdmin invisible. |
| Submitted | Awaiting coordinator endorsement (if applicable) or FinanceAdmin approval. |
| CoordinatorEndorsed | Coordinator has endorsed; awaiting FinanceAdmin approval. |
| Approved | FinanceAdmin approved. Holded outbox event queued. Eligible for SEPA payout. |
| SepaSent | Included in a generated SEPA pain.001 file. Awaiting Holded back-flow confirmation. |
| Paid | Holded sync detected the matching Holded transaction is fully paid. Terminal. |
| Withdrawn | Submitter withdrew before approval. Terminal. |

Stored as string via `HasConversion<string>()`.

## Lifecycle / state machine

```
              ┌─────────┐
   create →   │  Draft  │ ← reject (coordinator or FinanceAdmin)
              └────┬────┘   stamps LastRejection*
            submit │
                   ↓
              ┌────────────┐
              │ Submitted  │
              └────┬───────┘
                   │
         category has coord?
              yes ↓        no ↓
   ┌────────────────────┐    │
   │ CoordinatorEndorsed│    │
   └────────┬───────────┘    │
            ↓                ↓
              ┌─────────────┐
              │  Approved   │  ← Holded outbox row INSERTed in same tx
              └────┬────────┘
            included in SEPA payout
                   ↓
              ┌────────────┐
              │ SepaSent   │
              └────┬───────┘
            HoldedSyncJob sees PaymentsPending == 0
                   ↓
              ┌─────────┐
              │  Paid   │   (terminal)
              └─────────┘

  Withdrawn — submitter pulls report from {Draft, Submitted, CoordinatorEndorsed} (terminal).
```

**Transitions**

| From | Event | To | Side effects |
|------|-------|----|--------------|
| (none) | Create | Draft | row created, no submit/approval/payment stamps. |
| Draft | EditField | Draft | submitter only; normal edits. |
| Draft | Submit | Submitted | validates ≥ 1 line, every line has an attachment, `Profile.Iban` set; snapshots `PayeeName`/`PayeeIban`; `SubmittedAt = now`; clears `LastRejection*`; recomputes `Total`. |
| Draft | Withdraw | Withdrawn | submitter only. |
| Submitted | EditField | Submitted | submitter only. Header-only edits. |
| Submitted | EditLineOrAttachment | Submitted | submitter only. Already in this state — no status change. |
| Submitted | CoordinatorEndorse | CoordinatorEndorsed | coordinator only; only allowed when category has a coordinator. Stamps `CoordinatorEndorsedAt` / `CoordinatorEndorsedByUserId`. |
| Submitted | CoordinatorReject | Draft | coordinator only; rejection comment required. Stamps `LastRejection*`. |
| Submitted (no-coord) | Approve | Approved | FinanceAdmin/Admin only. Single tx: stamp `ApprovedAt` / `ApprovedByUserId`, optional category override, INSERT outbox row (`CreateIncomingDoc`). |
| Submitted (no-coord) | Reject | Draft | FinanceAdmin/Admin only. Stamps `LastRejection*`. |
| Submitted, CoordinatorEndorsed | Withdraw | Withdrawn | submitter only. |
| CoordinatorEndorsed | EditField | CoordinatorEndorsed | submitter only. Header-only. |
| CoordinatorEndorsed | EditLineOrAttachment | Submitted | submitter only. **Reverts** to Submitted; clears `CoordinatorEndorsed*`. |
| CoordinatorEndorsed | Approve | Approved | as above. |
| CoordinatorEndorsed | Reject | Draft | as above. |
| Approved | EditCategory | Approved | FinanceAdmin only. INSERT outbox row (`UpdateIncomingDocTag`). |
| Approved | IncludeInSepaPayout | SepaSent | FinanceAdmin/Admin only. Atomic across the multi-selected set: stamp `SepaSentAt`, audit-log per report. |
| SepaSent | (system) HoldedConfirmedPaid | Paid | `ExpensePaidPollingJob` polls Holded for the report's `HoldedDocId`. Stamps `PaidAt`. |

**Notes**

- Approval and outbox-event creation are a single DB transaction. There is no `Approved` report without a corresponding outbox row.
- `SepaSent` is set atomically across the multi-selected set inside the same transaction that emits the pain.001 XML.
- `Paid` is set only by the Holded back-flow. No manual route.
- Withdraw is terminal. Re-filing creates a new report Id.

## Routing

| Route | Method | Purpose | Authorized for |
|-------|--------|---------|----------------|
| `/Expenses` | GET | Submitter's own reports (all statuses) + "New report" CTA. | Any active human. |
| `/Expenses/New` | GET, POST | Empty draft form. | Any active human. |
| `/Expenses/{id}` | GET | View a report. | Submitter, relevant coordinator, FinanceAdmin/Admin. |
| `/Expenses/{id}/Edit` | GET, POST | Edit header + add/remove lines + upload attachments. | Submitter, while status ∈ {Draft, Submitted, CoordinatorEndorsed}. |
| `/Expenses/{id}/Submit` | POST | Validate + Draft → Submitted. | Submitter. |
| `/Expenses/{id}/Withdraw` | POST | Pull a non-final report. | Submitter. |
| `/Expenses/{id}/Iban` | GET, POST | First-time-IBAN modal in the submit flow; POST handles set, update, and clear (a "Remove" form button posts an empty value). | Self only. |
| `/Expenses/Attachment/{id}` | GET | Stream the attachment file with auth re-check. | Submitter, relevant coordinator, FinanceAdmin/Admin. |
| `/Expenses/Coordinator` | GET | Coordinator's queue: reports in `Submitted` for categories they coordinate. | Anyone who coordinates ≥ 1 budget category. |
| `/Expenses/{id}/Endorse` | POST | Coordinator endorse. | Coordinator of the report's category. |
| `/Expenses/{id}/CoordinatorReject` | POST | Coordinator reject (required comment). | Coordinator of the report's category. |
| `/Expenses/Review` | GET | Comprehensive FinanceAdmin dashboard: every non-Draft, non-Withdrawn report, with status column and filters (status, submitter, category, date range). Multi-select for SEPA. | FinanceAdmin/Admin. |
| `/Expenses/{id}/Approve` | POST | FinanceAdmin approve (optional category override). | FinanceAdmin/Admin. |
| `/Expenses/{id}/Reject` | POST | FinanceAdmin reject (required comment). | FinanceAdmin/Admin. |
| `/Expenses/Sepa/Generate` | POST | Generate pain.001 XML for a selection of `Approved` reports, mark them `SepaSent`, stream the file. | FinanceAdmin/Admin. Button enabled only when every selected row is `Approved`. |

`/Expenses` is **not** added to the main left nav. Discovery via a future button on `/Budget` (placement TBD, out of v1 scope).

`/Expenses/Coordinator` and `/Expenses/Review` live in the admin shell, mirroring `/Budget` and `/Finance` placement.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any active human | File reports for themselves; view, edit, withdraw their own non-final reports; view their own historical reports. Set/edit/remove their own IBAN — only via the expense-report flow (not visible elsewhere). |
| Budget category coordinator | View and endorse/reject (with comment) reports filed against categories they coordinate. Cannot edit the report's content. |
| FinanceAdmin | View all reports; approve, reject (with comment), or override the category on approval. Generate SEPA payouts. View any submitter's IBAN inside an expense-report context only. |
| Admin | All of FinanceAdmin's capabilities. Additionally, can view a user's IBAN on `/Admin/Users/{id}` via a masked display + "Reveal" toggle that writes an `AuditLogEntry`. |
| All other authenticated humans | None. |

## Invariants

- A report is scoped to exactly one `BudgetCategory`. Multi-category spend = multiple reports.
- At submit time, a report has ≥ 1 line, every line has exactly one attachment, and the submitter has a non-null `Profile.Iban`. Drafts may have 0 lines or unattached lines.
- `Total = SUM(Lines.Amount)` is recomputed and persisted on every line edit; never written directly by callers.
- `PayeeName` and `PayeeIban` are snapshotted at first submit and frozen for the lifetime of the report.
- `Profile.Iban` is set/edited/removed only via the expense-report flow's IBAN modal, never via the regular profile edit page. Validation is structural (length per country, mod-97 checksum); no external lookup.
- A coordinator endorsement is required iff the report's `BudgetCategory.TeamId` is non-null **and** `ITeamService.GetEffectiveBudgetCoordinatorTeamIdsAsync` resolves at least one coordinator for that team; otherwise the step is auto-skipped and the report flows Submitted → Approved on FinanceAdmin action alone. The endorsement-required check is evaluated at submit time and re-evaluated on category override.
- Editing lines or attachments while in `CoordinatorEndorsed` reverts status to `Submitted` and clears `CoordinatorEndorsedAt` / `CoordinatorEndorsedByUserId`. Header-only edits do not.
- Once `Approved`, the report is immutable to the submitter. The only edits allowed thereafter are FinanceAdmin category-override (which queues an `UpdateIncomingDocTag` outbox event) and the system-driven status transitions to `SepaSent` and `Paid`.
- Approval and outbox-event creation happen in a single DB transaction.
- `SepaSent` is set atomically across the multi-selected set inside the same transaction that emits the pain.001 XML. If the transaction fails, no report is moved and no XML is streamed.
- `Paid` is set only by `ExpensePaidPollingJob`, when `IHoldedClient.GetPurchaseDocumentAsync(HoldedDocId)` returns a document with `paymentsPending == 0` and `approvedAt != null`.
- Every state transition writes one `AuditLogEntry` via `IAuditLogService` with actor, from-state, to-state, and (for rejects) the rejection comment. Category-override-on-approval writes an additional entry recording from-category and to-category.
- `expense_attachments` rows and their on-disk files are append-only after a report leaves `Draft`. Withdrawal does not delete files. Draft-state delete of a line or attachment removes the on-disk file as well as the row.
- Currency is EUR-only. Reports do not carry a currency field; the SEPA file and Holded incoming-doc both assume EUR. Submitter notes original currency in the line description when applicable.
- IBANs appear in logs, error messages, and audit-log entries only via `IbanFormatter.Mask(...)` (format `NL75****123` — first 4 + `****` + last 3). The pain.001 SEPA XML and the Holded API call are the only places the unmasked IBAN goes.

## Negative Access Rules

- Regular humans **cannot** view another member's reports, attachments, or IBAN by id-guess or otherwise.
- Regular humans **cannot** see any FinanceAdmin or coordinator queues.
- A coordinator **cannot** see reports for categories they don't coordinate, even if they coordinate other categories.
- A coordinator **cannot** approve, only endorse. Approval is FinanceAdmin/Admin only.
- A coordinator **cannot** edit a report's lines, attachments, or category — only `Endorse` or `CoordinatorReject` (with required comment).
- FinanceAdmin **cannot** edit a submitter's lines or attachments. Category-override at approval time and reject-with-comment are the only writes; everything else stays the submitter's.
- FinanceAdmin **cannot** view `Profile.Iban` outside an expense-report context — never on the user's profile page, the user list, or any other surface.
- The submitter **cannot** change a report's category after submit (would invalidate coordinator routing). Withdraw and re-file instead.
- The submitter **cannot** edit, withdraw, or delete a report in `Approved`, `SepaSent`, or `Paid`.
- No route, including admin tools, deletes an `expense_reports` row. Withdrawn reports persist for audit.
- The Holded outbox **cannot** be triggered for a report that isn't `Approved` (or, for `UpdateIncomingDocTag`, an Approved-or-later report).
- Admin's "Reveal IBAN" action on `/Admin/Users/{id}` writes an `AuditLogEntry` on every reveal. The IBAN is never returned to the page until the action is taken; the masked form is the default.

## Triggers

- Every state transition (Submit, Endorse, CoordinatorReject, Approve, Reject, Withdraw, IncludeInSepaPayout, HoldedConfirmedPaid) writes an append-only `AuditLogEntry` recording actor, timestamp, from-state, to-state, and (for rejects) the rejection comment.
- Category-override-on-approval writes an additional `AuditLogEntry` recording from-category and to-category.
- Approval (and post-approval category override) inserts a row into `holded_expense_outbox_events` in the same DB transaction.
- `HoldedExpenseOutboxJob` (Hangfire recurring, `*/1 * * * *` — every minute, 5-field cron matching the codebase pattern) pulls unprocessed rows, calls Holded's purchase-document API, uploads attachments, stores the returned `HoldedDocId`, and marks `ProcessedAt`. Transient errors bump `RetryCount`. 4xx errors set `FailedPermanently`.
- `ExpensePaidPollingJob` (Hangfire recurring, `*/15 * * * *`) pulls all `SepaSent` reports, calls `IHoldedClient.GetPurchaseDocumentAsync(HoldedDocId)` for each, and transitions any whose Holded document reports `paymentsPending == 0` and `approvedAt != null` to `Paid` (`PaidAt = clock.GetCurrentInstant()`, one audit-log entry per transition).
- Admin's "Reveal IBAN" action writes an `AuditLogEntry` recording actor, target user, and timestamp.
- `ExpenseService.ContributeForUserAsync` (GDPR contributor) chain-follows merge tombstones via `IUserService.GetMergedSourceIdsAsync`.

## Cross-Section Dependencies

- **Budget:** `IBudgetService.GetActiveYearAsync()`, `IBudgetService.GetCategoriesByYearAsync(yearId)`, `IBudgetService.GetCategoryAsync(id)` — read-only category lookups.
- **Teams:** `ITeamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(userId)`, `ITeamService.IsCoordinatorOfBudgetCategoryAsync(userId, categoryId)` *(new narrow method)* — coordinator resolution and authz.
- **Users/Identity:** `IUserService.GetByIdsAsync(...)` — display names for queues. `IUserService.GetMergedSourceIdsAsync(...)` — merge-tombstone follow-through on GDPR export.
- **Holded** (new sibling section — see "Sibling section: Holded" below): `IHoldedClient.CreatePurchaseDocumentAsync`, `IHoldedClient.UpdatePurchaseDocumentTagsAsync`, `IHoldedClient.UploadAttachmentAsync`, `IHoldedClient.GetPurchaseDocumentAsync`. Holded ships its own section doc and its own narrow surface; Expenses is its first consumer.
- **Audit Log:** `IAuditLogService.LogAsync(...)` — every state transition + category override + IBAN reveal.

Budget, Teams, Users, and Holded never call into Expenses.

## Architecture

**Owning section:** `Expenses` (new top-level)
**Owning services:** `ExpenseReportService`, `ExpenseAttachmentStorageService`, `SepaPaymentFileBuilder`, `HoldedExpenseOutboxProcessor`
**Owned tables:** `expense_reports`, `expense_lines`, `expense_attachments`, `holded_expense_outbox_events`
**Owned column on existing table:** `profiles.iban`
**Status:** (A) New section — born under design-rules §15h(1).

- Services live in `Humans.Application.Services.Expenses/` and never import `Microsoft.EntityFrameworkCore`.
- `IExpenseRepository` (impl `Humans.Infrastructure/Repositories/ExpenseRepository.cs`, §15b Singleton + `IDbContextFactory`) is the only file that touches Expense tables via `DbContext`. Atomic per-method operations; multi-entity mutations (e.g., approve + outbox-insert) are single repository methods inside one short-lived `DbContext`.
- `IExpenseAttachmentStorageService` (impl `Humans.Infrastructure/Services/ExpenseAttachmentFilesystemStorage.cs`) handles filesystem reads/writes under a configured root (`ExpenseAttachments:Root`). API: `Task<Guid> StoreAsync(Stream, string ext, string contentType)`, `Task<Stream> OpenReadAsync(Guid id, string ext)`, `Task DeleteAsync(Guid id, string ext)`. No DB access.
- `ISepaPaymentFileBuilder` (impl in `Humans.Application` — pure XML composition, no IO) — generates pain.001.001.09 XML from a list of approved reports plus org-level config (creditor IBAN, BIC, name, NIF). Unit-testable in isolation.
- `IHoldedClient` is **owned by the new `Holded` sibling section**, not by Expenses (see "Sibling section: Holded" below). Expenses is the first consumer; future Finance/Holded sync work extends the same surface. Interface lives at `Humans.Application/Interfaces/Holded/IHoldedClient.cs`; impl at `Humans.Infrastructure/Services/Holded/HoldedClient.cs` (typed `HttpClient`). API key from `HOLDED_API_KEY` env var only; never logged.
- `HoldedExpenseOutboxJob` is a Hangfire recurring job at `*/1 * * * *` (every minute, 5-field cron). Idempotent: processes only `ProcessedAt IS NULL AND FailedPermanently = false` rows; bounded retry with `RetryCount`-driven exponential backoff. Lives in Expenses (`Humans.Infrastructure/Jobs/HoldedExpenseOutboxJob.cs`) since the outbox table is owned by Expenses; the job consumes the Holded section's `IHoldedClient` like any other caller.
- `ExpensePaidPollingJob` is a Hangfire recurring job at `*/15 * * * *`. Pulls all `SepaSent` reports, polls Holded per-report via `IHoldedClient.GetPurchaseDocumentAsync`, transitions to `Paid`. Bounded by a per-run cap (50 reports) to keep API load reasonable. Lives in Expenses for the same reason as the outbox job.
- `IbanFormatter` (`Humans.Application.Helpers.IbanFormatter`) — static helper with `Mask(string iban)` returning `NL75****123` format. Centralizes the masking pattern for logs/error/audit. A code-review rule (project-rule atom) forbids logging raw IBANs.
- **Decorator decision — no caching decorator.** Member-facing routes are scoped to one user's own reports (cheap query). Coordinator and FinanceAdmin queues are admin-only, low-traffic. Same rationale as Budget / Finance / Governance.
- **Cross-domain navs:** none. `BudgetCategoryId`, `SubmitterUserId`, `*ByUserId` are FK-only with no navigation properties.
- **Cross-section calls** route through `IBudgetService` (read-only), `ITeamService` (read-only), `IUserService` (read-only), `IHoldedClient` (read + write — sibling section), and `IAuditLogService` (write-only).
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/ExpensesArchitectureTests.cs` pins the shape: no EF Core import in service, no cross-section repositories injected, no direct foreign-table access.
- **Authorization** — resource-based (design-rules §11): `ExpenseReportAuthorizationHandler` + `ExpenseReportOperationRequirement` gate every per-report action against the report (submitter? coordinator of the report's category? FinanceAdmin? Admin?). The IBAN read/write is its own narrow handler (`IbanAccessHandler`) covering self / FinanceAdmin-in-report-context / Admin-on-admin-page.
- **GDPR** — `ExpenseService.ContributeForUserAsync` registered as `IUserDataContributor`. Returns the user's reports, lines, attachment metadata (filename, size, content-type — bytes referenced via attachment URLs, not inlined), `Profile.Iban`, and audit-log entries where they're the actor or subject. Chain-follows merge tombstones.

### Holded outbox flow

```
[Approve action]
  ↓ (single DB transaction)
  expense_reports.Status = Approved
  expense_reports.ApprovedAt / ApprovedByUserId stamped
  expense_reports.BudgetCategoryId optionally overridden
  holded_expense_outbox_events INSERT (EventType = "CreateIncomingDoc")
  AuditLogEntry written
  ↓
  commit
  ↓
[HoldedExpenseOutboxJob — every 1 min]
  for each unprocessed row:
    POST Holded /api/invoicing/v1/documents/purchase
      with line items mapped to category tag {group-slug}-{category-slug}
      with attachments uploaded as multipart
    receive HoldedDocId
    ↓ (single DB transaction)
    expense_reports.HoldedDocId = <id>
    holded_expense_outbox_events.ProcessedAt = now
  on transient error → bump RetryCount, leave unprocessed
  on permanent error (4xx) → set FailedPermanently, surface in /Expenses/Review banner
```

### Holded → Paid back-flow

```
[ExpensePaidPollingJob — every 15 min, capped at 50 reports per run]
  pulls expense_reports where Status = 'SepaSent' (oldest SepaSentAt first)
  ↓
  for each row:
    GET Holded /api/invoicing/v1/documents/purchase/{HoldedDocId}
    if response.paymentsPending == 0 AND response.approvedAt != null:
      ↓ (single DB transaction)
      expense_reports.Status = Paid
      expense_reports.PaidAt = clock.GetCurrentInstant()
      AuditLogEntry written
    else:
      no-op; will be polled again next cycle
  on transient error (5xx, network) → log + continue (next cycle retries)
  on 404 (Holded doc deleted) → log warning, leave SepaSent for manual review
```

## Sibling section: Holded

Expenses depends on a thin **`Holded`** section that owns the HTTP client surface and configuration for the Holded API. Expenses is the first consumer; the broader Finance/Holded reconciliation described in `docs/sections/Finance.md` is a future evolution that will extend the same `IHoldedClient` surface (and may add tables of its own — `holded_transactions`, etc.) without breaking Expenses' contract.

**Owning section:** `Holded` (new top-level)
**Owned tables:** none in v1.
**Owning services:** `IHoldedClient` (impl `HoldedClient`).
**Status:** (A) New section.

In v1, `Holded` ships exactly four methods on `IHoldedClient` — those needed by Expenses: `CreatePurchaseDocumentAsync`, `UpdatePurchaseDocumentTagsAsync`, `UploadAttachmentAsync`, `GetPurchaseDocumentAsync`. API base URL and auth header are configured from `HOLDED_API_KEY` env var (no `appsettings.json` fallback). Errors are surfaced as a small `HoldedApiException` hierarchy distinguishing transient (retry-eligible) vs permanent (4xx) failures so consumers can choose retry policy.

A separate `docs/sections/Holded.md` invariant doc captures: API key handling, retry semantics, EUR-only assumption, and the explicit out-of-scope statement that v1 has no transaction-pull / sync job (that lives in the future Finance section).

## Future cleanup (out of v1)

Once a `BudgetYear` is `Closed`, all its reports are `Paid`, and Holded confirms all corresponding incoming-docs have their attachments stored, a maintenance pass deletes the local `expense_attachments` files (`<guid>.<ext>`) on disk. Holded becomes the system of record for receipt data thereafter. The DB row stays (small) for audit reference.

For v1, this is design-only. Not implemented; not automated. The first cleanup runs are manual-trigger-only behind a FinanceAdmin admin route.

## Open questions

- Exact placement of the discovery button on `/Budget` (out of v1).
- Whether `/Admin/Users/{id}` already has a "Payment details" panel we extend, or we add one.
- Org-level SEPA config (creditor IBAN, BIC, NIF, charge-bearer code) — likely a single-row settings table or env-bound config. Likely an env-bound config seeded at startup; verify during implementation planning.

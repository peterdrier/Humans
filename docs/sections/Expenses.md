<!-- freshness:triggers
  src/Humans.Application/Services/Expenses/**
  src/Humans.Domain/Entities/ExpenseReport.cs
  src/Humans.Domain/Entities/ExpenseLine.cs
  src/Humans.Domain/Entities/ExpenseAttachment.cs
  src/Humans.Domain/Entities/HoldedExpenseOutboxEvent.cs
  src/Humans.Domain/Enums/ExpenseReportStatus.cs
  src/Humans.Infrastructure/Repositories/Expenses/**
  src/Humans.Infrastructure/Jobs/HoldedExpenseOutboxJob.cs
  src/Humans.Infrastructure/Jobs/ExpensePaidPollingJob.cs
  src/Humans.Web/Controllers/ExpensesController.cs
  src/Humans.Web/Authorization/Requirements/IbanAccessHandler.cs
-->
<!-- freshness:flag-on-change
  Expense lifecycle, IBAN access rules, SEPA generation, Holded sync, and resource-based authorization — review when Expenses services/entities/controllers/auth handlers change.
-->

# Expenses — Section Invariants

Members submit expense reports for reimbursement. Finance Admin approves and processes payment via SEPA batch; Holded is notified asynchronously. Full workflow and field-level detail in `docs/superpowers/plans/2026-05-10-expense-reports.md`.

## Concepts

- An **ExpenseReport** is the top-level reimbursement request. It moves through a state machine (see Invariants) and is owned by the submitter until submitted.
- An **ExpenseLine** is one line item within a report — a description, amount, and required attachment.
- An **ExpenseAttachment** is a receipt or supporting document uploaded to a line item. Files are stored on disk via the shared `IFileStorage` abstraction (key `uploads/expense-attachments/{attachmentId}{.ext}`); the download route at `/Expenses/Attachment/{id}` re-authorizes the caller and streams bytes with the original filename via `Content-Disposition`. Metadata only in the DB.
- A **HoldedExpenseOutboxEvent** is an async task queued when a report is approved or its category tag changes — drained by `HoldedExpenseOutboxJob` to create/update Holded purchase documents.
- **SEPA** — Finance Admin generates a pain.001 XML batch for all Approved/unpaid reports, then confirms sending; reports transition to `SepaSent`. `ExpensePaidPollingJob` polls Holded every 15 minutes and transitions `SepaSent` → `Paid` when Holded confirms payment.
- **IBAN** — snapshotted from `Profile.Iban` at submit time into `ExpenseReport.PayeeIban`. Raw IBAN appears only in the SEPA XML and in Holded API request bodies. All log/audit/error output goes through `IbanFormatter.Mask`.

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
| Note | string? | optional submitter note |
| PayeeName | string | snapshotted at submit |
| PayeeIban | string | snapshotted at submit; MUST be masked in all log/audit output |
| Total | decimal | sum of line amounts |
| SubmittedAt | Instant? | |
| CoordinatorEndorsedByUserId | Guid? | scalar FK |
| CoordinatorEndorsedAt | Instant? | |
| ApprovedByUserId | Guid? | scalar FK |
| ApprovedAt | Instant? | |
| SepaSentAt | Instant? | |
| PaidAt | Instant? | |
| HoldedDocId | string? | Holded purchase document id |
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
| AttachmentId | Guid? | FK → expense_attachments |
| SortOrder | int | |

### ExpenseAttachment

**Table:** `expense_attachments`

Metadata only; bytes on disk managed by the shared `IFileStorage` (key `uploads/expense-attachments/{Id}{Extension}`). See `memory/architecture/one-ifilestorage.md`.

### HoldedExpenseOutboxEvent

**Table:** `holded_expense_outbox_events`

Append-on-approve, drained by `HoldedExpenseOutboxJob`. Fields: `EventType` (CreateIncomingDoc | UpdateIncomingDocTag), `RetryCount`, `FailedPermanently`, `ProcessedAt`, `LastError`.

### ExpenseReportStatus

| Value | Description |
|-------|-------------|
| Draft | Being built; not yet submitted |
| Submitted | Submitted, awaiting coordinator endorsement (if required) or Finance review |
| CoordinatorEndorsed | Coordinator has endorsed; awaiting Finance review |
| Approved | Finance has approved; not yet in SEPA batch |
| SepaSent | Included in SEPA file, payment in transit |
| Paid | Holded confirmed payment |
| Withdrawn | Withdrawn by submitter |

## Routing

| Route | Method | Auth | Action |
|-------|--------|------|--------|
| `/Expenses` | GET | Authenticated | Submitter dashboard |
| `/Expenses/New` | GET/POST | Authenticated | Create draft |
| `/Expenses/{id}` | GET | Authenticated (resource-based: owner + Finance) | Detail |
| `/Expenses/{id}/Edit` | GET/POST | Authenticated (owner, Draft only) | Edit draft |
| `/Expenses/{id}/Lines/*` | POST | Authenticated (owner) | Line mutations |
| `/Expenses/{id}/Submit` | POST | Authenticated (owner) | Submit |
| `/Expenses/{id}/Withdraw` | POST | Authenticated (owner, submitted states) | Withdraw |
| `/Expenses/{id}/Iban` | GET/POST | Authenticated (resource-based: self, FinanceAdmin with report context) | View/set IBAN |
| `/Expenses/Attachment/{id}` | GET | Authenticated (resource-based) | Download attachment |
| `/Expenses/Coordinator` | GET | Authenticated (coordinator) | Coordinator queue |
| `/Expenses/{id}/Endorse` | POST | Authenticated (coordinator, resource-based) | Endorse |
| `/Expenses/{id}/CoordinatorReject` | POST | Authenticated (coordinator, resource-based) | Coordinator reject |
| `/Expenses/Review` | GET | FinanceAdminOrAdmin | Finance review queue |
| `/Expenses/{id}/Approve` | POST | FinanceAdminOrAdmin (resource-based) | Approve |
| `/Expenses/{id}/Reject` | POST | FinanceAdminOrAdmin (resource-based) | Finance reject |
| `/Expenses/Sepa/Generate` | POST | FinanceAdminOrAdmin | Generate SEPA file (form on Review page) |
| `/Profile/{id}/Admin/RevealIban` | POST | AdminOnly | Reveal raw IBAN (audit-logged) |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Authenticated member | Submit, edit, withdraw own reports. View own reports. Set own IBAN. |
| Budget Coordinator | All member capabilities. Additionally: endorse or coordinator-reject reports in categories they coordinate. |
| FinanceAdmin, Admin | All coordinator capabilities. Additionally: full review queue, approve, finance-reject, category override, generate SEPA batch, confirm SEPA sent, view Holded sync status. |
| Admin | All FinanceAdmin capabilities. Additionally: reveal raw IBAN on admin user page (audit-logged). |

## Invariants

- A report follows the lifecycle: Draft → Submitted → (CoordinatorEndorsed →) Approved → SepaSent → Paid. Terminal alternates: Withdrawn (from Submitted/CoordinatorEndorsed/Approved). `ExpenseReportService` enforces all transitions; `IExpenseRepository` persists them atomically.
- A report cannot be submitted without at least one line. Every line must have an attachment at submit time.
- `Profile.Iban` must be non-null at submit time. `PayeeIban` is snapshotted at that moment; later IBAN changes do not affect in-flight reports.
- `PayeeIban` (snapshotted) and `Profile.Iban` (current) MUST pass through `IbanFormatter.Mask` before appearing in any log, audit entry, or error message (enforced by convention; memory atom `memory/code/iban-mask-in-logs.md`).
- The coordinator endorsement step is required only if the report's category has at least one budget coordinator (`CategoryRequiresCoordinatorEndorsementAsync`). Finance Admin may approve directly from Submitted if no coordinator is assigned.
- Resource-based authorization (`IbanAccessRequirement` / `IbanAccessHandler`) gates raw IBAN access: self, FinanceAdmin with non-Draft/non-Withdrawn report context, or Admin on admin page.
- `HoldedExpenseOutboxJob` drains the `holded_expense_outbox_events` in order. Transient errors increment `RetryCount`; permanent errors set `FailedPermanently` and stop retrying.
- `ExpensePaidPollingJob` processes at most 50 `SepaSent` reports per run (oldest `SepaSentAt` first). A 404 from Holded means the doc was deleted out-of-band — log warning, do not transition.
- SEPA pain.001 XML and Holded API request bodies are the only code paths that may contain a raw IBAN (not masked).

## Negative Access Rules

- Regular members **cannot** see other users' expense reports or attachments.
- Regular members **cannot** approve, reject, endorse (unless they are a coordinator for the relevant category), or generate SEPA files.
- Coordinators **cannot** approve or generate SEPA batches — those require FinanceAdmin/Admin.
- FinanceAdmin **cannot** reveal a raw IBAN on the admin user page — that action is Admin-only.
- No role **can** transition a report backwards in the state machine (e.g., un-approve, un-submit).
- No code path **may** log or emit a raw IBAN in logs, audit entries, or error messages — only masked form via `IbanFormatter.Mask`.

## Triggers

- On **submit**: `Profile.Iban` and `User.DisplayName` are snapshotted into `PayeeIban` / `PayeeName`. Audit entry `ExpenseSubmit` written.
- On **approve**: `HoldedExpenseOutboxEvent` (CreateIncomingDoc) queued. Audit entry `ExpenseApprove` written.
- On **category override**: `HoldedExpenseOutboxEvent` (UpdateIncomingDocTag) queued. Audit entry `ExpenseCategoryOverride` written.
- On **SEPA generate + confirm**: all included report IDs transition to `SepaSent`. Audit entries `ExpenseSepaSent` written.
- On **`ExpensePaidPollingJob` mark paid**: Audit entry `ExpensePaid` written.
- On **IBAN reveal (admin page)**: `AuditAction.IbanReveal` written recording actor + target user.
- **`HoldedExpenseOutboxJob`** runs every minute. **`ExpensePaidPollingJob`** runs every 15 minutes.
- **GDPR export** (`IUserDataContributor`): contributes `ExpenseReports` and `ExpenseAuditLog` slices. Chain-follows merge tombstones.

## Cross-Section Dependencies

- **Budget**: `IBudgetService.GetCategoryByIdAsync` — category metadata and coordinator team resolution. `ITeamService.GetEffectiveBudgetCoordinatorTeamIdsAsync` — coordinator-scope check.
- **Teams**: `ITeamService.IsUserCoordinatorOfTeamAsync` — coordinator endorsement gate.
- **Profiles**: `IProfileService.GetProfileAsync` — IBAN snapshot at submit time; masked IBAN for GDPR export.
- **Users/Identity**: `IUserService.GetByIdAsync` / `GetByIdsAsync` — display names for Holded contact name. `IUserService.GetMergedSourceIdsAsync` — GDPR merge-tombstone chain-follow.
- **AuditLog**: `IAuditLogService.LogAsync` — all lifecycle transitions logged. `GetFilteredEntriesAsync` — GDPR export.
- **Admin (Profiles section)**: `/Profile/{id}/Admin/RevealIban` lives in `ProfileController` (Profiles section) and calls `IProfileService.GetProfileAsync` + `IAuditLogService.LogAsync`.

## Architecture

**Owning services:** `ExpenseReportService`
**Owned tables:** `expense_reports`, `expense_lines`, `expense_attachments`, `holded_expense_outbox_events`
**Status:** (A) Migrated (2026-05-10, this PR).

- `ExpenseReportService` lives in `Humans.Application.Services.Expenses` and depends only on Application-layer abstractions.
- `ExpenseRepository` (impl `Humans.Infrastructure/Repositories/Expenses/ExpenseRepository.cs`, §15b Singleton + `IDbContextFactory`) is the only file that touches expense tables via `DbContext`.
- **Decorator decision — no caching decorator.** Expense data is mutable and user-specific; low-traffic at ~500 users.
- **Cross-domain navs** — none declared. All cross-section linkage is scalar FK only.
- **Cross-section calls** route through `IBudgetService`, `ITeamService`, `IProfileService`, `IUserService`, `IAuditLogService`.
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/ExpensesArchitectureTests.cs` pins the shape.

# Preapprovals — Data Access

## Preapprovals

Folder: `src/Sections/Humans.Preapprovals/Services/` (namespace
`Humans.Preapprovals.Services`). **DbContext:** `PreapprovalsDbContext`.
`PreapprovalRepository` (`Data/PreapprovalRepository.cs`, **Singleton**) injects
`IDbContextFactory<PreapprovalsDbContext>` directly. Owns `pre_programs`,
`pre_program_managers`, `pre_grants`, `pre_reminders_sent`.

### PreapprovalManagement (Scoped — class `PreapprovalService`)

One partial class (`PreapprovalService.*.cs`) registered once and resolved as
`IPreapprovalManagement` (internal), `IPreapprovalService` (cross-section reads),
`IPreapprovalWrite` (cross-section grant claim / release / consume, used by Expenses),
`IUserDataContributor` and `IUserMerge`. Repository: `IPreapprovalRepository`.

| Table | R/W |
|-------|-----|
| pre_programs | R/W |
| pre_program_managers | R/W (replaced wholesale per program; deleted on erasure) |
| pre_grants | R/W (pending applications deleted on erasure; merge re-points) |

Cross-section calls via `IUserService` (account resolution), `IFinanceService`
(`ListExpenseAccountsAsync` — the expense account a program books to) and
`IAuditLogService`, plus `IClock`. Delivery uses the scoped `PreapprovalNotices`.
No `IMemoryCache`.

### PreapprovalDeadlineReminders (Scoped, internal)

`PreapprovalRemindersJob` (`Jobs/`) calls this component directly. It reads grants
and their programs through `IPreapprovalRepository`, writes only the reminder
dedupe ledger `pre_reminders_sent`, and calls `IAuditLogService` after the marker
transaction commits. `PreapprovalNotices` handles required delivery before the stamp.
Selection, transactions, cancellation and retry behavior retain the same rules.

### PreapprovalNotices (Scoped, internal)

No repository. Shared by management and reminders; owns recipient resolution through
`IUserService`, delivery through `INotificationEmitter` and `IEmailService`, and
standing-notice completion through `INotificationAutoResolve`. Reuses
`PreapprovalsEmails` and the existing `PreapprovalService` logging category.

### PreapprovalsEmails / PreapprovalsEmailPreviews (Scoped)

No repository, no DB access, no cache. `PreapprovalsEmails` builds `EmailMessage` values from
`PreapprovalsResource` and `EmailSettings`; `PreapprovalsEmailPreviews` is the
`IEmailPreviewContributor` gallery entry over it.

---

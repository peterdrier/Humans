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
| pre_reminders_sent | R/W (reminder dedupe ledger) |

Cross-section calls via `IUserService` (display names), `IUserEmailService`
(notification targets), `IHoldedFinanceService` (`ListExpenseAccountsAsync` — the expense
account a program books to), `INotificationEmitter`, `INotificationAutoResolve`
(`ResolveBySourceKeyAsync` — clears a decided application's inbox item), `IEmailService`,
`IAuditLogService`, plus the section's own `PreapprovalsEmails` builder and `IClock`. No
`IMemoryCache`.

`PreapprovalRemindersJob` (`Jobs/`) drives the reminder pass through
`IPreapprovalManagement`.

### PreapprovalsEmails / PreapprovalsEmailPreviews (Scoped)

No repository, no DB access, no cache. `PreapprovalsEmails` builds `EmailMessage` values from
`PreapprovalsResource` and `EmailSettings`; `PreapprovalsEmailPreviews` is the
`IEmailPreviewContributor` gallery entry over it.

---

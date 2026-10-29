# Grants — Data Access

## Grants

Folder: `src/Sections/Humans.Grants/Services/` (namespace `Humans.Grants.Services`).
**DbContext:** `GrantsDbContext`. `GrantRepository` (`Data/GrantRepository.cs`, **Singleton**)
injects `IDbContextFactory<GrantsDbContext>` directly. Owns `grant_programs`,
`grant_program_managers`, `grants`.

### GrantManagement (Scoped — class `GrantService`)

One partial class (`GrantService.*.cs`) registered once and resolved as `IGrantManagement`
(internal), `IGrantWrite` (cross-section issue / revoke by a source section),
`IUserDataContributor` and `IUserMerge`. Repository: `IGrantRepository`.

| Table | R/W |
|-------|-----|
| grant_programs | R/W |
| grant_program_managers | R/W (added/removed per program; deleted on erasure; re-keyed on merge) |
| grants | R/W (deleted on erasure; re-keyed on merge, duplicate live grant revoked) |

Cross-section calls via `IUserService` (merged ids, language, names), `IUserEmailService`
(notification targets), `INotificationEmitter`, `IEmailService`, `IAuditLogService`, `ISettingsService` (events), plus the
section's own `GrantsEmails` builder and `IClock`. No `IMemoryCache`.

### GrantsEmails / GrantsEmailPreviews (Scoped)

No repository, no DB access, no cache. `GrantsEmails` builds `EmailMessage` values from
`GrantsResource` and `EmailSettings`; `GrantsEmailPreviews` is the `IEmailPreviewContributor`
gallery entry over it.

---

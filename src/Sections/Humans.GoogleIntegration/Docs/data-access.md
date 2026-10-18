# GoogleIntegration — Data Access

## Google Integration

Folder: `src/Sections/Humans.GoogleIntegration/Services/` — repository
under `Data/`; the Directory / Drive / Groups / Translation API clients
live under `Services/Workspace/`. **DbContext:**
`GoogleIntegrationDbContext`. `GoogleResourceRepository`,
`GoogleSyncOutboxRepository`, `GoogleSyncLogRepository` and
`SyncSettingsRepository` all inject
`IDbContextFactory<GoogleIntegrationDbContext>` directly. Owns
`SyncServiceSettings`, `GoogleSyncOutboxEvents`, `GoogleSyncLog`, and
`GoogleResources` (the last via `TeamResourceService`). The per-address
`UserEmails.GoogleEmailStatus` write goes through `IUserWrite`
(`TrySetGoogleEmailStatusFromSyncAsync`, targeting the canonical Google row)
per §15.

`GoogleSyncOutboxEvents` is written only by this section's own
`GoogleSyncOutboxService` / `GoogleSyncOutboxRepository`; `TeamService`
appends outbox events transactionally through `IGoogleSyncOutboxService`
inside a `TransactionScope` so the team mutation and the outbox append
stay atomic.

### GoogleWorkspaceSyncService (Scoped)

Repositories: `IGoogleResourceRepository`, `IGoogleSyncOutboxRepository`.

| Table | R/W |
|-------|-----|
| GoogleResources | R/W |
| GoogleSyncOutboxEvents | R/W |

Implements the internal `IGoogleSyncManagement` (which extends the public `IGoogleSyncService` and `IGoogleSyncWrite`). Cross-section calls via `IUserService`,
`IUserWrite`, `ITeamService`, `ISyncSettingsService`,
`IAuditLogService`, `IGoogleSyncLogService`, `IGoogleDirectoryClient`,
`IGoogleDrivePermissionsClient`, `IGoogleGroupProvisioningClient`,
`IGoogleGroupSync`, `IGoogleDriveSync`, `IGoogleDriveAccessSyncScheduler`,
and `IGoogleDriveAccessSource` (source-owned grant levels). No cache.

### GoogleGroupSyncService (Scoped)

No repository directly — operates over external clients and the
in-process `IEnumerable<IGoogleGroupMembershipSource>` (`TeamService` and
Camps' `CampRoleService`). Cross-section calls via `IGoogleGroupMembershipClient`,
`IGoogleGroupProvisioningClient`, `ITeamResourceGoogleClient`,
`ITeamResourceManagement`, `ITeamService`, `IUserService`, `IUserWrite`,
`IUserEmailService`, `ISyncSettingsService`, `IAuditLogService`,
`IGoogleSyncLogService`, `IGoogleRemovalNotificationService`,
`IGoogleGroupSyncScheduler`. No direct DB access, no cache.

### GoogleDriveAccessSyncService (Scoped)

Uses its own `IGoogleResourceRepository` to load linked Drive metadata and record errors/sync stamps. Reconciles `IGoogleDriveAccessSource` claims from Teams and Workgroups through `IGoogleDrivePermissionsClient`; the connector never derives team membership. Cross-section calls use `IUserService`, `IUserWrite`, `IUserEmailService`, `ITeamService` (retirement status only), `ISyncSettingsService`, `IGoogleSyncLogService`, and `IGoogleRemovalNotificationService`. Resource retirement goes through the existing `ITeamResourceManagement` audit path. No cache.

### GoogleAdminService (Scoped)

No repository — no DbContext
or repository dependency; all cross-section data access routes through the
owning services (§15 pattern). Cross-section calls via `IGoogleWorkspaceUserService`,
`IGoogleSyncManagement`, `ITeamService`, `ITeamWrite`, `ITeamResourceManagement`,
`IUserService`, `IUserWrite`, `IUserEmailService`, `IAuditLogService`, plus
`ILogger<GoogleAdminService>`. No cache.

### GoogleWorkspaceUserService (Scoped)

No repository. Thin facade over `IWorkspaceUserDirectoryClient`
(`Services/Workspace/`). No DB access, no cache.

### EmailProvisioningService (Scoped)

No repository. Wraps the section's `IGoogleWorkspaceUserService` to provision
Google Workspace mailboxes; cross-section calls via `IUserService`,
`IUserEmailService`, `ITeamService`, `IEmailService` (messages built by
`GoogleIntegrationEmails`), `INotificationEmitter`, `IAuditLogService`. No direct
DB access, no cache.

### GoogleIntegrationEmails (Scoped, internal)

No repository. Pure builder — reads `GoogleIntegrationResource` (via
`IStringLocalizer<GoogleIntegrationResource>`), writes nothing. Returns
`EmailMessage` values for `EmailProvisioningService` and
`GoogleRemovalNotificationService` to pass to `IEmailService.SendAsync`.
No DB access, no cache.

### GoogleIntegrationEmailPreviews (Scoped)

No repository. Read-only gallery contributor (`IEmailPreviewContributor`,
registered in `Section.cs`) — builds one sample per template via
`GoogleIntegrationEmails` for `/Email/EmailPreview`. No DB access, no cache.

### SyncSettingsService (Scoped)

Repository: `ISyncSettingsRepository`.

| Table | R/W |
|-------|-----|
| SyncServiceSettings | R/W |

Calls `IAuditLogService` after each successful mode save, using the persisted row id and admin actor. No cache.

### GoogleSyncOutboxService (Scoped)

Repository: `IGoogleSyncOutboxRepository`.

| Table | R/W |
|-------|-----|
| GoogleSyncOutboxEvents | W (`AddAsync` / `AddRangeAsync`) |

Thin write surface over the outbox table so other sections append
events through a service interface rather than reaching into the repository.
`TeamService` calls `IGoogleSyncOutboxService.AddAsync` /
`AddRangeAsync` inside a `TransactionScope` to keep each team mutation
atomic with its outbox event. No cross-section calls, no cache.

### GoogleSyncLogService (Scoped)

Repository: `IGoogleSyncLogRepository`.

| Table | R/W |
|-------|-----|
| GoogleSyncLog | R/W |

Implements the internal `IGoogleSyncLogService` (writes, plus the
`/Google/ActivityLog` read, 500-row cap), the public read side
(`IGoogleSyncLogViewer`) backing `<vc:google-sync-log>`, and
`IUserDataContributor` for the GDPR export and erasure (design-rules §8a — the
table holds `UserId`/`UserEmail`). Writes are best-effort — a repository failure is logged
at Error and swallowed so a sync never fails on its own bookkeeping. The
contributor read is uncapped, unlike the 200-row display reads. Cross-section
calls: `ITeamResourceManagement` (resource display names),
`IUserService.GetUserInfoAsync`, whose `UserInfo.AllUserIds` chain-follows
merge tombstones on per-user reads, and `IUserEmailService` for erasure, which
suspends the human's Workspace account through a lazily resolved
`IGoogleAdminService` before deleting their rows. No cache.

### GoogleSyncHistoryMigrationService (Scoped, `internal`)

Repository: `IGoogleSyncLogRepository`.

| Table | R/W |
|-------|-----|
| GoogleSyncLog | R/W |

Backs the one-time `/Google/Admin/SyncHistoryMigration` screen. Reads the
Google-sync history left on `audit_log` through `ILegacyGoogleSyncAuditReader`
and appends it here in one batch; the copy keeps the source audit row's id, so
`GetExistingIdsAsync` makes a re-run a no-op. Never writes to `audit_log`.
Cross-section calls: `ILegacyGoogleSyncAuditReader` and
`ITeamResourceManagement` (resource display names for the preview table). No cache.
Comes out with the six Google columns on `audit_log`.

### TeamResourceService (Scoped)

Repository: `IGoogleResourceRepository`.

| Table | R/W |
|-------|-----|
| GoogleResources | R/W |

Sole owner of `google_resources`. All consumers call
`ITeamResourceService` / `ITeamResourceManagement` read methods rather than touching
`DbSet<GoogleResource>`; ownership is enforced by the section's `internal`
`GoogleIntegrationDbContext` and `IGoogleResourceRepository` plus
HUM0008/HUM0009/HUM0025. Cross-section calls via
`ITeamService`, `ITeamResourceGoogleClient`, `IGoogleDrivePermissionsClient`,
`IAuditLogService`, plus `IServiceProvider` to lazy-resolve
`IRoleAssignmentService` (breaks a DI cycle). No cache.

### GoogleSyncOutboxProcessor (Scoped, `internal`)

Repository: `IGoogleSyncOutboxRepository`.

| Table | R/W |
|-------|-----|
| GoogleSyncOutboxEvents | R/W (`GetProcessingBatchAsync` / `MarkProcessedAsync` / `MarkPermanentlyFailedAsync` / `IncrementRetryAsync`) |

The outbox drain, run via Hangfire.
Outbox processing writes Google email status back to the user:
`AddUserToTeamResources` / `RemoveUserFromTeamResources`
events are dispatched via `IGoogleSyncManagement`, then the user's
`GoogleEmailStatus` is written through `IUserWrite.TrySetGoogleEmailStatusFromSyncAsync`
(`Valid` when the grant result is `Accepted`, `Rejected` on a permanent
vendor failure — HTTP 400/403/404). Cross-section calls via `IUserService`,
`IUserWrite`, `ITeamService`, `IGoogleSyncManagement`, plus
`IGoogleDriveActivityClient` (credential signal), `IHumansMetrics` and `IClock`.
No `IMemoryCache`.

### GoogleRemovalNotificationService (Scoped)

No repository. Wraps `IUserEmailService` + `IUserService` +
`IEmailService` to send notifications when access is removed. No direct
DB access, no cache.

### GoogleTranslationService (Scoped)

No repository. Thin §15-compliant facade over `IGoogleTranslationClient`
(`Services/Workspace/`). Exists so cross-section callers (Survey authoring —
`SurveyService.PreFillTranslationsAsync`) depend on a GoogleIntegration
service interface rather than the raw connector client. No DB access,
no cache.

---



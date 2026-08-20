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
`UserEmails.GoogleEmailStatus` write goes through `IUserService`
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

Implements `IGoogleSyncService`. Cross-section calls via `IUserService`,
`ITeamService`, `IUserEmailService`, `ISyncSettingsService`,
`IAuditLogService`, `IGoogleDirectoryClient`, `IGoogleDrivePermissionsClient`,
`IGoogleGroupProvisioningClient`, `IGoogleGroupSync` (sync orchestrator),
`ITeamResourceGoogleClient`, `IGoogleRemovalNotificationService`. Lazy
`IServiceProvider` resolution for parallel/per-batch scope creation. No
`IMemoryCache`.

### GoogleGroupSyncService (Scoped)

No repository directly — operates over external clients and the
in-process `IEnumerable<IGoogleGroupMembershipSource>` (currently only
`TeamService`). Cross-section calls via `IGoogleGroupMembershipClient`,
`IGoogleGroupProvisioningClient`, `ITeamResourceGoogleClient`,
`ITeamResourceService`, `ITeamService`, `IUserService`,
`IUserEmailService`, `ISyncSettingsService`, `IAuditLogService`,
`IGoogleRemovalNotificationService`, `IGoogleGroupSyncScheduler`. No
direct DB access, no cache.

### GoogleAdminService (Scoped)

No repository — no DbContext
or repository dependency; all cross-section data access routes through the
owning services (§15 pattern). Cross-section calls via `IGoogleWorkspaceUserService`,
`IGoogleSyncService`, `ITeamService`, `ITeamResourceService`,
`IUserService`, `IUserEmailService`, `IAuditLogService`, plus
`ILogger<GoogleAdminService>`. No cache.

### GoogleWorkspaceUserService (Scoped)

No repository. Thin facade over `IWorkspaceUserDirectoryClient`
(`Services/Workspace/`). No DB access, no cache.

### EmailProvisioningService (Scoped)

No repository. Wraps `IGoogleAdminService` + `IUserEmailService` +
`IAuditLogService` to provision Google Workspace mailboxes. No direct DB
access, no cache.

### SyncSettingsService (Scoped)

Repository: `ISyncSettingsRepository`.

| Table | R/W |
|-------|-----|
| SyncServiceSettings | R/W |

No cross-section calls, no cache.

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

Implements the internal write side (`IGoogleSyncLogService`), the public read
side (`IGoogleSyncLogViewer`) backing `<vc:google-sync-log>`, and
`IUserDataContributor` for the GDPR export (design-rules §8a — the table holds
`UserId`/`UserEmail`). Writes are best-effort — a repository failure is logged
at Error and swallowed so a sync never fails on its own bookkeeping. The
contributor read is uncapped, unlike the 200-row display reads. Cross-section
calls: `ITeamResourceService` (resource display names) and
`IUserServiceRead.GetMergedSourceIdsAsync` (chain-follow merge tombstones on
per-user reads). No cache.

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
`ITeamResourceService` (resource display names for the preview table). No cache.
Comes out with the six Google columns on `audit_log`.

### TeamResourceService (Scoped)

Repository: `IGoogleResourceRepository`.

| Table | R/W |
|-------|-----|
| GoogleResources | R/W |

Sole owner of `google_resources`. All consumers call
`ITeamResourceService` read methods rather than touching
`DbSet<GoogleResource>`; ownership is enforced by the section's `internal`
`GoogleIntegrationDbContext` and `IGoogleResourceRepository` plus
HUM0008/HUM0009/HUM0025. Cross-section calls via
`ITeamService`, `ITeamResourceGoogleClient`, `IGoogleDrivePermissionsClient`,
`IAuditLogService`, plus `IServiceProvider` to break a DI cycle. No
cache.

### GoogleSyncOutboxProcessor (Scoped, `internal`)

Repositories: `IGoogleSyncOutboxRepository`, `IGoogleResourceRepository`
(read-only — active-resource check before marking Google email status
`Valid`).

| Table | R/W |
|-------|-----|
| GoogleSyncOutboxEvents | R/W (`GetProcessingBatchAsync` / `MarkProcessedAsync` / `MarkPermanentlyFailedAsync` / `IncrementRetryAsync`) |
| GoogleResources | R (`GetActiveByTeamIdAsync`, via `IGoogleResourceRepository`) |

The outbox drain, run via Hangfire.
`[CrossSectionWrite("Outbox processing writes Google email status back to
the user.")]`-marked: `AddUserToTeamResources` / `RemoveUserFromTeamResources`
events are dispatched via `IGoogleSyncService`, then the user's
`GoogleEmailStatus` is written through `IUserService.TrySetGoogleEmailStatusFromSyncAsync`
(`Valid` on a successful add with active resources, `Rejected` on a permanent
vendor failure — HTTP 400/403/404). Cross-section calls via `IUserService`,
`ITeamServiceRead`, `IGoogleSyncService`, plus `IHumansMetrics` and `IClock`.
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



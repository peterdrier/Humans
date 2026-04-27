<!-- freshness:triggers
  src/Humans.Application/Services/GoogleIntegration/**
  src/Humans.Domain/Entities/SyncServiceSettings.cs
  src/Humans.Domain/Entities/GoogleSyncOutboxEvent.cs
  src/Humans.Domain/Entities/GoogleResource.cs
  src/Humans.Domain/Constants/GoogleSyncOutboxEventTypes.cs
  src/Humans.Infrastructure/Data/Configurations/SyncServiceSettingsConfiguration.cs
  src/Humans.Infrastructure/Data/Configurations/GoogleSyncOutboxEventConfiguration.cs
  src/Humans.Infrastructure/Data/Configurations/GoogleResourceConfiguration.cs
  src/Humans.Web/Controllers/GoogleController.cs
-->
<!-- freshness:flag-on-change
  Sync mode invariants, Shared-Drive-only constraint, GoogleEmailStatus rules, and reconciliation gateway operations — review when Google Integration services/entities/controller change.
-->

# Google Integration — Section Invariants

Shared-Drive-only Google resource sync: Drive folders, Groups, Workspace accounts, reconciliation, Drive-activity monitoring.

## Concepts

- **Google Resources** are Shared Drive folders, Shared Drives, Drive files, and Google Groups linked to a team. When a human joins or leaves a team, their access to the team's linked Google resources is automatically managed. Resource rows live in `google_resources` and are owned by Team Resources (sub-aggregate of Teams) per design-rules §8.
- **Sync Mode** controls how the system interacts with Google APIs for each service type. Modes are: None (disabled), AddOnly (grant access but never revoke), or AddAndRemove (full bidirectional sync).
- **Reconciliation** compares the expected Google resource state (based on team membership) against the actual Google resource state, detecting drift.
- The **sync outbox** queues resource-level sync events for processing by a background job.

## Data Model

### SyncServiceSettings

**Table:** `sync_service_settings`

Per-service sync-mode configuration. Holds `UpdatedByUserId` (FK to `User`); the `UpdatedByUser` nav is still defined on the entity for EF cascade wiring (`OnDelete(DeleteBehavior.SetNull)`) but is not used by `SyncSettingsService` — display names are resolved via `IUserService.GetByIdsAsync` at the controller. One row per `SyncServiceType`, seeded with `SyncMode.None` for `GoogleDrive`, `GoogleGroups`, and `Discord` (reserved GUID block 0002).

### GoogleSyncOutboxEvent

**Table:** `google_sync_outbox`

Flat record — already clean. Holds `TeamId`/`UserId` scalars with no navs. Two event types defined in `GoogleSyncOutboxEventTypes`: `AddUserToTeamResources` and `RemoveUserFromTeamResources`.

### Google resource entities

`GoogleResource` rows (`google_resources` table) are documented under `docs/sections/Teams.md` (Team Resources sub-aggregate) — owned by `TeamResourceService`. Google Integration services call `ITeamResourceService` rather than querying the table.

### External-API surfaces

`IGoogleSyncService` (`GoogleWorkspaceSyncService`), `IGoogleAdminService`, `IGoogleWorkspaceUserService`, and `IDriveActivityMonitorService` wrap Google Drive / Groups / Admin SDK / Drive Activity HTTP APIs. They own no persistent tables beyond the two above; their "repository" surface is the Google HTTP client (via the connector interfaces listed under [Connector clients](#connector-clients) below), not EF Core.

## Controller Structure

All Google integration management is consolidated in `GoogleController` (`/Google`), with team-level resource linking remaining in `TeamAdminController`.

| Route | Purpose |
|-------|---------|
| `/Google` | Google integration dashboard |
| `/Google/SyncSettings` | Per-service sync mode management |
| `/Google/Sync` | Resource sync status dashboard (preview/execute) |
| `/Google/AllGroups` | Domain-wide group management |
| `/Google/Accounts` | @nobodies.team Workspace account management |
| `/Google/Human/{id}/SyncAudit` | Per-human sync audit |
| `/Google/Sync/Resource/{id}/Audit` | Per-resource sync audit |
| `/Google/CheckGroupSettings`, `/Google/CheckEmailMismatches` | Diagnostic tools |

Team-level resource linking stays at `/Teams/{slug}/Resources` in `TeamAdminController`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin | Manage sync settings (per-service mode). Trigger manual syncs and execute sync actions. View reconciliation results. Check and remediate Google Group settings drift. Link unlinked groups to teams. Review and apply email backfill corrections. Manage @nobodies.team Workspace accounts (provision, suspend, reactivate, reset password, link, generate/invalidate 2FA backup codes). Provision per-human @nobodies.team email |
| TeamsAdmin, Board, Admin | View resource sync status dashboard. Link and unlink Google resources (Drive folders, Groups) to teams via `TeamAdminController`. View resource status. Trigger per-resource sync |
| Coordinator | Link and unlink Google resources for their own department (via `TeamAdminController`). Trigger per-resource sync for their own department |
| Board, Admin | View Drive activity anomaly check results. View sync audit logs |
| HumanAdmin, Board, Admin | View per-human Google sync audit |
| Background jobs | Automated sync: system team sync (hourly), resource reconciliation (daily at 03:00), sync outbox processing, resource provisioning |

## Invariants

- All Google Drive resources are on Shared Drives. The system does not use regular (My Drive) folders.
- Only direct permissions are managed by the system. Inherited Shared Drive permissions are excluded from drift detection and sync.
- Drive folders with `RestrictInheritedAccess = true` have `inheritedPermissionsDisabled` enforced by the reconciliation job. Drift (manual re-enablement of inheritance) is detected and corrected automatically, with an audit trail entry.
- Sync settings are per-service (Google Drive, Google Groups, Discord). Setting a service to None disables sync without redeploying.
- A human's Google service email is their @nobodies.team email if provisioned, otherwise their OAuth login email.
- Each human has a `GoogleEmailStatus` (`Unknown`, `Valid`, `Rejected`). When Google permanently rejects an email (HTTP 400/403/404), the status is set to `Rejected` and new outbox events are not enqueued for that human. When a human changes their Google email, the status resets to `Unknown` and fresh sync events are enqueued.
- Permanent Google API errors (HTTP 400, 403, 404) mark outbox events as `FailedPermanently` and stop retrying immediately. Transient errors (5xx, 429, etc.) continue retrying up to the configured limit.
- The system authenticates to Google APIs as a service account — no domain-wide delegation or user impersonation.
- There are exactly four gateway operations that can modify Google access, and all enforce the current sync mode before executing.

## Negative Access Rules

- TeamsAdmin and Board **cannot** manage sync settings — that is Admin-only.
- Coordinators **cannot** manage sync settings, execute bulk sync actions, or remediate Google Group settings drift.
- Regular humans have no access to Google resource management.

## Triggers

- When team membership changes, sync outbox events are queued for Google Group and Drive updates.
- When a human's Google email changes, `GoogleEmailStatus` resets to `Unknown` and fresh sync events are enqueued for all current team memberships.
- When a Google resource is linked to a team, current team members are synced to that resource.
- When a Google resource is unlinked, managed permissions are removed (if sync mode allows).
- The system team sync job runs hourly, reconciling system team membership.
- The reconciliation job runs daily at 03:00, detecting drift between expected and actual Google resource state.

## Cross-Section Dependencies

- **Teams:** `ITeamService` / `ITeamResourceService` — Google resources are linked per team. Team membership drives Google Group and Drive access.
- **Profiles:** `IUserEmailService` / `IGoogleServiceEmailResolver` — a human's Google service email determines the email address used for Google Groups and Drive access.
- **Admin:** Sync settings management is Admin-only.
- **Onboarding:** Volunteer activation triggers system team sync, which cascades to Google Group membership.

## Architecture

**Owning services:** `GoogleWorkspaceSyncService` (implements `IGoogleSyncService`), `GoogleAdminService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, `EmailProvisioningService`
**Owned tables:** `sync_service_settings`, `google_sync_outbox`
**Status:** (A) Fully migrated. All Google Integration business services live in `Humans.Application.Services.GoogleIntegration`. Migration completed under umbrella issue nobodies-collective/Humans#554 across multiple parts: `GoogleAdminService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, `EmailProvisioningService` in peterdrier/Humans PR #267 (issue nobodies-collective/Humans#289); `IGoogleSyncOutboxRepository` extracted in Part 1 (2026-04-23); SDK bridge interfaces (`IGoogleDirectoryClient`, `IGoogleDrivePermissionsClient`, `IGoogleGroupMembershipClient`, `IGoogleGroupProvisioningClient`) extracted in Part 2a (issue nobodies-collective/Humans#574, PR #302); `GoogleWorkspaceSyncService` moved to Application in Part 2b (issue nobodies-collective/Humans#575, 2026-04-23); and the last direct-DbContext consumers (`ProcessGoogleSyncOutboxJob`, `GoogleController.SyncOutbox`) flipped onto the repository surface in Part 2c (issue nobodies-collective/Humans#576, 2026-04-23). The section now has zero non-repository direct `DbSet<GoogleSyncOutboxEvent>` / `DbSet<GoogleResource>` / `DbSet<SyncServiceSettings>` reads or writes across Application + Web layers.

### Repositories

- **`ISyncSettingsRepository`** — owns `sync_service_settings`
  - Aggregate-local navs kept: none
  - Cross-domain navs: `SyncServiceSettings.UpdatedByUser` is still defined on the entity for EF cascade wiring but is not used by `SyncSettingsService` — display names are resolved via `IUserService.GetByIdsAsync` at the controller (`GoogleController.SyncSettings`).
- **`IGoogleSyncOutboxRepository`** — owns `google_sync_outbox`
  - Aggregate-local navs kept: none (flat record)
  - Cross-domain navs stripped: none (entity already holds only `TeamId`/`UserId` scalars)
  - Surface: count queries (`NotificationMeterProvider`, `HumansMetricsService`, `SendAdminDailyDigestJob`, `GoogleWorkspaceSyncService.GetFailedSyncEventCountAsync`), admin read (`GetRecentAsync`), and the processor cycle (`GetProcessingBatchAsync` / `MarkProcessedAsync` / `MarkPermanentlyFailedAsync` / `IncrementRetryAsync`) used by `ProcessGoogleSyncOutboxJob`.
  - Enqueue: there is no outbox-only enqueue site — every write rides alongside a `TeamMember` mutation via `TeamRepository.AddMemberWithOutboxAsync` / `ApproveRequestWithMemberAsync` / `MarkMemberLeftWithOutboxAsync`, which persist the outbox row atomically with the membership change inside the Teams transaction boundary (§6d).
- **`IGoogleResourceRepository`** — narrow writes to the sibling-owned `google_resources` table; the table itself is owned by `TeamResourceService` (Teams section) per §8. `GoogleWorkspaceSyncService` uses this repository for the writes its reconciliation loop has to make atomically; broader reads/writes of `google_resources` go through `ITeamResourceService`.
- **`IDriveActivityMonitorRepository`** — owns the `DriveActivityMonitor:LastRunAt` key in `system_settings` (per-key ownership per §15) plus anomaly audit-entry persistence and the local-DB people-ID fallback for `DriveActivityMonitorService`.

### Connector clients

`IGoogleWorkspaceUserService`, `IGoogleAdminService`, `IDriveActivityMonitorService`, and the bulk of `IGoogleSyncService` (`GoogleWorkspaceSyncService`) are wrappers around Google Drive / Groups / Admin SDK / Drive Activity HTTP calls. The Application-layer services depend only on shape-neutral connector interfaces — `IGoogleDirectoryClient`, `IGoogleDrivePermissionsClient`, `IGoogleGroupMembershipClient`, `IGoogleGroupProvisioningClient`, `ITeamResourceGoogleClient`, `IGoogleDriveActivityClient`, `IWorkspaceUserDirectoryClient` — so they never import `Google.Apis.*` (design-rules §13). The real Google-backed implementations and dev-mode stubs live in `Humans.Infrastructure/Services/GoogleWorkspace/`.

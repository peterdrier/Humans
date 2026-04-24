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

Per-service sync-mode configuration. Target: `UpdatedByUser → UpdatedByUserId` (nav stripped; display name resolved via `IUserService` at the caller).

### GoogleSyncOutboxEvent

**Table:** `google_sync_outbox_events`

Flat record — already clean. Holds `TeamId`/`UserId` scalars with no navs.

### Google resource entities

`GoogleResource` rows (`google_resources` table) are documented under `docs/sections/Teams.md` (Team Resources sub-aggregate) — owned by `TeamResourceService`. Google Integration services call `ITeamResourceService` rather than querying the table.

### External-API surfaces

`IGoogleWorkspaceUserService`, `IGoogleAdminService`, `IDriveActivityMonitorService`, and most of `IGoogleWorkspaceSyncService` are **connectors** to Google Drive / Groups / Admin SDK HTTP APIs. They own no persistent tables beyond the two above. Per design-rules §7 they still need per-service caching and metrics/retry decorators, but their "repository" surface is the Google HTTP client, not EF Core. `IGoogleDriveActivityClient` is the shape-neutral connector for Drive Activity + Admin Directory calls (PR for sub-task nobodies-collective/Humans#554).

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
| Admin | Manage sync settings (per-service mode). Trigger manual syncs and execute sync actions. View reconciliation results. Check and remediate Google Group settings drift. Link unlinked groups to teams. Review and apply email backfill corrections. Manage @nobodies.team Workspace accounts (provision, suspend, reactivate, reset password, link). Provision per-human @nobodies.team email |
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

**Owning services:** `GoogleSyncService`, `GoogleAdminService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, `EmailProvisioningService`, `GoogleWorkspaceSyncService`
**Owned tables:** `sync_service_settings`, `google_sync_outbox_events`
**Status:** (B) Partially migrated. `GoogleAdminService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, `EmailProvisioningService` migrated in peterdrier/Humans PR #267 (issue nobodies-collective/Humans#289) — all now in `Humans.Application.Services.GoogleIntegration`. **`GoogleWorkspaceSyncService` remains in `Humans.Infrastructure/Services/`** and still injects `HumansDbContext` directly — its migration is the remaining work under umbrella issue nobodies-collective/Humans#554.

> **Note:** this is the highest fan-in section of the refactor — expect the largest touch surface outside Profiles when the remaining pieces land.

### Target repositories

- **`ISyncSettingsRepository`** — owns `sync_service_settings`
  - Aggregate-local navs kept: none
  - Cross-domain navs stripped: `SyncServiceSettings.UpdatedByUser` → `UpdatedByUserId` only (resolve display via `IUserService` at caller)
- **`IGoogleSyncOutboxRepository`** — owns `google_sync_outbox_events`
  - Aggregate-local navs kept: none (flat record)
  - Cross-domain navs stripped: none (entity already holds only `TeamId`/`UserId` scalars)
  - **Status:** extracted in Part 1 of issue #554 (2026-04-23). The count surface covers `NotificationMeterProvider`, `HumansMetricsService`, `SendAdminDailyDigestJob`, and `GoogleWorkspaceSyncService.GetFailedSyncEventCountAsync`. **Part 2c (issue #576, 2026-04-23)** extended the interface with a `GetRecentAsync` admin read and the full processor cycle (`GetProcessingBatchAsync` / `MarkProcessedAsync` / `MarkPermanentlyFailedAsync` / `IncrementRetryAsync`), flipping `ProcessGoogleSyncOutboxJob` and `GoogleController.SyncOutbox` onto the repo. `TeamService` is already clean — it builds the outbox entity and hands it to `TeamRepository.AddMemberWithOutboxAsync` / `ApproveRequestWithMemberAsync` / `MarkMemberLeftWithOutboxAsync`, which persist the outbox row atomically with the `TeamMember` state change inside the Teams transaction boundary (§6d). No enqueue method was added to `IGoogleSyncOutboxRepository` because there is no outbox-only enqueue site: every write must ride alongside a `TeamMember` mutation, and that co-persistence is the Teams repo's job.
- Note: `IGoogleWorkspaceUserService`, `IGoogleAdminService`, `IDriveActivityMonitorService`, and the bulk of `IGoogleWorkspaceSyncService` are **external-API wrappers** around Google Drive/Groups/Admin SDK HTTP calls. They do not own persistent tables beyond the two above. Per §7 they still need per-service caching and metrics/retry decorators, but their "repository" surface is the Google HTTP client, not EF Core. Their only legitimate EF touchpoints are `ISyncSettingsRepository` (to read the current `SyncMode` gate before each gateway op) and `IGoogleSyncOutboxRepository` (to enqueue/dequeue outbox events).
- All other table access currently performed by these services belongs to other sections and must move behind `ITeamService` / `ITeamResourceService` / `IUserService` / `IProfileService` / `ITeamMemberService` calls.

### Current violations

Baseline 2026-04-15 (updates marked inline):

- **Cross-domain `.Include()` calls (18 total):**
  - `GoogleWorkspaceSyncService.cs:196-197, 219-220` — `TeamMembers.Include(tm => tm.User).Include(tm => tm.Team)` (Users + Teams)
  - `GoogleWorkspaceSyncService.cs:676` — `TeamMembers.Include(tm => tm.User)` (Users)
  - `GoogleWorkspaceSyncService.cs:926, 1021, 1046, 1699, 1845` — `GoogleResources.Include(r => r.Team)` (Team Resources → Teams)
  - `GoogleWorkspaceSyncService.cs:1807` — `Users.Include(u => u.TeamMemberships)` (Users → Teams)
  - `GoogleWorkspaceSyncService.cs:2488` — `UserEmails.Include(ue => ue.User)` (Profiles → Users)
  - ~~`GoogleAdminService.cs:61, 338, 528`~~ — service migrated to Application in PR #267; cross-domain `.Include` calls addressed in the migration.
  - ~~`SyncSettingsService.cs:24`~~ — migrated in PR #267.
  - ~~`EmailProvisioningService.cs:55-56`~~ — migrated in PR #267.
- **Cross-section direct DbContext reads (~41 call sites, all in the remaining `GoogleWorkspaceSyncService`):**
  - **Team Resources (`GoogleResources`, 20 sites)** — `GoogleWorkspaceSyncService.cs:249, 284, 329, 402, 433, 527, 664, 744, 775, 805, 925, 1020, 1045, 1653, 1698, 1714, 1778, 1844, 2299, 2410`. Entire CRUD surface against `google_resources` lives in this service despite §8 assigning the table to `TeamResourceService`.
  - **Teams (`Teams`, 3 sites)** — `GoogleWorkspaceSyncService.cs:801, 1644, 2122`
  - **Teams (`TeamMembers`, 3 sites)** — `GoogleWorkspaceSyncService.cs:194, 217, 675`
  - **Users (4 sites)** — `GoogleWorkspaceSyncService.cs:492, 755, 1806, 2066`
  - ~~`DriveActivityMonitorService.cs:438, 464, 473` (SystemSettings)~~ — migrated in PR #267; now routes through owned `IDriveActivityMonitorRepository` for the `DriveActivityMonitor:LastRunAt` key. Each `system_settings` consumer owns its own key-space.
- **Inline `IMemoryCache` usage in service methods:** None found in the remaining Infrastructure-layer services. Caching, where present, is done inside the Google API client layer, not the EF-facing service code. Caching decorators will be introduced alongside the repositories rather than extracted from existing code.
- **Cross-domain nav properties on this section's entities:**
  - `SyncServiceSettings.UpdatedByUser` (→ `User`) — must be dropped from the entity; keep only `UpdatedByUserId`.
  - `GoogleSyncOutboxEvent` — clean.

### Touch-and-clean guidance

- **Do not add new `_dbContext.GoogleResources.*` calls in `GoogleWorkspaceSyncService`.** That table is owned by `TeamResourceService` — new reads/writes must go through `ITeamResourceService`. The 20 existing offenders are grandfathered; nothing new lands.
- **Replace `.Include(tm => tm.User).Include(tm => tm.Team)`** at `GoogleWorkspaceSyncService.cs:196-197, 219-220, 676` with an `ITeamMemberService` / `ITeamService` / `IUserService` call that returns a narrow DTO.
- **In `GoogleAdminService` / `GoogleWorkspaceUserService` / `EmailProvisioningService` / `DriveActivityMonitorService`** (all post-#267), do not reintroduce `_dbContext.Users` / `UserEmails` / `Profiles` / `TeamMembers` / `SystemSettings` reads — route through `IUserService`, `IProfileService`, `IUserEmailService`, `ITeamMemberService`, and the owning-key repository.
- `system_settings` ownership is resolved per-key: each consumer owns its own key-space via its own repository. Do not add new shared SystemSettings repositories that cross key ownership.

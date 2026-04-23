# Google Integration — Section Invariants

## Controller Structure

All Google integration management is consolidated in `GoogleController` (`/Google`), with team-level resource linking remaining in `TeamAdminController`. Key routes:

- `/Google` — Google integration dashboard
- `/Google/SyncSettings` — per-service sync mode management
- `/Google/Sync` — resource sync status dashboard (preview/execute)
- `/Google/AllGroups` — domain-wide group management
- `/Google/Accounts` — @nobodies.team Workspace account management
- `/Google/Human/{id}/SyncAudit` — per-human sync audit
- `/Google/Sync/Resource/{id}/Audit` — per-resource sync audit
- `/Google/CheckGroupSettings`, `/Google/CheckEmailMismatches` — diagnostic tools

Team-level resource linking stays at `/Teams/{slug}/Resources` in `TeamAdminController`.

## Concepts

- **Google Resources** are Shared Drive folders, Shared Drives, Drive files, and Google Groups linked to a team. When a human joins or leaves a team, their access to the team's linked Google resources is automatically managed.
- **Sync Mode** controls how the system interacts with Google APIs for each service type. Modes are: None (disabled), AddOnly (grant access but never revoke), or AddAndRemove (full bidirectional sync).
- **Reconciliation** compares the expected Google resource state (based on team membership) against the actual Google resource state, detecting drift.
- The **sync outbox** queues resource-level sync events for processing by a background job.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Admin | Manage sync settings (per-service mode). Trigger manual syncs and execute sync actions. View reconciliation results. Check and remediate Google Group settings drift. Link unlinked groups to teams. Review and apply email backfill corrections. Manage @nobodies.team Workspace accounts (provision, suspend, reactivate, reset password, link). Provision per-human @nobodies.team email |
| TeamsAdmin, Board, Admin | View resource sync status dashboard. Link and unlink Google resources (Drive folders, Groups) to teams via TeamAdminController. View resource status. Trigger per-resource sync |
| Coordinator | Link and unlink Google resources for their own department (via TeamAdminController). Trigger per-resource sync for their own department |
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

- **Teams**: Google resources are linked per team. Team membership drives Google Group and Drive access.
- **Profiles**: A human's Google service email determines the email address used for Google Groups and Drive access.
- **Admin**: Sync settings management is Admin-only.
- **Onboarding**: Volunteer activation triggers system team sync, which cascades to Google Group membership.

## Architecture — Current vs Target

See `docs/architecture/design-rules.md` for the full rules.

**Owning services:** `GoogleSyncService`, `GoogleAdminService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, `EmailProvisioningService`
**Owned tables:** `google_resources`, `sync_service_settings`

## Target Architecture Direction

> **Status:** This section currently follows the "services in Infrastructure, direct DbContext" model. It will be migrated to the repository/store/decorator pattern per [`../architecture/design-rules.md`](../architecture/design-rules.md). **Delete this block once the migration lands and this section's services live in `Humans.Application` with `*Repository.cs` impls in `Humans.Infrastructure/Repositories/`.** This is the highest fan-in section of the refactor — expect the largest touch surface outside Profiles.

### Target repositories

- **`ISyncSettingsRepository`** — owns `sync_service_settings`
  - Aggregate-local navs kept: none (`SyncServiceSettings` has no same-aggregate children)
  - Cross-domain navs stripped: `UpdatedByUser` (User nav) — replace with `UpdatedByUserId` only; resolve display name via `IUserService` at the caller
- **`IGoogleSyncOutboxRepository`** — owns `google_sync_outbox_events`
  - Aggregate-local navs kept: none (flat record)
  - Cross-domain navs stripped: none (entity already holds only `TeamId`/`UserId` scalars)
  - **Status:** extracted in Part 1 of issue #554 (2026-04-23). Current surface covers the count queries used by `NotificationMeterProvider`, `HumansMetricsService`, `SendAdminDailyDigestJob`, and `GoogleWorkspaceSyncService.GetFailedSyncEventCountAsync` — the read-only callers outside the outbox section itself. Part 2 will extend the interface with `AddAsync` / pending-batch dequeue / mark-processed / mark-failed-permanently so `GoogleWorkspaceSyncService`, `TeamService` (enqueue), `ProcessGoogleSyncOutboxJob` (drain), and `GoogleController.SyncOutbox` can drop their direct EF access.
- Note: `IGoogleWorkspaceUserService`, `IGoogleAdminService`, `IDriveActivityMonitorService`, and the bulk of `IGoogleWorkspaceSyncService` are **external-API wrappers** around Google Drive/Groups/Admin SDK HTTP calls. They do not own persistent tables beyond the two above. Per §7 they still need per-service caching and metrics/retry decorators, but their "repository" surface is the Google HTTP client, not EF Core. Their only legitimate EF touchpoints are `ISyncSettingsRepository` (to read the current `SyncMode` gate before each gateway op) and `IGoogleSyncOutboxRepository` (to enqueue/dequeue outbox events).
- All other table access currently performed by these services belongs to other sections and must move behind `ITeamService` / `ITeamResourceService` / `IUserService` / `IProfileService` / `ITeamMemberService` calls.

### Current violations

Observed in this section's service code as of 2026-04-15:

- **Cross-domain `.Include()` calls** (18 total):
  - `GoogleWorkspaceSyncService.cs:196-197, 219-220` — `TeamMembers.Include(tm => tm.User).Include(tm => tm.Team)` (Users + Teams)
  - `GoogleWorkspaceSyncService.cs:676` — `TeamMembers.Include(tm => tm.User)` (Users)
  - `GoogleWorkspaceSyncService.cs:926, 1021, 1046, 1699, 1845` — `GoogleResources.Include(r => r.Team)` (Team Resources → Teams)
  - `GoogleWorkspaceSyncService.cs:1807` — `Users.Include(u => u.TeamMemberships)` (Users → Teams)
  - `GoogleWorkspaceSyncService.cs:2488` — `UserEmails.Include(ue => ue.User)` (Profiles → Users)
  - `GoogleAdminService.cs:61` — `UserEmails.Include(ue => ue.User)` (Profiles → Users)
  - `GoogleAdminService.cs:338, 528` — `Users.Include(u => u.UserEmails)` (Users → Profiles)
  - `SyncSettingsService.cs:24` — `SyncServiceSettings.Include(s => s.UpdatedByUser)` (own table → Users)
  - `EmailProvisioningService.cs:55-56` — `Users.Include(u => u.UserEmails).Include(u => u.Profile)` (Users + Profiles)

- **Cross-section direct DbContext reads** (~41 call sites; own-table reads excluded):
  - **Team Resources (`GoogleResources`, 20 sites)** — `GoogleWorkspaceSyncService.cs:249, 284, 329, 402, 433, 527, 664, 744, 775, 805, 925, 1020, 1045, 1653, 1698, 1714, 1778, 1844, 2299, 2410`. The entire CRUD surface against `google_resources` lives in this service despite §8 assigning the table to `TeamResourceService`. This is the single biggest violation in the section.
  - **Teams (`Teams`, 5 sites)** — `GoogleWorkspaceSyncService.cs:801, 1644, 2122`; `GoogleAdminService.cs:386, 426`
  - **Teams (`TeamMembers`, 5 sites)** — `GoogleWorkspaceSyncService.cs:194, 217, 675`; `GoogleAdminService.cs:275, 453`
  - **Users (9 sites)** — `GoogleWorkspaceSyncService.cs:492, 755, 1806, 2066`; `GoogleAdminService.cs:250, 337, 439, 527`; `DriveActivityMonitorService.cs:417`; `EmailProvisioningService.cs:54`
  - **Profiles (`UserEmails`, 2 sites)** — `GoogleAdminService.cs:59, 258`
  - **Unclassified (`SystemSettings`, 3 sites)** — `DriveActivityMonitorService.cs:438, 464, 473`. `system_settings` is not listed in §8; ownership must be decided before migration (see ambiguity note below).

- **Inline `IMemoryCache` usage in service methods:** None found. None of the six services hold an `IMemoryCache` field — caching, where it exists, is done inside the Google API client layer, not the EF-facing service code. Caching decorators will need to be introduced alongside the repositories rather than extracted from existing code.

- **Cross-domain nav properties on this section's entities:**
  - `SyncServiceSettings.UpdatedByUser` (→ `User`) — must be dropped from the entity; keep only `UpdatedByUserId` scalar.
  - `GoogleSyncOutboxEvent` — clean. Holds `TeamId`/`UserId` scalars with no navs, already conforming.

### Touch-and-clean guidance

Until this section is migrated end-to-end, when touching its code:

- **Do not add new `_dbContext.GoogleResources.*` calls in `GoogleWorkspaceSyncService`.** That table is owned by `TeamResourceService` per §8 — new reads/writes must go through `ITeamResourceService`. The existing 20 offenders (e.g. `GoogleWorkspaceSyncService.cs:284, 329, 1653, 1778`) are grandfathered; nothing new should land.
- **Replace `.Include(tm => tm.User).Include(tm => tm.Team)`** at `GoogleWorkspaceSyncService.cs:196-197, 219-220, 676` with an `ITeamMemberService` / `ITeamService` / `IUserService` call that returns a narrow DTO. Do not widen the surface by adding another `Include`.
- **In `GoogleAdminService.cs:59, 250, 258, 275, 337, 439, 453, 527`, stop reading `Users`/`UserEmails`/`TeamMembers` directly.** Route through `IUserService`, `IProfileService`, and `ITeamMemberService`. When adding a new admin action, wire the cross-section call at the top of the method and pass narrow inputs down.
- **`SyncSettingsService.cs:24` — drop `.Include(s => s.UpdatedByUser)`** next time this method is edited. Resolve the display name at the controller via `IUserService.GetDisplayNameAsync(updatedByUserId)` instead of pulling the `User` aggregate through an EF nav.
- **`EmailProvisioningService.cs:54-56` — the `Users.Include(UserEmails).Include(Profile)` read is three sections in one query.** Split into `IUserService.GetByIdAsync` + `IProfileService.GetByUserIdAsync` + (Profile-owned) `IUserEmailService.GetForUserAsync` when next touched.
- **Do not add more `SystemSettings` reads** in `DriveActivityMonitorService` until the owner of `system_settings` is resolved in §8. Leave the existing three lines alone; surface any new config needs through `ISyncSettingsService` or a dedicated settings service.
  - **Update 2026-04-22:** `DriveActivityMonitorService` has been migrated to `Humans.Application.Services.GoogleIntegration` (issue #554). Its `SystemSettings` reads now go through the owned `IDriveActivityMonitorRepository`, which is the only file that touches the `DriveActivityMonitor:LastRunAt` row. Each `system_settings` consumer owns its own key-space; other keys remain owned by their respective services. Google Drive Activity and Admin Directory API calls now route through the new shape-neutral `IGoogleDriveActivityClient` connector.

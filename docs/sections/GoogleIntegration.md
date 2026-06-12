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
- **Google Group membership sources** implement `IGoogleGroupMembershipSource`. Each source claims group keys and returns expected user IDs only; `IGoogleGroupSync` owns email hydration, user/profile filtering, Google API diffing, collision handling, and mutation.
- The **sync outbox** queues resource-level sync events for processing by a background job.

## Data Model

### SyncServiceSettings

**Table:** `sync_service_settings`

Per-service sync-mode configuration. Holds `UpdatedByUserId` (FK to `User`); the nav property was removed — the EF config uses typed-FK form (`HasOne<User>()`) with `OnDelete(DeleteBehavior.SetNull)`. Display names are resolved via `IUserService.GetByIdsAsync` at the controller. One row per `SyncServiceType`, seeded with `SyncMode.None` for `GoogleDrive`, `GoogleGroups`, and `Discord` (reserved GUID block 0002).

### GoogleSyncOutboxEvent

**Table:** `google_sync_outbox`

Flat record — already clean. Holds `TeamId`/`UserId` scalars with no navs. Two event types defined in `GoogleSyncOutboxEventTypes`: `AddUserToTeamResources` and `RemoveUserFromTeamResources`.

### Google resource entities

`GoogleResource` rows (`google_resources` table) are documented under `docs/sections/Teams.md` (Team Resources sub-aggregate) — owned by `TeamResourceService`. Google Integration services call `ITeamResourceService` rather than querying the table.

### External-API surfaces

`IGoogleSyncService` (`GoogleWorkspaceSyncService`), `IGoogleAdminService`, `IGoogleWorkspaceUserService`, `IDriveActivityMonitorService`, and `IGoogleTranslationService` (`GoogleTranslationService`) wrap Google Drive / Groups / Admin SDK / Drive Activity / Cloud Translation HTTP APIs. They own no persistent tables beyond the two above; their "repository" surface is the Google HTTP client (via the connector interfaces listed under [Connector clients](#connector-clients) below), not EF Core.

## Routing

All Google integration management is consolidated in `GoogleController` (`[Route("Google")]`). Team-level resource linking stays in `TeamAdminController` at `/Teams/{slug}/Admin/Resources`.

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/Google` | GET | Admin | Integration dashboard |
| `/Google/SyncSettings` | GET | Admin | Per-service sync mode view |
| `/Google/SyncSettings` | POST | Admin | Update sync mode for a service |
| `/Google/SyncSystemTeams` | POST | Admin | Manual system-team sync trigger |
| `/Google/SyncResults` | GET | Admin | PRG landing for sync report |
| `/Google/Sync` | GET | TeamsAdmin, Board, Admin | Resource sync status dashboard |
| `/Google/Sync/Preview/{resourceType}` | GET | TeamsAdmin, Board, Admin | AJAX: drift preview for resource type |
| `/Google/Sync/Execute/{resourceId}` | POST | Admin | Execute sync for one resource |
| `/Google/Sync/ExecuteAll/{resourceType}` | POST | Admin | Execute sync for all resources of a type |
| `/Google/SyncOutbox` | GET | Admin | Recent outbox events with user/team/resource display |
| `/Google/SyncOutbox/{id}/Requeue` | POST | Admin | Requeue one permanently-failed outbox event for retry |
| `/Google/SyncOutbox/RequeueAll` | POST | Admin | Requeue all permanently-failed outbox events for retry |
| `/Google/Human/{id}/RerunSync` | POST | Admin | Enqueue fresh Google sync events for all of a user's current teams |
| `/Google/Human/{id}/ProvisionEmail` | POST | HumanAdmin, Admin | Provision @nobodies.team email for a human |
| `/Google/AllGroups` | GET | Admin | Domain-wide group listing with drift status |
| `/Google/CheckGroupSettings` | POST | Admin | Trigger group settings drift check |
| `/Google/GroupSettingsResults` | GET | Admin | PRG landing for group settings drift results |
| `/Google/RemediateGroupSettings` | POST | Admin | Remediate settings drift for one group |
| `/Google/RemediateAllGroupSettings` | POST | Admin | Batch remediate all drifted groups |
| `/Google/LinkGroupToTeam` | POST | Admin | Link an unlinked domain group to a team |
| `/Google/CheckEmailRenames` | POST | Admin | Detect OAuth email renames |
| `/Google/EmailRenames` | GET | Admin | PRG landing for email rename results |
| `/Google/FixEmailRename` | POST | Admin | Apply one email rename fix |
| `/Google/EmailFlagViolations` | GET | Admin | List users with `IsGoogle`/`IsPrimary` flag violations |
| `/Google/Accounts` | GET | Admin | @nobodies.team Workspace account list (2FA, recovery email) |
| `/Google/Accounts/Provision` | POST | Admin | Provision standalone @nobodies.team account |
| `/Google/Accounts/Suspend` | POST | Admin | Suspend a Workspace account |
| `/Google/Accounts/Reactivate` | POST | Admin | Reactivate a suspended Workspace account |
| `/Google/Accounts/ResetPassword` | POST | Admin | Reset password (shown once in modal) |
| `/Google/Accounts/ResetPasswordAndGenerate2Fa` | POST | Admin | Reset password + issue one backup 2FA code |
| `/Google/Accounts/Link` | POST | Admin | Link existing Workspace account to a human |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin | Manage sync settings (per-service mode). Trigger manual syncs and execute sync actions. View reconciliation results. Check and remediate Google Group settings drift. Link unlinked groups to teams. Review and apply email backfill corrections. Manage @nobodies.team Workspace accounts (provision, suspend, reactivate, link, reset password, combined Reset + 2FA recovery for locked-out humans). Provision per-human @nobodies.team email. Requeue permanently-failed outbox events (individually or all at once). Re-enqueue Google sync for a specific user across all their current teams. |
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
<!-- wheat: docs/plans/2026-03-09-google-groups-sync-modes-design.md §Design Principles -->
- **Sync mode guards automation, not operators.** Per-service `SyncMode` controls what scheduled jobs do; manual actions from the Admin UI always execute the requested `SyncAction` regardless of the stored mode (an operator escape hatch when automation is set to `None` or `AddOnly`).
- **App is source of truth for expected membership.** Reconciliation computes expected members from team membership in the database; anyone present in Google but absent from the DB is classified as "extra" and shows as a removal candidate.
- **One sync code path for preview / scheduled / manual.** Diff computation is shared; the `SyncAction` parameter (`Preview` / `AddOnly` / `AddAndRemove`) is the only variable controlling which mutations execute.
- A human's Google service email is their @nobodies.team email if provisioned, otherwise their OAuth login email.
- Each human has a `GoogleEmailStatus` (`Unknown`, `Valid`, `Rejected`). When Google permanently rejects an email (HTTP 400/403/404), the status is set to `Rejected` and new outbox events are not enqueued for that human. When a human changes their Google email, the status resets to `Unknown` and fresh sync events are enqueued.
- Permanent Google API errors (HTTP 400, 403, 404) mark outbox events as `FailedPermanently` and stop retrying immediately. Transient errors (5xx, 429, etc.) continue retrying up to the configured limit.
- The system authenticates to Google APIs as a service account — no domain-wide delegation or user impersonation.
- Drive permissions are modified only by the Drive paths in `IGoogleSyncService`. Google Group membership is modified only by `IGoogleGroupSync`; `IGoogleSyncService` still provisions groups and remediates group settings.
- `IGoogleGroupSync.ReconcileAllAsync` loads all registered group claims, hydrates expected users once for the pass, records per-group errors, and schedules capped scoped retries for groups that fail during Execute; it is the daily, hourly system-team-sync, and bulk reconcile path.
- `IGoogleGroupSync.ReconcileOneAsync` reconciles one group key and, on Google API failure during Execute, schedules delayed scoped Hangfire retries for the same group key, capped at five retry attempts.
- If more than one `IGoogleGroupMembershipSource` claims the same group key, the orchestrator logs/audits a collision and skips mutation for that group. First-wins is forbidden because it would silently revoke access claimed by another owner.
- `TeamService` directly implements `IGoogleGroupMembershipSource` for team Google Groups. `ITeamService` does not inherit that interface; Google Integration registers the concrete `TeamService` as a source.

## Negative Access Rules

- TeamsAdmin and Board **cannot** manage sync settings — that is Admin-only.
- Coordinators **cannot** manage sync settings, execute bulk sync actions, or remediate Google Group settings drift.
- Regular humans have no access to Google resource management.

## Triggers

- When team membership changes, Drive sync outbox events are queued and scoped Google Group membership sync requests are enqueued after the team write commits.
- When a human's Google email changes, `GoogleEmailStatus` resets to `Unknown`; fresh Drive sync events and scoped Google Group sync requests are enqueued for current team memberships.
- When a Google resource is linked to a team, current team members are synced to that resource.
- When a Google resource is unlinked, managed permissions are removed (if sync mode allows).
- The system team sync job runs hourly, reconciling system team membership.
- After the hourly system team sync completes, all Google Group memberships are reconciled through `IGoogleGroupSync.ReconcileAllAsync` so membership changes are reflected in Google Groups.
- The reconciliation job runs daily at 03:00, detecting drift between expected and actual Google resource state.

## Cross-Section Dependencies

- **Teams:** `ITeamService` / `ITeamResourceService` — Google resources are linked per team. Team membership drives Google Group and Drive access.
- **Profiles:** `IUserEmailService` / `FullProfile.GoogleEmail` — a human's Google service email determines the email address used for Google Groups and Drive access. `IGoogleServiceEmailResolver` does not exist; resolution is done via `FullProfile.GoogleEmail` (§15i, 2026-05-04) and `IUserEmailService.MatchByEmailsAsync`.
- **Admin:** Sync settings management is Admin-only.
- **Onboarding:** Volunteers membership is reconciled by the scheduled `SystemTeamSyncJob` (hourly, on name + consents), which cascades to Google Group membership. Consent-check clear/flag no longer triggers per-user team sync — the name-only access switch (#881) decoupled membership from CC review.
- **Email:** `IGoogleRemovalNotificationService` (Application-layer, Google Integration-owned) dispatches the Google removal messages via `IEmailService.SendAsync(emailMessages.Google*(...))` after every confirmed Google API delete in `IGoogleGroupSync` / `RemoveUserFromDriveAsync` (issue peterdrier/Humans#639). Variant 1 (loss-of-access) vs Variant 2 (secondary-email cleanup) is chosen by inspecting the recipient's `UserEmail` rows; messages are `MessageCategory.System` (no unsubscribe footer) and localized to `User.PreferredLanguage`. `SyncRemovalReason.EmailRotation` is plumbed through for audit/telemetry but does not suppress the notification — Workspace identity rotation produces a Variant 2 email so the user can confirm which address was tidied up. Suppression is limited to the orphan-address case (no matching `UserEmail` row, e.g. deleted user, anonymized human, or OAuth-rename-in-place).

### Pending consumer-side `/section-align` targets

Three sections have cross-domain drift that must be resolved on their side (not ours). Each is flagged as a follow-up `/section-align` target:

- **AuditLog** — `AuditLogEntry.Resource` is a cross-domain nav into `GoogleResource`. It is actively read via `.Include(e => e.Resource)` in `AuditLogRepository.cs:64,80`, violating §6c. The AuditLog align run must drop the nav and the two `.Include` calls, and switch to `ITeamResourceService.GetResourceNamesByIdsAsync` (added in this PR — PR #500) for resource label resolution.
- **Teams** — `GoogleResource.Team` (`GoogleResource.cs:44`) is a live cross-domain nav owned by the Teams section per §8. The Teams align run must remove the nav from the entity and convert `GoogleResourceConfiguration.cs` to the typed-FK form (`HasOne<Team>().WithMany().HasForeignKey(r => r.TeamId)`), matching the Shifts pattern.
- **Users/Profiles** — `IMemoryCache.InvalidateNobodiesTeamEmails()` is called directly from `GoogleController` (×2), `ProfileController` (×14), and `TeamAdminController` (×1), bypassing §15's rule that caching belongs in the service layer. The Users/Profiles align run must expose `IUserEmailService.InvalidateNobodiesTeamEmailsAsync()` (or fold invalidation into the mutating service methods) so controllers no longer inject `IMemoryCache` for this purpose.

## Architecture

**Owning services:** `GoogleWorkspaceSyncService` (implements `IGoogleSyncService`), `GoogleGroupSyncService` (implements `IGoogleGroupSync`), `GoogleAdminService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, `EmailProvisioningService`, `GoogleTranslationService` (implements `IGoogleTranslationService`)
**Owned tables:** `sync_service_settings`, `google_sync_outbox`
**Status:** (A) Fully migrated. Three consumer-side cross-domain gaps remain on other sections (AuditLog, Teams, Users/Profiles) — see [Pending consumer-side `/section-align` targets](#pending-consumer-side-section-align-targets) above. All Google Integration business services live in `Humans.Application.Services.GoogleIntegration`. Migration completed under umbrella issue nobodies-collective/Humans#554 across multiple parts: `GoogleAdminService`, `GoogleWorkspaceUserService`, `DriveActivityMonitorService`, `SyncSettingsService`, `EmailProvisioningService` in peterdrier/Humans PR #267 (issue nobodies-collective/Humans#289); `IGoogleSyncOutboxRepository` extracted in Part 1 (2026-04-23); SDK bridge interfaces (`IGoogleDirectoryClient`, `IGoogleDrivePermissionsClient`, `IGoogleGroupMembershipClient`, `IGoogleGroupProvisioningClient`) extracted in Part 2a (issue nobodies-collective/Humans#574, PR #302); `GoogleWorkspaceSyncService` moved to Application in Part 2b (issue nobodies-collective/Humans#575, 2026-04-23); and the last direct-DbContext consumers (`ProcessGoogleSyncOutboxJob`, `GoogleController.SyncOutbox`) flipped onto the repository surface in Part 2c (issue nobodies-collective/Humans#576, 2026-04-23). The section now has zero non-repository direct `DbSet<GoogleSyncOutboxEvent>` / `DbSet<GoogleResource>` / `DbSet<SyncServiceSettings>` reads or writes across Application + Web layers. Surface alignment completed in PR #500 (2026-05-12): DI registrations consolidated into `GoogleIntegrationSectionExtensions`; `GoogleSyncAuditView` / `BuildGoogleSyncAuditViewModel` helpers moved from `HumansControllerBase` into `GoogleController`; Google-owned ViewModels regrouped under `Models/Google/`; service and repository tests relocated to `tests/Humans.Application.Tests/GoogleIntegration/`.

- Service(s) live in `Humans.Application.Services.GoogleIntegration/` and never import `Microsoft.EntityFrameworkCore`.
- `ISyncSettingsRepository`, `IGoogleSyncOutboxRepository`, `IGoogleResourceRepository` (all in `Humans.Application/Interfaces/Repositories/`) are the only code paths that touch this section's tables via `DbContext`.
- **Decorator decision** — no caching decorator. All Google Integration services are either request-scoped admin operations or background-job processors; no hot bulk-read path warrants a `ConcurrentDictionary` projection.
- **Cross-domain navs** — `SyncServiceSettings.UpdatedByUser` nav was removed; the EF config was updated to the typed-FK form (`HasOne<User>()`). `GoogleResource.Team` (`GoogleResource.cs:44`) is a live cross-domain nav property not yet stripped or `[Obsolete]`-marked — it is owned by the Teams section and the EF configuration should be updated to use the typed-FK form. This is the one remaining cross-domain nav in the section.
- **Cross-section calls** — `ITeamService`, `ITeamResourceService`, `IUserService`, `IUserEmailService`, `IProfileService`, `IEmailService`, `ISystemSettingsService` (Drive monitor `DriveActivityMonitor:LastRunAt` marker in `system_settings`), `IAuditLogRepository` (read path via `IAuditViewerService`).
- **Architecture tests** — `tests/Humans.Application.Tests/Architecture/GoogleIntegrationArchitectureTests.cs` (pins `EmailProvisioningService` + `GoogleWorkspaceSyncService`: namespace, no-DbContext, no-Google.Apis, sealed); `GoogleAdminArchitectureTests.cs` (pins `GoogleAdminService`); `GoogleWorkspaceUserArchitectureTests.cs` (pins `GoogleWorkspaceUserService` + `IWorkspaceUserDirectoryClient` shape-neutrality + `Humans.Application` assembly-level no-Google.Apis assertion); `GoogleWorkspaceSyncBridgeArchitectureTests.cs` (pins all four Part 2a bridge interfaces for namespace, shape-neutrality, and no-Google.Apis at the assembly level).

### Repository surface

- **`ISyncSettingsRepository`** — owns `sync_service_settings`. Unique index on `ServiceType` (`SyncServiceSettingsConfiguration.cs:34`). One row per `SyncServiceType`, seeded (reserved GUID block 0002).
- **`IGoogleSyncOutboxRepository`** — owns `google_sync_outbox` (table name in EF config: `google_sync_outbox`; `design-rules.md §8` lists this as `google_sync_outbox_events` — that name is stale, the table is `google_sync_outbox`). Entity holds `TeamId`/`UserId` scalars only; no entity-level navs. EF config wires typed FKs to `Team`/`User` with `OnDelete(Cascade)`. Indexes on `(ProcessedAt, OccurredAt)`, `(TeamId, UserId, ProcessedAt)`, and `DeduplicationKey` (unique). Enqueue writes live here (`AddAsync` / `AddRangeAsync`) and are surfaced to producers through `IGoogleSyncOutboxService`. When an enqueue must be atomic with another section's mutation — e.g. `TeamService` queuing an event on a `TeamMember` change — the producer wraps the team-repository call and `IGoogleSyncOutboxService.AddAsync` in an ambient `TransactionScope` rather than folding the outbox write into `TeamRepository`.
- **`IGoogleResourceRepository`** — narrow writes to the sibling-owned `google_resources` table (Teams section §8 owner). Used by `GoogleWorkspaceSyncService` for reconciliation-loop atomic writes. All broader reads/writes route through `ITeamResourceService`.

> `DriveActivityMonitorService` persists its `DriveActivityMonitor:LastRunAt` marker through `ISystemSettingsService` (SystemSettings section owns `system_settings`); it has no repository of its own.

### Connector clients

Application-layer services depend only on shape-neutral connector interfaces in `Humans.Application.Interfaces.GoogleIntegration/` — `IGoogleDirectoryClient`, `IGoogleDrivePermissionsClient`, `IGoogleGroupMembershipClient`, `IGoogleGroupProvisioningClient`, `ITeamResourceGoogleClient`, `IGoogleDriveActivityClient`, `IWorkspaceUserDirectoryClient`, `IGoogleTranslationClient` — so they never import `Google.Apis.*` (design-rules §13). Real Google-backed implementations and dev-mode stubs live in `Humans.Infrastructure/Services/GoogleWorkspace/`.

### Touch-and-clean guidance

- `GoogleResource.cs:44` — `public Team Team { get; set; } = null!;` is a live cross-domain nav. Tracked as a Teams-section follow-up (see above). Should be removed and `GoogleResourceConfiguration.cs` converted to typed-FK form — low risk since no Google Integration service reads it directly.

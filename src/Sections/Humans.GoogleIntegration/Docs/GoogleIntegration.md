<!-- freshness:triggers
  src/Sections/Humans.GoogleIntegration/**
  src/Sections/Humans.GoogleIntegration/Contracts/**
  src/Sections/Humans.Teams.Contracts/ISystemTeamSync.cs
  src/Sections/Humans.AuditLog/Contracts/ILegacyGoogleSyncAuditReader.cs
  src/Sections/Humans.Monitor/Services/DriveActivityMonitorService.cs
  src/Sections/Humans.Users/Views/UsersAdmin/AdminDetail.cshtml
  src/Sections/Humans.Users/Services/UserEmailService.cs
-->
<!-- freshness:flag-on-change
  Sync mode invariants, Shared-Drive-only constraint, GoogleEmailStatus rules, and reconciliation gateway operations — review when Google Integration services/entities/controller change.
-->

# Google Integration — Section Invariants

Shared-Drive-only Google resource sync: Drive folders, Groups, Workspace accounts, reconciliation, Drive-activity monitoring.

## Concepts

- Resource administration audits persisted Drive/group links and reactivations, active unlinks, permission-level saves and inherited-access preferences, with the acting manager and owning team. Refused or missing-resource paths do not write entries; the committed inherited-access preference is audited even when subsequent Drive enforcement fails.
- **Google Resources** are Shared Drive folders, Shared Drives, Drive files, and Google Groups linked to a team. When a human joins or leaves a team, their access to the team's linked Google resources is automatically managed. Resource rows live in GoogleIntegration's `google_resources`, owned by its `TeamResourceService`.
- **Sync Mode** controls how the system interacts with Google APIs for each service type. Modes are: None (disabled), AddOnly (grant access but never revoke), or AddAndRemove (full bidirectional sync).
- **Reconciliation** compares the expected Google resource state (based on team membership) against the actual Google resource state, detecting drift.
- **Google Group membership sources** implement `IGoogleGroupMembershipSource`. Each source claims group keys and returns expected user IDs only; `IGoogleGroupSync` owns email hydration, user/profile filtering, Google API diffing, collision handling, and mutation.
- **Drive access sources** implement `IGoogleDriveAccessSource`. Teams claims linked Drive resources, unions members across shared resources and rolls active child-team members into parent claims; Workgroups claims its folders. Sources return user IDs, permission levels, optional team-link metadata and an optional `ResourceName`. Registered resources carry their full Drive path as their name (refreshed daily by `UpdateDriveFolderPathsAsync`); a folder with no `GoogleResource` row (Workgroups' folders) gets the same path walk (`DriveFolderPath`) per reconciliation pass, falling back to the source's `ResourceName`, so removal emails, the sync log and the sync page never show a raw file id. `IGoogleDriveSync` owns user/email hydration, multi-source unions, diffing, mutations and sync logs for both. Manual results retain linked-resource metadata; the daily job runs one Drive pass. `GoogleWorkspaceSyncService` retains provisioning, immediate join grants and outbox dispatch; those grants use the same source-owned permission levels. Consumer sections use `RequestSyncAsync` to enqueue a scoped reconciliation, `CreateSubfolderAsync` to provision folders and `IsFolderReachableAsync` to check a folder setting before saving it.
- The **sync outbox** queues resource-level sync events for processing by a background job.

## Data Model

### SyncServiceSettings

**Table:** `sync_service_settings`

Per-service sync-mode configuration. Holds `UpdatedByUserId` — a bare cross-section Guid column, no FK constraint, no nav. Display names are resolved via `IUserService.GetUserInfosAsync` at the controller. One row per `SyncServiceType`, seeded with `SyncMode.None` for `GoogleDrive`, `GoogleGroups`, and `Discord` (reserved GUID block 0002).

### WorkspaceIdentityRecord

**Table:** `google_workspace_identities`

A member's designated Google Workspace identity: `UserId` (PK, bare Guid, no FK), `Email` (nullable, max 256), `Status` (`GoogleEmailStatus` as string: `Unknown`, `Valid`, `Rejected`), `UpdatedAt`. One row per user. A row with `Email = null` is a **cleared** row: the designated address left the account, and the read must not fall back to the frozen legacy flag. Moved out of Users' `user_emails.IsGoogle` / `user_emails.GoogleEmailStatus` in Users decomposition commit 15; those columns are frozen (no writes) and read only by `WorkspaceIdentityService`'s legacy fallback and the `google.workspace-identities` copy until the drop PR.

### GoogleSyncOutboxEvent

**Table:** `google_sync_outbox`

Flat record — already clean. Holds `TeamId`/`UserId` scalars with no navs. Two event types defined in `GoogleSyncOutboxEventTypes`: `AddUserToTeamResources` and `RemoveUserFromTeamResources`.

### GoogleSyncLogEntry

**Table:** `google_sync_log`

One row per permission grant or revocation the sync path applies: `Action` (`AccessGranted` / `AccessRevoked`), `ResourceId`, optional `UserId`, `UserEmail` at the time of the call, `Role`, `Source` (`GoogleSyncSource`), `Success`, `ErrorMessage`, `Description`, `JobName`, `OccurredAt`. All Guids are bare columns — no navs, no FKs. Enums persist as strings; indexes on `ResourceId`, `UserId` and `OccurredAt`.

This wing lived in `audit_log` until nobodies-collective/Humans#1083. The section that produces the rows owns them.

The rows written before the split are still on `audit_log`. **`/Google/Admin/SyncHistoryMigration`** (admin-only, under the *Google* nav group) copies them forward: GET is a dry run reporting how many rows were examined, are already here, would move and cannot be mapped; POST copies. `GoogleSyncHistoryMigrationService` reads through `ILegacyGoogleSyncAuditReader` (AuditLog's `Contracts/` folder) and writes through `IGoogleSyncLogRepository`, so neither section touches the other's context. A copied row keeps its source audit row's id, which is what makes a second run a no-op; only `GoogleResourceAccessGranted` / `GoogleResourceAccessRevoked` rows carrying a `SyncSource` and a `Success` map, and the rest are reported as skipped. Nothing on `audit_log` is changed or deleted — the column drop is its own PR, after this has been run and verified. The screen, the service and the reader all come out with the columns.

`UserId` is nullable only because a sync can act on an address no `user_emails` row claims — an outside collaborator, or a mailbox left behind after a purge. Everywhere a human is identifiable the writers record them: expected members carry their id on the plan, and **extra** members — the revocation case, where the human has already left the team and is reachable only through the address Google still holds — are resolved by email (`IUserEmailService.MatchByEmailsAsync`) in Group reconciliation and both Drive reconciliation paths, including source-claimed folders. That query returns every matching `user_emails` row in no order, so both go through `UserEmailMatchOwner.ByEmail` — verified > most-recently-updated > stable id — because an unverified duplicate outranking the real owner would file the row in the wrong human's GDPR export. That dictionary is keyed by `GmailAliasEmailComparer` (nobodies-collective/Humans#1101): the matched row may be stored as a Gmail alias (`old.user+burn@googlemail.com`) of the canonical address Google reports, and a case-only comparer would hash it into an unreachable bucket. `GetWorkspaceAccountListAsync` shares the same picker. That is what makes `/Google/Human/{id}` and the GDPR slice non-empty.

### Google resource entities

**Table:** `google_resources`

`GoogleResource` rows are owned by this section's `TeamResourceService`; `TeamId` is a bare Guid column with no FK constraint and no nav. Google Integration services call `ITeamResourceManagement` (internal) rather than querying the table. Cross-section readers use the public `ITeamResourceService`; the link, unlink, permission-level and restrict-inherited-access writes are internal (`ITeamResourceManagement`), called only by this section's `TeamResourcesController`. Both interfaces resolve to the same scoped service.

### External-API surfaces

`IGoogleSyncManagement` (`GoogleWorkspaceSyncService`), `IGoogleAdminService`, `IGoogleWorkspaceUserService`, and `IGoogleTranslationService` (`GoogleTranslationService`) wrap Google Drive / Groups / Admin SDK / Drive Activity / Cloud Translation HTTP APIs. They own no persistent tables; their "repository" surface is the Google HTTP client (via the connector interfaces listed under [Connector clients](#connector-clients) below), not EF Core.

## Routing

Google integration management is consolidated in `GoogleController` (`[Route("Google")]`). A team's linked resources are managed by `TeamResourcesController` at `/Google/Teams/{slug}/Resources`, linked from the team page and the team Members page.

| Route | Method | Auth | Purpose |
|-------|--------|------|---------|
| `/Google` | GET | Admin | Integration dashboard |
| `/Google/SyncSettings` | POST | Admin | Update sync mode for a service (posted from the `/Settings#google-sync` tab, no GET — peterdrier/Humans#1634) |
| `/Google/SyncSystemTeams` | POST | Admin | Manual system-team sync trigger |
| `/Google/SyncResults` | GET | Admin | PRG landing for sync report |
| `/Google/ActivityLog` | GET | Admin | Newest-first `google_sync_log` (500 rows), filterable by date, action, trigger, success, resource name |
| `/Google/Sync` | GET | TeamsAdmin, Board, Admin | Resource sync status dashboard |
| `/Google/Resource/{id}` | GET | Board, Admin | Sync history for one resource |
| `/Google/Human/{id}` | GET | HumanAdmin, Board, Admin | Sync history for one human |
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
| `/Google/EmailFlagViolations` | GET | Admin | List users with `IsPrimary` flag violations |
| `/Google/Accounts` | GET | Admin | @nobodies.team Workspace account list (2FA, recovery email) |
| `/Google/Accounts/Provision` | POST | Admin | Provision standalone @nobodies.team account |
| `/Google/Accounts/Suspend` | POST | Admin | Suspend a Workspace account |
| `/Google/Accounts/Reactivate` | POST | Admin | Reactivate a suspended Workspace account |
| `/Google/Accounts/ResetPassword` | POST | Admin | Reset password (shown once in modal) |
| `/Google/Accounts/ResetPasswordAndGenerate2Fa` | POST | Admin | Reset password + issue one backup 2FA code |
| `/Google/Accounts/Link` | POST | Admin | Link existing Workspace account to a human |
| `/Google/WorkspaceEmail` | POST | Signed-in member (self) | `WorkspaceIdentityController.Set`: designate one of the member's own verified addresses as their Google identity (antiforgery; exempt from `MembershipRequiredFilter` like the rest of the Emails grid) |
| `/Google/Teams/{slug}/Resources` | GET | TeamsAdmin, Board, Admin, or a resource manager of the team's department | `TeamResourcesController.Index`: linked resources + link form |
| `/Google/Teams/{slug}/Resources/LinkDrive` | POST | same | Link a Drive folder or file by URL |
| `/Google/Teams/{slug}/Resources/LinkGroup` | POST | same | Link a Google Group by email |
| `/Google/Teams/{slug}/Resources/{resourceId}/PermissionLevel` | POST | same; resource must belong to the team (else 404) | Change a Drive resource's permission level |
| `/Google/Teams/{slug}/Resources/{resourceId}/RestrictInheritedAccess` | POST | same; resource must belong to the team | Toggle inherited-access restriction on a Drive folder |
| `/Google/Teams/{slug}/Resources/{resourceId}/Unlink` | POST | same; resource must belong to the team | Soft-unlink (IsActive = false) |
| `/Google/Teams/{slug}/Resources/{resourceId}/Sync` | POST | same; resource must belong to the team | Reconcile one resource now |
| `/Google/Admin/{id}/WorkspaceEmail` | POST | HumanAdmin, Board, Admin (`HumanAdminBoardOrAdmin`) | `WorkspaceIdentityController.AdminSet`: same, on another member's grid; a merged-away `{id}` is 404 |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin | Manage sync settings (per-service mode). Trigger manual syncs and execute sync actions. View reconciliation results. Check and remediate Google Group settings drift. Link unlinked groups to teams. Review and apply email backfill corrections. Manage @nobodies.team Workspace accounts (provision, suspend, reactivate, link, reset password, combined Reset + 2FA recovery for locked-out humans). Provision per-human @nobodies.team email. Requeue permanently-failed outbox events (individually or all at once). Re-enqueue Google sync for a specific user across all their current teams. |
| TeamsAdmin, Board, Admin | View resource sync status dashboard. Link and unlink Google resources (Drive folders, Groups) to teams via `TeamResourcesController`. View resource status. Trigger per-resource sync |
| Coordinator | Link and unlink Google resources for their own department (via `TeamResourcesController`). Trigger per-resource sync for their own department |
| Board, Admin | View Drive activity anomaly check results. View sync audit logs |
| HumanAdmin, Board, Admin | View per-human Google sync audit |
| Background jobs | Automated sync: system team sync (hourly), resource reconciliation (daily at 03:00), sync outbox processing, resource provisioning |

## Invariants

- Persisted resource errors are bounded to 2000 UTF-16 units; sync-log descriptions (including their job prefix) and errors are bounded to 4000. Bounds preserve surrogate pairs so long vendor failures cannot prevent failure-state or audit-row persistence.

- Outbox error previews preserve whole UTF-16 surrogate pairs within their existing 120-unit limit; tooltips retain the complete stored error, and retry behavior is unchanged.

- Domain-group listing and its team picker use browser cancellation. Aborted per-group settings fetches propagate through the listing instead of becoming group errors; caller cancellation logs warnings without exception stacks. Dependency failures retain their existing error feedback.

- Workspace accounts, per-resource/per-human audit frames and Sync Outbox reads pass the browser abort token to every data lookup. Account-list caller cancellation logs a warning without an exception stack and propagates; dependency cancellation remains an error with generic feedback.

- The Workspace accounts page renders existing `WorkspaceAccountInfo` rows directly, preserving service email ordering and status, member-link, 2FA and recovery fields.

- Sync-dashboard preview loads apply only the latest response or failure for each resource tab. Drive and group previews load independently; sync POSTs keep their existing execution boundaries.
- Preview requests reject redirects and display the load failure; login or access-denied pages cannot become sync previews.

- All Google Drive resources are on Shared Drives. The system does not use regular (My Drive) folders.
- Inherited access cannot be removed at a child folder. Drive reconciliation retains direct/inherited component details (nobodies-collective/Humans#945): a direct elevation on a mixed permission is updated in place to the greater of the expected role and inherited floor. Departure or retirement reduces the elevation to that floor; equal-floor mixed or purely inherited permissions then need no further mutation. Pure direct extras are deleted. Role-change successes and failures enter the sync log; reducing an elevation while inherited access remains never sends a total-removal notice. A Drive delete that still 403s as inherited (a race between listing and deleting) is classified terminal and not retried until the next reconciliation pass.
- Drive reconciliation retries a permission create or role change up to twice in the same pass (1s, then 3s backoff) when Google reports a transient error: 5xx, 429, or the 400 "an internal error has occurred" shape (peterdrier/Humans#2037). Only the final outcome is logged; permanent failures, including target rejections, are never retried in-pass.
- Drive plus-address fallback (peterdrier/Humans#2037): when Drive refuses a non-Gmail `local+tag@domain` identity (Gmail ones are canonicalized first) with a target rejection or its 400 internal error (never on an outage, rate limit or caller-permission failure), reconciliation and outbox grants retry the base `local@domain`. If Drive accepts it, the base address is recorded as `google_workspace_identities.DriveEmail` and later Drive passes match and grant it directly; the sync log names both addresses, even when the base already had access. If the base also fails, the original failure (and any rejection verdict) stands. `DriveEmail` is Drive-only, since Google treats plus addresses differently across services, so Groups keep using `Email`. A changed `Email` clears it. It rides the identity's GDPR slice and erasure. Follow-up: show it on the member's email addresses panel.
- Drive role changes use Google's permission update operation rather than delete/recreate. `AddOnly` may elevate access but cannot downgrade it, including the mixed writer-to-inherited-reader transition; `None` and `Preview` never mutate.
- Drive folders with `RestrictInheritedAccess = true` have `inheritedPermissionsDisabled` enforced by the reconciliation job. Drift (manual re-enablement of inheritance) is detected and corrected automatically, with an audit trail entry.
- Sync settings are per-service (Google Drive, Google Groups, Discord). Setting a service to None disables sync without redeploying. The admin settings tab propagates the browser abort token through its settings and updater-name reads.
- **Sync-mode changes are auditable.** Successful admin saves record `SyncModeChanged` against the saved settings row, with service, selected mode and authenticated actor. Missing settings rows do not audit; malformed posted fields return HTTP 400 before writing.
- **Sync mode gates every Execute — scheduled and manual alike.** `SyncAction` only distinguishes `Preview` (diff only) from `Execute` (apply); on `Execute`, the per-service `SyncMode` still decides whether adds/removes actually apply (`None` → no writes, `AddOnly` → adds only, `AddAndRemove` → adds + removes). Admin UI "Sync Now" / "Sync All" pass `SyncAction.Execute` the same as the scheduled jobs — there is no operator bypass of a `None` or `AddOnly` mode.
- **App is source of truth for expected membership.** Reconciliation computes expected members from team membership in the database; anyone present in Google but absent from the DB is classified as "extra" and shows as a removal candidate.
- Group settings drift checks use the same group-email resolver as membership sync: team email first, then the URL domain (configured-domain fallback for domainless URLs), then an email-valued legacy Google id. Trailing URL slashes do not become part of the group email.
- Single-resource Drive reconciliation unions every active team link for the same Google id and resource type, including individual files, so one team’s manual sync cannot revoke another linked team’s access.
- **One sync code path for preview / scheduled / manual.** Diff computation is shared; the `SyncAction` parameter (`Preview` / `Execute`) selects diff-only vs. apply, and the stored `SyncMode` determines which mutations execute. The initiating `GoogleSyncSource` is threaded through the Drive gateways so scheduled reconciliation and outbox dispatch leave truthful sync-log rows; `admin-resync:` outbox keys (per-human rerun) and `:resync:` keys (account link) identify manual reruns while ordinary add events record `TeamMemberJoined`.
- **Manual Execute runs to completion regardless of the admin's browser.** `GoogleController.SyncExecute` / `SyncExecuteAll` pass `CancellationToken.None` to the sync call, not the request's `HttpContext.RequestAborted` — an admin closing the tab mid-reconcile must not abort partway through applying group memberships and Drive permissions, which would leave Workspace half-synced with no record of where it stopped (nobodies-collective/Humans#950).
- **Cancelled health probes propagate cancellation.** A stopped Google credential/API probe is never reported as an authentication failure; credential-free instances still report degraded. The per-probe SDK client is disposed after every outcome.
- **Drive Activity reads remain cancellable.** Activity queries and Directory identity lookups pass their caller token through the shared credential loader and Google API read. A cancelled identity lookup propagates cancellation instead of returning an unresolved person.
- **Workspace account timestamps are UTC.** Directory creation and last-login timestamps retain their instant regardless of server timezone; missing creation time remains `DateTime.MinValue` and missing login time remains null.
- A human's Google service email is their @nobodies.team email if provisioned, otherwise their OAuth login email.
- Successful Workspace provisioning queues credentials and sends an inbox notice in the recipient's supported preferred language (English fallback), reusing the credential email subject and linking to their profile. Neither the preferred recovery address nor its OAuth fallback may be an `@nobodies.team` mailbox. Once the account is provisioned and linked, a credentials preparation/enqueue failure remains a successful provisioning with an operator warning to request an admin password reset; no additional password state is persisted for provisioning retries. Missing recovery addresses get the same recovery guidance. The member notice asks for admin help when credentials were not queued, and only asks them to check personal email after an acknowledged enqueue. Both human-admin and team-coordinator actions show the warning instead of claiming delivery.
- `EmailProvisioningService.ProvisionNobodiesEmailAsync` refuses an archived id: a merge-resolved-forward id (lookup returns the survivor, whose `Id` differs) or a GDPR/legacy tombstone (`UserInfo.IsActive` false) is rejected before any Workspace call (peterdrier/Humans#1707).
- Human-linked and standalone provisioning share mailbox-syntax validation and [Google Workspace's primary-username bounds](https://knowledge.workspace.google.com/admin/users/name-guidelines-for-users-and-groups): at most 64 characters, no leading, trailing or consecutive periods, and no reserved `abuse`/`postmaster` names. Invalid usernames return operator feedback before Workspace reads or writes. Human-linked provisioning retains its German/diacritic transliteration to ASCII and checks the resulting length; standalone provisioning retains the entered lowercase Unicode username. Other accepted symbols are preserved rather than restricted to an outdated character allowlist.
- **Workspace identity resolution** (replaces Users' `EnsureGoogleInvariantAsync`, Users decomposition commit 15). `IGoogleIntegrationService.GetWorkspaceIdentityAsync` resolves at read time over the member's verified addresses: (1) a verified `@nobodies.team` address (the designated one when it is `@nobodies.team`); else (2) the designated address — the `google_workspace_identities` row, or, when the member has no row yet, the frozen legacy `IsGoogle` flag — if still verified; else (3) the verified primary; else (4) the first verified address by email. No verified address → no identity. For a member satisfying the old invariant (exactly one verified `IsGoogle` row, the `@nobodies.team` one when present) this is that same address, before and after the copy (pinned by tests).
- `Status` counts only for the address it was recorded against: a row whose `Email` differs from the resolved address reads `Unknown`. In legacy mode (no row) the status is the legacy per-address `GoogleEmailStatus` of the resolved address. Explicit recipient rejection marks the identity `Rejected` through `SetStatusFromSyncAsync(userId, attemptedEmail, …)`, which is dropped when `attemptedEmail` is no longer the current identity; `Valid` never overwrites `Rejected`. Caller-permission and sharing-policy failures do not reject addresses. Re-designating the same address (the Retry button) resets `Rejected` to `Unknown`; designating another address starts at `Unknown`.
- `Set` accepts only a verified address of the member's own (not a merged-away id) and, while the member has a verified `@nobodies.team` address, only a `@nobodies.team` one. Each set is audited as `UserEmailGoogleSet` on the `User` (same action and wording as Users' former `SetGoogleAsync`).
- Users reports every address that leaves an account — email delete, admin delete-by-id, provider unlink, an OAuth tag-move or rename, and a cross-user displacement — through `IGoogleIntegrationWrite.ClearIfAddressAsync`; when it is the designated address, the row becomes a cleared row (so the frozen legacy flag cannot resurrect it) and the read falls through to the default.
- GDPR: `WorkspaceIdentityService` is an `IUserDataContributor` (slice `WorkspaceIdentity`: `Email`, `Status`; erasure deletes the row) and an `IUserMerge` (the archived account's row is deleted; the survivor keeps its own identity). Data migration `google.workspace-identities` (Debug → Data migrations) copies every member with legacy state (a verified `IsGoogle` row or a non-`Unknown` legacy status) and no row yet; idempotent (insert-if-absent).
- Drive permission-create failures (HTTP 400/403) that match `GoogleWorkspaceSyncService.IsDriveTargetRejection` — including 403 "Cannot share a shared drive with non Google accounts" — set the address `Rejected` in both the Teams-keyed Drive path and `GoogleDriveAccessSyncService`; the access-source sync then skips that member until the status is cleared (peterdrier/Humans#1938).
- The Google outbox drain is marked `[ExternalWrite]`, matching its Workspace mutations; its Hangfire entry receives a literal `CancellationToken.None`.
- Team provisioning returns `GoogleResourceGrantResult`, carrying the resolved owner and exact attempted email row, retryability and failure details. `Accepted` means Google creates or confirms an existing Drive grant and no grant fails. Disabled Drive sync, no resources and queued group reconciliation return `Deferred`; failed grants or ineligible addresses return `Failed`. Only `Accepted` lets the outbox mark the address Valid; resource existence alone is never verification.
- Successful outbox dispatch finishes its applicable Google-email status update before marking the event processed and counting success. A dispatch/status persistence failure keeps the event pending under the existing retry limit rather than closing it with an unreachable retry count.
- Outbox retry and permanent-failure error text keeps at most 4000 UTF-16 code units, without splitting a Unicode surrogate pair. Retry counts and failure transitions are independent of truncation.
- Permanent Google API errors (HTTP 400, 403, 404) mark outbox events as `FailedPermanently` and stop retrying immediately. Returned failures follow the same lifecycle: transient failures increment retry count and remain pending; permanent/local refusals record failure, never success. Mixed results retry when any grant is retryable. Escaping exceptions without an attempted-row identity cannot reject a guessed current address. Transient errors (5xx, 429, etc.) continue retrying up to the configured limit.
- Without Google credentials, the outbox job reports `skipped` and the processor leaves every event pending without dispatching stubs or changing `GoogleEmailStatus`. The nightly reconciliation job likewise reports `skipped` without running a phase, the hourly Teams system-team sync skips its Google Group reconcile step, and the scoped Hangfire schedulers log and enqueue nothing, so no reconciler ever writes sync-log rows against the in-memory stubs. `IGoogleDriveActivityClient.IsConfigured` is the one credential signal for all of them.
- The system authenticates to Google APIs as a service account — no domain-wide delegation or user impersonation.
- Drive permissions are modified through `IGoogleDriveSync` reconciliation and the provisioning/join gateways in `IGoogleSyncManagement`. Google Group membership is modified only by `IGoogleGroupSync`; `IGoogleSyncManagement` still provisions groups and remediates group settings.
- `IGoogleGroupSync.ReconcileAllAsync` loads all registered group claims, hydrates expected users once for the pass, records per-group errors, and schedules capped scoped retries for groups that fail during Execute; it is the daily, hourly teams-system-sync, and bulk reconcile path.
- `IGoogleGroupSync.ReconcileOneAsync` reconciles one group key and, on Google API failure during Execute, schedules delayed scoped Hangfire retries for the same group key, capped at five retry attempts.
- Group sync logs retain their trigger through direct, bulk, queued and retry paths: admin operations use `ManualSync`, inline provisioning carries its existing trigger, the hourly team pass uses `SystemTeamSync`, and the daily job uses `ScheduledSync`. Scoped Group and Drive Hangfire calls pass every current argument explicitly with `CancellationToken.None`; neither retains a legacy signature overload (standing policy D13).
- If more than one `IGoogleGroupMembershipSource` claims the same group key, the orchestrator logs/audits a collision and skips mutation for that group. First-wins is forbidden because it would silently revoke access claimed by another owner.
- `TeamService` directly implements `IGoogleGroupMembershipSource` for team Google Groups. `ITeamService` does not inherit that interface; Teams registers the concrete `TeamService` as a source. Camps' `CampRoleService` is a second source, for camp role groups.
- Group and Drive source fan-outs share one verified-email and user-eligibility policy; rejected, suspended, deletion-pending and merged-away claimed users are excluded. Inline team provisioning uses the same suspension, deletion-pending and email-rejection gate before scheduling groups or granting Drive access, so delayed outbox events cannot restore excluded access. Address selection and rejection state use the same `UserInfo` snapshot; they never pair one snapshot’s status with another email lookup. Vendor rejection and acceptance update the exact attempted email row, preserving a newer selection’s state.
- Signup rejection (`UserState.Rejected`) also excludes Google access, even when a non-system team or camp-role claim remains. Group/Drive reconciliation and inline provisioning share this gate; configured sync modes still govern removals. Email-address rejection remains a separate per-address state.
- Drive and Group plans compare Gmail identities with `GmailAliasEmailComparer` (dots, plus tags and gmail/googlemail aliases); other domains keep their distinct local parts. Existing alias-form access is never classified as extra against the same expected identity. Drive claims reconcile once at the highest already-claimed level, using a stable user ID on ties; Group alias claims reconcile once with stable user-ID attribution. API targets retain their accepted dotted form.
- Drive file ids are matched exactly, including case, in both scoped reconciliation and claim collision detection.
- If more than one `IGoogleDriveAccessSource` claims the same folder id (e.g. Teams and Workgroups), `IGoogleDriveSync` reconciles the union of their claims, highest level per user — unlike the group side, which skips colliding keys. If any source throws, the pass still grants but holds every removal and demotion and records an error (in every sync mode, and for previews) on each claimed resource, since the failed source may have claimed those members; active resources no surviving source claimed get the same error rather than being skipped silently. The join-time grant (`AddUserToTeamResourcesAsync`) does not tolerate a failing source: the exception propagates so the outbox retries the event instead of marking it processed with no grant.
- Drive roles are fully managed: a direct role above the claimed level (e.g. raised by hand) is `MemberSyncState.Downgrade`. Preview and the `/Google/Sync` page list every pending downgrade with current and expected role; `AddAndRemove` then lowers it in place and logs `AccessRevoked` in the sync log. `AddOnly` never demotes.
- Drive folder-name maintenance saves only complete paths reaching a root. Failed parent reads, unavailable root names and the walk-depth limit preserve the stored name; a Shared Drive root (the drive itself: its id is the drive id, `files.get` reads it as an anonymous parentless folder named "Drive") is named only by `drives.get`, never by the `files.get` placeholder, and is recognised from its own or a descendant's `driveId`, or by a `drives.get` probe of a parentless folder — a failed drive-name read leaves the path unresolved.
- `IGoogleSyncWrite.CreateSubfolderAsync` respects the `GoogleDrive` `SyncMode` (throws when it is `None`) and throws on a Google API failure rather than returning a bogus id, so a failed creation is visible to the caller.

## Negative Access Rules

- TeamsAdmin and Board **cannot** manage sync settings — that is Admin-only.
- Coordinators **cannot** manage sync settings, execute bulk sync actions, or remediate Google Group settings drift.
- Regular humans have no access to Google resource management.
- A member **cannot** designate another member's Google identity, nor an address that is not their own verified row: `/Google/WorkspaceEmail` acts on the signed-in member only, and `/Google/Admin/{id}/WorkspaceEmail` requires HumanAdmin, Board or Admin.
- The admin Google Sync / @nobodies.team cards (`AdminDetailSidebar` user part) render nothing outside the admin view; the Re-run Google Sync button inside them is Admin-only.

## Triggers

- When team membership changes, Drive sync outbox events are queued and scoped Google Group membership sync requests are enqueued after the team write commits.
- When a human selects a different Google email, the new canonical address starts `Unknown` (status is per-address, #687); fresh Drive sync events and scoped Google Group sync requests are enqueued for current team memberships.
- When a Google resource is linked to a team, current team members are synced to that resource.
- When a Google resource is unlinked, it is deactivated and audited; the unlink itself does not revoke Google permissions.
- The system team sync job runs hourly, reconciling system team membership.
- After the hourly system team sync completes, all Google Group memberships are reconciled through `IGoogleGroupSync.ReconcileAllAsync` so membership changes are reflected in Google Groups.
- The reconciliation job runs daily at 03:00, detecting drift between expected and actual Google resource state.
- On every applied or failed Drive/Group permission grant or revocation, `GoogleWorkspaceSyncService` / `GoogleGroupSyncService` / `GoogleDriveAccessSyncService` append a `google_sync_log` row via `IGoogleSyncLogService.LogAsync`, carrying `success: false` + the API error on failure. Group recipient failures record the resource error, failed sync log and applicable scoped retry before updating the attempted Users email row; a status-write failure propagates with those diagnostics preserved.
- Source-claimed groups without a linked resource row still log applied and failed membership writes with `ResourceId = Guid.Empty`, the group key in the description and the known member ID. Auto-provisioning a missing claimed group writes `GoogleResourceProvisioned` to the audit trail. Existing sync-log export and erasure cover these rows.
- The daily reconciliation job runs `IGoogleDriveSync.ReconcileAllAsync` once for all registered source claims, including linked team files/folders and Workgroups folders. Retired teams' resources are deactivated only in `AddAndRemove` after every linked resource of the selected type reconciles without error; API failures remain visible and block deactivation. Per-resource errors in the returned result count as a failed Drive phase: the job records `partial_failure` and sends a SyncError alert. Source-only claims (no linked resource row, e.g. Workgroup folders) show the folder id read-only on the sync dashboard, with no per-resource Audit or Execute controls.

## Cross-Section Dependencies

- **Teams:** `ITeamService` / `ITeamResourceManagement` — Google resources are linked per team. Team membership drives Google Group and Drive access.
- **Users:** `IUserService.GetUserInfoAsync` / `GetUserInfosAsync` supply the member's verified addresses (and, until the drop PR, the frozen legacy `IsGoogle` / `GoogleEmailStatus` flags the read falls back to); the Workspace identity itself is this section's `google_workspace_identities`. Vendor status writes go through `SetStatusFromSyncAsync` with the attempted address, never re-resolving the target. Users calls `IGoogleIntegrationWrite.ClearIfAddressAsync` when an address leaves an account. `IUserEmailService.MatchByEmailsAsync` attributes extra permissions, and `GetByEmailOrAlternateAsync` checks already-linked identities during standalone account provisioning. Users' Emails grid hosts `<vc:workspace-identity>` (the Google column and the rejection banner) and its admin list and profile card host `<vc:nobodies-email-badge>`; both view components are this section's.
- **Teams, Camps:** read `IGoogleIntegrationService` — Teams skips outbox events for a `Rejected` identity and targets Drive/Group grants at the identity address; Camps' role drill-down shows each holder's identity address.
- **Admin:** Sync settings management is Admin-only.
- **Onboarding:** Volunteers membership is reconciled by `SystemTeamSyncJob` on name + consents only, which cascades to Google Group membership; the Consent Coordinator's clear/flag does not affect it.
- **Email:** `IGoogleRemovalNotificationService` (Google Integration-owned) dispatches the Google removal messages via `IEmailService.SendAsync(...)`, built by the section's own `GoogleIntegrationEmails` builder (this section owns the copy and its `/Email/EmailPreview` samples, peterdrier/Humans#1651), after every confirmed Google API delete in `IGoogleGroupSync` / `IGoogleDriveSync` (issue peterdrier/Humans#639). Variant 1 (loss-of-access) vs Variant 2 (secondary-email cleanup) is chosen by inspecting the recipient's `UserEmail` rows; messages are `MessageCategory.System` (no unsubscribe footer) and localized to the recipient's supported `User.PreferredLanguage`, falling back to English for blank, invalid or unsupported preferences. `SyncRemovalReason.EmailRotation` is plumbed through for audit/telemetry but does not suppress the notification — Workspace identity rotation produces a Variant 2 email so the user can confirm which address was tidied up. Suppression is limited to the orphan-address case (no matching `UserEmail` row, e.g. deleted user, anonymized human, or OAuth-rename-in-place).

## Architecture

**Owning services:** `GoogleWorkspaceSyncService` (implements `IGoogleSyncManagement`), `GoogleGroupSyncService` (implements `IGoogleGroupSync`), `GoogleDriveAccessSyncService` (implements `IGoogleDriveSync`), `GoogleAdminService`, `GoogleWorkspaceUserService`, `SyncSettingsService`, `EmailProvisioningService`, `GoogleTranslationService` (implements `IGoogleTranslationService`)
**Owned tables:** `sync_service_settings`, `google_sync_outbox`, `google_sync_log`, `google_resources`, `google_workspace_identities`
**Status:** (A) Migrated.

### Public contract

| Public interface | Methods | Notes |
|---|---:|---|
| `IGoogleSyncService` | 2 | Read contract: `GetFailedSyncEventCountAsync` (Notifications meter), `IsFolderReachableAsync` (Workgroups root-folder check). |
| `IGoogleSyncWrite` | 4 | `AddUserToTeamResourcesAsync` (Teams system-team sync), `EnsureTeamGroupAsync` (Teams), `CreateSubfolderAsync` / `RequestSyncAsync` (Workgroups). Each provisions or reconciles a Google resource on another section's behalf. |
| `ITeamResourceService` | 7 | Read contract: team-resource snapshots and summaries (Teams, Users, Monitor), `CanManageTeamResourcesAsync`, `GetServiceAccountEmailAsync`. |
| `IGoogleIntegrationService` | 2 | Read contract: `GetWorkspaceIdentityAsync` / `GetWorkspaceIdentitiesAsync` — a member's effective Workspace identity (`WorkspaceIdentity(UserId, Email, Status)`), keyed by the requested id (merge-aware). Teams, Camps, Users' admin list. |
| `IGoogleIntegrationWrite` | 1 | `ClearIfAddressAsync` — Users reports an address leaving an account. |

`GoogleWorkspaceSyncService` (or `StubGoogleSyncService`) implements the internal `IGoogleSyncManagement`; `TeamResourceService` implements the internal `ITeamResourceManagement`; `WorkspaceIdentityService` implements the internal `IWorkspaceIdentityManagement` (reads + `ClearIfAddressAsync` + `SetAsync`, `SetStatusFromSyncAsync` and the copy). DI registers each face to the same instance. Everything else (sync-by-type, drift checks, outbox requeue, metrics counts) is internal.

- Service(s) live in `src/Sections/Humans.GoogleIntegration/Services/` (namespace `Humans.GoogleIntegration.Services`) and never import `Microsoft.EntityFrameworkCore`.
- `ISyncSettingsRepository`, `IGoogleSyncOutboxRepository`, `IGoogleSyncLogRepository`, `IGoogleResourceRepository`, `IWorkspaceIdentityRepository` (all `internal`, in `src/Sections/Humans.GoogleIntegration/Data/`) are the only code paths that touch this section's tables via `DbContext`. They are unreachable from outside the section assembly, which is what retired the old `check-google-resource-ownership.sh` grep guard.
- **Decorator decision** — no caching decorator. All Google Integration services are either request-scoped admin operations or background-job processors; no hot bulk-read path warrants a `ConcurrentDictionary` projection.
- **Cross-domain navs** — none remain. `SyncServiceSettings.UpdatedByUserId`, `GoogleResource.TeamId`, and `GoogleSyncOutboxEvent.TeamId`/`UserId` are all bare Guid columns with no nav and no FK constraint. Team deletion is guarded instead by a pre-check in `TeamService.PermanentlyDeleteTeamAsync`.
- **Cross-section calls** — `ITeamService`, `ITeamWrite`, `IUserService`, `IUserEmailService`, `IEmailService`, `ILegacyGoogleSyncAuditReader`, `IAuditLogService` (write path — `GoogleController` logs requeue/link/remediate actions; `IAuditLogRepository` itself is `internal` to the AuditLog section and not reachable cross-section).
- **Architecture tests** (`tests/Humans.GoogleIntegration.Tests/Architecture/`) — `GoogleWorkspaceSyncBridgeArchitectureTests.cs` sweeps every type in `Humans.GoogleIntegration.Services` for Google SDK types in its signatures and pins the four sync bridge interfaces' namespace and shape-neutrality; `GoogleWorkspaceUserArchitectureTests.cs` pins `IWorkspaceUserDirectoryClient`; `TeamResourceArchitectureTests.cs` pins `ITeamResourceGoogleClient`.

### `<vc:google-sync-log>` — the sync-log render path

The section's `/Google/Resource/{id}` and `/Google/Human/{id}` pages emit `<vc:google-sync-log>` with the applicable predicate. The component owns the read (`IGoogleSyncLogViewer`) and render, while the page supplies its title, empty text, and back link. Keeping the host page here avoids a cross-section Razor tag-helper dependency. The render test seeds a row and asserts it reaches the page. `/Google/ActivityLog` reads the internal `IGoogleSyncLogService.GetRecentAsync`, capped at 500: a name filter is resolved to resource ids first (the log's distinct ids, named through `ITeamResourceService`), then the repository filters by those ids. Rows whose resource has no `google_resources` row (unregistered workgroup Drive folders, `ResourceId = Guid.Empty`) show their sync-log description instead (the Drive path the sync service writes, peterdrier/Humans#2000), or "(unlinked)" when it is empty, and the name filter also matches that description case-insensitively.

The read/write split is deliberate: `IGoogleSyncLogViewer` and its `GoogleSyncLogView` DTO are public only because a cross-assembly ViewComponent's constructor parameters must be. The write side and the Activity Log's cross-resource read, `IGoogleSyncLogService`, stay `internal` — nothing outside this section appends to the log or needs that query.

The GDPR sync-log export preserves the viewer fields and resolves resource names, but formats `OccurredAt` as UTC ISO 8601 so download JSON contains a timestamp. Page rendering retains the public viewer DTO.

### Repository surface

- **`ISyncSettingsRepository`** — owns `sync_service_settings`. Unique index on `ServiceType` (`SyncServiceSettingsConfiguration.cs:31`). One row per `SyncServiceType`, seeded (reserved GUID block 0002).
- **`IGoogleSyncOutboxRepository`** — owns `google_sync_outbox`. Entity holds `TeamId`/`UserId` scalars only; no entity-level navs and no FK constraint — bare Guid columns. Indexes on `(ProcessedAt, OccurredAt)`, `(TeamId, UserId, ProcessedAt)`, and `DeduplicationKey` (unique). Enqueue writes live here (`AddAsync` / `AddRangeAsync`) and are surfaced to producers through `IGoogleSyncOutboxService`. When an enqueue must be atomic with another section's mutation — e.g. `TeamService` queuing an event on a `TeamMember` change — the producer wraps the team-repository call and `IGoogleSyncOutboxService.AddAsync` in an ambient `TransactionScope` rather than folding the outbox write into `TeamRepository`.
- **`IGoogleSyncLogRepository`** — owns `google_sync_log`. Append + three top-N reads (by resource, by user ids at 200 rows each; recent-across-resources for the Activity Log, limit set by the caller) + the distinct resource ids it references + the GDPR paths (`DeleteByUserIdsAsync` erasure, `GetAllByUserIdsContributorAsync` export). Its main caller is `GoogleSyncLogService`, which writes best-effort (a failed log is swallowed after an Error, never failing the sync) and reads for `<vc:google-sync-log>`. `AddRangeAsync` + `GetExistingIdsAsync` exist for the one-time history migration and go with it.
- **`IGoogleResourceRepository`** — owns `google_resources`. `TeamResourceService` is the main caller; `GoogleWorkspaceSyncService` and `GoogleDriveAccessSyncService` use it for resource reads and reconciliation-loop atomic writes. Other sections read through `ITeamResourceService`; writes stay inside the section.

> `DriveActivityMonitorService` moved to the **Monitor** section (`src/Sections/Humans.Monitor`) — it injects five sections' services and calls no repository, so it is a cross-section orchestrator rather than GoogleIntegration's. It reads this section through `IGoogleDriveActivityClient` and `ITeamResourceService`, and persists its `DriveActivityMonitor:LastRunAt` marker through `ISettingsService`. See [Monitor.md](../../Humans.Monitor/Docs/Monitor.md).

### Connector clients

Section services depend only on shape-neutral connector interfaces in `Humans.GoogleIntegration.Services.Workspace` (`IGoogleDriveActivityClient` is in the section's `Contracts/` folder, because Monitor consumes it across an assembly boundary) — `IGoogleDirectoryClient`, `IGoogleDrivePermissionsClient`, `IGoogleGroupMembershipClient`, `IGoogleGroupProvisioningClient`, `ITeamResourceGoogleClient`, `IGoogleDriveActivityClient`, `IWorkspaceUserDirectoryClient`, `IGoogleTranslationClient` — so they never import `Google.Apis.*` (design-rules §13). Real Google-backed implementations and dev-mode stubs live beside them in `src/Sections/Humans.GoogleIntegration/Services/Workspace/`. `IGoogleDrivePermissionsClient.CreateFolderAsync` is the one folder-creation capability on that connector — used by `IGoogleSyncWrite.CreateSubfolderAsync`, not by any reconciliation path.

### Touch-and-clean guidance

- None open.

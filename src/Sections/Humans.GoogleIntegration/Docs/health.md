# GoogleIntegration — Target Shape

## 1. What the section does

The collective works in Google Workspace, and this section keeps Google in step with
Humans. Every team may own a Google Group (made on demand, named from the team) and any
number of pre-shared Drive folders or files that an admin links by URL; other sections
(Workgroups) may also claim Drive folders they created through this section. The section
never decides who belongs where: it asks the sections that know, merges their answers,
and makes Google match. When someone joins a team, the section is told, remembers the
request, and drains it shortly after, granting that person's Google identity on each of
the team's resources. Every night it walks every claimed resource, repairs drift in both
directions, repairs the settings on every group, tightens folder inheritance, refreshes
folder names, and tells admins what it did. Each removal earns the person an email
explaining what they lost, unless the address is nobody's any more.

Around that core sit the smaller services the collective needed from Google: provisioning
a `@nobodies.team` mailbox for a person (with recovery-address handling so the credentials
reach them), spotting people whose Workspace address was renamed under them, reporting
which mail addresses break the one-Google-identity rule, watching Drive activity for the
Monitor section, and translating survey text. Admins choose per service whether automation
may add, add-and-remove, or do nothing, can look at the queue and re-run failed items, and
can run the reconcile and group-settings checks by hand. Every permission grant or
revocation lands in the section's own sync log, which an activity page lists and other
pages render for a resource or a person.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "This person joined/left this team — update Google, later" | Teams | `IGoogleSyncOutboxService.AddAsync` / `AddRangeAsync` |
| "Make sure this team has its Group" / "Reconcile this one resource now" | Teams | `IGoogleSyncWrite.EnsureTeamGroupAsync` / `SyncSingleResourceAsync` |
| "Grant this person the team's resources now" | Teams (system-team sync), the outbox processor | `IGoogleSyncWrite.AddUserToTeamResourcesAsync` |
| "Remove this person from the team's resources" | Users (suspension), Teams (system-team sync), the outbox processor | `IGoogleSyncWrite.RemoveUserFromTeamResourcesAsync` — a logged no-op: removal happens at the next reconcile |
| "Create a Drive folder under X" / "Re-check this folder's access soon" / "Can the service account read X?" | Workgroups | `IGoogleSyncWrite.CreateSubfolderAsync` / `RequestSyncAsync`, `IGoogleSyncService.IsFolderReachableAsync` |
| "Reconcile every group" (the system-team sync) | Teams | `IGoogleGroupSync.ReconcileAllAsync` |
| "Who is supposed to be in group X?" — asked *by* this section | answered by Teams and Camps | `IGoogleGroupMembershipSource.GetExpectedAsync` |
| "Who is supposed to have access to Drive folder X, at what level?" — asked *by* this section | answered by Teams and Workgroups | `IGoogleDriveAccessSource.GetExpectedAccessAsync` |
| "Which resources does this team have?" / "Link, unlink, change level, deactivate" | Teams' pages and this section's sync page | `ITeamResourceService` (reads) / `ITeamResourceWrite` (mutations) |
| "Provision `name@nobodies.team` for this person" | Teams' team-admin page, this section's person page | `IEmailProvisioningService.ProvisionNobodiesEmailAsync` |
| "What happened in these Drive folders since T?" | Monitor | `IGoogleDriveActivityClient` + `ITeamResourceService.GetActiveDriveFoldersAsync` |
| "Translate this text" | Surveys | `IGoogleTranslationService.TranslateAsync` |
| "How many sync events have failed?" | Notifications' admin meter | `IGoogleSyncService.GetFailedSyncEventCountAsync` |
| "What did sync do to this resource / person / overall?" | `/Google/Resource/{id}`, `/Google/Human/{id}`, `/Google/ActivityLog` | `IGoogleSyncLogViewer` / `IGoogleSyncLogService` |
| Everything an admin does by hand: modes, outbox, accounts, groups, renames, flags, checks | the section's own `/Google/*` screens and its `/Settings` tab | `GoogleController` → internal services |
| Nightly: reconcile, inheritance, paths, settings; every 10 min: drain the queue | Hangfire | `GoogleResourceReconciliationJob`, `ProcessGoogleSyncOutboxJob` |

Vocabulary: `GoogleResource` / `GoogleResourceType` / `DrivePermissionLevel` (a linked thing
and how much access members get), `GoogleSyncOutboxEvent` + `GoogleSyncOutboxEventTypes`
(a remembered membership change), `SyncServiceType` / `SyncMode` (what automation may do
per service), `SyncAction` / `ResourceSyncDiff` / `SyncPreviewResult` (what a reconcile
would or did change), `GoogleDriveAccessClaim` (one source's expected access to one
folder), `GoogleResourceGrantResult` (whether Google actually accepted a grant),
`GoogleSyncLogView` (one line of the trail), `GoogleWorkspaceOptions` (domain, customer id,
default group settings; credentials live in Base's `GoogleWorkspaceSettings`).

## 3. Structure

The shapes imply:

- **A `Contracts/` folder** carrying the cross-section interfaces above and the DTO/enum
  vocabulary, and nothing else. Each interface names one shape; the cross-section reads sit
  on `IGoogleSyncService` / `ITeamResourceService`, the writes on `IGoogleSyncWrite` /
  `ITeamResourceWrite`, and everything only this section calls on the internal
  `IGoogleSyncManagement` / `ITeamResourceManagement`.
- **One outbox service + one processor**: remember, then drain. The processor owns the
  retry and permanent-failure rules and marks the person's Google email valid once Google
  accepted a grant. Without credentials it leaves the batch pending and reports the job as
  skipped.
- **One reconciler per resource kind**, each fed by its own source fan-out: groups
  (`GoogleGroupSyncService` over `IGoogleGroupMembershipSource`) and Drive
  (`GoogleDriveAccessSyncService` over `IGoogleDriveAccessSource`). Both union their
  sources, hydrate and filter users the same way (`GoogleSyncMemberHydration`), apply the
  per-service mode, write the sync log, and have a Hangfire-scheduled single-resource path.
- **One sync facade** (`GoogleWorkspaceSyncService`) for what is neither reconciler:
  immediate grants from the outbox, team group provisioning, group-settings drift, folder
  paths and inheritance, subfolder creation, and the outbox admin reads.
- **One workspace-admin facade** (accounts, renames, domain groups) and **one Drive
  activity client**, each behind an internal client interface so the stubbed,
  credential-less configuration remains resolvable without making external calls.
- **One email-provisioning orchestration**, **one removal-notification decision**, **one
  translation client**, **one settings service** (the per-service mode).
- **Repositories** over the section's tables (resources, outbox, sync log, service
  settings), each internal, each the only writer of its table.
- **One controller** for every admin screen, authorized per action, plus the view
  components the rest of the app embeds, the Hangfire jobs, health check, metrics and nav
  contributions.
- **`Section.cs` + `SectionAdminNav.cs` + `SectionConfiguration.cs` + `SectionHealthChecks.cs`
  + `SectionJobs.cs` + `SectionSettings.cs`** and the resource class at the root, nothing
  else.

## 4. Invariants

- **Only direct permissions are ever removed** on Drive resources; inherited Shared Drive
  permissions are classified `Inherited` and never deleted, and a delete Google refuses as
  inherited is recorded as such (`GoogleDriveAccessSyncService.cs:385`,
  `GoogleDriveAccessSyncService.cs:561`).
- **A membership change is never lost**: it is written to the outbox before the caller
  returns, left pending while credentials are absent (`GoogleSyncOutboxProcessor.cs:54`),
  retried up to ten times (`GoogleSyncOutboxProcessor.cs:31`), and a permanent failure
  (HTTP 400, 403, 404 — `DrivePermissionErrorPolicy.cs:7`) is parked visibly with a
  per-event retry (`GoogleSyncOutboxProcessor.cs:128`), never silently dropped.
- **Sync mode gates every Execute, scheduled and manual alike**: `None` means no writes;
  `AddOnly` means adds only; an admin's "Sync Now" has no bypass
  (`GoogleDriveAccessSyncService.cs:315`, `GoogleGroupSyncService.cs:382`,
  `GoogleWorkspaceSyncService.cs:52`).
- **Every Drive/Group permission grant or revocation leaves a sync-log row**, success or
  failure (`GoogleDriveAccessSyncService.cs:466`, `GoogleGroupSyncService.cs:448`); account,
  rename and group-settings writes leave audit entries instead.
- **Outbox-driven grants record their trigger**: `TeamMemberJoined` for a membership event,
  `ManualSync` for an admin rerun (`GoogleSyncOutboxProcessor.cs:81`).
- **A removal never notifies an orphan address**: no `UserEmail` row → suppressed and
  logged (`GoogleRemovalNotificationService.cs:39`).
- **The person's Google-email status is only set from sync when Google actually answered**:
  valid after an accepted grant (`GoogleSyncOutboxProcessor.cs:118`), rejected after Google
  refused the target (`GoogleWorkspaceSyncService.cs:148`,
  `GoogleDriveAccessSyncService.cs:487`, `GoogleGroupSyncService.cs:437`), untouched
  otherwise.
- **Provisioning captures the recovery address before linking the new mailbox**
  (`EmailProvisioningService.cs:91`), rejects any address already bound to another human
  or to a team's group (`EmailProvisioningService.cs:55`, `EmailProvisioningService.cs:70`),
  and audits the act (`EmailProvisioningService.cs:119`).
- **Without credentials the section boots, stubbed**: registration swaps real clients for
  stubs by configuration alone, in every environment (`Section.cs:82`); the missing
  credentials surface through the health check, never as a refusal to start. The
  reconciliation job (`GoogleResourceReconciliationJob.cs:39`) and the scoped sync
  schedulers (`HangfireGoogleDriveAccessSyncScheduler.cs:14`,
  `HangfireGoogleGroupSyncScheduler.cs:14`) skip rather than reconcile against stubs.
- **A reconcile never stops mid-list**: one resource's failure is recorded against that
  resource and the walk continues (`GoogleDriveAccessSyncService.cs:220`), and one phase's
  failure in the nightly job does not stop the next phase
  (`GoogleResourceReconciliationJob.cs:108`).
- **Every `/Google/*` screen and action denies Volunteers and Coordinators**: the sync page
  and its preview admit TeamsAdmin and Board (`GoogleController.cs:283`), a resource's sync
  trail admits Board (`GoogleController.cs:28`), a person's sync trail admits HumanAdmin and
  Board (`GoogleController.cs:44`), mailbox provisioning admits HumanAdmin
  (`GoogleController.cs:369`), everything else is Admin-only.

## 5. Seams

- **`GoogleWorkspaceHealthCheck` calls `Google.Apis` directly** instead of a client
  interface; moving it behind a client is unbuilt, so items touching the health check are
  shaped by it.
- **`SyncExecute` reconciles inline on the request thread**; the "enqueue and return" shape
  is unbuilt.
- **Manual sync and group-settings remediation carry no actor**: `SyncExecute`,
  `SyncExecuteAll` and `RemediateGroupSettings` pass no acting user, `IGoogleSyncLogService`
  has no actor parameter, and the remediation audit names the service. Propagating the
  admin's identity is unbuilt.
- **Sync-history migration** (`GoogleSyncHistoryMigrationAdminController` and its service,
  the "Sync history migration" nav entry) is a one-shot data move awaiting its retirement
  with the Google columns on `audit_log`; nothing new should hang off it.
- **`GoogleResourceType.SharedDrive`** is reserved, rendered, and never linked; whether a
  whole-drive link ever ships decides whether it stays.

## 6. Deliberately not done

- **No Drive folder creation for teams.** Linking pre-shared resources is the only team
  entry path; the service account never owns team content (feature spec US-7.1).
  `CreateSubfolderAsync` exists only for Workgroups' own registered folders.
- **No immediate per-user removal.** Removals wait for the reconcile, which sees every
  source's claim at once; removing on one team's leave event would revoke access another
  team or workgroup still grants.
- **No per-event failure notification.** Failed outbox events surface through the admin
  meter and the outbox screen with Retry; the alert was removed as noise.
- **No admin fix for renamed addresses.** Renames self-heal on the person's next Google
  sign-in; the screen is read-only by design.
- **No resx for the admin screens** (`localization-admin-exempt`); the section's resx keys
  carry the member-facing email and notification copy and the Accounts page's labels.
- **No unique index on anything user-editable** (`unique-constraints-ids-only`): the
  outbox's `DeduplicationKey` and the settings row's `ServiceType` are the only non-Id
  unique indexes, both system keys; service guards and tests enforce the rest.
- **No central membership registry.** Teams and Camps each register as an
  `IGoogleGroupMembershipSource`, Teams and Workgroups as an `IGoogleDriveAccessSource`; the
  section asks all of them and merges. A central "who is in what" service would re-widen the
  lane.
- **No caching decorator on the sync services.** Every call is a Google round-trip whose
  freshness is the point.

## Load-bearing weirdness

- **The dependency points both ways on purpose.** Teams, Camps and Workgroups depend on
  this section to enqueue, link and create, and this section depends on them (as membership
  and Drive-access sources) to learn expected access. The source interfaces live here so the
  section never references Teams, Camps or Workgroups for that.
- **Hangfire jobs carry their current signature only.** Every Hangfire argument is
  explicit, including `CancellationToken.None` and the original sync trigger. Legacy
  signature overloads are forbidden by standing policy D13
  (`memory/process/debt-sweep-standing-policy.md`); jobs re-register at startup and a job
  queued across a signature change is dropped.
- **Sync writes to Users** — marking the person's Google email valid/rejected is Users'
  data, written through Users' own `TrySetGoogleEmailStatusFromSyncAsync` — a narrow
  public write, not a leak.
- **Paired DI registrations per client type** (real vs stub) chosen by
  `hasGoogleCredentials` in `Section.cs`; the stubs are the test doubles for the whole
  section and are deliberately not in the test project.
- **`GoogleWorkspaceSyncService` is still the section's largest class**: the facade
  every outside caller sees (via `IGoogleSyncService` / `IGoogleSyncWrite`) plus group
  provisioning, settings drift, and folder housekeeping. The Drive reconciler has moved out;
  splitting the rest is real work, not a doctor strike.
- **`SyncServiceType.Discord` is seeded** although no Discord integration exists — the
  settings row and the mode UI were built ahead of it; the enum value is data, not code.
- **Removal notifications pick Variant 2 ("secondary cleanup") when the person still has
  another verified Google address**, so a person with two Google identities is never told
  they lost access they still have through the other one.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-06 | Dead drift-notification link fixed; migration history cut; `ITeamResourceService` narrowed | peterdrier/Humans#1599 |
| 2 | 2026-10-18 | Docs and comments caught up with the folded-in contracts and source fan-outs; Hangfire compat overload cut | peterdrier/Humans#2023 |

# GoogleIntegration — Target Shape

## 1. What the section does

The collective works in Google Workspace, and this section keeps Google in step with
Humans. Every team may own a Google Group (made on demand, named from the team) and any
number of pre-shared Drive folders or files that an admin links by URL; the section never
creates Drive resources itself. When someone joins or leaves a team, the section is told,
remembers the request, and drains it shortly after — adding or removing that person's
Google identity on each of the team's resources. Every night it walks every resource,
repairs drift in both directions, repairs the settings on every group, tightens folder
inheritance, refreshes folder names, and tells admins what it did. Each removal earns the
person an email explaining what they lost, unless the address is nobody's any more.

Around that core sit the smaller services the collective needed from Google: provisioning
a `@nobodies.team` mailbox for a person (with recovery-address handling so the credentials
reach them), spotting people whose Workspace address was renamed under them, reporting
which mail addresses break the one-Google-identity rule, watching Drive activity for the
Monitor section, and translating survey text. Admins choose per service whether automation
may add, add-and-remove, or do nothing, can look at the queue and re-run failed items, and
can run any of the nightly checks by hand. Every Google-side action lands in the section's
own sync log, which other pages render for a resource or a person.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "This person joined/left this team — update Google, later" | Teams | `IGoogleSyncOutboxService.AddAsync` / `AddRangeAsync` |
| "Make sure this team has its Group" / "Reconcile this one resource now" | Teams | `IGoogleSyncService.EnsureTeamGroupAsync` / `SyncSingleResourceAsync` |
| "Remove this person from a team's resources now" | Users (suspension), Teams | `IGoogleSyncService.RemoveUserFromTeamResourcesAsync` / `AddUserToTeamResourcesAsync` |
| "Reconcile every group" (the system-team sync) | Teams | `IGoogleGroupSync.ReconcileAllAsync` |
| "Who is supposed to be in group X?" — asked *by* this section | answered by Teams and Camps | `IGoogleGroupMembershipSource.GetExpectedAsync` |
| "Which resources does this team have?" / "Link, unlink, change level, deactivate" | Teams' team and team-admin pages, Monitor | `ITeamResourceService` |
| "Provision `name@nobodies.team` for this person" | Teams' team-admin page | `IEmailProvisioningService.ProvisionNobodiesEmailAsync` |
| "What happened in these Drive folders since T?" | Monitor | `IGoogleDriveActivityClient` + `ITeamResourceService.GetActiveDriveFoldersAsync` |
| "Translate this text" | Surveys | `IGoogleTranslationService.TranslateAsync` |
| "How many sync events are pending / failed?" | Notifications' admin meters | `IGoogleSyncServiceRead` |
| "What did sync do to this resource / person?" | Teams' and Users' pages via `<vc:google-sync-log>` | `IGoogleSyncLogViewer` |
| "Which Google resources are mine?" | the member dashboard slot | `MyGoogleResourcesViewComponent` (own) |
| Everything an admin does by hand: modes, outbox, accounts, groups, renames, flags, checks | the section's own `/Google/*` screens | `GoogleController` → internal services |
| Nightly: reconcile, settings, inheritance, paths; every 10 min: drain the queue | Hangfire | `GoogleResourceReconciliationJob`, `ProcessGoogleSyncOutboxJob` |

Vocabulary: `GoogleResource` / `GoogleResourceType` / `DrivePermissionLevel` (a linked thing
and how much access members get), `GoogleSyncOutboxEvent` + `GoogleSyncOutboxEventTypes`
(a remembered membership change), `SyncServiceType` / `SyncMode` (what automation may do
per service), `SyncAction` / `ResourceSyncDiff` / `SyncPreviewResult` (what a reconcile
would or did change), `GoogleSyncLogView` (one line of the trail), `GoogleWorkspaceOptions`
(domain, service account, admin identity).

## 3. Structure

The shapes imply:

- **A contracts leaf** carrying the eight cross-section interfaces above and the DTO/enum
  vocabulary, and nothing else. Each interface names one shape; the wide ones
  (`IGoogleSyncService`, `ITeamResourceService`) should carry only the methods an outside
  caller actually asks — the rest belong to internal interfaces.
- **One outbox service + one processor**: remember, then drain. The processor owns the
  retry and permanent-failure rules and marks the person's Google email valid or rejected.
- **One reconciler per resource kind**: groups (with the Hangfire-scheduled single-group
  path) and Drive permissions, both fed by the membership sources and both writing the
  sync log through one logging service.
- **One workspace-admin facade** (accounts, renames, domain groups, group settings) and
  **one Drive activity client**, each behind an internal client interface so the stubbed,
  credential-less configuration runs the same code paths.
- **One email-provisioning orchestration**, **one removal-notification decision**, **one
  translation client**, **one settings service** (the per-service mode).
- **Repositories** over the section's four tables (resources, outbox, sync log, service
  settings), each internal, each the only writer of its table.
- **One controller** for every admin screen, authorized per action, plus the two view components
  the rest of the app embeds, plus the two Hangfire jobs, health check, metrics and nav
  contributions.
- **`Section.cs` + `SectionAdminNav.cs` + `SectionJobs.cs` + `SectionMemberDashboard.cs`
  + `SectionHealthChecks.cs`** and nothing else at the root.

## 4. Invariants

- **Only direct permissions are ever removed** on Drive resources; inherited Shared Drive
  permissions are never touched. A removal that drops an inherited grant is a bug.
- **A membership change is never lost**: it is written to the outbox before the caller
  returns, drained in order, retried up to ten times, and a permanent failure (HTTP 400,
  403, 404) is parked visibly with a per-event retry, never silently dropped.
- **Sync mode gates every Execute, scheduled and manual alike**: `None` means no writes;
  `AddOnly` means adds only; an admin's "Sync Now" has no bypass.
- **Every Google-side write leaves a sync-log row** (success or failure), and every
  admin-triggered write leaves an audit entry naming the admin.
- **A removal notifies the person exactly once, and never an orphan address** (no
  `UserEmail` row → suppressed and logged).
- **The person's Google-email status is only set from sync when Google actually answered**:
  valid after a real add, rejected after a permanent failure, untouched otherwise.
- **Provisioning captures the recovery address before linking the new mailbox**, rejects
  any address already bound to another human or to a team's group, and audits the act.
- **Without credentials the section runs, stubbed**: registration swaps real clients for
  stubs by configuration alone; production refuses to start stubbed.
- **The reconciliation job never stops mid-list**: one resource's failure is recorded
  against that resource and the walk continues.
- **Every `/Google/*` screen and action denies Volunteers and Coordinators**: the sync
  dashboard and its preview admit TeamsAdmin and Board, email provisioning admits
  HumanAdmin, everything else is Admin-only; the team-resource actions on Teams' pages
  defer to `CanManageTeamResourcesAsync`.

## 5. Seams

- **No `ITeamResourceServiceRead`.** Teams' page services and Monitor take the full
  `ITeamResourceService` for reads (`debt-ledger.yml`); the read split is the next
  PR-sized cut and reshapes every item touching that interface.
- **`GoogleWorkspaceHealthCheck` calls `Google.Apis` directly** instead of a client
  interface (`debt-ledger.yml`); moving it behind a client is queued, so items touching
  the health check are shaped by it.
- **`SyncExecute` reconciles inline on the request thread** (`debt-ledger.yml`); the
  queued shape is "enqueue and return".
- **Sync-history migration** (`GoogleSyncHistoryMigrationAdminController` and its service,
  the "Temp" nav group) is a one-shot data move awaiting its retirement; nothing new
  should hang off it.
- **`GoogleResourceType.SharedDrive`** is reserved, rendered, and never linked; whether a
  whole-drive link ever ships decides whether it stays.

## 6. Deliberately not done

- **No Drive folder creation.** Linking pre-shared resources is the only entry path; the
  service account never owns content (feature spec US-7.1).
- **No per-event failure notification.** Failed outbox events surface through the admin
  meter and the outbox screen with Retry; the alert was removed as noise.
- **No admin fix for renamed addresses.** Renames self-heal on the person's next Google
  sign-in; the screen is read-only by design.
- **No resx for the admin screens** (`localization-admin-exempt`); the section's two
  keys exist only for the Accounts page.
- **No DB-level uniqueness on the outbox or the settings row** — project rule; service
  guards and tests are the enforcement.
- **No second membership source registry.** Teams and Camps each register themselves as
  an `IGoogleGroupMembershipSource`; the section asks all of them and merges. A central
  "who is in what" service would re-widen the lane.
- **No caching decorator on the sync services.** Every call is a Google round-trip whose
  freshness is the point.

## Load-bearing weirdness

- **The dependency points both ways on purpose.** Teams and Camps depend on this leaf to
  enqueue and link, and this section depends on them (as `IGoogleGroupMembershipSource`) to
  learn expected membership. The interface lives here so the section never references
  Teams or Camps directly.
- **`IGoogleGroupSync.ReconcileOneAsync` has a 4-parameter overload only for Hangfire**:
  the scheduler binds the expression to a signature Hangfire can serialise; callers in
  code use the 5-parameter one.
- **`GoogleSyncOutboxProcessor` carries `[CrossSectionWrite]`** because marking the
  person's Google email valid/rejected is Users' data, written through Users' own
  `TrySetGoogleEmailStatusFromSyncAsync` — a declared, narrow write, not a leak.
- **Two DI registrations per client type** (real vs stub) chosen by `hasGoogleCredentials`
  in `Section.cs`, with a production guard; the stubs are the test doubles for the whole
  section and are deliberately not in the test project.
- **`GoogleWorkspaceSyncService` is ~1.8k lines** because it is the facade every outside
  caller sees (`IGoogleSyncService`) *and* the Drive reconciler. The split into a thin
  facade over the group and Drive reconcilers is real work, not a doctor strike; until
  then its size is known, not news.
- **`SyncServiceType.Discord` is seeded** although no Discord integration exists — the
  settings row and the mode UI were built ahead of it; the enum value is data, not code.
- **Removal notifications pick Variant 2 ("secondary cleanup") when the person still has
  another verified Google address**, so a person with two Google identities is never told
  they lost access they still have through the other one.
- **The member dashboard component gates on Volunteer membership itself** rather than
  trusting the shared slot: the slot is shared, the rule is this section's.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-06 | Dead drift-notification link fixed; migration history cut; `ITeamResourceService` narrowed | peterdrier/Humans#1599 |

<!-- freshness:triggers
  src/Sections/Humans.Teams/**
  src/Sections/Humans.Teams.Contracts/**
  src/Humans.Base/Constants/SystemTeamIds.cs
-->
<!-- freshness:flag-on-change
  Department/sub-team hierarchy rules, system-team automation, coordinator-vs-manager scope, hidden/promoted team visibility, and SystemTeamIds constants — review when Teams services/entities/controllers/auth handlers change.
-->

# Teams — Section Invariants

Departments and sub-teams, their members, join requests, named role slots, team pages, early-entry grants, and the system teams computed from other sections' facts.

## Concepts

- A **Department** is a team with no parent. A **Sub-Team** sits under exactly one department; only one level of nesting is allowed.
- **System teams** (Volunteers, Coordinators, Board, Asociados, Colaboradors, Barrio Leads) are computed by the system-team sync; nobody adds or removes their members by hand.
- A **Coordinator** holds the management role on a department: full authority over the department and its sub-teams, including Google resources. Coordinators are members of the Coordinators system team.
- A **Sub-team Manager** holds the management role on a sub-team: members, join requests, roles, shifts and the page of that sub-team only. Managers have no authority over Google resources, the parent department or sibling sub-teams, and are not added to the Coordinators system team.
- A **Team Page** is the Markdown page of a department, public or member-facing, with up to three calls to action.
- A **hidden team** (`IsHidden`) is invisible to everyone except Admin, Board and TeamsAdmin. A **sensitive team** (`IsSensitive`) is an admin-only flag that adds a confirmation step before adding or approving a member.
- **Send as team** relays a one-off message from a coordinator to another member on behalf of a team, with replies going to the team's Google Group.

## Data Model

### Team

**Table:** `teams`

Name, slug and optional custom slug, description, approval mode, parent, system-team type, Google Group prefix, and the flags `IsActive`, `IsHidden`, `IsSensitive`, `IsPromotedToDirectory`, `HasBudget`, `EarlyEntryEnabled`, `IsPublicPage`, `ShowCoordinatorsOnPublicPage`. Page content lives on the row (`PageContent`, `PageContentUpdatedAt`, `PageContentUpdatedByUserId`, `CallsToAction` as JSONB). Aggregate-local navs: `ParentTeam`, `ChildTeams`, `Members`, `EarlyEntryGrants`, `JoinRequests`, `RoleDefinitions`.

### TeamMember

**Table:** `team_members`

One membership of a human in a team, with `TeamMemberRole` and `JoinedAt`/`LeftAt`. Ended memberships are kept as history.

### TeamJoinRequest / TeamJoinRequestStateHistory

**Tables:** `team_join_requests`, `team_join_request_state_history`

A request to join an approval-required team (`Pending → Approved / Rejected / Withdrawn`), with the requester's message and the reviewer's notes, both at most 2,000 characters. The state history is append-only per design-rules.md, *Append-only entities*.

### TeamRoleDefinition / TeamRoleAssignment

**Tables:** `team_role_definitions`, `team_role_assignments`

Named role slots on a team: name, description, slot count, a `SlotPriority` per slot, sort order, `IsManagement`, `IsPublic`, `RolePeriod`, and nullable `EstimatedHours` (non-negative whole hours a year; informational, gates nothing). Role names are unique per team. An assignment puts a team member in one slot of one definition.

### TeamEarlyEntryGrant

**Table:** `team_early_entry_grants`

One event-scoped early-entry grant: a human may enter on a date for a named project, on a team with `EarlyEntryEnabled`. `EventSettingsId` identifies the Settings event and is nullable only for legacy grants awaiting review. Only grants for the active event contribute to the EarlyEntry roster as `"{TeamName}: {ProjectName}"`; dates and years never infer an event.

### Enums

- `SystemTeamType`: `None`, `Volunteers`, `Coordinators`, `Board`, `Asociados`, `Colaboradors`, `BarrioLeads`.
- `RolePeriod` (stored as string): `YearRound`, `Build`, `Event`, `Strike`; filters the roster.
- `SlotPriority`: `None`, `Critical`, `Important`, `NiceToHave`. Badge colours for both are registered in `Section.Register` through `EnumBadgeMap`.

### SystemTeamIds (constants)

| Constant | Value |
|----------|-------|
| Volunteers | `00000000-0000-0000-0001-000000000001` |
| Coordinators | `00000000-0000-0000-0001-000000000002` |
| Board | `00000000-0000-0000-0001-000000000003` |
| Asociados | `00000000-0000-0000-0001-000000000004` |
| Colaboradors | `00000000-0000-0000-0001-000000000005` |
| BarrioLeads | `00000000-0000-0000-0001-000000000006` |

## Routing

`TeamController` (`[Route("Teams")]`) serves the member-facing and TeamsAdmin pages. `TeamAdminController` (`[Route("Teams/{slug}")]`) serves per-team management through `HumansTeamControllerBase`. `TeamMessageController` serves send as team. A team's Google resources page is GoogleIntegration's (`/Google/Teams/{slug}/Resources`); the team page and the Members page link to it.

| Route | Method | Controller | Auth |
|-------|--------|------------|------|
| `GET /Teams` | `Index` | `TeamController` | `[AllowAnonymous]` — anonymous sees public teams only |
| `GET /Teams/{slug}` | `Details` | `TeamController` | `[AllowAnonymous]` — anonymous sees public pages; hidden teams 404 for non-admins |
| `GET /Teams/Tree` | `Tree` | `TeamController` | `[AllowAnonymous]` |
| `GET /Teams/Roster` | `Roster` | `TeamController` | `[Authorize]` |
| `GET /Teams/My` | `MyTeams` | `TeamController` | `[Authorize]` |
| `GET /Teams/{slug}/Join` | `Join` (GET) | `TeamController` | `[Authorize]` — hidden teams 404 for non-admins |
| `POST /Teams/{slug}/Join` | `Join` (POST) | `TeamController` | `[Authorize]` — hidden teams 404 for non-admins |
| `POST /Teams/{slug}/Leave` | `Leave` | `TeamController` | `[Authorize]` |
| `POST /Teams/Requests/{id}/Withdraw` | `WithdrawRequest` | `TeamController` | `[Authorize]` |
| `GET /Teams/Summary` | `Summary` | `TeamController` | `TeamsPolicies.Read` |
| `GET /Teams/Create` | `CreateTeam` (GET) | `TeamController` | `TeamsPolicies.Manage` |
| `POST /Teams/Create` | `CreateTeam` (POST) | `TeamController` | `TeamsPolicies.Manage` |
| `GET /Teams/{id:guid}/Edit` | `EditTeam` (GET) | `TeamController` | `TeamsPolicies.Read` |
| `POST /Teams/{id:guid}/Edit` | `EditTeam` (POST) | `TeamController` | `TeamsPolicies.Manage` |
| `POST /Teams/{id:guid}/Delete` | `DeleteTeam` | `TeamController` | `PolicyNames.AdminOnly` |
| `GET /Teams/{teamId:guid}/GoogleResources` | `GetTeamGoogleResources` | `TeamController` | `TeamsPolicies.Read` — JSON for the parent-resource warning |
| `GET /Teams/{teamId:guid}/SendMessage/{id:guid}` | `SendMessage` (GET) | `TeamMessageController` | `[Authorize]` + `ITeamMessageOptionsProvider` |
| `POST /Teams/{teamId:guid}/SendMessage/{id:guid}` | `SendMessage` (POST) | `TeamMessageController` | `[Authorize]` + the same check |
| `GET /Teams/{slug}/Members` | `Members` | `TeamAdminController` | `ResolveTeamViewAsync` |
| `POST /Teams/{slug}/Members/Add` | `AddMember` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Members/{userId}/Remove` | `RemoveMember` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Members/{userId}/ProvisionEmail` | `ProvisionEmail` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `GET /Teams/{slug}/Members/Search` | `SearchUsers` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `GET /Teams/{slug}/Roster` | `Roster` | `TeamAdminController` | `BoardOrAdmin` + `ResolveTeamViewAsync` |
| `POST /Teams/{slug}/Requests/{requestId}/Approve` | `ApproveRequest` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Requests/{requestId}/Reject` | `RejectRequest` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `GET /Teams/{slug}/Roles` | `Roles` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Roles/Create` | `CreateRole` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Roles/{roleId}/Edit` | `EditRole` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Roles/{roleId}/Delete` | `DeleteRole` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Roles/{roleId}/ToggleManagement` | `ToggleManagement` | `TeamAdminController` | `ResolveTeamManagementAsync` + `TeamsPolicies.Manage` |
| `POST /Teams/{slug}/Roles/{roleId}/Assign` | `AssignRole` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Roles/{roleId}/Unassign/{memberId}` | `UnassignRole` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `GET /Teams/{slug}/Roles/SearchMembers` | `SearchMembersForRole` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `GET /Teams/{slug}/EditPage` | `EditPage` (GET) | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/EditPage` | `EditPage` (POST) | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `GET /Teams/{slug}/EarlyEntry` | `EarlyEntry` | `TeamAdminController` | `ResolveEarlyEntryManagementAsync` — only when `EarlyEntryEnabled` |
| `POST /Teams/{slug}/EarlyEntry/Add` | `AddEarlyEntry` | `TeamAdminController` | `ResolveEarlyEntryManagementAsync` |
| `POST /Teams/{slug}/EarlyEntry/Edit` | `EditEarlyEntry` | `TeamAdminController` | `ResolveEarlyEntryManagementAsync` |
| `POST /Teams/{slug}/EarlyEntry/Remove` | `RemoveEarlyEntry` | `TeamAdminController` | `ResolveEarlyEntryManagementAsync` |
| `GET /Teams/{slug}/EarlyEntry/LookupTicket` | `LookupTicket` | `TeamAdminController` | `ResolveEarlyEntryManagementAsync` |

`ResolveTeamManagementAsync` checks `TeamAuthorizationHandler` with `TeamOperationRequirement.ManageCoordinators`; `ResolveTeamViewAsync` admits the same, or a `TeamsPolicies.Read` holder (Board) with every form hidden; `ResolveEarlyEntryManagementAsync` checks `TeamOperationRequirement.ManageEarlyEntry`. `tree` and the other route words in `TeamLifecycle.ReservedTeamSlugs` can never be team slugs.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone (including anonymous) | Browse the directory and the tree, view public team pages |
| Any active human | View team pages, join or request to join, leave, withdraw a pending request, view own teams and the roster |
| Coordinator | Manage members, join requests, roles, the team page and (when `EarlyEntryEnabled`) early entry for their department and its sub-teams; send as their team |
| Sub-team Manager | The same, for their sub-team only |
| EarlyEntryAdmin | Early entry on any team with `EarlyEntryEnabled`, and nothing else in Teams |
| TeamsAdmin | Every coordinator capability on every team; create teams, edit team settings, toggle the management role |
| Board | Read the summary, team settings, every team's Members page and the Board roster; change nothing |
| Admin | Every TeamsAdmin capability; deactivate and permanently delete teams; set the sensitive flag |

## Invariants

- A team has at most one role flagged `IsManagement`. Only TeamsAdmin or Admin set or clear the flag; the edit controller authorizes that field through `TeamsPolicies.Manage` and omits it for other editors. The internal update command accepts a nullable management value: null preserves the current stored flag, so no caller-supplied privilege boolean reaches the service. Toggling management on refuses a role that has assignees.
- Editing a role into or out of management promotes or demotes its assignees' `TeamMemberRole` in the same save, audits the definition change, then reconciles each affected human's Coordinators system-team membership.
- A role's slot count cannot shrink past an occupied slot; empty trailing slots may go. Undefined slot priorities and negative estimated hours are refused before anything is written.
- Role definitions may exist on any team, system teams included; assigning a role on a system team never adds a non-member.
- Members of a sub-team count as members of its department for the roster, legal requirements and Google access.
- System-team membership is written only by the system-team sync and its bulk-apply members; manual add, remove, join and leave on a system team are refused.
- A human joins an approval-required team only through an approved request; an open team adds them at once. A coordinator may add someone directly, which approves any pending request they hold. One pending request per human per team; concurrent submissions resolve to one Pending row.
- Inactive teams accept no joins or new requests and offer no Join action; their pages stay readable under the usual visibility rules.
- Removing a member removes all their role assignments on that team.
- Every membership add or remove made one at a time is audited (`TeamsAuditActions`) and its Google outbox event commits in the same transaction as the membership. Deactivating a team closes its live memberships, audits each closure against the acting admin, and reconciles its former coordinators' Coordinators system-team membership.
- Hidden teams are invisible to non-admins everywhere Teams lists or links a team: directory, tree, detail, join, My Teams, roster, parent and child links, pending-request links, and other people's profile cards. A human still sees their own hidden memberships on their own profile.
- The directory lists active departments and the sub-teams with `IsPromotedToDirectory`; every sub-team stays reachable from its department's page. Anonymous visitors see public-page departments and promoted sub-teams only, coordinators only if `ShowCoordinatorsOnPublicPage`, and never emails.
- Only departments can have a public page. Calls to action accept relative URLs and absolute `http`, `https` and `mailto` links; anything else is refused on save and left out of the rendered page. Page Markdown renders through the shared sanitizer.
- `IsSensitive` is written only by a global Admin, checked in `TeamLifecycle.UpdateTeamAsync` on a change to the flag; non-Admin editors never see the checkbox.
- A private role (`IsPublic = false`) shows only to Admin, Board, TeamsAdmin and the team's direct or inherited coordinators; coordinating one team never reveals another team's private roles.
- Slugs are unique across `Slug` and `CustomSlug` and both resolve. A rename regenerates the slug unless the new one is reserved or taken, in which case the old URL stays.
- Early-entry grants can be added, edited or revoked only while their team has `EarlyEntryEnabled`; the service refuses mutations for disabled or missing teams before writes. Turning the flag off, or deactivating the team, keeps the grants but drops them from the roster. Every grant mutation is audited (`EarlyEntryGranted`, `EarlyEntryUpdated`, `EarlyEntryRevoked`); removing an absent grant is a no-op.
- Early-entry add/edit requires an explicit event: empty, unknown or Deleted events are refused before writes; Inactive events remain valid for planning. Add defaults to the active event, while edit uses the stored event. Legacy grants stay visible with a review warning until an operator selects an event and saves; the nullable-column migration performs no backfill. Add/edit audit text names the event, and the `TeamEarlyEntry` GDPR slice includes `EventSettingsId`; erasure and merge cover all events. Reactivated teams contribute kept grants only when their selected event is active.
- Editing a prefix that matches a previously unlinked Google group redisplays an unchecked reactivation confirmation. Confirmation is tied to that archived resource ID; a changed target requires fresh confirmation. Confirming restores the resource through Manage; leaving it unchecked keeps the link pending. GoogleIntegration reuses the archived row and retires only its selected previous group link in one save, preserving other manually linked groups.
- Teams owns its access rule for send as team (`TeamMessageOptionsProvider`): an active team the viewer coordinates, whose Google Group is linked, active, synced at least once and error-free. The recipient must accept facilitated messages and cannot be the sender. Each message is audited `FacilitatedMessageSent`; the audit names the team and never its address.
- Expected refusals (missing record, no permission, validation) return a result before anything is written or audited, and log at Warning without a stack. Unexpected failures propagate.
- Mutations of the cached team graph invalidate the whole cache, even when the inner call throws after writing. Pending join-request reads bypass the cache.
- Resource keys use the `Teams_` prefix; shared vocabulary stays in `SharedResource`.

## Negative Access Rules

- Regular humans **cannot** manage any team's members, roles or settings.
- Coordinators and sub-team managers **cannot** create or delete teams or edit team settings (name, approval mode, parent, Google prefix, flags).
- Sub-team managers **cannot** manage Google resources, the parent department or sibling sub-teams.
- Board **cannot** change anything in Teams; the per-team management checks deny Board.
- TeamsAdmin **cannot** delete teams or set the sensitive flag.
- EarlyEntryAdmin **cannot** do anything on a team beyond early entry.
- Nobody **can** add or remove members of a system team by hand.
- Nobody **can** send as a team they do not coordinate, or as a team whose group is unlinked, unsynced or failing, not even Admin or TeamsAdmin. Nobody **can** send to an opted-out recipient or to themselves.

## Triggers

- Rejecting a join request requires a nonblank reason in `TeamJoining`, including direct service calls. A missing, empty or whitespace reason returns the existing localized refusal key before reading or changing the request; no review metadata, rejection audit or notification is emitted. The controller only formats the result. Approval notes remain optional.
- When a join request is approved, or a coordinator adds a member, the membership is created and the human gets a welcome email and an inbox notice. Coordinators get a notice for every new request and direct join. Notices and emails go in the recipient's saved language, falling back to English, and finish after the membership commits; a notice failure never undoes the membership.
- When a member is added, the Google outbox event grants their Drive and Group access, including the parent's resources for a sub-team add. Removals wait for GoogleIntegration's daily reconciliation.
- When a department's management-role assignment changes, or a team moves into or out of being an active department, the affected humans' Coordinators system-team membership is recalculated. Sub-team manager changes do not touch the Coordinators system team.
- The system-team sync runs hourly (Hangfire `teams-system-sync`) and on demand from the sections whose facts it reads. It recomputes Volunteers (approved, unsuspended, consents signed, not rejected by the Consent Coordinator), Coordinators, Board, Asociados and Colaboradors (approved tiers with active terms), and Barrio Leads (active camp leads), reconciles `TeamMember.Role` against management assignments, audits every committed change before Google provisioning, and ends with a Google Group reconciliation pass when Workspace credentials are configured. It sends no "added to team" email for hidden teams.
- When an account merge is accepted, Teams ends the source's non-system memberships, adds the survivor where needed, moves role assignments, join requests and early-entry grants to the survivor (a source Pending request colliding with a survivor Pending request for the same team is dropped), and reconciles both humans' Coordinators membership.
- On erasure, Teams ends live memberships, hard-deletes the human's join requests and early-entry grants, and keeps ended membership rows. The GDPR export carries the `TeamMemberships`, `TeamJoinRequests` (own requests, notes and status history) and `TeamEarlyEntry` slices, following the merge chain.

## Cross-Section Dependencies

**Outbound** — every `ProjectReference` of `Humans.Teams.csproj` beyond Base and its own leaf:

- **AuditLog:** `IAuditLogService` for every audited action in `TeamsAuditActions`, and `<vc:audit-log>` on the Members page.
- **Notifications.Contracts:** `INotificationEmitter` for the sources in `TeamsNotificationSources`.
- **Users.Contracts:** `IUserService` for member display data, account resolution and the sync's person reads.
- **Auth:** `IRoleAssignmentService` for role holders, `IAdminAuthorizationService` for actor checks.
- **GoogleIntegration:** `IGoogleSyncOutboxService` and `IGoogleSyncWrite` for membership grants, `ITeamResourceService` for a team's linked resources, `IGoogleGroupSync` for the sync's reconciliation pass; Teams implements its `IGoogleGroupMembershipSource` and `IGoogleDriveAccessSource`.
- **Shifts.Contracts:** `IShiftService` for the team page's shifts card and pending-shift counts.
- **Email.Contracts:** `IEmailService`, `IEmailMessageFactory` and `ICommunicationPreferenceService` for send as team and the welcome email; Teams owns its templates (`TeamsEmails`, previewed through `TeamsEmailPreviews`).
- **EarlyEntry:** Teams implements `IEarlyEntryProvider` for the roster and reads `EarlyEntryRoles`.
- **Tickets.Contracts:** `ITicketService.FindCurrentEventAttendeeByBarcodeAsync` for the early-entry ticket lookup.
- **Camps.Contracts:** `ICampLeadDirectory` for Barrio Leads.
- **Governance.Contracts:** `IGovernanceService` for the tier teams.
- **Consent:** `IConsentService` for the system teams' required-consents check and the consent-review gate.
- **Settings:** `ISettingsService` for the active event on the team page, the early-entry event picker, event validation and the active-event roster filter. Grants store an event ID without a cross-section EF relationship.
- **Issues:** Teams implements `IIssueQueueOwner` for its `Teams` issue queue.
- **Base (`Humans.Base.Gdpr`):** `IUserDataContributor` (export and erasure).

**Inbound** — sections that reference Teams:

- **Search:** renders team hits with `<vc:teams-search-result>`.
- **Budget, Expenses:** coordinator scoping (`GetEffectiveBudgetCoordinatorTeamIdsAsync`, `IsUserCoordinatorOfTeamAsync`); Budget's dev seeder uses `ITeamSeeding`.
- **Onboarding, Governance, Auth, Camps, GoogleIntegration, Development:** call `ISystemTeamSync` after the facts they own change.
- **Users:** account deletion calls `RevokeAllMembershipsAsync`; account merge reaches `IUserMerge.ReassignAsync`; profiles read team memberships.
- **Shifts:** `ShiftAdminController` derives from `HumansTeamControllerBase`; shift authorization reads `GetUserCoordinatedTeamIdsAsync`.
- **Agent, Calendar, Campaigns, CityPlanning, Consent, Debug, Guide, Notifications, Store, Surveys, Tickets, the Shell:** read `ITeamService` / `TeamInfo`.

## Architecture

**Owning services:** `TeamService` behind `CachingTeamService`, and `TeamPageService`.
**Owned tables:** `teams`, `team_members`, `team_join_requests`, `team_join_request_state_history`, `team_role_definitions`, `team_role_assignments`, `team_early_entry_grants`.
**Status:** (A) Migrated.

### Public contract

`Humans.Teams.Contracts` carries `ITeamService` (cross-section reads, `TeamInfo` / `TeamSearchHit` / `UserTeamMembershipInfo` projections only), `ITeamWrite` (the writes other sections make: Google Group prefix, member role, Google resync, permanent delete, the account-deletion hook and the system-team bulk apply), `ISystemTeamSync`, and `ITeamSeeding` for the dev fixture seeders. The internal `ITeamManagementService` carries the rest of the section's surface. `HumansTeamControllerBase` is public in the section project, not the leaf, because it derives from Base's `HumansControllerBase`.

`GetUserTeamMembershipsAsync` returns active memberships, including those on inactive teams. Each `UserTeamMembershipInfo` carries the owning team's active/hidden flags and system-team type in both direct and cached reads. Its `IsProfileMembership` predicate selects active teams other than Volunteers for profile and Agent summaries; hidden memberships remain in that projection for the caller's existing rendering rules.


### Migrated (A)

- **Repository:** `ITeamRepository` / `TeamRepository` owns every Teams table; only `TeamService` and its internal workflow owners call it.
- **Inner service:** `TeamService` is a facade over internal owners, one per shape: `TeamLifecycle`, `TeamJoining`, `TeamMembershipAdministration`, `TeamRoleDefinitions`, `TeamPageContent`, `TeamEarlyEntry`, `TeamDataExport`, `TeamGoogleGroupMemberships`, sharing `TeamSnapshotReader` for team, permission and account reads and `TeamMembershipOutbox` for the atomic membership-plus-outbox write.
- **Decorator decision:** caching decorator. `CachingTeamService` is a singleton `TrackedCache` over `ITeamManagementService` holding the Teams-owned graph and a user-to-team index; member display data resolves live through `IUserService` on each read. Every write to the graph clears the cache and the next read re-warms it whole; early-entry grants are read live. Team search is served only from the cache.
- **Page composer:** `TeamPageService` builds the directory, team detail, My Teams and roster pages from the cached graph and role checks; it owns no tables and calls no repository.
- **Display stitching:** `IUserService.GetUserInfosAsync`.
- **Architecture enforcement:** `tests/Humans.Teams.Tests/Architecture/TeamsArchitectureTests.cs` (one singleton behind every face), `TeamPageArchitectureTests.cs`.
- **System-team sync:** `SystemTeamSyncJob` (internal, registered and scheduled in `Section.cs`) behind `ISystemTeamSync`. Hangfire keys the recurring job on its id.
- **Cross-domain navs** to `User` are `[Obsolete]` and never populated; display data comes from `IUserService`.

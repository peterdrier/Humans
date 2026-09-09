<!-- freshness:triggers
  src/Sections/Humans.Teams/**
  src/Sections/Humans.Teams.Contracts/**
  src/Humans.Base/Constants/SystemTeamIds.cs
  src/Sections/Humans.GoogleIntegration.Contracts/GoogleResource.cs
  src/Sections/Humans.GoogleIntegration/Data/Configurations/GoogleResourceConfiguration.cs
-->
<!-- freshness:flag-on-change
  Department/sub-team hierarchy rules, system-team automation, coordinator-vs-manager scope, hidden/promoted team visibility, and SystemTeamIds constants — review when Teams services/entities/controllers/auth handlers change.
-->

# Teams — Section Invariants

Departments and sub-teams, join requests, role definitions, team pages, and linked Google resources.

## Concepts

- A **Department** is a team with no parent.
- A **Sub-Team** is a team within a department. Only one level of nesting is allowed.
- **System teams** (Volunteers, Coordinators, Board, Asociados, Colaboradors, Barrio Leads) are managed automatically — members cannot be manually added or removed.
- A **Coordinator** is a team member assigned to the management role on a department. Coordinators have full authority over the department and all its sub-teams, including Google resource management. They are added to the Coordinators system team.
- A **Sub-team Manager** is a team member assigned to the management role on a sub-team. Managers have scoped authority over their sub-team only: member management, join requests, roles, shifts, and team page editing. They **cannot** manage Google resources, the parent department, or sibling sub-teams. They are **not** added to the Coordinators system team.
- A **Team Page** is a Markdown-based public or member-facing page for a department, with optional calls to action.

## Data Model

### Team

**Table:** `teams`

Aggregate-local navs kept: `Team.ParentTeam`, `Team.ChildTeams`, `Team.Members`, `Team.EarlyEntryGrants`, `Team.JoinRequests`, `Team.RoleDefinitions` — all intra-section, and the only navs on the entity. Public/member team page content lives directly on the row as `PageContent` / `PageContentUpdatedAt` / `PageContentUpdatedByUserId` / `CallsToAction` (JSONB) / `ShowCoordinatorsOnPublicPage` columns (no separate `team_pages` table or entity).

### TeamMember

**Table:** `team_members`

Cross-domain nav `TeamMember.User → TeamMember.UserId` (target: strip nav). Aggregate-local: `TeamMember.Team`.

### TeamJoinRequest

**Table:** `team_join_requests`

Cross-domain navs: `TeamJoinRequest.User`, `TeamJoinRequest.ReviewedByUser` (both target: FK-only). Aggregate-local: `TeamJoinRequest.StateHistory`.

### TeamJoinRequestStateHistory

Append-only per design-rules §12.

**Table:** `team_join_request_state_history`

Cross-domain nav `TeamJoinRequestStateHistory.ChangedByUser → ChangedByUserId` (target: FK-only).

### TeamRoleDefinition

**Table:** `team_role_definitions`

Named role slots on a team (name, description, slot count, priorities, sort order, `IsManagement` flag, `IsPublic` flag, `Period`, nullable `EstimatedHours`). `EstimatedHours` is the estimated workload in whole hours/year that holding the role represents — informational only (gates nothing); surfaced on `TeamRoleDefinitionSnapshot`/`TeamInfo` so workload aggregations can quantify role hours alongside shift hours. Aggregate-local: `TeamRoleDefinition.Team`. Per-team unique index `IX_team_role_definitions_team_name_unique` on `(TeamId, Name)`.

### TeamRoleAssignment

**Table:** `team_role_assignments`

Assigns a team member to a specific slot in a role definition. Aggregate-local: `TeamRoleAssignment.TeamRoleDefinition`, `TeamRoleAssignment.TeamMember`. Cross-domain nav `TeamRoleAssignment.AssignedByUser → AssignedByUserId` (target: FK-only).

### GoogleResource

**Table:** `google_resources`

Team Resources sub-aggregate. Aggregate-local back-ref `GoogleResource.Team` is still declared but never `Include`-d by the repository. Per-team filtered unique index on `(TeamId, GoogleId)` where `IsActive = true`. Drive resources (`DriveFolder`, `DriveFile`, `SharedDrive`) carry a `DrivePermissionLevel` (Viewer / Commenter / Contributor / ContentManager / Manager) — `Group` resources keep `None`. `DriveFolder` resources may also set `RestrictInheritedAccess = true` to enforce `inheritedPermissionsDisabled` on the underlying folder; the daily reconciliation job corrects drift.

### RolePeriod

Period tag on a `TeamRoleDefinition` indicating when the role is active. Used for roster page filtering.

| Value | Int | Description |
|-------|-----|-------------|
| YearRound | 0 | Active year-round |
| Build | 1 | Active during build period |
| Event | 2 | Active during event period |
| Strike | 3 | Active during strike period |

Stored as string via `HasConversion<string>()`.

### TeamEarlyEntryGrant

**Table:** `team_early_entry_grants`

One early-entry (EE) grant: a human may enter on a `LocalDate` for a named project on a team that has the `Team.EarlyEntryEnabled` flag set. Aggregate-local nav `TeamEarlyEntryGrant.Team` (loaded for the cross-section projection). Teams implements `IEarlyEntryProvider`, projecting each grant to the EE roster as `"{TeamName}: {ProjectName}"`. EE is a generic per-team capability (multiple teams may enable it). Managed per-team at `Teams/{slug}/EarlyEntry` by the team's coordinators (de facto) or the cross-team `EETeamAdmin` role (and TeamsAdmin/Board/Admin), gated by `TeamOperationRequirement.ManageEarlyEntry`. GDPR export + right-to-erasure + user-merge fold are covered.

### SystemTeamType

| Value | Int | Description |
|-------|-----|-------------|
| None | 0 | User-created team |
| Volunteers | 1 | Approved, non-suspended profiles with all required consents signed |
| Coordinators | 2 | All department-level team coordinators |
| Board | 3 | Board members |
| Asociados | 4 | Approved Asociados with active terms |
| Colaboradors | 5 | Approved Colaboradors with active terms |
| BarrioLeads | 6 | Active camp leads across all camps |

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

This section's controllers. `TeamController` (`[Route("Teams")]`) handles both anonymous/member-facing and TeamsAdmin-gated actions. `TeamAdminController` (`[Route("Teams/{slug}")]`) handles per-team management under coordinator/admin authorization via `HumansTeamControllerBase.ResolveTeamManagementAsync` (which calls `TeamAuthorizationHandler` + `TeamOperationRequirement.ManageCoordinators`). Routes that go through `CanManageResourcesAsync` (coordinator-of-department check) are marked separately.

| Route | Method | Controller | Auth |
|-------|--------|------------|------|
| `GET /Teams` | `Index` | `TeamController` | `[AllowAnonymous]` — anonymous sees public teams only; authenticated sees full directory |
| `GET /Teams/{slug}` | `Details` | `TeamController` | `[AllowAnonymous]` — anonymous sees public pages; hidden teams return 404 for non-admin |
| `GET /Teams/Birthdays` | `Birthdays` | `TeamController` | `[Authorize]` (any authenticated human with profile) |
| `GET /Teams/Roster` | `Roster` | `TeamController` | `[Authorize]` |
| `GET /Teams/Map` | `Map` | `TeamController` | `[Authorize]` |
| `GET /Teams/My` | `MyTeams` | `TeamController` | `[Authorize]` |
| `GET /Teams/{slug}/Join` | `Join` (GET) | `TeamController` | `[Authorize]` — hidden teams return 404 for non-admin |
| `POST /Teams/{slug}/Join` | `Join` (POST) | `TeamController` | `[Authorize]` — hidden teams return 404 for non-admin |
| `POST /Teams/{slug}/Leave` | `Leave` | `TeamController` | `[Authorize]` |
| `POST /Teams/Requests/{id}/Withdraw` | `WithdrawRequest` | `TeamController` | `[Authorize]` |
| `GET /Teams/Summary` | `Summary` | `TeamController` | `[Authorize(Policy = TeamsAdminBoardOrAdmin)]` |
| `GET /Teams/Create` | `CreateTeam` (GET) | `TeamController` | `[Authorize(Policy = TeamsAdminBoardOrAdmin)]` |
| `POST /Teams/Create` | `CreateTeam` (POST) | `TeamController` | `[Authorize(Policy = TeamsAdminBoardOrAdmin)]` |
| `GET /Teams/{id:guid}/Edit` | `EditTeam` (GET) | `TeamController` | `[Authorize(Policy = TeamsAdminBoardOrAdmin)]` |
| `POST /Teams/{id:guid}/Edit` | `EditTeam` (POST) | `TeamController` | `[Authorize(Policy = TeamsAdminBoardOrAdmin)]` |
| `POST /Teams/{id:guid}/Delete` | `DeleteTeam` | `TeamController` | `[Authorize(Policy = BoardOrAdmin)]` |
| `GET /Teams/{teamId:guid}/GoogleResources` | `GetTeamGoogleResources` | `TeamController` | `[Authorize(Policy = TeamsAdminBoardOrAdmin)]` — JSON API for resource picker |
| `GET /Teams/{slug}/Members` | `Members` | `TeamAdminController` | `ResolveTeamManagementAsync` (coordinator or TeamsAdmin/Admin) |
| `POST /Teams/{slug}/Members/Add` | `AddMember` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Members/{userId}/Remove` | `RemoveMember` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Members/{userId}/ProvisionEmail` | `ProvisionEmail` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `GET /Teams/{slug}/Members/Search` | `SearchUsers` | `TeamAdminController` | `ResolveTeamManagementAsync` — AJAX name search |
| `GET /Teams/{slug}/Roster` | `Roster` | `TeamAdminController` | `[Authorize(Policy = BoardOrAdmin)]` + `ResolveTeamManagementAsync` — burner name + legal name; policy narrows the coordinator-inclusive resolver to Board/Admin |
| `POST /Teams/{slug}/Requests/{requestId}/Approve` | `ApproveRequest` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Requests/{requestId}/Reject` | `RejectRequest` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `GET /Teams/{slug}/Resources` | `Resources` | `TeamAdminController` | `[Authorize]` + `CanManageResourcesAsync` (Coordinator of dept or TeamsAdmin/Admin) |
| `POST /Teams/{slug}/Resources/LinkDrive` | `LinkDriveResource` | `TeamAdminController` | `[Authorize]` + `CanManageResourcesAsync` |
| `POST /Teams/{slug}/Resources/LinkGroup` | `LinkGroup` | `TeamAdminController` | `[Authorize]` + `CanManageResourcesAsync` |
| `POST /Teams/{slug}/Resources/{resourceId}/PermissionLevel` | `UpdatePermissionLevel` | `TeamAdminController` | `[Authorize]` + `CanManageResourcesAsync` |
| `POST /Teams/{slug}/Resources/{resourceId}/RestrictInheritedAccess` | `ToggleRestrictInheritedAccess` | `TeamAdminController` | `[Authorize]` + `CanManageResourcesAsync` |
| `POST /Teams/{slug}/Resources/{resourceId}/Unlink` | `UnlinkResource` | `TeamAdminController` | `[Authorize]` + `CanManageResourcesAsync` |
| `POST /Teams/{slug}/Resources/{resourceId}/Sync` | `SyncResource` | `TeamAdminController` | `[Authorize]` + `CanManageResourcesAsync` |
| `GET /Teams/{slug}/Roles` | `Roles` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Roles/Create` | `CreateRole` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Roles/{roleId}/Edit` | `EditRole` | `TeamAdminController` | `ResolveTeamManagementAsync` (IsManagement field additionally gated to TeamsAdmin/Admin) |
| `POST /Teams/{slug}/Roles/{roleId}/Delete` | `DeleteRole` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Roles/{roleId}/ToggleManagement` | `ToggleManagement` | `TeamAdminController` | `ResolveTeamManagementAsync` + explicit `IsTeamsAdmin || IsAdmin` |
| `POST /Teams/{slug}/Roles/{roleId}/Assign` | `AssignRole` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/Roles/{roleId}/Unassign/{memberId}` | `UnassignRole` | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `GET /Teams/{slug}/EditPage` | `EditPage` (GET) | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `POST /Teams/{slug}/EditPage` | `EditPage` (POST) | `TeamAdminController` | `ResolveTeamManagementAsync` |
| `GET /Teams/{slug}/Roles/SearchMembers` | `SearchMembersForRole` | `TeamAdminController` | `ResolveTeamManagementAsync` — AJAX member search |
| `GET /Teams/{slug}/EarlyEntry` | `EarlyEntry` | `TeamAdminController` | `ResolveEarlyEntryManagementAsync` (`ManageEarlyEntry`) — only renders when `EarlyEntryEnabled` |
| `POST /Teams/{slug}/EarlyEntry/Add` | `AddEarlyEntry` | `TeamAdminController` | `ResolveEarlyEntryManagementAsync` |
| `POST /Teams/{slug}/EarlyEntry/Edit` | `EditEarlyEntry` | `TeamAdminController` | `ResolveEarlyEntryManagementAsync` |
| `POST /Teams/{slug}/EarlyEntry/Remove` | `RemoveEarlyEntry` | `TeamAdminController` | `ResolveEarlyEntryManagementAsync` |
| `GET /Teams/{slug}/EarlyEntry/LookupTicket` | `LookupTicket` | `TeamAdminController` | `ResolveEarlyEntryManagementAsync` — AJAX ticket-barcode lookup; returns 0-or-1 `HumanLookupSearchResult` |

`ResolveTeamManagementAsync` authorizes via `TeamAuthorizationHandler` + `TeamOperationRequirement.ManageCoordinators`; `ResolveEarlyEntryManagementAsync` authorizes the same handler with `TeamOperationRequirement.ManageEarlyEntry`. `CanManageResourcesAsync` checks coordinator-of-department specifically (sub-team managers cannot manage Google resources).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone (including anonymous) | Browse the team directory and view public team pages |
| Any active human | View team detail pages, request to join a team, leave a team, withdraw a pending request, view own memberships, browse the birthday calendar, search humans, view the roster and map |
| Coordinator | Manage members, approve/reject join requests, manage roles, edit the team page, manage Google resources, and (when `EarlyEntryEnabled`) grant/edit/revoke Early Entry for their department (and its sub-teams) |
| EETeamAdmin | Cross-team role: grant/edit/revoke Early Entry on **any** team that has `EarlyEntryEnabled` (the `ManageEarlyEntry` operation only — no other team-management authority) |
| Sub-team Manager | Manage members, approve/reject join requests, manage roles, manage shifts, and edit the team page for their sub-team only. Cannot manage Google resources, the parent department, or sibling sub-teams |
| TeamsAdmin | All coordinator capabilities on all teams. Create teams, edit team settings (name, slug, approval mode, parent, Google group prefix, budget flag, hidden flag, directory promotion, Early Entry enabled flag), toggle the management role, and link/unlink Google resources on all teams. **Cannot** change the sensitive flag via Edit Team (Admin-only — a non-Admin's save leaves `IsSensitive` unchanged) |
| Board | All TeamsAdmin capabilities. Additionally can delete (deactivate) teams |
| Admin | All Board capabilities. Additionally can change the sensitive flag (`IsSensitive`) via Edit Team, execute Google sync actions, trigger system team sync, and view sync previews |

## Invariants

- A department can have **at most one** role flagged as management (coordinator). Enforced in both the toggle and edit paths.
- A sub-team can have **at most one** role flagged as management (manager).
- Toggling or changing the `IsManagement` flag on a role definition is restricted to **TeamsAdmin / Admin** (`ToggleManagement` action and `EditRole` IsManagement field). Coordinators / sub-team managers can still create, rename, and delete other (non-management) role definitions on their team — they just cannot promote/demote the management role itself.
- A `TeamRoleDefinition.IsPublic = false` role is hidden from volunteer-facing views (team detail, roster) but remains visible to coordinators and admins.
- Members of sub-teams are also considered members of the department. They appear in the department's member roster and inherit the department's legal requirements and Google resource access.
- A human can be a member of multiple teams simultaneously.
- System team membership is managed exclusively by an automated sync job. Manual add/remove is blocked for system teams.
- Role definitions can be created on any team, including system teams (e.g. governance roles on the Board team). However, `AssignToRoleAsync` blocks assigning a **non-member** to a role on a system team — only existing sync-managed members can be assigned, so role assignment cannot become a backdoor for the manual-membership block above.
- Joining a team that requires approval creates a join request (Pending). The request must be approved by a coordinator or TeamsAdmin before membership is granted. Teams that do not require approval add the human immediately.
- Coordinators can approve/reject join requests for their own department and any sub-teams within that department (enforced by `IsUserCoordinatorOfTeamAsync`).
- All member additions and removals are audit-logged via `AuditLogEntry`.
- Google resource access changes triggered by membership changes (Drive folder permissions, Group memberships) are logged in the audit trail.
- Removing a member from a team also removes all their role assignments on that team.
- Each team has a unique slug used for URL routing. A custom slug can override the auto-generated one.
- A Google Group prefix, if set, provisions a `@nobodies.team` group for the team.
- Only departments (not sub-teams or system teams) can have public team pages.
- A **hidden team** (`IsHidden = true`) is invisible to non-admin users: it does not appear on profile cards, team listings, public pages, birthday team names, or the "My Teams" page. Only Admin, Board, and TeamsAdmin can see and manage hidden teams. Campaigns can still target hidden teams for code distribution. The system-team sync skips the "added to team" email for hidden teams.
- A **sensitive team** (`IsSensitive = true`) is an admin-only flag (not publicly visible). **Only a global Admin can set or clear `IsSensitive`** via Edit Team — the checkbox is suppressed for non-Admin editors, so a non-Admin's save passes `null` (leave-unchanged) and never alters it (ref #824). Adding or approving a member surfaces a deterrent confirmation modal in the Members admin view that shows the audit record that will be created.
- The Teams directory (`/Teams`) shows only **directory-visible** teams: top-level teams (departments) always appear; sub-teams only appear if `IsPromotedToDirectory` is true. Sub-teams are always accessible from their parent team's detail page regardless of this flag.
- `team_join_request_state_history` is append-only per §12.
- Resource-based authorization per design-rules §11: `TeamAuthorizationHandler` + `TeamOperationRequirement`.
- Early Entry is gated by the per-team `EarlyEntryEnabled` flag: only enabled teams contribute EE grants to the cross-section roster, expose the EE management page, and accept new grants (`AddEarlyEntryGrantAsync` rejects a disabled team). Multiple teams may enable it.
- Toggling `EarlyEntryEnabled` off never deletes existing grants — they simply stop appearing on the roster while disabled.
- `RemoveEarlyEntryGrantAsync` is idempotent (removing an absent grant is a no-op).
- `ManageEarlyEntry` authority: Admin / TeamsAdmin / Board on any team; `EETeamAdmin` on any team (this operation only); a team coordinator (or parent-department coordinator) on their own team.
- `EETeamAdmin` is in `RoleNames.All` and `RoleNames.BoardManageableRoles` (board-grantable, surfaces in the role-assignment UI) but deliberately **not** in the `AnyAdminRole` policy — its only surface is the per-team `Teams/{slug}/EarlyEntry` page reached from Team Details, not the admin shell. The omission is the design, not a gap.

## Negative Access Rules

- Regular humans **cannot** manage other teams' members, roles, or settings.
- Coordinators **cannot** create, delete, or edit team admin settings (name, approval mode, parent, Google prefix). They can only edit the team page and manage members/roles for their own department.
- Sub-team managers **cannot** manage Google resources, the parent department, sibling sub-teams, or team admin settings.
- TeamsAdmin **cannot** delete teams or execute sync actions.
- Nobody can manually add or remove members from system teams.

## Triggers

- When a join request is approved, a team membership record is created and the human is notified.
- When a member is removed from a team, all their role assignments for that team are also removed.
- When a member is added to a team, Google resource sync (Drive folder permissions, Group memberships) runs inline against the Google APIs (and rolls up to the parent department's resources for sub-team adds). Per-user removals are deferred to the daily reconciliation job rather than running inline. Failed sync calls fall through to the Google sync outbox, processed by `google-sync-outbox-process`.
- When a department coordinator role assignment changes, the Coordinators system team membership is recalculated for the affected human. Sub-team manager changes do not affect the Coordinators system team.
- The system team sync job runs hourly (Hangfire recurring job `teams-system-sync`), reconciling system team membership for Volunteers (consent compliance), Coordinators (department-level management role assignments), Board (active Board role assignments), Asociados/Colaboradors (approved tier applications with active terms), and Barrio Leads (active camp lead assignments). The job also reconciles `TeamMember.Role` against `IsManagement` role assignments and backfills `User.GoogleEmail` for verified `@nobodies.team` accounts.
- When an account merge accepts, `ITeamService.ReassignToUserAsync` re-FKs `TeamMember`, `TeamJoinRequest`, and `TeamEarlyEntryGrant` rows from source to target, collapsing duplicates so the same target doesn't end up with two memberships of the same team. Called only by `IAccountMergeService.AcceptAsync` (Profiles section).
- Each Early Entry mutation writes an `AuditLogEntry` (`EarlyEntryGranted` on add, `EarlyEntryUpdated` on edit, `EarlyEntryRevoked` on remove) against the `TeamEarlyEntryGrant` and evicts the affected user's EE cache.
- Right-to-erasure (`IUserDataContributor.EraseForUserAsync`): ends live memberships, then hard-deletes the user's join requests and EE grants; the GDPR export contributes a `TeamEarlyEntry` data slice.

## Cross-Section Dependencies

**Outbound** — every `ProjectReference` of `Humans.Teams.csproj` beyond Base and its own leaf, and what it is for:

- **Users.Contracts** (also the leaf's one reference, for `GoogleEmailStatus`): `IUserServiceRead.GetUserInfosAsync` batch-resolves `UserInfo` for nav-stripped reads, projected straight into DTOs; `IUserService` feeds the reconciler.
- **Auth.Contracts:** `IRoleAssignmentService` for role-holder reads and `IAdminAuthorizationService` for the actor checks; `IRoleAssignmentClaimsCacheInvalidator` after Coordinators reconciliation.
- **GoogleIntegration.Contracts:** each team can have linked Google resources (Drive folders, Groups). Membership changes call `IGoogleSyncService.AddUserToTeamResourcesAsync` / `RemoveUserFromTeamResourcesAsync` inline (per-user removals are no-ops, handled by the daily reconciliation job); failed Google API calls land in the sync outbox via `IGoogleSyncOutboxService`; `ITeamResourceService` for the Resources page and the Google-group reconciliation.
- **Shifts.Contracts:** `IShiftManagementServiceRead` for the team page's shifts card; `IShiftAuthorizationInvalidator` after coordinator changes. Rotas belong to a department or sub-team, and coordinator/manager status is what scopes their shift management.
- **Notifications.Contracts:** `INotificationEmitter` on join-request events; `INotificationMeterCacheInvalidator`.
- **AuditLog.Contracts + AuditLog:** `IAuditLogService` for every membership and EE mutation; the full section for `<vc:audit-log>` in `TeamAdmin/Members`.
- **Email.Contracts:** the reconciler's "added to team" mail.
- **Gdpr.Contracts:** `IUserDataContributor` (export + erasure).
- **EarlyEntry** (full section): `IEarlyEntryProvider` — `GetEarlyEntriesAsync` projects grants from `EarlyEntryEnabled` teams to the cross-section `EarlyEntryGrant` view (`"{TeamName}: {ProjectName}"`) via `TeamEarlyEntryProjection`; `IEarlyEntryInvalidator` on grant writes.
- **Camps.Contracts:** active camp lead assignments feed the Barrio Leads system team via `ICampLeadDirectory`.
- **Governance.Contracts:** `IMembershipCalculatorRead` decides Asociados / Colaboradors eligibility.
- **Tickets.Contracts:** the EE page's ticket-barcode lookup.

**Inbound** — sections that reference Teams and are not dependencies of it:

- **Search:** the global `/Search` page renders every team hit through this section's own public `<vc:teams-search-result team-id>`, which fetches its display fields — including the slug it links to — from `ITeamServiceRead.GetTeamAsync`. Search passes the team id and no display fields; `TeamSearchHit` is `(TeamId, Name, Score)`, carrying the section's own `Score`.
- **Budget, Expenses:** coordinator status (`GetEffectiveBudgetCoordinatorTeamIdsAsync`, `IsUserCoordinatorOfTeamAsync`) scopes budget and expense editing; Budget's dev seeder uses `ITeamSeeding`.
- **Onboarding, Governance, Auth, Camps, GoogleIntegration, Development:** call `ISystemTeamSync` after the facts they own change (volunteer activation, tier approval or expiry, role grants, camp leads, the sync admin page, the persona seeder).
- **Users:** `AccountDeletionService` calls `RevokeAllMembershipsAsync` / `RemoveMemberFromAllTeamsCache`; account merge reaches `IUserMerge.ReassignAsync`; the profile card and popovers read `GetTeamsAsync` / `GetUserTeamMembershipsAsync`.
- **Shifts:** `ShiftAdminController` derives from `HumansTeamControllerBase`; shift authorization reads `GetUserCoordinatedTeamIdsAsync`.
- **Agent, Calendar, Campaigns, CityPlanning, Consent, Debug, Feedback, Guide, Notifications, Store, Surveys, Tickets, the Shell:** read-side consumers of `ITeamServiceRead` / `TeamInfo`; Consent's legal-document sync, Calendar's controller, Governance's membership query and Budget's repository also hold `ITeamService`.

## Architecture

**Owning services:** `TeamService`, `TeamPageService` (Teams section); `TeamResourceService` (GoogleIntegration section — see note below)
**Owned tables:** `teams`, `team_members`, `team_join_requests`, `team_join_request_state_history`, `team_role_definitions`, `team_role_assignments`, `team_early_entry_grants` (Teams section); `google_resources` is a Team Resources sub-aggregate but is managed from the GoogleIntegration section
**Layout:** `src/Sections/Humans.Teams` plus the `Humans.Teams.Contracts` leaf. `TeamService`, `TeamPageService` and `CachingTeamService` are `internal sealed` in `Humans.Teams.Services`; the entities are `internal` in `Humans.Teams.Domain`; `ITeamRepository` / `TeamRepository` / `TeamsDbContext` / the EF configurations and the section's migration chain are in `Humans.Teams.Data`; the two controllers, their view models and their views are in `Humans.Teams.{Controllers,Models,Views}`; the `TeamsResource` set sits at the project root. `TeamResourceService` lives in `Humans.GoogleIntegration.Services` — its repository, EF impl, and connector clients all live in the `Humans.GoogleIntegration` section project (see `memory/architecture/team-resources-google-integration-section.md`).

- `TeamService` goes through `ITeamRepository` for owned-table access and routes every cross-section read through the public service interface (`IUserServiceRead`, `IRoleAssignmentService`, `IShiftManagementServiceRead`, `ITeamResourceService`).
- `TeamResourceService` (in `Humans.GoogleIntegration.Services`) uses `IGoogleResourceRepository` + the `ITeamResourceGoogleClient` connector. `IGoogleResourceRepository` lives in `Humans.GoogleIntegration.Data` alongside its EF impl `GoogleResourceRepository`.
- `TeamPageService` owns no tables — it is a read-only composer over `ITeamService`, `ITeamResourceService`, and sibling services. It has no repository dependency.
- **Read/write interface split.** `ITeamServiceRead` (methods: `GetTeamsAsync`, `GetTeamAsync`, `GetTeamBySlugAsync`, `SearchAsync`, `GetUserCoordinatedTeamIdsAsync`, `GetEffectiveBudgetCoordinatorTeamIdsAsync`, `IsUserCoordinatorOfTeamAsync`, `GetUserTeamMembershipsAsync`, `GetTeamsWithParentsAsync`) is the cross-section read surface — only `TeamInfo` / `TeamSearchHit` projections, no EF entities. `ITeamService : ITeamServiceRead, IApplicationService` on the `Humans.Teams.Contracts` leaf adds only the members some other section calls (the Google-group prefix, the account-deletion and cache-invalidation hooks, the bulk-apply protocol the reconciler and Development's persona seeder use), all in flat projections; a member with no caller outside the assembly does not belong on it. The internal `ITeamManagementService : ITeamService` in `Humans.Teams.Services` carries the rest — the directory, detail, admin list, roster, join-request, role-definition, role-assignment, coordinator-reconciliation, team-page and early-entry surface, plus the entity-returning `GetTeamEntityBySlugAsync` / `GetTeamByIdAsync` reads that never leave the assembly. Looking for a removed entity-returning read? `GetAllTeamsAsync` → `GetTeamsAsync`, `GetUserTeamsAsync` → `GetUserTeamMembershipsAsync`, `GetByIdsWithParentsAsync` → `GetTeamsWithParentsAsync`. See [`memory/architecture/section-read-write-split.md`](../../../../memory/architecture/section-read-write-split.md).
- **`ITeamSeeding`** is the fourth entry on the leaf: `CreateTeamAsync` / `UpdateTeamAsync` / `AddSeededMemberAsync` for the dev/demo fixture seeders in `Humans.Development` and `Humans.Budget`, which build multi-section fixtures and so cannot come into this section. Implemented explicitly by `TeamService` and `CachingTeamService` — the same-named members on those classes return the section's entities.
- **Decorator decision — caching decorator.** `CachingTeamService` is a Singleton transparent decorator for `ITeamService`. It owns the canonical `ConcurrentDictionary<Guid, TeamInfo> _byTeamId` read model. It inherits `TrackedCache<Guid, TeamInfo>` which implements `IHostedService`; registered via `services.AddHostedService(sp => sp.GetRequiredService<CachingTeamService>())`. Bulk invalidations call `Clear()` (flips warmed flag) and the next read re-warms via `EnsureWarmedAsync`. `TeamInfo` / `TeamMemberInfo` are the service read models; the EF `Team` entity remains legacy surface area until the service-entity-boundary cleanup removes it from read APIs. `TeamInfo` is the canonical read shape — it carries `ChildTeamIds`, page-content fields, and the CTA list so `GetTeamDetailAsync` projects entirely from cache (slug → cached `TeamInfo` → walk `ChildTeamIds` → stitch `RoleDefinitions`); `ITeamRepository.GetBySlugWithRelationsAsync` / `GetRoleDefinitionsAsync` are retained only for inner write-path flows (admin actions resolve a team by slug then mutate). Pending-request lookups (`GetUserPendingRequestAsync`, `GetPendingRequestsForTeamAsync`) still route through the inner service on the auth/management read path — real-time accuracy is required and a stale per-team pending count would mislead coordinators.
- **Cross-domain navs `[Obsolete]`-marked:** `TeamMember.User`, `TeamJoinRequest.User`, `TeamJoinRequest.ReviewedByUser`, `TeamRoleAssignment.AssignedByUser`, `TeamJoinRequestStateHistory.ChangedByUser`. Nothing populates them — display data is resolved via `IUserServiceRead.GetUserInfosAsync` / `GetUserInfoAsync` into DTOs. The navs exist only for the FK relationship; the `#pragma warning disable CS0618` blocks in the section gate in-section `.Team` nav reads, never a `.User` read.

### Architecture tests

- `tests/Humans.Teams.Tests/Architecture/TeamsArchitectureTests.cs` — pins the read-split shape: `ITeamService` inherits `ITeamServiceRead`, `CachingTeamService` implements it, and both interfaces resolve to the same singleton so reader and writer never see different caches.
- `tests/Humans.Teams.Tests/Architecture/TeamPageArchitectureTests.cs` — pins that `TeamPageService` is the `ITeamPageService` registration.
- `tests/Humans.GoogleIntegration.Tests/Architecture/TeamResourceArchitectureTests.cs` — pins `TeamResourceService`, which lives in the GoogleIntegration section.
- `tests/Humans.Integration.Tests/Controllers/TeamsPageRenderTests.cs` — the standing render check (local-only, self-skips under CI): every page renders, no unbound `<vc:>`, no raw resource key, the Shell access-matrix widget resolves by name, the volunteer negative-access rule, and Spanish from the RCL's satellite assemblies.

### Target repositories

- **`ITeamRepository`** (`Humans.Teams.Data`, impl `Humans.Teams.Data.TeamRepository`) — owns `teams`, `team_members`, `team_join_requests`, `team_join_request_state_history`, `team_role_definitions`, `team_role_assignments`, `team_early_entry_grants`
  - Aggregate-local navs kept: `Team.ParentTeam`, `Team.ChildTeams`, `Team.Members`, `Team.EarlyEntryGrants`, `Team.JoinRequests`, `Team.RoleDefinitions`, `TeamJoinRequest.StateHistory`, `TeamEarlyEntryGrant.Team`, `TeamMember.Team`, `TeamRoleDefinition.Team`, `TeamRoleAssignment.TeamRoleDefinition`, `TeamRoleAssignment.TeamMember`
  - Cross-domain navs stripped: `TeamMember.User → TeamMember.UserId`, `TeamJoinRequest.User → TeamJoinRequest.UserId`, `TeamJoinRequest.ReviewedByUser → TeamJoinRequest.ReviewedByUserId`, `TeamRoleAssignment.AssignedByUser → TeamRoleAssignment.AssignedByUserId`, `TeamJoinRequestStateHistory.ChangedByUser → TeamJoinRequestStateHistory.ChangedByUserId`
- **`IGoogleResourceRepository`** (`Humans.GoogleIntegration.Data`, impl `Humans.GoogleIntegration.Data.GoogleResourceRepository`) — owns `google_resources` (Team Resources sub-aggregate).
  - Aggregate-local navs kept: `GoogleResource.Team` back-ref is still declared but never `Include`-d by the repository (the one consumer, `GoogleController`, only reads `resource.Name`).
  - Cross-domain navs stripped: none.
  - Companion connector: `ITeamResourceGoogleClient` encapsulates Drive/Cloud-Identity calls so `TeamResourceService`'s business logic stays free of `Google.Apis.*`.

### System-team reconciler

`SystemTeamSyncJob` is Teams' own: `Services/SystemTeamSyncJob.cs`, `internal sealed`, registered in `Section.cs` and scheduled by `SectionJobs.cs`. Its interface `ISystemTeamSync` lives on `Humans.Teams.Contracts`, which is what the consuming sections reference. Hangfire keys the recurring job on its id and rewrites the stored type name at every startup, so the implementation's assembly and namespace are free to move.

<!-- freshness:triggers
  src/Humans.Application/Services/Teams/**
  src/Humans.Domain/Entities/Team.cs
  src/Humans.Domain/Entities/TeamMember.cs
  src/Humans.Domain/Entities/TeamJoinRequest.cs
  src/Humans.Domain/Entities/TeamJoinRequestStateHistory.cs
  src/Humans.Domain/Entities/TeamRoleDefinition.cs
  src/Humans.Domain/Entities/TeamRoleAssignment.cs
  src/Humans.Domain/Entities/GoogleResource.cs
  src/Humans.Domain/Constants/SystemTeamIds.cs
  src/Humans.Infrastructure/Data/Configurations/Teams/**
  src/Humans.Infrastructure/Data/Configurations/GoogleResourceConfiguration.cs
  src/Humans.Web/Controllers/TeamController.cs
  src/Humans.Web/Controllers/TeamAdminController.cs
  src/Humans.Web/Authorization/Requirements/TeamAuthorizationHandler.cs
  src/Humans.Web/Authorization/Requirements/TeamOperationRequirement.cs
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

Aggregate-local navs kept: `Team.ParentTeam`, `Team.ChildTeams`, `Team.Members`, `Team.JoinRequests`, `Team.RoleDefinitions`. Public/member team page content lives directly on the row as `PageContent` / `PageContentUpdatedAt` / `PageContentUpdatedByUserId` / `CallsToAction` (JSONB) / `ShowCoordinatorsOnPublicPage` columns (no separate `team_pages` table or entity). Cross-domain nav `Team.LegalDocuments` (Legal section) is declared on the entity but should be FK-only; not used by `ITeamRepository`.

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

Three controllers serve this section. `TeamController` (`[Route("Teams")]`) handles both anonymous/member-facing and TeamsAdmin-gated actions. `TeamAdminController` (`[Route("Teams/{slug}")]`) handles per-team management under coordinator/admin authorization via `HumansTeamControllerBase.ResolveTeamManagementAsync` (which calls `TeamAuthorizationHandler` + `TeamOperationRequirement.ManageCoordinators`). Routes that go through `CanManageResourcesAsync` (coordinator-of-department check) are marked separately.

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

`ResolveTeamManagementAsync` authorizes via `TeamAuthorizationHandler` + `TeamOperationRequirement.ManageCoordinators`. `CanManageResourcesAsync` checks coordinator-of-department specifically (sub-team managers cannot manage Google resources).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone (including anonymous) | Browse the team directory and view public team pages |
| Any active human | View team detail pages, request to join a team, leave a team, withdraw a pending request, view own memberships, browse the birthday calendar, search humans, view the roster and map |
| Coordinator | Manage members, approve/reject join requests, manage roles, edit the team page, and manage Google resources for their department (and its sub-teams) |
| Sub-team Manager | Manage members, approve/reject join requests, manage roles, manage shifts, and edit the team page for their sub-team only. Cannot manage Google resources, the parent department, or sibling sub-teams |
| TeamsAdmin | All coordinator capabilities on all teams. Create teams, edit team settings (name, slug, approval mode, parent, Google group prefix, budget flag, hidden flag, sensitive flag, directory promotion), toggle the management role, and link/unlink Google resources on all teams |
| Board | All TeamsAdmin capabilities. Additionally can delete (deactivate) teams |
| Admin | All Board capabilities. Additionally can execute Google sync actions, trigger system team sync, and view sync previews |

## Invariants

- A department can have **at most one** role flagged as management (coordinator). Enforced in both the toggle and edit paths.
- A sub-team can have **at most one** role flagged as management (manager).
- Toggling or changing the `IsManagement` flag on a role definition is restricted to **TeamsAdmin / Admin** (`ToggleManagement` action and `EditRole` IsManagement field). Coordinators / sub-team managers can still create, rename, and delete other (non-management) role definitions on their team — they just cannot promote/demote the management role itself.
- A `TeamRoleDefinition.IsPublic = false` role is hidden from volunteer-facing views (team detail, roster) but remains visible to coordinators and admins.
- Members of sub-teams are also considered members of the department. They appear in the department's member roster and inherit the department's legal requirements and Google resource access.
- A human can be a member of multiple teams simultaneously.
- System team membership is managed exclusively by an automated sync job. Manual add/remove is blocked for system teams.
- Joining a team that requires approval creates a join request (Pending). The request must be approved by a coordinator or TeamsAdmin before membership is granted. Teams that do not require approval add the human immediately.
- Coordinators can approve/reject join requests for their own department and any sub-teams within that department (enforced by `IsUserCoordinatorOfTeamAsync`).
- All member additions and removals are audit-logged via `AuditLogEntry`.
- Google resource access changes triggered by membership changes (Drive folder permissions, Group memberships) are logged in the audit trail.
- Removing a member from a team also removes all their role assignments on that team.
- Each team has a unique slug used for URL routing. A custom slug can override the auto-generated one.
- A Google Group prefix, if set, provisions a `@nobodies.team` group for the team.
- Only departments (not sub-teams or system teams) can have public team pages.
- A **hidden team** (`IsHidden = true`) is invisible to non-admin users: it does not appear on profile cards, team listings, public pages, birthday team names, or the "My Teams" page. Only Admin, Board, and TeamsAdmin can see and manage hidden teams. Campaigns can still target hidden teams for code distribution. The system-team sync skips the "added to team" email for hidden teams.
- A **sensitive team** (`IsSensitive = true`) is an admin-only flag (not publicly visible). Adding or approving a member surfaces a deterrent confirmation modal in the Members admin view that shows the audit record that will be created.
- The Teams directory (`/Teams`) shows only **directory-visible** teams: top-level teams (departments) always appear; sub-teams only appear if `IsPromotedToDirectory` is true. Sub-teams are always accessible from their parent team's detail page regardless of this flag.
- `team_join_request_state_history` is append-only per §12.
- Resource-based authorization per design-rules §11: `TeamAuthorizationHandler` + `TeamOperationRequirement`.

## Negative Access Rules

- Regular humans **cannot** manage other teams' members, roles, or settings.
- Coordinators **cannot** create, delete, or edit team admin settings (name, approval mode, parent, Google prefix). They can only edit the team page and manage members/roles for their own department.
- Sub-team managers **cannot** manage Google resources, the parent department, sibling sub-teams, or team admin settings.
- TeamsAdmin **cannot** delete teams or execute sync actions.
- Nobody can manually add or remove members from system teams.

## Triggers

- When a join request is approved, a team membership record is created and the human is notified.
- When a member is removed from a team, all their role assignments for that team are also removed.
- When a member is added to a team, Google resource sync (Drive folder permissions, Group memberships) runs inline against the Google APIs (and rolls up to the parent department's resources for sub-team adds). Per-user removals are deferred to the daily reconciliation job rather than running inline. Failed sync calls fall through to the Google sync outbox, processed by `process-google-sync-outbox`.
- When a department coordinator role assignment changes, the Coordinators system team membership is recalculated for the affected human. Sub-team manager changes do not affect the Coordinators system team.
- The system team sync job runs hourly (Hangfire `Cron.Hourly` recurring job `system-team-sync`), reconciling system team membership for Volunteers (consent compliance), Coordinators (department-level management role assignments), Board (active Board role assignments), Asociados/Colaboradors (approved tier applications with active terms), and Barrio Leads (active camp lead assignments). The job also reconciles `TeamMember.Role` against `IsManagement` role assignments and backfills `User.GoogleEmail` for verified `@nobodies.team` accounts.
- When an account merge accepts, `ITeamService.ReassignToUserAsync` re-FKs `TeamMember` and `TeamJoinRequest` rows from source to target, collapsing duplicates so the same target doesn't end up with two memberships of the same team. Called only by `IAccountMergeService.AcceptAsync` (Profiles section).

## Cross-Section Dependencies

- **Google Integration:** Each team can have linked Google resources (Drive folders, Groups). Membership changes call `IGoogleSyncService.AddUserToTeamResourcesAsync` / `RemoveUserFromTeamResourcesAsync` inline (per-user removals are no-ops, handled by the daily reconciliation job); failed Google API calls land in the sync outbox.
- **Shifts:** Rotas belong to a department or sub-team. Coordinator/manager status determines shift management access (scoped to their team).
- **Budget:** Budget categories can be linked to a department. Coordinator status determines budget line item editing access.
- **Onboarding:** Volunteer activation adds the human to the Volunteers system team.
- **Governance:** Colaborador/Asociado approval or expiry adds/removes humans from the respective system teams.
- **Camps:** Active camp lead assignments feed the Barrio Leads system team via `ICampRepository.GetActiveLeadUserIdsAsync` / `IsLeadAnywhereAsync`.
- **Users/Identity:** `IUserService.GetByIdsAsync` — display data stitching for nav-stripped sections.
- **Profiles:** Called by `IAccountMergeService` (Profiles section) — `ITeamService.ReassignToUserAsync` re-FKs `TeamMember` and `TeamJoinRequest` from source to target during account merge fold.

## Architecture

**Owning services:** `TeamService`, `TeamPageService` (Teams section); `TeamResourceService` (GoogleIntegration section — see note below)
**Owned tables:** `teams`, `team_members`, `team_join_requests`, `team_join_request_state_history`, `team_role_definitions`, `team_role_assignments` (Teams section); `google_resources` is a Team Resources sub-aggregate but is managed from the GoogleIntegration section
**Status:** (A) Migrated (2026-04-23). `TeamService` and `TeamPageService` live in `Humans.Application.Services.Teams`. `TeamResourceService` was relocated to `Humans.Application.Services.GoogleIntegration` (alongside `GoogleWorkspaceSyncService`) — its repository, EF impl, and connector clients all live there, and consolidating the section label keeps HUM0017 satisfied (see `memory/architecture/team-resources-google-integration-section.md`).

- `TeamService` goes through `ITeamRepository` for owned-table access and routes every cross-section read through the public service interface (`IUserService`, `IRoleAssignmentService`, `IShiftManagementService`, `ITeamResourceService`).
- `TeamResourceService` (now in `Humans.Application.Services.GoogleIntegration`) uses `IGoogleResourceRepository` + the `ITeamResourceGoogleClient` connector (PR #274). `IGoogleResourceRepository` lives in `Humans.Application.Interfaces.Repositories` with its EF impl in `Humans.Infrastructure.Repositories.GoogleIntegration`.
- `TeamPageService` owns no tables — it is a read-only composer over `ITeamService`, `ITeamResourceService`, and sibling services. It has no repository dependency (enforced by `TeamPageArchitectureTests`).
- **Read/write interface split.** `ITeamServiceRead` (4 methods: `GetTeamsAsync`, `GetTeamAsync`, `GetTeamBySlugAsync`, `SearchAsync`) is the cross-section read surface — only `TeamInfo` / `TeamSearchHit` projections, no EF entities. `ITeamService : ITeamServiceRead` adds writes, cache invalidation, and Teams-internal reads (including entity-returning `GetTeamEntityBySlugAsync` / `GetTeamByIdAsync`). External sections inject `ITeamServiceRead`. See [`memory/architecture/section-read-write-split.md`](../../memory/architecture/section-read-write-split.md). `TeamUserInfo`-based `GetUserTeamsAsync` is a planned addition pending the `TeamUserInfo` projection PR.
- **Decorator decision — caching decorator.** `CachingTeamService` is a Singleton transparent decorator for `ITeamService`. It owns the canonical `ConcurrentDictionary<Guid, TeamInfo> _byTeamId` read model. It inherits `TrackedCache<Guid, TeamInfo>` which implements `IHostedService`; registered via `services.AddHostedService(sp => sp.GetRequiredService<CachingTeamService>())`. Bulk invalidations call `Clear()` (flips warmed flag) and the next read re-warms via `EnsureWarmedAsync`. `TeamInfo` / `TeamMemberInfo` are the service read models; the EF `Team` entity remains legacy surface area until the service-entity-boundary cleanup removes it from read APIs. `TeamInfo` is the canonical read shape — extended in T-01 with `ChildTeamIds`, page-content fields, and CTA list so `GetTeamDetailAsync` projects entirely from cache (slug → cached `TeamInfo` → walk `ChildTeamIds` → stitch `RoleDefinitions`); `ITeamRepository.GetBySlugWithRelationsAsync` / `GetRoleDefinitionsAsync` are retained only for inner write-path flows (admin actions resolve a team by slug then mutate). Pending-request lookups (`GetUserPendingRequestAsync`, `GetPendingRequestsForTeamAsync`) still route through the inner service on the auth/management read path — real-time accuracy is required and a stale per-team pending count would mislead coordinators.
- **Cross-domain navs `[Obsolete]`-marked:** `TeamMember.User`, `TeamJoinRequest.User`, `TeamJoinRequest.ReviewedByUser`, `TeamRoleAssignment.AssignedByUser`, `TeamJoinRequestStateHistory.ChangedByUser`. `TeamService` populates them in-memory via `IUserService.GetByIdsAsync` (§6b); controllers/views still read them under file-wide `#pragma warning disable CS0618` pragmas pending the cross-cutting User-nav strip (§15i).

### Architecture tests

- `tests/Humans.Application.Tests/Architecture/TeamsArchitectureTests.cs` — pins `TeamService` namespace, no-DbContext, `ITeamRepository` dependency, assembly.
- `tests/Humans.Application.Tests/Architecture/TeamResourceArchitectureTests.cs` — pins `TeamResourceService`.
- `tests/Humans.Application.Tests/Architecture/TeamPageArchitectureTests.cs` — pins `TeamPageService` namespace, no-DbContext, no-repository dependency (composer-only).

### Target repositories

- **`ITeamRepository`** (`Humans.Application.Interfaces.Repositories`, impl `Humans.Infrastructure.Repositories.Teams.TeamRepository`) — owns `teams`, `team_members`, `team_join_requests`, `team_join_request_state_history`, `team_role_definitions`, `team_role_assignments`
  - Aggregate-local navs kept: `Team.ParentTeam`, `Team.ChildTeams`, `Team.Members`, `Team.JoinRequests`, `Team.RoleDefinitions`, `TeamJoinRequest.StateHistory`, `TeamMember.Team`, `TeamRoleDefinition.Team`, `TeamRoleAssignment.TeamRoleDefinition`, `TeamRoleAssignment.TeamMember`
  - Cross-domain navs stripped: `TeamMember.User → TeamMember.UserId`, `TeamJoinRequest.User → TeamJoinRequest.UserId`, `TeamJoinRequest.ReviewedByUser → TeamJoinRequest.ReviewedByUserId`, `TeamRoleAssignment.AssignedByUser → TeamRoleAssignment.AssignedByUserId`, `TeamJoinRequestStateHistory.ChangedByUser → TeamJoinRequestStateHistory.ChangedByUserId`
- **`IGoogleResourceRepository`** (`Humans.Application.Interfaces.Repositories`, impl `Humans.Infrastructure.Repositories.GoogleIntegration.GoogleResourceRepository` — landed 2026-04-22, PR for sub-task nobodies-collective/Humans#540c) — owns `google_resources` (Team Resources sub-aggregate).
  - Aggregate-local navs kept: `GoogleResource.Team` back-ref is still declared but never `Include`-d by the repository (the one consumer, `GoogleController`, only reads `resource.Name`).
  - Cross-domain navs stripped: none.
  - Companion connector: `ITeamResourceGoogleClient` encapsulates Drive/Cloud-Identity calls so the Application project stays free of `Google.Apis.*`.

### Post-migration follow-ups

- **Nav-strip (design-rules §6c).** `TeamMember.User`, `TeamJoinRequest.User`, `TeamJoinRequest.ReviewedByUser`, `TeamRoleAssignment.AssignedByUser`, and `TeamJoinRequestStateHistory.ChangedByUser` are `[Obsolete]`-marked and populated in memory by `TeamService` via `IUserService.GetByIdsAsync` before the entity graph leaves the service. Razor views and controllers still read through these navs under file-wide `#pragma warning disable CS0618` blocks (`TeamAdminController`, `TeamController`, `TeamViewModels`, `TeamServiceTests`). The pragmas are cleared when the consumers migrate to service-layer DTOs — tracked as the User-entity nav-strip follow-up alongside Shifts / GoogleWorkspaceSync / SystemTeamSyncJob.
- **Infrastructure-side callers (`SystemTeamSyncJob`, `GoogleWorkspaceSyncService`).** Both still live in Infrastructure and still read `TeamMember.User` directly via EF `Include`; they are covered by file-wide CS0618 pragmas pending their own Application-layer migrations (tracked in §15i).

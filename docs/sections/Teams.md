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

Departments and sub-teams, join requests, role definitions, team pages, and linked Google resources. The largest section; migration is in flight.

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

Aggregate-local navs kept: `Team.ParentTeam`, `Team.ChildTeams`, `Team.Members`, `Team.JoinRequests`, `Team.RoleDefinitions`.

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

Named role slots on a team (name, description, slot count, priorities, sort order, `IsManagement` flag, `IsPublic` flag, `Period`). Aggregate-local: `TeamRoleDefinition.Team`. Per-team unique index `IX_team_role_definitions_team_name_unique` on `(TeamId, Name)`.

### TeamRoleAssignment

**Table:** `team_role_assignments`

Assigns a team member to a specific slot in a role definition. Aggregate-local: `TeamRoleAssignment.TeamRoleDefinition`, `TeamRoleAssignment.TeamMember`. Cross-domain nav `TeamRoleAssignment.AssignedByUser → AssignedByUserId` (target: FK-only).

### TeamPage

**Table:** `team_pages`

Aggregate-local back-ref: `TeamPage.Team`.

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

## Cross-Section Dependencies

- **Google Integration:** Each team can have linked Google resources (Drive folders, Groups). Membership changes call `IGoogleSyncService.AddUserToTeamResourcesAsync` / `RemoveUserFromTeamResourcesAsync` inline (per-user removals are no-ops, handled by the daily reconciliation job); failed Google API calls land in the sync outbox.
- **Shifts:** Rotas belong to a department or sub-team. Coordinator/manager status determines shift management access (scoped to their team).
- **Budget:** Budget categories can be linked to a department. Coordinator status determines budget line item editing access.
- **Onboarding:** Volunteer activation adds the human to the Volunteers system team.
- **Governance:** Colaborador/Asociado approval or expiry adds/removes humans from the respective system teams.
- **Camps:** Active camp lead assignments feed the Barrio Leads system team via `ICampRepository.GetActiveLeadUserIdsAsync` / `IsLeadAnywhereAsync`.
- **Users/Identity:** `IUserService.GetByIdsAsync` — display data stitching for nav-stripped sections.

## Architecture

**Owning services:** `TeamService`, `TeamPageService`, `TeamResourceService`
**Owned tables:** `teams`, `team_members`, `team_join_requests`, `team_join_request_state_history`, `team_role_definitions`, `team_role_assignments`, `team_pages`, `google_resources`
**Status:** (A) Migrated (2026-04-23). All three services — `TeamPageService`, `TeamResourceService`, and `TeamService` — now live in `Humans.Application.Services.Teams`.

- `TeamService` goes through `ITeamRepository` for owned-table access and routes every cross-section read through the public service interface (`IUserService`, `IRoleAssignmentService`, `IShiftManagementService`, `ITeamResourceService`).
- `TeamPageService` is a pure composer (PR #270 — no repository needed).
- `TeamResourceService` uses `IGoogleResourceRepository` + the `ITeamResourceGoogleClient` connector (PR #274).
- **Decorator decision — no caching decorator.** `TeamService` keeps a short-TTL `IMemoryCache` projection at `CacheKeys.ActiveTeams` (10-minute TTL) inside the service itself (same precedent as Camps per §15f / §15i).
- **Cross-domain navs `[Obsolete]`-marked:** `TeamMember.User`, `TeamJoinRequest.User`, `TeamJoinRequest.ReviewedByUser`, `TeamRoleAssignment.AssignedByUser`, `TeamJoinRequestStateHistory.ChangedByUser`. `TeamService` populates them in-memory via `IUserService.GetByIdsAsync` (§6b); controllers/views still read them under file-wide `#pragma warning disable CS0618` pragmas pending the cross-cutting User-nav strip (§15i).

### Target repositories

- **`ITeamRepository`** — owns `teams`, `team_members`, `team_join_requests`, `team_join_request_state_history`, `team_role_definitions`, `team_role_assignments`
  - Aggregate-local navs kept: `Team.ParentTeam`, `Team.ChildTeams`, `Team.Members`, `Team.JoinRequests`, `Team.RoleDefinitions`, `TeamJoinRequest.StateHistory`, `TeamMember.Team`, `TeamRoleDefinition.Team`, `TeamRoleAssignment.TeamRoleDefinition`, `TeamRoleAssignment.TeamMember`
  - Cross-domain navs stripped: `TeamMember.User → TeamMember.UserId`, `TeamJoinRequest.User → TeamJoinRequest.UserId`, `TeamJoinRequest.ReviewedByUser → TeamJoinRequest.ReviewedByUserId`, `TeamRoleAssignment.AssignedByUser → TeamRoleAssignment.AssignedByUserId`, `TeamJoinRequestStateHistory.ChangedByUser → TeamJoinRequestStateHistory.ChangedByUserId`
- **`ITeamPageRepository`** — owns `team_pages`
  - Aggregate-local navs kept: `TeamPage.Team` (back-ref within Teams section)
  - Cross-domain navs stripped: none
- **`IGoogleResourceRepository`** (landed 2026-04-22, PR for sub-task nobodies-collective/Humans#540c) — owns `google_resources` (Team Resources sub-aggregate).
  - Aggregate-local navs kept: `GoogleResource.Team` back-ref is still declared but never `Include`-d by the repository (the one consumer, `GoogleController`, only reads `resource.Name`).
  - Cross-domain navs stripped: none.
  - Companion connector: `ITeamResourceGoogleClient` encapsulates Drive/Cloud-Identity calls so the Application project stays free of `Google.Apis.*`.

### Post-migration follow-ups

- **Nav-strip (design-rules §6c).** `TeamMember.User`, `TeamJoinRequest.User`, `TeamJoinRequest.ReviewedByUser`, `TeamRoleAssignment.AssignedByUser`, and `TeamJoinRequestStateHistory.ChangedByUser` are `[Obsolete]`-marked and populated in memory by `TeamService` via `IUserService.GetByIdsAsync` before the entity graph leaves the service. Razor views and controllers still read through these navs under file-wide `#pragma warning disable CS0618` blocks (`TeamAdminController`, `TeamController`, `VolController`, `TeamViewModels`, `Views/Vol/ChildTeamDetail.cshtml`, `TeamServiceTests`). The pragmas are cleared when the consumers migrate to service-layer DTOs — tracked as the User-entity nav-strip follow-up alongside Shifts / GoogleWorkspaceSync / SystemTeamSyncJob.
- **Decorator split (§15 Part 2 candidate).** `TeamService` keeps the active-teams projection in an `IMemoryCache` entry at `CacheKeys.ActiveTeams` (10-minute TTL, in-service — same precedent as Camps per §15f / §15i Camps entry). A Singleton `CachingTeamService` decorator + `ITeamMembershipInvalidator` can replace this later if profiling shows it matters; the `ITeamService` surface does not need to change.
- **Infrastructure-side callers (`SystemTeamSyncJob`, `GoogleWorkspaceSyncService`).** Both still live in Infrastructure and still read `TeamMember.User` directly via EF `Include`; they are covered by file-wide CS0618 pragmas pending their own Application-layer migrations (tracked in §15i).

# Teams — Section Invariants

## Concepts

- A **Department** is a team with no parent.
- A **Sub-Team** is a team within a department. Only one level of nesting is allowed.
- **System teams** (Volunteers, Coordinators, Board, Asociados, Colaboradors) are managed automatically — members cannot be manually added or removed.
- A **Coordinator** is a team member assigned to the management role on a department. Coordinators have full authority over the department and all its sub-teams, including Google resource management. They are added to the Coordinators system team.
- A **Sub-team Manager** is a team member assigned to the management role on a sub-team. Managers have scoped authority over their sub-team only: member management, join requests, roles, shifts, and team page editing. They **cannot** manage Google resources, the parent department, or sibling sub-teams. They are **not** added to the Coordinators system team.
- A **Team Page** is a Markdown-based public or member-facing page for a department, with optional calls to action.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Anyone (including anonymous) | Browse the team directory and view public team pages |
| Any active human | View team detail pages, request to join a team, leave a team, withdraw a pending request, view own memberships, browse the birthday calendar, search humans, view the roster and map |
| Coordinator | Manage members, approve/reject join requests, manage roles, edit the team page, and manage Google resources for their department (and its sub-teams) |
| Sub-team Manager | Manage members, approve/reject join requests, manage roles, manage shifts, and edit the team page for their sub-team only. Cannot manage Google resources, the parent department, or sibling sub-teams |
| TeamsAdmin | All coordinator capabilities on all teams. Create teams, edit team settings (name, slug, approval mode, parent, Google group prefix, budget flag), and link/unlink Google resources on all teams |
| Board | All TeamsAdmin capabilities. Additionally can delete (deactivate) teams |
| Admin | All Board capabilities. Additionally can execute Google sync actions, trigger system team sync, and view sync previews |

## Invariants

- A department can have **at most one** role flagged as management (coordinator). This is enforced in both the toggle and edit paths. If present, members assigned to it gain coordinator-level access over the whole department and all its sub-teams: member management, join request handling, role management, team page editing, and Google resource management.
- A sub-team can have **at most one** role flagged as management (manager). Members assigned to it gain scoped management access over that sub-team only. Sub-team managers cannot manage Google resources, the parent department, or sibling sub-teams, and are not added to the Coordinators system team.
- Members of sub-teams are also considered members of the department. They appear in the department's member roster and inherit the department's legal requirements and Google resource access (Drive folders, Groups).
- A human can be a member of multiple teams simultaneously.
- System team membership is managed exclusively by an automated sync job. Manual add/remove is blocked for system teams.
- Joining a team that requires approval creates a join request (Pending). The request must be approved by a coordinator or TeamsAdmin before membership is granted. Teams that do not require approval add the human immediately.
- Coordinators can approve/reject join requests for their own department and any sub-teams within that department. This scope is enforced by `IsUserCoordinatorOfTeamAsync`, which checks coordinator role on the target team or its parent department.
- Coordinators **cannot** approve/reject join requests for departments or teams they do not coordinate. The Members page returns Forbid for unauthorized coordinators.
- All member additions and removals are audit-logged with actor, target, team, and timestamp via `AuditLogEntry`.
- Google resource access changes triggered by membership changes (Drive folder permissions, Group memberships) are logged in the audit trail.
- Removing a member from a team also removes all their role assignments on that team.
- Each team has a unique slug used for URL routing. A custom slug can override the auto-generated one.
- A Google Group prefix, if set, provisions a @nobodies.team group for the team.
- Only departments (not sub-teams or system teams) can have public team pages.
- A **hidden team** (`IsHidden = true`) is invisible to non-admin users: it does not appear on profile cards, team listings, public pages, birthday team names, or the "My Teams" page. Only Admin, Board, and TeamsAdmin can see and manage hidden teams. Campaigns can still target hidden teams for code distribution.
- The Teams directory (`/Teams`) shows only **directory-visible** teams: top-level teams (departments) always appear; sub-teams only appear if `IsPromotedToDirectory` is true. Sub-teams are always accessible from their parent team's detail page regardless of this flag.

## Negative Access Rules

- Regular humans **cannot** manage other teams' members, roles, or settings.
- Coordinators **cannot** create, delete, or edit team admin settings (name, approval mode, parent, Google prefix). They can only edit the team page and manage members/roles for their own department.
- Sub-team managers **cannot** manage Google resources, the parent department, sibling sub-teams, or team admin settings.
- TeamsAdmin **cannot** delete teams or execute sync actions.
- Nobody can manually add or remove members from system teams.

## Triggers

- When a join request is approved, a team membership record is created and the human is notified.
- When a member is removed from a team, all their role assignments for that team are also removed.
- When a member is added or removed from a team, Google resource sync events (Drive, Groups) are queued.
- When a department coordinator role assignment changes, the Coordinators system team membership is recalculated for the affected human. Sub-team manager changes do not affect the Coordinators system team.
- The system team sync job runs hourly, reconciling system team membership based on role assignments and tier status.

## Cross-Section Dependencies

- **Google Integration**: Each team can have linked Google resources (Drive folders, Groups). Membership changes trigger sync outbox events.
- **Shifts**: Rotas belong to a department or sub-team. Coordinator/manager status determines shift management access (scoped to their team).
- **Budget**: Budget categories can be linked to a department. Coordinator status determines budget line item editing access.
- **Onboarding**: Volunteer activation adds the human to the Volunteers system team.
- **Governance**: Colaborador/Asociado approval or expiry adds/removes humans from the respective system teams.

## Architecture — Current vs Target

See `docs/architecture/design-rules.md` for the full rules.

**Owning services:** `TeamService`, `TeamPageService`, `TeamResourceService`
**Owned tables:** `teams`, `team_members`, `team_join_requests`, `team_join_request_state_histories`, `team_role_definitions`, `team_role_assignments`, `team_pages`

## Target Architecture Direction

> **Status:** **Partially migrated** (2026-04-22). `TeamPageService` lives in `Humans.Application.Services.Teams` (PR #270, pure composer — no repo needed). `TeamResourceService` also lives in `Humans.Application.Services.Teams` and is backed by `IGoogleResourceRepository` (`Humans.Application.Interfaces.Repositories`) + `ITeamResourceGoogleClient` connector (implementations: `TeamResourceGoogleClient` / `StubTeamResourceGoogleClient` in `Humans.Infrastructure/Services/GoogleWorkspace/`). `StubTeamResourceService` no longer exists as a separate service — dev mode is now selected at the connector layer. **`TeamService` (2,698 LOC) is still the "services in Infrastructure, direct DbContext" model** and is pending sub-task #540a. Delete this block once that migration lands.

### Target repositories

- **`ITeamRepository`** — owns `teams`, `team_members`, `team_join_requests`, `team_join_request_state_histories`, `team_role_definitions`, `team_role_assignments`
  - Aggregate-local navs kept: `Team.ParentTeam`, `Team.ChildTeams`, `Team.Members`, `Team.JoinRequests`, `Team.RoleDefinitions`, `TeamJoinRequest.StateHistory`, `TeamMember.Team`, `TeamRoleDefinition.Team`, `TeamRoleAssignment.TeamRoleDefinition`, `TeamRoleAssignment.TeamMember`
  - Cross-domain navs stripped: `TeamMember.User → TeamMember.UserId`, `TeamJoinRequest.User → TeamJoinRequest.UserId`, `TeamJoinRequest.ReviewedByUser → TeamJoinRequest.ReviewedByUserId`, `TeamRoleAssignment.AssignedByUser → TeamRoleAssignment.AssignedByUserId`, `TeamJoinRequestStateHistory.ChangedByUser → TeamJoinRequestStateHistory.ChangedByUserId`
- **`ITeamPageRepository`** — owns `team_pages`
  - Aggregate-local navs kept: `TeamPage.Team` (back-ref within Teams section)
  - Cross-domain navs stripped: none
- **`IGoogleResourceRepository`** (landed 2026-04-22, PR for #540c) — owns `google_resources` (Team Resources subsection).
  - Aggregate-local navs kept: `GoogleResource.Team` back-ref is still declared but never `Include`-d by the repository (the one consumer, `GoogleController`, only reads `resource.Name`).
  - Cross-domain navs stripped: none.
  - Companion connector: `ITeamResourceGoogleClient` encapsulates Drive/Cloud-Identity calls so the Application project stays free of `Google.Apis.*`.

### Current violations

Observed in this section's service code as of 2026-04-15:

- **Cross-domain `.Include()` calls:**
  - `TeamService.cs:999` — `.Include(r => r.User)` in `GetPendingRequestsForApproverAsync` (navigates to Users domain)
  - `TeamService.cs:1026` — `.Include(r => r.User)` in `GetPendingRequestsForTeamAsync` (navigates to Users domain)
  - `TeamService.cs:1297` — `.Include(tm => tm.User)` in `GetTeamMembersAsync` (navigates to Users domain)
  - `TeamService.cs:1912` — `.Include(u => u.UserEmails)` in `SendAddedToTeamEmailAsync` (navigates to Users/Profiles domain)
  - `TeamService.cs:2160` — `.Include(m => m.User)` nested inside `_cache.GetOrCreateAsync` in `GetCachedTeamsAsync` (navigates to Users domain)
- **Cross-section direct DbContext reads** (reading tables owned by OTHER sections):
  - `TeamService.cs:783` — `_dbContext.Users.FindAsync()` in `JoinTeamAsync` cache-update path (owned by Users/Identity)
  - `TeamService.cs:929` — `_dbContext.Users.FindAsync()` in `ApproveJoinRequestAsync` cache-update path (owned by Users/Identity)
  - `TeamService.cs:1278` — `_dbContext.Users.FindAsync()` in `AddMemberAsync` cache-update path (owned by Users/Identity)
  - `TeamService.cs:1830` — `_dbContext.Users.FindAsync()` in `AssignToRoleAsync` cache-update path (owned by Users/Identity)
  - `TeamService.cs:1911` — `_dbContext.Users.Include(u => u.UserEmails)` in `SendAddedToTeamEmailAsync` (owned by Users/Identity)
  - `TeamService.cs:1955` — `_dbContext.Users.Find()` in `EnqueueGoogleSyncOutboxEvent` (owned by Users/Identity)
  - `TeamService.cs:1964` — `_dbContext.GoogleSyncOutboxEvents.Add(...)` in `EnqueueGoogleSyncOutboxEvent` (cross-cutting outbox owned by Google Integration)
  - `TeamService.cs:2083` — `_dbContext.EventSettings` in `GetAdminTeamListAsync` (owned by Shifts)
  - `TeamPageService.cs:130` — `_dbContext.Users` in `GetPageContentUpdatedByDisplayNameAsync` (owned by Users/Identity)
  - `TeamPageService.cs:179` — `_dbContext.Rotas` in `GetShiftsSummaryAsync` (owned by Shifts)
- **Within-section cross-service direct DbContext reads** (§2c — each service owns tables, not each section):
  - ~~`TeamResourceService.cs:149` — `_dbContext.TeamMembers` in `GetUserTeamResourcesAsync`~~ — resolved 2026-04-22 (PR for #540c): the migrated service calls `ITeamService.GetUserTeamsAsync` and stitches in memory.
  - ~~`TeamResourceService.cs:151` — `_dbContext.Teams` join in `GetUserTeamResourcesAsync`~~ — same fix as above.
- **Inline `IMemoryCache` usage in service methods:**
  - `TeamService.cs:2154` — `_cache.GetOrCreateAsync(CacheKeys.ActiveTeams, …)` in `GetCachedTeamsAsync` (cache load with cross-domain `.Include` at line 2160)
  - `TeamService.cs:2302` — `_cache.TryUpdateExistingValue<...>(CacheKeys.ActiveTeams, mutate)` in `TryUpdateCachedTeam` (inline dictionary mutation)
  - `TeamService.cs:470, 564, 1503, 1623` — `_cache.InvalidateActiveTeams()` scattered across `DeleteTeamAsync`, `UpdateTeamAsync`, role-definition toggles (invalidation logic inline in service)
  - `TeamService.cs:862, 928, 968` — `_cache.InvalidateNotificationMeters()` in join-request workflow paths (cross-cache invalidation inline)
  - `TeamService.cs:2354, 2362, 2370` — `_cache.InvalidateShiftAuthorization(userId)` in `InvalidateShiftAuthorizationIfNeeded` (cross-cache invalidation inline)
- **Cross-domain nav properties on this section's entities** (target: FK-only):
  - `TeamMember.User → TeamMember.UserId` (Users/Identity is a separate domain — TeamMember.cs:34)
  - `TeamJoinRequest.User → TeamJoinRequest.UserId` (Users/Identity is a separate domain — TeamJoinRequest.cs:15)
  - `TeamJoinRequest.ReviewedByUser → TeamJoinRequest.ReviewedByUserId` (Users/Identity is a separate domain — TeamJoinRequest.cs:21)
  - `TeamRoleAssignment.AssignedByUser → TeamRoleAssignment.AssignedByUserId` (Users/Identity is a separate domain — TeamRoleAssignment.cs:53)
  - `TeamJoinRequestStateHistory.ChangedByUser → TeamJoinRequestStateHistory.ChangedByUserId` (Users/Identity is a separate domain — TeamJoinRequestStateHistory.cs:44)

### Touch-and-clean guidance

Until this section is migrated end-to-end, when touching its code:

- When rendering team members or join requests for display, collect `UserId` FKs and call `IUserService.GetByIdsAsync`, then stitch display data in memory. Do not add new `.Include(... => ... .User)` chains — lines 999, 1026, 1297, 1912, 2160 in `TeamService.cs` are the existing offenders; none of them are a pattern to copy.
- When a Team service method needs a User entity to populate a cached projection (lines 783, 929, 1278, 1830 in `TeamService.cs`), call `IUserService` rather than `_dbContext.Users.FindAsync()`. Same for the email-path read at line 1911 and the outbox guard at line 1955 — route through `IUserService`.
- When touching `GetCachedTeamsAsync` at `TeamService.cs:2154` or `TryUpdateCachedTeam` at line 2302, do not add new fields to the cached projection and do not add new `_cache.*` call sites. Both get replaced by a `TeamStore` + `CachingTeamService` decorator per §4–§5 — keep the current call small so the migration is mechanical.
- When `GetAdminTeamListAsync` at `TeamService.cs:2083` or `GetShiftsSummaryAsync` at `TeamPageService.cs:179` is touched, replace the `_dbContext.EventSettings` / `_dbContext.Rotas` reads with `IShiftManagementService` calls. Do not add new Shifts-domain reads from Teams services.
- ~~When `GetUserTeamResourcesAsync` at `TeamResourceService.cs:148–156` is touched, replace the in-query join on `_dbContext.TeamMembers` / `_dbContext.Teams` with an `ITeamService` call.~~ Done in PR for #540c: `Humans.Application.Services.Teams.TeamResourceService.GetUserTeamResourcesAsync` calls `ITeamService.GetUserTeamsAsync` and stitches in memory.
- When `EnqueueGoogleSyncOutboxEvent` at `TeamService.cs:1964` is touched, route the outbox write through an `IGoogleSyncOutbox` interface owned by Google Integration rather than writing `_dbContext.GoogleSyncOutboxEvents` directly from Teams.

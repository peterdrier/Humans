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

# Teams — Section Invariants

## Concepts

- A **Department** is a team with no parent.
- A **Sub-Team** is a team within a department. Only one level of nesting is allowed.
- **System teams** (Volunteers, Coordinators, Board, Asociados, Colaboradors) are managed automatically — members cannot be manually added or removed.
- A **Coordinator** is a team member assigned to the management role on a department. Sub-teams do not have coordinator roles.
- A **Team Page** is a Markdown-based public or member-facing page for a department, with optional calls to action.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Anyone (including anonymous) | Browse the team directory and view public team pages |
| Any active human | View team detail pages, request to join a team, leave a team, withdraw a pending request, view own memberships, browse the birthday calendar, search humans, view the roster and map |
| Coordinator | Manage members, approve/reject join requests, manage roles, edit the team page, and manage Google resources for their department (and its sub-teams) |
| TeamsAdmin | All coordinator capabilities on all teams. Create teams, edit team settings (name, slug, approval mode, parent, Google group prefix, budget flag), and link/unlink Google resources on all teams |
| Board | All TeamsAdmin capabilities. Additionally can delete (deactivate) teams |
| Admin | All Board capabilities. Additionally can execute Google sync actions, trigger system team sync, and view sync previews |

## Invariants

- A department can have zero or one role flagged as management (coordinator). If present, members assigned to it gain coordinator-level access over the whole department: member management, join request handling, role management, and team page editing.
- Sub-teams do not have coordinator roles.
- Members of sub-teams are also considered members of the department. They appear in the department's member roster and inherit the department's legal requirements and Google resource access (Drive folders, Groups).
- A human can be a member of multiple teams simultaneously.
- System team membership is managed exclusively by an automated sync job. Manual add/remove is blocked for system teams.
- Joining a team that requires approval creates a join request (Pending). The request must be approved by a coordinator or TeamsAdmin before membership is granted. Teams that do not require approval add the human immediately.
- Removing a member from a team also removes all their role assignments on that team.
- Each team has a unique slug used for URL routing. A custom slug can override the auto-generated one.
- A Google Group prefix, if set, provisions a @nobodies.team group for the team.
- Only departments (not sub-teams or system teams) can have public team pages.
- A **hidden team** (`IsHidden = true`) is invisible to non-admin users: it does not appear on profile cards, team listings, public pages, birthday team names, or the "My Teams" page. Only Admin, Board, and TeamsAdmin can see and manage hidden teams. Campaigns can still target hidden teams for code distribution.

## Negative Access Rules

- Regular humans **cannot** manage other teams' members, roles, or settings.
- Coordinators **cannot** create, delete, or edit team admin settings (name, approval mode, parent, Google prefix). They can only edit the team page and manage members/roles for their own department.
- TeamsAdmin **cannot** delete teams or execute sync actions.
- Nobody can manually add or remove members from system teams.

## Triggers

- When a join request is approved, a team membership record is created and the human is notified.
- When a member is removed from a team, all their role assignments for that team are also removed.
- When a member is added or removed from a team, Google resource sync events (Drive, Groups) are queued.
- When a coordinator role assignment changes, the Coordinators system team membership is recalculated for the affected human.
- The system team sync job runs hourly, reconciling system team membership based on role assignments and tier status.

## Cross-Section Dependencies

- **Google Integration**: Each team can have linked Google resources (Drive folders, Groups). Membership changes trigger sync outbox events.
- **Shifts**: Rotas belong to a department. Coordinator status on a department determines shift management access.
- **Budget**: Budget categories can be linked to a department. Coordinator status determines budget line item editing access.
- **Onboarding**: Volunteer activation adds the human to the Volunteers system team.
- **Governance**: Colaborador/Asociado approval or expiry adds/removes humans from the respective system teams.

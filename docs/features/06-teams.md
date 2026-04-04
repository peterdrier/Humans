# Teams & Working Groups

## Business Context

Nobodies Collective operates through self-organizing working groups (teams). Teams can be created for specific initiatives and managed by their members. Teams can optionally be organized into departments (parent-child hierarchy) for logical grouping. Three system-managed teams automatically track key organizational roles: all volunteers, all team coordinators, and board members.

## User Stories

### US-6.1: Browse Available Teams
**As a** member
**I want to** see all active teams in the organization
**So that** I can discover groups I might want to join

**Acceptance Criteria:**
- Page split into two sections: "My Teams" at top, "Other Teams" below
- "My Teams" shows teams the user belongs to (empty state: "You haven't joined any teams yet")
- "Other Teams" shows remaining teams with pagination
- Each team card shows name, description, member count, role badge, and system badge
- Shows if team requires approval to join
- Distinguishes system teams from user-created teams
- Separate `/Teams/My` page retained for Leave/Manage actions

### US-6.2: View Team Details
**As a** member
**I want to** view detailed information about a team
**So that** I can decide if I want to join

**Acceptance Criteria:**
- Shows team name, description, creation date
- Lists all current members with roles
- Shows coordinator(s) who manage the team
- Displays join requirements (open vs approval)
- Shows my current relationship with the team

### US-6.3: Join Team (Open)
**As a** member
**I want to** join a team that doesn't require approval
**So that** I can immediately participate

**Acceptance Criteria:**
- One-click join for open teams
- Immediately added as Member role
- Redirected to team page
- Google resources access granted

### US-6.4: Request to Join Team
**As a** member
**I want to** request to join a team that requires approval
**So that** the coordinators can review my request

**Acceptance Criteria:**
- Can submit request with optional message
- Request enters Pending status
- Cannot submit if already have pending request
- Can withdraw pending request

### US-6.5: Approve/Reject Join Requests
**As a** team coordinator or board member
**I want to** review and process join requests
**So that** appropriate members can join the team

**Acceptance Criteria:**
- View list of pending requests for my teams
- See requester info and their message
- Approve (adds member) or reject (with reason)
- Notification sent to requester

### US-6.6: Leave Team
**As a** team member
**I want to** leave a team I'm no longer participating in
**So that** I'm not listed as an active member

**Acceptance Criteria:**
- Can leave any user-created team
- Cannot leave system teams (auto-managed)
- Membership soft-deleted (LeftAt set)
- Google resources access revoked

### US-6.7: Manage Team Members
**As a** team coordinator or board member
**I want to** manage team membership and roles
**So that** the team is properly organized

**Acceptance Criteria:**
- View all team members
- Assign coordinator role via management role definition
- Remove member from team
- Cannot modify system team membership

### US-6.8: Create Team (Admin)
**As a** board member
**I want to** create new teams for organizational initiatives
**So that** members can organize around specific projects

**Acceptance Criteria:**
- Specify team name and description
- Choose if approval is required
- Optionally assign a parent team (department)
- System generates URL-friendly slug
- Team is immediately active

### US-6.9: View Public Team Page (Anonymous)
**As an** anonymous visitor
**I want to** view a team's public page
**So that** I can learn about the team before joining

**Acceptance Criteria:**
- Anonymous visitors can access `/Teams/{slug}` for public teams
- Shows team description, page content (markdown), and call-to-action buttons
- Shows coordinators with display name and avatar (no email or contact info)
- Regular members are hidden from anonymous visitors
- Non-public teams return 404 for anonymous visitors
- System teams and sub-teams cannot be made public

### US-6.10: Edit Team Page Content (Coordinator/Admin)
**As a** team coordinator, board member, or admin
**I want to** edit my team's public page content
**So that** I can provide information to potential volunteers

**Acceptance Criteria:**
- Edit page at `/Teams/{slug}/EditPage`
- Toggle public visibility (only for departments, not sub-teams or system teams)
- Write page content in markdown format
- Configure up to 3 call-to-action buttons (text + URL + style)
- Only one CTA can be styled as Primary
- Changes are audit-logged with `TeamPageContentUpdated`
- Edit Page link appears in team management sidebar

### US-6.11: Browse Public Team Directory (Anonymous)
**As an** anonymous visitor
**I want to** browse the public team directory
**So that** I can discover teams I might want to join

**Acceptance Criteria:**
- Anonymous visitors can access `/Teams` and see only public teams
- Shows team name, description snippet, and "Learn More" link
- No My Teams section, no admin buttons, no system teams shown
- Authenticated users see the full existing layout unchanged

## Data Model

### Team Entity
```
Team
├── Id: Guid
├── Name: string (256)
├── Description: string? (2000)
├── Slug: string (256) [unique, URL-friendly]
├── IsActive: bool
├── RequiresApproval: bool
├── SystemTeamType: SystemTeamType [enum]
├── ParentTeamId: Guid? (FK → Team, self-referencing)
├── GoogleGroupPrefix: string? (100) [email prefix before @nobodies.team]
├── CreatedAt: Instant
├── UpdatedAt: Instant
├── IsPublicPage: bool [default false, opt-in public visibility]
├── PageContent: string? (50000) [markdown content for public page]
├── PageContentUpdatedAt: Instant? [last edit timestamp]
├── PageContentUpdatedByUserId: Guid? [FK → User, who last edited]
├── CallsToAction: List<CallToAction> [JSONB, max 3 items]
├── Computed: IsSystemTeam (SystemTeamType != None)
├── Computed: GoogleGroupEmail (prefix + "@nobodies.team", or null)
└── Navigation: Members, JoinRequests, GoogleResources, ChildTeams, ParentTeam
```

### TeamMember Entity
```
TeamMember
├── Id: Guid
├── TeamId: Guid (FK → Team)
├── UserId: Guid (FK → User)
├── Role: TeamMemberRole [enum: Member, Coordinator]
├── JoinedAt: Instant
├── LeftAt: Instant? (null = active)
└── Computed: IsActive (LeftAt == null)
```

### TeamJoinRequest Entity
```
TeamJoinRequest
├── Id: Guid
├── TeamId: Guid (FK → Team)
├── UserId: Guid (FK → User)
├── Status: TeamJoinRequestStatus [enum]
├── Message: string? (2000)
├── RequestedAt: Instant
├── ResolvedAt: Instant?
├── ReviewedByUserId: Guid?
├── ReviewNotes: string? (2000)
└── Navigation: StateHistory
```

### Enums
```
TeamMemberRole:
  Member = 0
  Coordinator = 1

SystemTeamType:
  None = 0            // User-created team
  Volunteers = 1      // Auto: all with signed docs
  Coordinators = 2    // Auto: all team coordinators
  Board = 3           // Auto: active Board role

TeamJoinRequestStatus:
  Pending = 0
  Approved = 1
  Rejected = 2
  Withdrawn = 3

CallToActionStyle:
  Primary = 0
  Secondary = 1
```

### CallToAction Value Object
```
CallToAction (JSONB on Team.CallsToAction)
├── Text: string (100) [button label]
├── Url: string (512) [button link]
└── Style: CallToActionStyle [Primary or Secondary]
```

## Team Hierarchy (Departments)

Teams are either **departments** (top-level, no parent) or **sub-teams** (have a parent). A department may have child sub-teams.

### Terminology
- **Department**: Any user-created team that is NOT a sub-team. May or may not have children.
- **Sub-team**: A team with a `ParentTeamId` set. Always belongs to a department.

### Hierarchy Rules
- Only user-created teams can participate in hierarchy (system teams cannot be parents or children)
- Only single-level nesting (a sub-team cannot also be a parent)
- `ParentTeamId` is set during team creation or editing
- When a department becomes a sub-team, its coordinators are immediately synced out of the Coordinators system team. Management roles and assignments are preserved (coordinators become sub-team managers).

### Coordinator / Manager (IsManagement) Rules
- Both departments and sub-teams can have an `IsManagement` role
- At most one role per team can have `IsManagement = true`
- Department `IsManagement` role holders are **coordinators** (added to Coordinators system team, full department access)
- Sub-team `IsManagement` role holders are **managers** (scoped to their sub-team only, not added to Coordinators system team, no Google resource access, no budget access)
- Assigning a member to an `IsManagement` role sets their `TeamMember.Role = Coordinator`
- Unassigning from an `IsManagement` role demotes to Member (if no other management assignments remain)
- `IsManagement` cannot be toggled while members are assigned to the role
- `IsManagement` roles can be renamed and deleted (if no assignments)
- No roles are auto-created on team creation — admins add roles manually

### Permission Inheritance
- Department coordinators automatically have management permissions on all child sub-teams
- This includes: viewing/approving/rejecting join requests, managing members, editing sub-team pages
- Permission checks cascade upward: checking coordinator status on a sub-team also checks the parent department
- Both `TeamMember.Role == Coordinator` and `TeamRoleAssignment.IsManagement` paths are checked for consistency

### Google Resource Rollup
- Sub-team members are automatically included in the parent department's Google Group and Drive folder sync
- Effective membership = direct department members + all active child team members (deduplicated)
- Rollup is one-way: sub-team members get parent resources; parent members do NOT get sub-team resources
- When a user joins a sub-team, they are immediately added to parent department resources
- When a user leaves a sub-team, the reconciliation job removes them from parent resources (unless they remain in another sub-team or are a direct department member)
- The department detail page (`/Teams/{slug}`) shows all effective humans with source team badges

### Display
- Sub-team names display as "Department - SubTeam" on profile pills, team details, and MyTeams
- `/Teams` page groups cards into: My Teams, Departments, System Teams
- `/Teams/Summary` shows hierarchy with sub-teams indented below their parent

## System Teams

### Automatic Membership Sync

| Team | Auto-Add Trigger | Auto-Remove Trigger |
|------|------------------|---------------------|
| **Volunteers** | Approved + all required consents signed | Missing consent, suspended, or approval revoked |
| **Coordinators** | Become Coordinator of any team + team consents | No longer Coordinator anywhere |
| **Board** | Active "Board" RoleAssignment + team consents | RoleAssignment expires |

Volunteers team membership is the source of truth for "active volunteer" status. Both approval (`AdminController.ApproveVolunteer`) and consent completion (`ConsentController.Submit`) trigger an immediate single-user sync via `SyncVolunteersMembershipForUserAsync` -- the user doesn't wait for the scheduled job.

### System Team Properties
- `RequiresApproval = false` (auto-managed)
- Name, slug, active status, and parent team cannot be changed
- Description and Google Group prefix can be edited by admins
- Cannot be deleted
- Cannot manually join or leave
- Cannot change member roles

### Sync Job
```
SystemTeamSyncJob (scheduled hourly, currently disabled; also triggered inline):

  1. SyncVolunteersTeamAsync()
     - Get all users where IsApproved = true AND !IsSuspended
     - Filter to those with all required Volunteers-team consents
     - Add missing members, remove ineligible

  2. SyncCoordinatorsTeamAsync()
     - Get all users with TeamMember.Role = Coordinator (non-system teams)
     - Filter by Coordinators-team consents
     - Add missing members, remove ineligible

  3. SyncBoardTeamAsync()
     - Get all users with active Board RoleAssignment
     - Where ValidFrom <= now AND (ValidTo == null OR ValidTo > now)
     - Filter by Board-team consents
     - Add missing members, remove ineligible

  Single-user variants:
     - SyncVolunteersMembershipForUserAsync(userId)
     - SyncCoordinatorsMembershipForUserAsync(userId)
     - Called by AdminController (after approval) and ConsentController (after consent)
     - Evaluates one user without affecting others
```

### Access Gating

Volunteers team membership controls app access. Non-volunteers can only access Home, Profile, Consent, Account, and Application pages. Teams, Governance, and other member features require the `ActiveMember` claim, which is granted when the user is in the Volunteers team.

## Join Request State Machine

```
                  +---------+
                  | Pending |
                  +----+----+
                       |
        +--------------+--------------+
        |              |              |
   +----v----+   +----v----+   +----v----+
   | Approve |   | Reject  |   |Withdraw |
   +----+----+   +----+----+   +----+----+
        |              |              |
   +----v----+   +----v----+   +----v-----+
   |Approved |   |Rejected |   |Withdrawn |
   |         |   |         |   |          |
   |(+Member)|   |         |   |          |
   +---------+   +---------+   +----------+
```

## Approval Authority

### Who Can Approve Join Requests

| User Type | Can Approve |
|-----------|-------------|
| Team Coordinator | Own team only |
| Board Member | Any team |
| Regular Member | No |

### Authorization Check
```csharp
bool CanApprove(teamId, userId)
{
    // Board members can approve any team
    if (IsUserBoardMember(userId)) return true;

    // Coordinators can approve their own team
    return IsUserCoordinatorOfTeam(teamId, userId);
}
```

## TeamsAdmin Role

The `TeamsAdmin` role provides system-wide team management capabilities without requiring Board or Admin access.

### Capabilities
- Manage all teams (edit settings, approve join requests, assign coordinators)
- Configure `GoogleGroupPrefix` on teams
- View sync status at `/Teams/Sync`

### Limitations
- Cannot execute sync actions (Admin-only)
- Cannot access Admin area pages (Sync Settings, Configuration, etc.)
- Cannot assign roles

### Authorization
TeamsAdmin bypasses the `MembershipRequiredFilter` (like ConsentCoordinator and VolunteerCoordinator), so it works even if the user hasn't completed full volunteer onboarding.

## Google Group Lifecycle

Teams can be associated with a Google Group via the `GoogleGroupPrefix` property.

### Setting a Prefix
When a TeamsAdmin, Board, or Admin user sets `GoogleGroupPrefix` on a team (e.g., `"events"`):
1. The computed `GoogleGroupEmail` becomes `events@nobodies.team`
2. `EnsureTeamGroupAsync` is called to create or link the Google Group
3. The group is created with configured `GroupSettings` (from `GoogleWorkspace:Groups` in appsettings)
4. A `GoogleResource` record (type: Group) is created and linked to the team

### Clearing a Prefix
When `GoogleGroupPrefix` is cleared:
1. Any active Group resource for the team is deactivated (`IsActive = false`)
2. The Google Group itself is NOT deleted (soft unlink only)

### Changing a Prefix
When the prefix changes (e.g., `"events"` to `"events-team"`):
1. The old Group resource is deactivated
2. A new Google Group is created with the new email
3. A new `GoogleResource` record is linked

## Join Workflow

### Direct Join (No Approval)
```
User clicks "Join"
        |
        v
+-------------------+
| Create TeamMember |
| Role = Member     |
| JoinedAt = now    |
+---------+---------+
          |
          v
+-------------------+
| Sync Google       |
| Resources         |
+---------+---------+
          |
          v
    [User is member]
```

### Approval Join
```
User submits request
        |
        v
+-------------------+
| Create            |
| TeamJoinRequest   |
| Status = Pending  |
+---------+---------+
          |
    [Wait for review]
          |
          v
+---------------------+
| Coordinator/Board   |
| reviews request     |
+---------+-----------+
          |
    +-----+-----+
    |           |
 Approve     Reject
    |           |
    v           v
+--------+  +--------+
|+Member |  |Notify  |
|+Google |  |User    |
+--------+  +--------+
```

## Leave Workflow

```
User clicks "Leave"
        |
        v
+-------------------+
| Validate:         |
| - Not system team |
| - Is member       |
+---------+---------+
          |
          v
+-------------------+
| Set LeftAt = now  |
| (soft delete)     |
+---------+---------+
          |
          v
+-------------------+
| Revoke Google     |
| resource access   |
+---------+---------+
          |
          v
    [User removed]
```

## Google Integration

When membership changes:
- **Join**: `AddUserToTeamResourcesAsync(teamId, userId)`
- **Leave**: `RemoveUserFromTeamResourcesAsync(teamId, userId)`

Currently uses `StubGoogleSyncService` that logs actions.
Real implementation will manage Google Drive folder permissions.

## URL Structure

| Route | Description | Auth |
|-------|-------------|------|
| `/Teams` | Teams directory | AllowAnonymous (anonymous: public teams only) |
| `/Teams/{slug}` | Team details | AllowAnonymous (anonymous: public teams only, 404 for non-public) |
| `/Teams/{slug}/Join` | Join form | Authenticated |
| `/Teams/My` | User's teams | Authenticated |
| `/Teams/Birthdays` | Birthday calendar | Authenticated |
| `/Teams/Sync` | Sync status | TeamsAdmin, Board, Admin |
| `/Teams/{slug}/Members` | Manage members | Coordinator, Board, Admin, TeamsAdmin |
| `/Teams/{slug}/EditPage` | Edit public page content | Coordinator, Board, Admin, TeamsAdmin |
| `/Teams/Summary` | Team summary with resource columns | Board, Admin, TeamsAdmin |
| `/Teams/Create` | Create team form | Board, Admin, TeamsAdmin |
| `/Teams/{id}/Edit` | Edit team settings | Board, Admin, TeamsAdmin |

## Role Slots

Teams can define named role slots that members fill. Each role has a configurable number of slots with explicit priority levels (Critical, Important, Nice to Have). This helps teams track which positions are filled and where gaps exist.

### Key Concepts

- **Role Definition**: A named role on a team (e.g., "Social Media", "Designer") with a slot count and priority per slot
- **Role Assignment**: Links a team member to a specific slot in a role definition
- **IsManagement flag**: One role per team can be marked `IsManagement = true`. Assigning a member to this role sets their `TeamMember.Role = Coordinator`. On departments this grants coordinator access; on sub-teams this grants scoped manager access.
- **Auto-add**: Assigning a non-member to a role automatically adds them to the team
- **Roster Summary**: Cross-team view showing all slots with priority/status filtering

### Routes

- `GET /Teams/Roster` -- cross-team roster summary
- `GET /Teams/{slug}/Roles` -- role management page
- Role CRUD and assignment via POST actions on TeamAdminController

## Related Features

- [Authentication](01-authentication.md) - Board role enables team creation
- [Volunteer Status](05-volunteer-status.md) - Determines Volunteers team membership
- [Google Integration](07-google-integration.md) - Team resource provisioning
- [Background Jobs](08-background-jobs.md) - System team sync job

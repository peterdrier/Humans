# Teams & Working Groups

## Business Context

Nobodies Collective operates through self-organizing working groups (teams). Teams can be created for specific initiatives and managed by their members. Three system-managed teams automatically track key organizational roles: all volunteers, all team leaders (metaleads), and board members.

## User Stories

### US-6.1: Browse Available Teams
**As a** member
**I want to** see all active teams in the organization
**So that** I can discover groups I might want to join

**Acceptance Criteria:**
- Lists all active teams with name and description
- Shows member count for each team
- Indicates if user is already a member
- Shows if team requires approval to join
- Distinguishes system teams from user-created teams

### US-6.2: View Team Details
**As a** member
**I want to** view detailed information about a team
**So that** I can decide if I want to join

**Acceptance Criteria:**
- Shows team name, description, creation date
- Lists all current members with roles
- Shows metalead(s) who manage the team
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
**So that** the metaleads can review my request

**Acceptance Criteria:**
- Can submit request with optional message
- Request enters Pending status
- Cannot submit if already have pending request
- Can withdraw pending request

### US-6.5: Approve/Reject Join Requests
**As a** team metalead or board member
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
**As a** team metalead or board member
**I want to** manage team membership and roles
**So that** the team is properly organized

**Acceptance Criteria:**
- View all team members
- Promote member to metalead
- Demote metalead to member
- Remove member from team
- Cannot modify system team membership

### US-6.8: Create Team (Admin)
**As a** board member
**I want to** create new teams for organizational initiatives
**So that** members can organize around specific projects

**Acceptance Criteria:**
- Specify team name and description
- Choose if approval is required
- System generates URL-friendly slug
- Team is immediately active

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
├── CreatedAt: Instant
├── UpdatedAt: Instant
├── Computed: IsSystemTeam (SystemTeamType != None)
└── Navigation: Members, JoinRequests, GoogleResources
```

### TeamMember Entity
```
TeamMember
├── Id: Guid
├── TeamId: Guid (FK → Team)
├── UserId: Guid (FK → User)
├── Role: TeamMemberRole [enum: Member, Metalead]
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
  Metalead = 1

SystemTeamType:
  None = 0       // User-created team
  Volunteers = 1 // Auto: all with signed docs
  Metaleads = 2  // Auto: all team metaleads
  Board = 3      // Auto: active Board role

TeamJoinRequestStatus:
  Pending = 0
  Approved = 1
  Rejected = 2
  Withdrawn = 3
```

## System Teams

### Automatic Membership Sync

| Team | Auto-Add Trigger | Auto-Remove Trigger |
|------|------------------|---------------------|
| **Volunteers** | Approved + all required consents signed | Missing consent, suspended, or approval revoked |
| **Metaleads** | Become Metalead of any team + team consents | No longer Metalead anywhere |
| **Board** | Active "Board" RoleAssignment + team consents | RoleAssignment expires |

Volunteers team membership is the source of truth for "active volunteer" status. Both approval (`AdminController.ApproveVolunteer`) and consent completion (`ConsentController.Submit`) trigger an immediate single-user sync via `SyncVolunteersMembershipForUserAsync` — the user doesn't wait for the scheduled job.

### System Team Properties
- `RequiresApproval = false` (auto-managed)
- Cannot be edited or deleted
- Cannot manually join or leave
- Cannot change member roles

### Sync Job
```
SystemTeamSyncJob (scheduled hourly, currently disabled; also triggered inline):

  1. SyncVolunteersTeamAsync()
     - Get all users where IsApproved = true AND !IsSuspended
     - Filter to those with all required Volunteers-team consents
     - Add missing members, remove ineligible

  2. SyncMetaleadsTeamAsync()
     - Get all users with TeamMember.Role = Metalead (non-system teams)
     - Filter by Metaleads-team consents
     - Add missing members, remove ineligible

  3. SyncBoardTeamAsync()
     - Get all users with active Board RoleAssignment
     - Where ValidFrom <= now AND (ValidTo == null OR ValidTo > now)
     - Filter by Board-team consents
     - Add missing members, remove ineligible

  Single-user variant: SyncVolunteersMembershipForUserAsync(userId)
     - Called by AdminController (after approval) and ConsentController (after consent)
     - Evaluates one user without affecting others
```

### Access Gating

Volunteers team membership controls app access. Non-volunteers can only access Home, Profile, Consent, Account, and Application pages. Teams, Governance, and other member features require the `ActiveMember` claim, which is granted when the user is in the Volunteers team.

## Join Request State Machine

```
                  ┌─────────┐
                  │ Pending │
                  └────┬────┘
                       │
        ┌──────────────┼──────────────┐
        │              │              │
   ┌────▼────┐   ┌────▼────┐   ┌────▼────┐
   │ Approve │   │ Reject  │   │Withdraw │
   └────┬────┘   └────┬────┘   └────┬────┘
        │              │              │
   ┌────▼────┐   ┌────▼────┐   ┌────▼─────┐
   │Approved │   │Rejected │   │Withdrawn │
   │         │   │         │   │          │
   │(+Member)│   │         │   │          │
   └─────────┘   └─────────┘   └──────────┘
```

## Approval Authority

### Who Can Approve Join Requests

| User Type | Can Approve |
|-----------|-------------|
| Team Metalead | Own team only |
| Board Member | Any team |
| Regular Member | No |

### Authorization Check
```csharp
bool CanApprove(teamId, userId)
{
    // Board members can approve any team
    if (IsUserBoardMember(userId)) return true;

    // Metaleads can approve their own team
    return IsUserMetaleadOfTeam(teamId, userId);
}
```

## Join Workflow

### Direct Join (No Approval)
```
User clicks "Join"
        │
        ▼
┌───────────────────┐
│ Create TeamMember │
│ Role = Member     │
│ JoinedAt = now    │
└─────────┬─────────┘
          │
          ▼
┌───────────────────┐
│ Sync Google       │
│ Resources         │
└─────────┬─────────┘
          │
          ▼
    [User is member]
```

### Approval Join
```
User submits request
        │
        ▼
┌───────────────────┐
│ Create            │
│ TeamJoinRequest   │
│ Status = Pending  │
└─────────┬─────────┘
          │
    [Wait for review]
          │
          ▼
┌───────────────────┐
│ Metalead/Board    │
│ reviews request   │
└─────────┬─────────┘
          │
    ┌─────┴─────┐
    │           │
 Approve     Reject
    │           │
    ▼           ▼
┌────────┐  ┌────────┐
│+Member │  │Notify  │
│+Google │  │User    │
└────────┘  └────────┘
```

## Leave Workflow

```
User clicks "Leave"
        │
        ▼
┌───────────────────┐
│ Validate:         │
│ - Not system team │
│ - Is member       │
└─────────┬─────────┘
          │
          ▼
┌───────────────────┐
│ Set LeftAt = now  │
│ (soft delete)     │
└─────────┬─────────┘
          │
          ▼
┌───────────────────┐
│ Revoke Google     │
│ resource access   │
└─────────┬─────────┘
          │
          ▼
    [User removed]
```

## Google Integration

When membership changes:
- **Join**: `AddUserToTeamResourcesAsync(teamId, userId)`
- **Leave**: `RemoveUserFromTeamResourcesAsync(teamId, userId)`

Currently uses `StubGoogleSyncService` that logs actions.
Real implementation will manage Google Drive folder permissions.

## URL Structure

| Route | Description |
|-------|-------------|
| `/Teams` | All teams list |
| `/Teams/{slug}` | Team details |
| `/Teams/{slug}/Join` | Join form |
| `/Teams/My` | User's teams |
| `/Teams/{slug}/Admin/Requests` | Pending requests |
| `/Teams/{slug}/Admin/Members` | Manage members |
| `/Admin/Teams` | Admin team management |
| `/Admin/Teams/Create` | Create team form |
| `/Admin/Teams/{id}/Edit` | Edit team |

## Related Features

- [Authentication](01-authentication.md) - Board role enables team creation
- [Volunteer Status](05-volunteer-status.md) - Determines Volunteers team membership
- [Google Integration](07-google-integration.md) - Team resource provisioning
- [Background Jobs](08-background-jobs.md) - System team sync job

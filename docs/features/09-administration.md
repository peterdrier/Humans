# Administration

## Business Context

System administrators need comprehensive tools to manage members, review applications, oversee teams, and maintain organizational compliance. The admin interface provides dashboards and management screens for all key operations.

## User Stories

### US-9.1: View Admin Dashboard
**As an** administrator
**I want to** see an overview of system status
**So that** I can quickly identify items needing attention

**Acceptance Criteria:**
- Total member count
- Active vs inactive members
- Pending applications count
- Pending consent reminders
- Quick action links
- System health indicators

### US-9.2: Search and View Members
**As an** administrator
**I want to** search for and view member details
**So that** I can assist with member issues

**Acceptance Criteria:**
- Search by email or display name
- Paginated results
- Click through to member detail
- View full profile including legal name
- See application history and consent status

### US-9.3: Suspend/Unsuspend Members
**As an** administrator
**I want to** suspend or unsuspend member accounts
**So that** I can enforce organizational rules

**Acceptance Criteria:**
- Suspend with required notes
- Unsuspend clears suspension
- Status updates immediately
- Action logged for audit
- Notification sent to member

### US-9.4: Review Applications
**As an** administrator
**I want to** review and process Asociado applications
**So that** qualified applicants can join

**Acceptance Criteria:**
- Filter by status
- View full application details
- Start review, approve, reject, request info
- Add notes visible to applicant
- See complete state history

### US-9.5: Manage Teams
**As an** administrator
**I want to** create and manage teams
**So that** the organization has appropriate working groups

**Acceptance Criteria:**
- View all teams (user-created and system)
- Create new teams
- Edit team settings (non-system only)
- Deactivate teams
- View member counts and pending requests

## Volunteer Approval

### US-9.7: Approve Pending Volunteers
**As a** Board member
**I want to** approve new volunteers before they receive organizational access
**So that** we can vet who joins the Volunteers team and gets Google Workspace resources

**Acceptance Criteria:**
- New profiles default to `IsApproved = false`
- User sees "Pending Approval" alert on their profile page
- Dashboard shows count of pending volunteers
- Board can filter member list to show only pending volunteers (`/Admin/Members?filter=pending`)
- Board can approve a volunteer from member detail page
- `SystemTeamSyncJob` only enrolls approved, non-suspended profiles in Volunteers team
- Approval is logged for audit

### Volunteer Approval Workflow
```
New User Signs In
    │
    ▼
Creates Profile (IsApproved = false)
    │
    ▼
Signs Required Consents (can happen before or after approval)
    │
    ▼
Sees "Pending Approval" on dashboard
    │                                    Board sees pending count
    │                                    on Admin Dashboard
    ▼                                         │
[Waits for Board] ◄─────────────────── Board approves
    │
    ▼
IsApproved = true
    │
    ▼
SyncVolunteersMembershipForUserAsync (immediate, not waiting for scheduled job)
    │
    ▼
If approved + all consents signed → Enrolled in Volunteers team
    │
    ▼
ActiveMember claim granted → full app access + Google Workspace
```

Approval and consent completion both trigger `SyncVolunteersMembershipForUserAsync`. Whichever happens last causes the user to be added to the Volunteers team immediately — there is no waiting for the hourly `SystemTeamSyncJob`.

### Data Model
- `Profile.IsApproved` (bool, default false): Must be true for `SystemTeamSyncJob` to enroll the user in Volunteers team

## Admin Controller Routes

| Route | Action | Description |
|-------|--------|-------------|
| `/Admin` | Index | Dashboard |
| `/Admin/Members` | Members | Member list with search |
| `/Admin/Members/{id}` | MemberDetail | Individual member view |
| `/Admin/Members/{id}/Approve` | ApproveVolunteer | POST: Approve volunteer |
| `/Admin/Members/{id}/Suspend` | SuspendMember | POST: Suspend member |
| `/Admin/Members/{id}/Unsuspend` | UnsuspendMember | POST: Unsuspend member |
| `/Admin/Applications` | Applications | Application list |
| `/Admin/Applications/{id}` | ApplicationDetail | Application review |
| `/Admin/Applications/{id}/Action` | ApplicationAction | POST: Process application |
| `/Admin/Teams` | Teams | Team list |
| `/Admin/Teams/Create` | CreateTeam | New team form |
| `/Admin/Teams/{id}/Edit` | EditTeam | Edit team form |
| `/Admin/Teams/{id}/Delete` | DeleteTeam | POST: Deactivate team |
| `/Admin/Roles` | Roles | Role assignment management |
| `/Admin/LegalDocuments` | LegalDocuments | Legal document management |
| `/Admin/GoogleSync` | GoogleSync | Google resource sync status |
| `/Admin/AuditLog` | AuditLog | Global audit log |
| `/Admin/Configuration` | Configuration | Configuration status page |
| `/Admin/SyncSystemTeams` | SyncSystemTeams | POST: Trigger system team sync |

## Dashboard Metrics

### AdminDashboardViewModel
```csharp
public class AdminDashboardViewModel
{
    public int TotalMembers { get; set; }
    public int ActiveMembers { get; set; }
    public int PendingVolunteers { get; set; }
    public int PendingApplications { get; set; }
    public int PendingConsents { get; set; }
    public List<RecentActivityViewModel> RecentActivity { get; set; }
}
```

### Dashboard Cards
| Metric | Query | Color |
|--------|-------|-------|
| Total Members | `Users.Count()` | Default |
| Active Members | `Profiles.Count(p => !p.IsSuspended)` | Green |
| Pending Volunteers | `Profiles.Count(p => !p.IsApproved && !p.IsSuspended)` | Yellow (bordered) |
| Pending Apps | `Applications.Count(Submitted OR UnderReview)` | Yellow |
| Pending Consents | `Users with missing consents` | Blue |

## Member Management

### Member List View
```
┌─────────────────────────────────────────────────────────┐
│ Members                               [Search: ____]    │
├─────────────────────────────────────────────────────────┤
│ Photo │ Name           │ Email          │ Status │ View │
│ ───── │ ────────────── │ ────────────── │ ────── │ ──── │
│ [img] │ Alice Johnson  │ alice@...      │ Active │ [→]  │
│ [img] │ Bob Smith      │ bob@...        │ Inactive│ [→]  │
│ ...   │ ...            │ ...            │ ...    │ ...  │
├─────────────────────────────────────────────────────────┤
│ Showing 1-20 of 156                    [< 1 2 3 4 >]    │
└─────────────────────────────────────────────────────────┘
```

### Member Detail View
```
┌─────────────────────────────────────────────────────────┐
│ [Photo]  Alice Johnson                                  │
│          alice@nobodies.es                              │
│          Member since Jan 15, 2024                      │
├─────────────────────────────────────────────────────────┤
│ PROFILE INFORMATION                                     │
│ Legal Name: Alice Marie Johnson                         │
│ Phone: +34 612 345 678                                  │
│ Location: Madrid, ES                                    │
│ Status: [Active]                                        │
├─────────────────────────────────────────────────────────┤
│ APPLICATIONS (2)                                        │
│ • Approved - Jan 20, 2024                              │
│ • Withdrawn - Jan 10, 2024                             │
├─────────────────────────────────────────────────────────┤
│ CONSENTS (3/3)                                          │
│ ✓ Privacy Policy                                        │
│ ✓ Terms and Conditions                                  │
│ ✓ Code of Conduct                                       │
├─────────────────────────────────────────────────────────┤
│ ADMIN ACTIONS                                           │
│ [Suspend Member]  Notes: [_______________]              │
└─────────────────────────────────────────────────────────┘
```

## Application Management

### Application List
- **Default filter**: Pending (Submitted + UnderReview)
- **Sort**: By submission date (oldest first)
- **Columns**: Applicant, Email, Status, Submitted, Motivation preview

### Application Detail
```
┌─────────────────────────────────────────────────────────┐
│ Application #abc123                                     │
│ Status: [Under Review]                                  │
├─────────────────────────────────────────────────────────┤
│ APPLICANT                                               │
│ [Photo] Bob Smith                                       │
│         bob@email.com                                   │
├─────────────────────────────────────────────────────────┤
│ MOTIVATION                                              │
│ "I want to join because..."                             │
│                                                         │
│ ADDITIONAL INFO                                         │
│ "I have experience with..."                             │
├─────────────────────────────────────────────────────────┤
│ TIMELINE                                                │
│ • Submitted: Jan 15, 2024 10:30                        │
│ • Review started: Jan 16, 2024 by Admin                │
├─────────────────────────────────────────────────────────┤
│ ACTIONS                     Notes: [_______________]    │
│ [Approve] [Reject] [Request More Info]                  │
└─────────────────────────────────────────────────────────┘
```

### Application Actions

| Action | From Status | Result | Notes Required |
|--------|-------------|--------|----------------|
| Start Review | Submitted | UnderReview | No |
| Approve | UnderReview | Approved | Optional |
| Reject | UnderReview | Rejected | Yes |
| Request Info | UnderReview | Submitted | Yes |

## Team Management

### Team List View
```
┌─────────────────────────────────────────────────────────┐
│ Teams                                    [Create Team]  │
├─────────────────────────────────────────────────────────┤
│ Name      │ Type      │ Members │ Pending │ Actions    │
│ ───────── │ ───────── │ ─────── │ ─────── │ ────────── │
│ Volunteers│ System    │ 45      │ 0       │ (managed)  │
│ Metaleads │ System    │ 8       │ 0       │ (managed)  │
│ Board     │ System    │ 5       │ 0       │ (managed)  │
│ Events    │ Approval  │ 12      │ 3       │ [Edit][Del]│
│ Tech      │ Open      │ 7       │ 0       │ [Edit][Del]│
└─────────────────────────────────────────────────────────┘
```

### Create/Edit Team Form
```
┌─────────────────────────────────────────────────────────┐
│ Create Team                                             │
├─────────────────────────────────────────────────────────┤
│ Name:        [___________________________]              │
│                                                         │
│ Description: [___________________________]              │
│              [___________________________]              │
│                                                         │
│ [✓] Require approval to join                           │
│                                                         │
│ [Create Team]  [Cancel]                                 │
└─────────────────────────────────────────────────────────┘
```

## Authorization

### Role Separation: Board vs Admin

The admin area uses two distinct roles with different responsibilities:

| Role | Purpose | Can Access Admin Area | Can Access Hangfire | Can Assign Roles |
|------|---------|----------------------|--------------------|--------------------|
| **Board** | Governance (members, applications, teams) | Yes | No | Board, Metalead |
| **Admin** | Tech ops (Hangfire, health, metrics) | Yes | Yes | Admin, Board, Metalead |

A user can hold both roles simultaneously. Admin is a superset for role assignment purposes.

### How It Works

Role claims are synced from the `RoleAssignment` table to Identity claims via `RoleAssignmentClaimsTransformation` (an `IClaimsTransformation`). This makes `User.IsInRole()` and `[Authorize(Roles = "...")]` work correctly based on temporal role assignments.

```csharp
[Authorize(Roles = "Board,Admin")]
[Route("Admin")]
public class AdminController : Controller
```

### Role Assignment Authorization
- **Admin** can assign/end any role: Admin, Board, Metalead
- **Board** (non-Admin) can assign/end: Board, Metalead only
- Attempting to assign a role outside your permissions returns 403 Forbidden

### Hangfire Dashboard
- Restricted to **Admin** role only via `HangfireAuthorizationFilter`

### Role Assignment
- Configured via `RoleAssignment` with temporal validity (ValidFrom/ValidTo)
- Created by existing Admin or Board member (within their permissions)
- Bootstrap: First Admin must be created directly in the database

## Audit Logging

All admin actions are logged via Serilog:
```csharp
_logger.LogInformation(
    "Admin {AdminId} {Action} member {MemberId}",
    currentUser.Id, "suspended", memberId);
```

### Logged Actions
- Member suspension/unsuspension
- Application status changes
- Team creation/modification
- Role assignments

## Quick Actions (Dashboard)

| Action | Link | Badge |
|--------|------|-------|
| Review Pending Volunteers | `/Admin/Members?filter=pending` | Pending count |
| Review Applications | `/Admin/Applications` | Pending count |
| Manage Members | `/Admin/Members` | - |
| Manage Teams | `/Admin/Teams` | - |
| Manage Roles | `/Admin/Roles` | - |
| Legal Documents | `/Admin/LegalDocuments` | - |
| Google Sync Status | `/Admin/GoogleSync` | - |
| Audit Log | `/Admin/AuditLog` | - |
| Configuration Status | `/Admin/Configuration` | - |
| Background Jobs | `/hangfire` | - |

## System Health

### Dashboard Indicators
- **Database Connection**: Green if responsive
- **Background Jobs**: Green if Hangfire server active
- **Health Check URL**: `/health/ready`
- **Sync System Teams**: Button to manually trigger `SystemTeamSyncJob.ExecuteAsync()`, which recalculates membership for Volunteers, Metaleads, and Board teams. Useful for fixing users who were approved before the immediate sync was implemented.

### Prometheus Metrics
- Available at `/metrics`
- Scraped by monitoring infrastructure

## Related Features

- [Authentication](01-authentication.md) - Admin role authorization
- [Asociado Applications](03-asociado-applications.md) - Voting member application review
- [Teams](06-teams.md) - Team management
- [Background Jobs](08-background-jobs.md) - Hangfire dashboard

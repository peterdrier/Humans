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
- Board can filter member list to show only pending volunteers (`/Admin/Humans?filter=pending`)
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

## Controller Routes

### BoardController (`/Board/`) — Board, Admin

`BoardController` is now a slim dashboard-only controller. Most human management and Google sync operations have been extracted to `ProfileController` and `GoogleController`.

| Route | Action | Description |
|-------|--------|-------------|
| `/Board` | Index | Dashboard with stats and recent audit log |
| `/Board/AuditLog` | AuditLog | Global audit log (paginated, filterable) |

### ProfileController (`/Profile/`) — Human management actions

Human management (previously on `BoardController`) is now on `ProfileController` under the `/Profile/Admin` sub-path, accessible to HumanAdmin, Board, and Admin roles.

| Route | Action | Roles |
|-------|--------|-------|
| `/Profile/Admin` | AdminList | HumanAdmin, Board, Admin — member list |
| `/Profile/{id}/Admin` | AdminDetail | HumanAdmin, Board, Admin — member detail |
| `/Profile/{id}/Admin/Outbox` | AdminOutbox | HumanAdmin, Board, Admin — view email outbox |
| `/Profile/{id}/Admin/Suspend` | Suspend | POST: HumanAdmin, Board, Admin |
| `/Profile/{id}/Admin/Unsuspend` | Unsuspend | POST: HumanAdmin, Board, Admin |
| `/Profile/{id}/Admin/Approve` | Approve | POST: HumanAdmin, Board, Admin |
| `/Profile/{id}/Admin/Reject` | Reject | POST: HumanAdmin, Board, Admin |
| `/Profile/{id}/Admin/Roles/Add` | AddRole | GET/POST: HumanAdmin, Board, Admin |
| `/Profile/{id}/Admin/Roles/{roleId}/End` | EndRole | POST: HumanAdmin, Board, Admin |

### ContactsController (`/Contacts/`) — HumanAdmin, Board, Admin

External contacts (not yet registered users) are managed at `/Contacts/`, NOT `/Human/Admin/Contacts/` as the spec may suggest.

| Route | Action | Description |
|-------|--------|-------------|
| `/Contacts` | Index | Contacts list |
| `/Contacts/{id}` | Detail | Contact detail |
| `/Contacts/Create` | Create | GET/POST: Create contact |

### GoogleController (`/Google/`) — Admin (mostly)

Google sync, settings, account provisioning, and audit routes have been extracted from `AdminController` and `BoardController` into `GoogleController`. These were previously documented as `/Admin/*` or `/Board/*` routes.

| Route | Auth | Description |
|-------|------|-------------|
| `/Google/SyncSettings` | Admin | GET/POST: Per-service sync mode configuration |
| `/Google/SyncResults` | Admin | GET: Results of last sync check |
| `/Google/CheckGroupSettings` | Admin | POST: Check Google Group settings for drift |
| `/Google/GroupSettingsResults` | Admin | GET: Display group settings drift results |
| `/Google/RemediateGroupSettings` | Admin | POST: Fix settings drift on one group |
| `/Google/RemediateAllGroupSettings` | Admin | POST: Fix settings drift on all groups |
| `/Google/AllGroups` | Admin | GET: All Google groups |
| `/Google/Sync` | TeamsAdmin, Board, Admin | GET: Sync status page (tabbed: Drive/Groups) |
| `/Google/Sync/Preview/{resourceType}` | TeamsAdmin, Board, Admin | GET: AJAX preview drift |
| `/Google/Sync/Execute/{resourceId}` | Admin | POST: Sync one resource |
| `/Google/Sync/ExecuteAll/{resourceType}` | Admin | POST: Sync all of a type |
| `/Google/AuditLog/CheckDriveActivity` | Board, Admin | POST: Manual Drive Activity check |
| `/Google/Sync/Resource/{id}/Audit` | Board, Admin | GET: Per-resource sync audit log |
| `/Google/Human/{id}/SyncAudit` | HumanAdmin, Admin | GET: Per-user sync audit log |
| `/Google/Human/{id}/ProvisionEmail` | Admin | POST: Provision @nobodies.team email |
| `/Google/Accounts` | Admin | GET: All @nobodies.team accounts |
| `/Google/Accounts/Provision` | Admin | POST: Provision account |
| `/Google/Accounts/Suspend` | Admin | POST: Suspend account |
| `/Google/Accounts/Reactivate` | Admin | POST: Reactivate account |
| `/Google/Accounts/ResetPassword` | Admin | POST: Reset password |
| `/Google/Accounts/Link` | Admin | POST: Link existing account |
| `/Google/LinkGroupToTeam` | Admin | POST: Link group to team |
| `/Google/CheckEmailMismatches` | Admin | POST: Check email mismatches |
| `/Google/EmailBackfillReview` | HumanAdmin, Admin | GET: Review email backfill |
| `/Google/ApplyEmailBackfill` | Admin | POST: Apply email backfill |

### AdminController (`/Admin/`) — Admin only

`AdminController` is now a slim technical-operations controller.

| Route | Action | Description |
|-------|--------|-------------|
| `/Admin` | Index | Admin dashboard |
| `/Admin/Humans/{id}/Purge` | PurgeHuman | POST: Dev/QA user purge (non-production) |
| `/Admin/Configuration` | Configuration | Configuration status page |
| `/Admin/Logs` | Logs | View recent log entries |
| `/Admin/DbStats` | DbStats | Database query statistics |
| `/Admin/DbStats/Reset` | ResetDbStats | POST: Reset query statistics |
| `/Admin/CacheStats` | CacheStats | Cache hit/miss statistics per key type |
| `/Admin/CacheStats/Reset` | ResetCacheStats | POST: Reset cache statistics |
| `/Admin/DbVersion` | DbVersion | Database migration version |
| `/Admin/ClearHangfireLocks` | ClearHangfireLocks | POST: Admin-only lock cleanup |

### AdminDuplicateAccountsController (`/Admin/DuplicateAccounts/`) — Admin only

Duplicate account detection and resolution (added in Controller Consolidation PR).

| Route | Action | Description |
|-------|--------|-------------|
| `/Admin/DuplicateAccounts` | Index | List duplicate account candidates |
| `/Admin/DuplicateAccounts/Detail?userId1=&userId2=` | Detail | Review a specific duplicate candidate |

### AdminMergeController (`/Admin/MergeRequests/`) — Admin only

| Route | Action | Description |
|-------|--------|-------------|
| `/Admin/MergeRequests` | Index | List pending merge requests |
| `/Admin/MergeRequests/{id}` | Detail | Merge request detail |
| `/Admin/MergeRequests/{id}/Accept` | Accept | Accept a merge request |
| `/Admin/MergeRequests/{id}/Reject` | Reject | Reject a merge request |

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
| Pending Apps | `Applications.Count(Submitted)` | Yellow |
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
- **Default filter**: Pending (Submitted)
- **Sort**: By submission date (oldest first)
- **Columns**: Applicant, Email, Status, Submitted, Motivation preview

### Application Detail
```
┌─────────────────────────────────────────────────────────┐
│ Application #abc123                                     │
│ Status: [Submitted]                                     │
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
├─────────────────────────────────────────────────────────┤
│ ACTIONS                     Notes: [_______________]    │
│ [Approve] [Reject]                                      │
└─────────────────────────────────────────────────────────┘
```

### Application Actions

| Action | From Status | Result | Notes Required |
|--------|-------------|--------|----------------|
| Approve | Submitted | Approved | Optional (DecisionNote) |
| Reject | Submitted | Rejected | Yes (DecisionNote required) |

## Team Management

### Team List View
```
┌─────────────────────────────────────────────────────────┐
│ Teams                                    [Create Team]  │
├─────────────────────────────────────────────────────────┤
│ Name      │ Type      │ Members │ Pending │ Actions    │
│ ───────── │ ───────── │ ─────── │ ─────── │ ────────── │
│ Volunteers│ System    │ 45      │ 0       │ (managed)  │
│ Coordinators│ System  │ 8       │ 0       │ (managed)  │
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

The system uses two distinct controller areas, each with its own route prefix:

| Area | Route prefix | Controller | Authorized roles |
|------|-------------|------------|-----------------|
| **Board** | `/Board/` | `BoardController` | Board, Admin |
| **Admin** | `/Admin/` | `AdminController` | Admin only (except where noted) |

**Board area** (`/Board/`): Governance operations — member management, applications, teams, roles, audit log. Accessible to Board and Admin roles.

**Admin area** (`/Admin/`): Technical operations — configuration, sync settings, Hangfire, email preview, system team sync, Google sync (legacy). Accessible to Admin role only.

A user can hold both roles simultaneously. Admin is a superset for role assignment purposes.

### Additional Roles

All roles are defined in `RoleNames` constants and use temporal `RoleAssignment` records (same as Board and Admin).

| Role | Purpose |
|------|---------|
| **HumanAdmin** | View human admin pages, approve/suspend/reject humans, provision @nobodies.team accounts, manage role assignments. Does NOT include Board or Admin capabilities. |
| **TeamsAdmin** | System-wide team management (edit teams, approve joins, assign coordinators, configure Google Group prefixes). Can view sync status at `/Google/Sync` but cannot execute sync actions. |
| **CampAdmin** | Manage camps, approve/reject season registrations, configure camp settings system-wide. |
| **TicketAdmin** | Manage ticket vendor integration, trigger syncs, generate discount codes, export ticket data. |
| **NoInfoAdmin** | Approve/voluntell shift signups (cannot create/edit shifts). Access to volunteer event profile medical data. |
| **FeedbackAdmin** | View all feedback reports, respond to reporters, manage feedback status, link GitHub issues. |
| **FinanceAdmin** | Manage budgets, budget years, groups, categories, and line items. Full Finance section access. |
| **ConsentCoordinator** | Safety checks on new humans during onboarding. Can clear or flag consent checks. Bypasses MembershipRequiredFilter. |
| **VolunteerCoordinator** | Read-only access to onboarding review queue. Bypasses MembershipRequiredFilter. |

### Authorization Foundation

The app uses both role-based `[Authorize(Roles = "...")]` attributes (on controllers) and policy-based `[Authorize(Policy = "...")]` attributes (on views and tag helpers via `PolicyNames` constants). Policies are defined in `AuthorizationPolicyExtensions` and registered in `Program.cs`. Policy names are in `PolicyNames` constants.

Role claims are synced from the `RoleAssignment` table to Identity claims via `RoleAssignmentClaimsTransformation` (an `IClaimsTransformation`). This makes `User.IsInRole()` and `[Authorize(Roles = "...")]` work correctly based on temporal role assignments.

### Role Assignment Authorization
- **Admin** can assign/end any role
- **Board** (non-Admin) can assign/end: Board, ConsentCoordinator, VolunteerCoordinator (not Admin)
- **HumanAdmin** actions use `HumanAdminBoardOrAdmin` role group
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

### Board Dashboard (`/Board`)

| Action | Link | Badge |
|--------|------|-------|
| Manage Humans | `/Profile/Admin` | - |
| Audit Log | `/Board/AuditLog` | - |
| Sync Status | `/Google/Sync` | - |

### Admin Dashboard (`/Admin`)

| Action | Link | Badge |
|--------|------|-------|
| Sync Settings | `/Google/SyncSettings` | - |
| Configuration Status | `/Admin/Configuration` | - |
| Background Jobs | `/hangfire` | - |
| Check Group Settings | `/Google/CheckGroupSettings` | - |

## System Health

### Dashboard Indicators
- **Database Connection**: Green if responsive
- **Background Jobs**: Green if Hangfire server active
- **Health Check URL**: `/health/ready`
- **Sync System Teams**: Button to manually trigger `SystemTeamSyncJob.ExecuteAsync()`, which recalculates membership for Volunteers, Coordinators, and Board teams. Useful for fixing users who were approved before the immediate sync was implemented.

### Prometheus Metrics
- Available at `/metrics`
- Scraped by monitoring infrastructure

## Related Features

- [Authentication](01-authentication.md) - Admin role authorization
- [Asociado Applications](03-asociado-applications.md) - Voting member application review
- [Teams](06-teams.md) - Team management
- [Background Jobs](08-background-jobs.md) - Hangfire dashboard

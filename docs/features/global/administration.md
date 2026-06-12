<!-- freshness:triggers
  src/Humans.Web/Controllers/AdminController.cs
  src/Humans.Web/Controllers/UsersAdminAccountMergesController.cs
  src/Humans.Web/Controllers/UsersAdminController.cs
  src/Humans.Web/Controllers/BoardController.cs
  src/Humans.Web/Controllers/ProfileController.cs
  src/Humans.Web/Controllers/GoogleController.cs
  src/Humans.Web/Authorization/PolicyNames.cs
  src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs
  src/Humans.Web/Authorization/RoleAssignmentClaimsTransformation.cs
  src/Humans.Web/Views/Admin/**
  src/Humans.Web/Views/Board/**
  src/Humans.Domain/Constants/RoleNames.cs
  src/Humans.Domain/Constants/RoleGroups.cs
-->
<!-- freshness:flag-on-change
  Admin/Board/Profile/Google route tables, role catalog, dashboard metrics, and authorization split between Admin and Board areas — review when admin-area controllers, role names, or authorization policies change.
-->

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

## UserState and Volunteers Provisioning

Human app access is `UserState == Active`, set when the human enters their legal name. There is no manual "approve volunteer" admin action. The Volunteers system team is a Google Workspace provisioning group reconciled from name + consents by `SystemTeamSyncJob`; consent-review status remains an audit/safety workflow.

## Controller Routes

### BoardController (`/Board/`) — Board, Admin

`BoardController` is now a slim dashboard-only controller. Most human management and Google sync operations have been extracted to `UsersAdminController` and `GoogleController`.

| Route | Action | Description |
|-------|--------|-------------|
| `/Board` | Index | Dashboard with stats and recent audit log |
| `/AuditLog` | AuditLog | Global audit log (paginated, filterable) — *(moved to AuditLogController — see PR #499)* |

### UsersAdminController (`/Users/Admin`) — Human management actions

Human management is now on `UsersAdminController` under `/Users/Admin`, accessible to HumanAdmin, Board, and Admin roles.

| Route | Action | Roles |
|-------|--------|-------|
| `/Users/Admin` | AdminList | HumanAdmin, Board, Admin — member list |
| `/Users/Admin/{id}` | AdminDetail | HumanAdmin, Board, Admin — member detail |
| `/Users/Admin/{id}/Outbox` | AdminOutbox | HumanAdmin, Board, Admin — view email outbox |
| `/Users/Admin/{id}/Suspend` | Suspend | POST: HumanAdmin, Board, Admin |
| `/Users/Admin/{id}/Unsuspend` | Unsuspend | POST: HumanAdmin, Board, Admin |
| `/Users/Admin/{id}/Reject` | Reject | POST: HumanAdmin, Board, Admin |
| `/Users/Admin/{id}/Roles/Add` | AddRole | GET/POST: HumanAdmin, Board, Admin |
| `/Users/Admin/{id}/Roles/{roleId}/End` | EndRole | POST: HumanAdmin, Board, Admin |

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
| `/AuditLog/CheckDriveActivity` | Board, Admin | POST: Manual Drive Activity check |
| `/AuditLog/Resource/{id}` | Board, Admin | GET: Per-resource sync audit log |
| `/AuditLog/Human/{id}` | HumanAdmin, Board, Admin | GET: Per-user sync audit log |
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
| `/Google/SyncOutbox/{id}/Requeue` | Admin | POST: Requeue a single failed sync outbox event |
| `/Google/SyncOutbox/RequeueAll` | Admin | POST: Requeue all permanently-failed sync outbox events |
| `/Google/Human/{id}/RerunSync` | Admin | POST: Enqueue Google sync for all of a user's teams |

### Admin dashboard and diagnostics

`AdminController` owns only the shared dashboard. Legacy technical operations now live on `DebugController`; per-human purge lives on `UsersAdminController`.

| Route | Action | Description |
|-------|--------|-------------|
| `/Admin` | Index | Admin dashboard |
| `/Users/Admin/{id}/Purge` | PurgeHuman | POST: Dev/QA user purge (non-production); on `UsersAdminController` |
| `/Debug/Configuration` | Configuration | Configuration status page |
| `/Debug/Logs` | Logs | View recent log entries |
| `/Debug/DbStats` | DbStats | Database query statistics |
| `/Debug/DbStats/Reset` | ResetDbStats | POST: Reset query statistics |
| `/Debug/CacheStats` | CacheStats | Cache hit/miss statistics per key type |
| `/Debug/CacheStats/Reset` | ResetCacheStats | POST: Reset cache statistics |
| `/Debug/DbVersion` | DbVersion | Database migration version |
| `/Debug/Maintenance/ClearHangfireLocks` | ClearHangfireLocks | POST: Admin-only lock cleanup |

### UsersAdminAccountMergesController (`/Users/Admin/AccountMerges/`) — Admin only

The single unified account-merge surface (PR #899 consolidation). It combines duplicate-account detection (`IDuplicateAccountService`, detection-only) and user-submitted merge requests (`IAccountMergeService`) into one queue. The old separate `/Admin/DuplicateAccounts` and `/Admin/MergeRequests` screens (and their controllers) were deleted. Both `AccountMergeService` and `DuplicateAccountService` are now Users-section services.

| Route | Action | Description |
|-------|--------|-------------|
| `/Users/Admin/AccountMerges` | Index | Unified queue of pending merge requests + detected duplicate pairs |
| `/Users/Admin/AccountMerges/Merge` | Merge | POST: Merge an ad-hoc survivor/archived pair |
| `/Users/Admin/AccountMerges/{requestId}/Merge` | MergeRequest | POST: Accept a merge request (admin picks survivor) |
| `/Users/Admin/AccountMerges/{requestId}/Dismiss` | Dismiss | POST: Reject a merge request, no account changes |
| `/Users/Admin/AccountMerges/{requestId}/Close` | Close | POST: Reconcile an orphan request whose accounts already merged |

## Dashboard Metrics

### AdminDashboardViewModel
```csharp
public sealed record AdminDashboardViewModel(
    string GreetingFirstName,
    int TotalUsers,
    int ActiveProfileUsers,
    int TicketHolders,
    int ShiftCoveragePercent,
    int? ShiftFilledOf,
    int? ShiftTotalOf,
    int OpenFeedback,
    int OnlineNow,
    int OnlineLastHour,
    int OnlineLast24h,
    IReadOnlyList<DepartmentCoverage> StaffingByDepartment,
    IReadOnlyList<DashboardActivityRow> RecentActivity,
    DashboardApplicationStats AppStats,
    IReadOnlyList<DashboardLanguageCount> LanguageDistribution,
    UserSetMembership SetMembership);
```

`AdminController.Index` builds these from a single `IUserServiceRead.GetAllUserInfosAsync` snapshot (counts derived from `UserInfo.IsActive` / `HasTicketForYear`), shift coverage from `IShiftManagementService`, actionable feedback from `IFeedbackService`, recent audit rows from `IAuditViewerService`, and application/language/set-membership stats from `IAdminDashboardService` — not from direct table queries.

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
| **HumanAdmin** | View human admin pages, suspend/reject humans, provision @nobodies.team accounts, manage role assignments. Does NOT include Board or Admin capabilities. |
| **TeamsAdmin** | System-wide team management (edit teams, approve joins, assign coordinators, configure Google Group prefixes). Can view sync status at `/Google/Sync` but cannot execute sync actions. |
| **CampAdmin** | Manage camps, approve/reject season registrations, configure camp settings system-wide. |
| **TicketAdmin** | Manage ticket vendor integration, trigger syncs, generate discount codes, export ticket data. |
| **NoInfoAdmin** | Approve/voluntell shift signups (cannot create/edit shifts). Access to volunteer event profile medical data. |
| **FeedbackAdmin** | View all feedback reports, respond to reporters, manage feedback status, link GitHub issues. |
| **FinanceAdmin** | Manage budgets, budget years, groups, categories, and line items. Full Finance section access. |
| **ConsentCoordinator** | Safety checks on new humans during onboarding. Can clear or flag consent checks. |
| **VolunteerCoordinator** | Read-only access to onboarding review queue. |
| **EETeamAdmin** | Cross-team Early-Entry administrator — grant/edit/revoke early-entry grants on any team that has early entry enabled. Confers nothing else; team coordinators manage EE on their own team without this role. |

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
| Manage Humans | `/Users/Admin` | - |
| Audit Log | `/AuditLog` | - |
| Sync Status | `/Google/Sync` | - |

### Admin Dashboard (`/Admin`)

| Action | Link | Badge |
|--------|------|-------|
| Sync Settings | `/Google/SyncSettings` | - |
| Configuration Status | `/Debug/Configuration` | - |
| Background Jobs | `/hangfire` | - |
| Check Group Settings | `/Google/CheckGroupSettings` | - |

## System Health

### Dashboard Indicators
- **Database Connection**: Green if responsive
- **Background Jobs**: Green if Hangfire server active
- **Health Check URL**: `/health/ready`
- **Sync System Teams**: Button to manually trigger `SystemTeamSyncJob.ExecuteAsync()`, which recalculates membership for Volunteers, Coordinators, and Board teams. Useful for fixing users whose name or consent state changed before the scheduled sync ran.

### Prometheus Metrics
- Available at `/metrics`
- Scraped by monitoring infrastructure

## Related Features

- [Authentication](../auth/authentication.md) - Admin role authorization
- [Asociado Applications](../governance/asociado-applications.md) - Voting member application review
- [Teams](../teams/teams.md) - Team management
- [Background Jobs](../global/background-jobs.md) - Hangfire dashboard
